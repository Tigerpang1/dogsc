using System.Diagnostics;
using System.IO.Pipes;

namespace FrameTrail.Media;

/// <summary>Local, user-scoped raw frame transport with enough buffering for high-resolution video.</summary>
internal sealed class PreviewFramePipe : IDisposable
{
    private readonly NamedPipeServerStream stream;
    internal string OutputPath { get; }

    internal PreviewFramePipe()
    {
        var name = "frametrail-preview-" + Guid.NewGuid().ToString("N");
        OutputPath = @"\\.\pipe\" + name;
        stream = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 1024 * 1024, 0);
    }

    internal async Task ConnectAsync(Process producer, CancellationToken cancellation)
    {
        using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        connectionCancellation.CancelAfter(TimeSpan.FromSeconds(10));
        var connected = stream.WaitForConnectionAsync(connectionCancellation.Token);
        var exited = producer.WaitForExitAsync(connectionCancellation.Token);
        try
        {
            var first = await Task.WhenAny(connected, exited).ConfigureAwait(false);
            if (first == exited && !connected.IsCompletedSuccessfully)
            {
                await exited.ConfigureAwait(false);
                throw new IOException("视频解码器在建立预览帧连接前退出。");
            }
            await connected.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new IOException("视频解码器未能及时建立预览帧连接。");
        }
        finally
        {
            connectionCancellation.Cancel();
            try { await connected.ConfigureAwait(false); } catch { }
            try { await exited.ConfigureAwait(false); } catch { }
        }
    }

    internal async Task ReadExactlyAsync(byte[] buffer, CancellationToken cancellation) =>
        await stream.ReadExactlyAsync(buffer, cancellation).ConfigureAwait(false);

    public void Dispose() => stream.Dispose();
}
