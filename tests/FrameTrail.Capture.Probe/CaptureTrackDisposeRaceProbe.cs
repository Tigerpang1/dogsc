using FrameTrail.Capture;

internal static class CaptureTrackDisposeRaceProbe
{
    internal static async Task RunAsync()
    {
        await using var service = new CaptureTrackService();
        var startupEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStartup = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var pendingStart = service.RunStartGateProbeAsync(async () =>
        {
            startupEntered.TrySetResult(true);
            await releaseStartup.Task.ConfigureAwait(false);
        });
        await startupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        var firstDispose = service.DisposeAsync().AsTask();
        var secondDispose = service.DisposeAsync().AsTask();
        Require(ReferenceEquals(firstDispose, secondDispose), "concurrent DisposeAsync callers share one completion task");
        Require(!firstDispose.IsCompleted, "DisposeAsync waits for a start operation already inside the gate");

        var queuedStart = service.RunStartGateProbeAsync(() => Task.CompletedTask);
        releaseStartup.TrySetResult(true);
        await Task.WhenAll(pendingStart, firstDispose, secondDispose).ConfigureAwait(false);

        var rejected = false;
        try { await queuedStart.ConfigureAwait(false); }
        catch (ObjectDisposedException) { rejected = true; }
        Require(rejected, "a start queued behind disposal rechecks the disposed state after acquiring the gate");

        // A later idempotent call observes the same already-completed disposal task.
        Require(ReferenceEquals(firstDispose, service.DisposeAsync().AsTask()), "repeated DisposeAsync remains idempotent");
        Console.WriteLine("通过：CaptureTrackService 并发启动/释放回归（不使用物理音频设备）。");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
