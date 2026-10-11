using System.Diagnostics;
using System.IO;
using System.Text.Json;
using FrameTrail.Media;

namespace FrameTrail.Render.Probe;

/// <summary>Background-only timing and resource-lifetime probe; creates no windows or audio.</summary>
public static class FrameClockProbe
{
    public static async Task RunAsync(string? outputPath = null, int samplesPerRate = 180)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Frame clock probe requires Windows.");
        if (samplesPerRate < 1) throw new ArgumentOutOfRangeException(nameof(samplesPerRate));

        var highResolution = false;
        var rateResults = new List<RateResult>();
        var taskDelayResults = new List<RateResult>();
        foreach (var hz in new[] { 30, 60 })
        {
            var legacyPeriodTicks = Stopwatch.Frequency / (double)hz;
            var legacyOrigin = Stopwatch.GetTimestamp();
            var legacyOvershoots = new double[samplesPerRate];
            for (var i = 1; i <= samplesPerRate; i++)
            {
                var deadline = legacyOrigin + (long)Math.Round(i * legacyPeriodTicks);
                var remaining = deadline - Stopwatch.GetTimestamp();
                if (remaining > 0) await Task.Delay(TimeSpan.FromSeconds(remaining / (double)Stopwatch.Frequency));
                legacyOvershoots[i - 1] = Math.Max(0, (Stopwatch.GetTimestamp() - deadline) * 1000d / Stopwatch.Frequency);
            }
            Array.Sort(legacyOvershoots);
            taskDelayResults.Add(new RateResult(hz, samplesPerRate, Percentile(legacyOvershoots, .50), Percentile(legacyOvershoots, .95), legacyOvershoots[^1]));

            await using var clock = new PlaybackFrameClock();
            highResolution = clock.IsHighResolution;
            var periodTicks = Stopwatch.Frequency / (double)hz;
            var origin = Stopwatch.GetTimestamp();
            var overshootsMs = new double[samplesPerRate];
            for (var i = 1; i <= samplesPerRate; i++)
            {
                var deadline = origin + (long)Math.Round(i * periodTicks);
                await clock.WaitUntilAsync(deadline);
                overshootsMs[i - 1] = Math.Max(0, (Stopwatch.GetTimestamp() - deadline) * 1000d / Stopwatch.Frequency);
            }
            Array.Sort(overshootsMs);
            rateResults.Add(new RateResult(hz, samplesPerRate, Percentile(overshootsMs, .50), Percentile(overshootsMs, .95), overshootsMs[^1]));
        }

        double cancellationMs;
        double reuseWaitElapsedMs;
        await using (var clock = new PlaybackFrameClock())
        using (var source = new CancellationTokenSource())
        {
            var wait = clock.WaitUntilAsync(PlaybackFrameClock.DeadlineAfter(TimeSpan.FromSeconds(5)), source.Token).AsTask();
            await Task.Delay(50);
            var requestedAt = Stopwatch.GetTimestamp();
            source.Cancel();
            try { await wait; throw new InvalidOperationException("Canceled wait completed normally."); }
            catch (OperationCanceledException) { cancellationMs = (Stopwatch.GetTimestamp() - requestedAt) * 1000d / Stopwatch.Frequency; }

            var reuseStarted = Stopwatch.GetTimestamp();
            var reuseDeadline = PlaybackFrameClock.DeadlineAfter(TimeSpan.FromMilliseconds(30));
            await clock.WaitUntilAsync(reuseDeadline);
            reuseWaitElapsedMs = (Stopwatch.GetTimestamp() - reuseStarted) * 1000d / Stopwatch.Frequency;
            if (reuseWaitElapsedMs < 20) throw new InvalidOperationException($"A canceled timer event leaked into the next wait ({reuseWaitElapsedMs:F3} ms).");
        }

        var disposingClock = new PlaybackFrameClock();
        var pendingWait = disposingClock.WaitUntilAsync(PlaybackFrameClock.DeadlineAfter(TimeSpan.FromSeconds(10))).AsTask();
        await Task.Delay(50);
        disposingClock.Dispose();
        disposingClock.Dispose();
        var disposeCanceledPendingWait = false;
        try { await pendingWait.WaitAsync(TimeSpan.FromSeconds(1)); }
        catch (OperationCanceledException) { disposeCanceledPendingWait = true; }
        if (!disposeCanceledPendingWait) throw new InvalidOperationException("Dispose did not cancel the pending frame wait.");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var process = Process.GetCurrentProcess();
        var before = process.HandleCount;
        for (var i = 0; i < 1000; i++)
        {
            using var clock = new PlaybackFrameClock();
            await clock.WaitUntilAsync(Stopwatch.GetTimestamp());
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var after = process.HandleCount;
        var report = new ProbeResult(DateTimeOffset.UtcNow, Environment.OSVersion.VersionString, highResolution, rateResults, taskDelayResults, cancellationMs, reuseWaitElapsedMs, disposeCanceledPendingWait, before, after, after - before);
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        if (string.IsNullOrWhiteSpace(outputPath)) Console.WriteLine(json);
        else
        {
            var fullPath = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, json);
            Console.WriteLine(fullPath);
        }
    }

    private static double Percentile(double[] sorted, double p) => sorted[(int)Math.Ceiling(p * sorted.Length) - 1];

    private sealed record RateResult(int Hertz, int Samples, double P50OvershootMs, double P95OvershootMs, double MaxOvershootMs);
    private sealed record ProbeResult(DateTimeOffset CapturedAtUtc, string WindowsVersion, bool HighResolutionTimer, IReadOnlyList<RateResult> Rates, IReadOnlyList<RateResult> TaskDelayBaseline, double CancellationResponseMs, double ReuseWaitElapsedMs, bool DisposeCanceledPendingWait, int HandlesBefore, int HandlesAfter, int HandleDelta);
}
