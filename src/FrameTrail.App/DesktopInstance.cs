using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace FrameTrail.App;

/// <summary>
/// Coordinates one FrameTrail process per Windows login session and relays later launches
/// to the primary process through a current-user-only named pipe.
/// </summary>
internal sealed class DesktopInstance : IDisposable
{
    private const int MaxRequestBytes = 32 * 1024;
    private const int MaxArgumentCount = 512;
    private const int StartupWaitMilliseconds = 2500;
    private const int ConnectAttemptMilliseconds = 250;
    private const int RequestDeadlineMilliseconds = 3000;
    private const byte AcceptedAck = 0x06;
    private const byte RejectedAck = 0x15;

    private readonly Mutex? instanceMutex;
    private readonly Dispatcher dispatcher;
    private readonly int ownerThreadId;
    private readonly string pipeName;
    private CancellationTokenSource? listenerCancellation;
    private Task? listeningTask;
    private bool disposed;

    public bool IsPrimary { get; }

    /// <summary>
    /// Completes when the listener stops. A fault indicates that the primary process
    /// could no longer accept launch requests and should be reported by the caller.
    /// </summary>
    public Task ListeningTask => listeningTask ?? Task.CompletedTask;
    internal string PipeNameForProbe => pipeName;

    public DesktopInstance() : this(string.Empty)
    {
    }

