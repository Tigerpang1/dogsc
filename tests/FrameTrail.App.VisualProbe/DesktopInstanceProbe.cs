using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Windows.Threading;
using FrameTrail.App;

internal static class DesktopInstanceProbe
{
    private const int MaxRequestBytes = 32 * 1024;
    private const byte RejectedAck = 0x15;

    public static void Verify(Action<bool, string, object?> check)
    {
        var scope = $"probe.{Guid.NewGuid():N}";
        using var receivedRequests = new BlockingCollection<string[]>();
        var primaryReady = new TaskCompletionSource<(DesktopInstance Instance, Dispatcher Dispatcher)>(TaskCreationOptions.RunContinuationsAsynchronously);
        DesktopInstance? primary = null;
        Dispatcher? primaryDispatcher = null;

        var primaryThread = new Thread(() =>
        {
            try
            {
                primaryDispatcher = Dispatcher.CurrentDispatcher;
                primary = new DesktopInstance(scope);
                primary.StartListening(arguments => receivedRequests.Add(arguments));
                primaryReady.TrySetResult((primary, primaryDispatcher));
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                primaryReady.TrySetException(ex);
            }
            finally
            {
                primary?.Dispose();
            }
        })
        {
            IsBackground = true,
            Name = "FrameTrail desktop instance probe"
        };
        primaryThread.SetApartmentState(ApartmentState.STA);
        primaryThread.Start();

        DesktopInstance? secondary = null;
        try
        {
            var ready = primaryReady.Task.GetAwaiter().GetResult();
            primary = ready.Instance;
            primaryDispatcher = ready.Dispatcher;

            secondary = new DesktopInstance(scope);
            check(primary.IsPrimary && !secondary.IsPrimary,
                "the isolated primary owns the session mutex and an instance on another thread is secondary",
                new { Primary = primary.IsPrimary, Secondary = secondary.IsPrimary });

            var arguments = new[] { "--open", "C:\\probe\\project.frametrail" };
            var acknowledged = secondary.SendToPrimaryAsync(arguments, CancellationToken.None).GetAwaiter().GetResult();
            var received = receivedRequests.TryTake(out var firstRequest, TimeSpan.FromSeconds(3));
            check(acknowledged && received && firstRequest!.SequenceEqual(arguments),
                "relays launch arguments to the primary dispatcher and receives its acknowledgement",
                new { acknowledged, Received = firstRequest });

            var senderRejectedOversized = false;
            try
            {
                secondary.SendToPrimaryAsync([new string('x', MaxRequestBytes)], CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (ArgumentException)
            {
                senderRejectedOversized = true;
            }
            check(senderRejectedOversized,
                "rejects an oversized launch request before opening a pipe connection",
                new { senderRejectedOversized });

            using (var oversized = CreateClient(primary.PipeNameForProbe))
            {
                oversized.Connect(2500);
                var header = new byte[sizeof(int)];
                BinaryPrimitives.WriteInt32LittleEndian(header, MaxRequestBytes + 1);
                oversized.Write(header);
                oversized.Flush();
                var acknowledgement = new byte[1];
                oversized.ReadExactly(acknowledgement);
                check(acknowledgement[0] == RejectedAck,
                    "rejects a peer that declares a request larger than the pipe limit",
                    new { Acknowledgement = acknowledgement[0] });
            }

            using (var stalled = CreateClient(primary.PipeNameForProbe))
            {
                stalled.Connect(2500);
                stalled.Write(new byte[] { 0x04, 0x00 });
                stalled.Flush();
                Task.Delay(TimeSpan.FromMilliseconds(3400)).GetAwaiter().GetResult();
            }

            var afterStallArgs = new[] { "--open", "after-stalled-client.frametrail" };
            var reconnected = secondary.SendToPrimaryAsync(afterStallArgs, CancellationToken.None).GetAwaiter().GetResult();
            var receivedAfterStall = receivedRequests.TryTake(out var afterStallRequest, TimeSpan.FromSeconds(3));
            check(reconnected && receivedAfterStall && afterStallRequest!.SequenceEqual(afterStallArgs),
                "closes an incomplete request after its deadline and accepts a later client",
                new { reconnected, Received = afterStallRequest });

            secondary.Dispose();
            secondary = null;
            StopPrimary(primary, primaryDispatcher, primaryThread);
            primary = null;
            primaryDispatcher = null;

            using var replacement = new DesktopInstance(scope);
            check(replacement.IsPrimary,
                "releases the primary mutex on Dispose so a later launch can become primary",
                new { replacement.IsPrimary });
        }
        finally
        {
            secondary?.Dispose();
            if (primary is not null && primaryDispatcher is not null)
                StopPrimary(primary, primaryDispatcher, primaryThread);
            else if (primaryThread.IsAlive)
                primaryThread.Join(TimeSpan.FromSeconds(3));
        }
    }

    private static NamedPipeClientStream CreateClient(string pipeName) =>
        new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

    private static void StopPrimary(DesktopInstance primary, Dispatcher dispatcher, Thread thread)
    {
        if (!thread.IsAlive)
            return;

        if (!dispatcher.HasShutdownStarted)
        {
            dispatcher.InvokeAsync(() =>
            {
                primary.Dispose();
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }).Task.GetAwaiter().GetResult();
        }

        if (!thread.Join(TimeSpan.FromSeconds(3)))
            throw new TimeoutException("DesktopInstance probe primary dispatcher did not stop.");
    }
}