    internal DesktopInstance(string scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        dispatcher = Dispatcher.CurrentDispatcher;
        ownerThreadId = Environment.CurrentManagedThreadId;

        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value;
        if (string.IsNullOrWhiteSpace(sid))
            throw new InvalidOperationException("无法读取当前 Windows 用户 SID，不能安全确定 DogSC 单实例标识。");

        var identityHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid))).ToLowerInvariant();
        using var process = Process.GetCurrentProcess();
        var sessionId = process.SessionId;
        var scopeSuffix = scope.Length == 0
            ? string.Empty
            : $".{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope))).ToLowerInvariant()}";
        var objectName = $"FrameTrail.DesktopInstance.{identityHash}.{sessionId}{scopeSuffix}";
        pipeName = objectName;

        try
        {
            instanceMutex = new Mutex(false, $"Local\\{objectName}");
            try
            {
                IsPrimary = instanceMutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // An abandoned mutex is granted to the waiter, so this process takes over.
                IsPrimary = true;
            }
        }
        catch (Exception ex)
        {
            instanceMutex?.Dispose();
            throw new InvalidOperationException("无法创建或取得 DogSC 单实例互斥锁。", ex);
        }
    }

    /// <summary>Starts the primary process pipe server and dispatches requests on the captured WPF dispatcher.</summary>
    public void StartListening(Action<string[]> onRequest)
    {
        ArgumentNullException.ThrowIfNull(onRequest);
        VerifyDispatcher();
        ThrowIfDisposed();
        if (!IsPrimary)
            throw new InvalidOperationException("只有 DogSC 主实例可以启动唤回管道。");
        if (listenerCancellation is not null)
            throw new InvalidOperationException("DogSC 唤回管道已经启动。");

        var cancellation = new CancellationTokenSource();
        listenerCancellation = cancellation;
        listeningTask = Task.Run(() => ListenLoopAsync(onRequest, cancellation.Token));
    }

    /// <summary>
    /// Sends this launch's arguments to the primary instance once, waiting up to
    /// 2.5 seconds for its pipe server to become available. False means no acknowledgement arrived.
    /// </summary>
    public async Task<bool> SendToPrimaryAsync(string[] arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ThrowIfDisposed();
        if (IsPrimary) return false;

        var payload = SerializeRequest(arguments);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(StartupWaitMilliseconds);
        try
        {
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < StartupWaitMilliseconds)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var remaining = StartupWaitMilliseconds - (int)timer.ElapsedMilliseconds;
                var timeout = Math.Min(ConnectAttemptMilliseconds, Math.Max(1, remaining));

                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await client.ConnectAsync(timeout, deadline.Token).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    await Task.Delay(Math.Min(50, Math.Max(1, StartupWaitMilliseconds - (int)timer.ElapsedMilliseconds)), deadline.Token).ConfigureAwait(false);
                    continue;
                }
                catch (IOException)
                {
                    await Task.Delay(Math.Min(50, Math.Max(1, StartupWaitMilliseconds - (int)timer.ElapsedMilliseconds)), deadline.Token).ConfigureAwait(false);
                    continue;
                }

                var header = new byte[sizeof(int)];
                BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
                await client.WriteAsync(header, deadline.Token).ConfigureAwait(false);
                await client.WriteAsync(payload, deadline.Token).ConfigureAwait(false);
                await client.FlushAsync(deadline.Token).ConfigureAwait(false);

                var ack = new byte[1];
                await client.ReadExactlyAsync(ack, deadline.Token).ConfigureAwait(false);
                return ack[0] == AcceptedAck;
            }

            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        VerifyDispatcher();
        if (IsPrimary && Environment.CurrentManagedThreadId != ownerThreadId)
            throw new InvalidOperationException("主实例必须在取得互斥锁的线程上释放 DesktopInstance。");

        disposed = true;
        listenerCancellation?.Cancel();
        listenerCancellation?.Dispose();
        listenerCancellation = null;

        if (IsPrimary)
        {
            instanceMutex!.ReleaseMutex();
            instanceMutex.Dispose();
        }
        else
        {
            instanceMutex?.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private async Task ListenLoopAsync(Action<string[]> onRequest, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                4096,
                4096);

            await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            await HandleRequestAsync(server, onRequest, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleRequestAsync(NamedPipeServerStream server, Action<string[]> onRequest, CancellationToken cancellationToken)
    {
        var acknowledgement = RejectedAck;
        using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestDeadline.CancelAfter(RequestDeadlineMilliseconds);
        try
        {
            var header = new byte[sizeof(int)];
            await server.ReadExactlyAsync(header, requestDeadline.Token).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length <= 0 || length > MaxRequestBytes)
                throw new InvalidDataException("唤回请求长度超出限制。");

            var payload = new byte[length];
            await server.ReadExactlyAsync(payload, requestDeadline.Token).ConfigureAwait(false);
            var arguments = JsonSerializer.Deserialize<string[]>(payload);
            if (arguments is null || arguments.Length > MaxArgumentCount || arguments.Any(argument => argument is null))
                throw new InvalidDataException("唤回请求必须是有限长度的字符串数组。");

            var operation = dispatcher.InvokeAsync(() => onRequest(arguments), DispatcherPriority.Normal, requestDeadline.Token);
            await operation.Task.ConfigureAwait(false);
            acknowledgement = AcceptedAck;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            // Reject this request; the primary keeps listening for a later launch.
        }

        try
        {
            await server.WriteAsync(new[] { acknowledgement }, requestDeadline.Token).ConfigureAwait(false);
            await server.FlushAsync(requestDeadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A stalled peer cannot hold the single-instance listener past the request deadline.
        }
        catch (IOException)
        {
            // The secondary process may have exited before receiving the acknowledgement.
        }
    }

    private static byte[] SerializeRequest(string[] arguments)
    {
        if (arguments.Length > MaxArgumentCount || arguments.Any(argument => argument is null))
            throw new ArgumentException($"唤回参数必须是最多 {MaxArgumentCount} 项的非空字符串数组。", nameof(arguments));

        var payload = JsonSerializer.SerializeToUtf8Bytes(arguments);
        if (payload.Length == 0 || payload.Length > MaxRequestBytes)
            throw new ArgumentException($"唤回请求不得超过 {MaxRequestBytes} 字节。", nameof(arguments));
        return payload;
    }

    private void VerifyDispatcher()
    {
        if (!dispatcher.CheckAccess())
            throw new InvalidOperationException("DesktopInstance 必须在创建它的 WPF Dispatcher 线程上启动和释放。");
    }

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(DesktopInstance));
    }
}
