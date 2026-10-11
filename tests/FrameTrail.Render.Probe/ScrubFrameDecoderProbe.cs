using System.Diagnostics;
using System.IO;
using System.Reflection;
using FrameTrail.Media;

namespace FrameTrail.Render.Probe;

internal static class ScrubFrameDecoderProbe
{
    public static async Task RunAsync(string? outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory ?? Path.Combine("artifacts", "validation", "scrub-decoder-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(root);
        var report = new List<string> { "ScrubFrameDecoder 本机 FFmpeg CFR 对照", $"Output: {root}" };
        foreach (var frameRate in new[] { 30, 60 })
            await ProbeRateAsync(root, frameRate, report);
        await ProbeVariableRateAsync(root, report);
        await ProbeTrimmedIndexCoverageAsync(root, report);
        var reportPath = Path.Combine(root, "scrub-decoder-report.txt");
        await File.WriteAllLinesAsync(reportPath, report);
        Console.WriteLine(string.Join(Environment.NewLine, report));
        Console.WriteLine($"Report: {reportPath}");
    }

    private static async Task ProbeTrimmedIndexCoverageAsync(string root, List<string> report)
    {
        const int width = 32, height = 18, frameRate = 30, frameCount = 4_400;
        var path = Path.Combine(root, "testsrc2-index-trim.mp4");
        await MediaTools.RunAsync(MediaTools.Ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", $"testsrc2=size={width}x{height}:rate={frameRate}:duration=147",
             "-frames:v", frameCount.ToString(System.Globalization.CultureInfo.InvariantCulture), "-an", "-c:v", "libx264", "-preset", "ultrafast",
             "-crf", "24", "-g", "30", "-bf", "0", "-pix_fmt", "yuv420p", "-y", path]);
        var info = await MediaTools.ProbeAsync(path);

        await using var decoder = new ScrubFrameDecoder(path, width, height, frameRate, info.Duration);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var restart = typeof(ScrubFrameDecoder).GetMethod("RestartAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(ScrubFrameDecoder).FullName, "RestartAsync");
        await ((Task)restart.Invoke(decoder, [0d, timeout.Token])!).ConfigureAwait(false);
        var readAndIndex = typeof(ScrubFrameDecoder).GetMethod("ReadAndIndexFrameAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(ScrubFrameDecoder).FullName, "ReadAndIndexFrameAsync");
        for (var i = 1; i < frameCount; i++)
            await ((Task)readAndIndex.Invoke(decoder, [timeout.Token])!).ConfigureAwait(false);

        var startsBeforeOldRequest = decoder.ProcessStartCount;
        var oldRequestedTime = .1;
        var actual = await decoder.ReadFrameAsync(oldRequestedTime, timeout.Token).ConfigureAwait(false);
        var expected = await MediaTools.DecodeFrameAsync(path, oldRequestedTime, width, height, timeout.Token).ConfigureAwait(false);
        if (!actual.AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException("An old request below the trimmed index coverage returned the first retained frame instead of restarting to the requested frame.");
        if (startsBeforeOldRequest != 1 || decoder.ProcessStartCount != 2 || decoder.OneShotFallbackActive)
            throw new InvalidOperationException($"Trimmed-index coverage did not restart cleanly: startsBefore={startsBeforeOldRequest}, startsAfter={decoder.ProcessStartCount}, fallback={decoder.OneShotFallbackActive}.");
        report.Add($"Index trim: frames={frameCount}, retained window=4096, old request exact match after one clean restart, starts={decoder.ProcessStartCount}, fallback={decoder.OneShotFallbackActive}");
    }

    private static async Task ProbeVariableRateAsync(string root, List<string> report)
    {
        const int width = 320, height = 180, nominalRate = 30;
        var path = Path.Combine(root, "testsrc2-vfr.mp4");
        await MediaTools.RunAsync(MediaTools.Ffmpeg,
        ["-v", "error", "-f", "lavfi", "-i", $"testsrc2=size={width}x{height}:rate={nominalRate}:duration=4",
         "-vf", "select='if(lt(t,2),not(mod(n,2)),not(mod(n,3)))'", "-fps_mode", "vfr", "-c:v", "libx264",
         "-preset", "ultrafast", "-crf", "18", "-pix_fmt", "yuv420p", "-y", path]);
        var info = await MediaTools.ProbeAsync(path);
        var framePtsText = await MediaTools.RunAsync(MediaTools.Ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-show_frames", "-show_entries", "frame=best_effort_timestamp_time", "-of", "csv=p=0", path]);
        var pts = framePtsText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(',')[0])
            .Where(value => double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
            .Select(value => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        if (pts.Length < 20 || pts.Select((value, index) => index == 0 ? 0 : Math.Round(value - pts[index - 1], 5)).Distinct().Count() < 3)
            throw new InvalidOperationException("Generated VFR source did not contain multiple frame intervals.");

        await using var decoder = new ScrubFrameDecoder(path, width, height, nominalRate, info.Duration);
        var requests = new[] { .1, .34, 1.99, 2.01, 2.3, 3.61, 1.99, .34 };
        foreach (var time in requests)
        {
            var actual = await decoder.ReadFrameAsync(time);
            var expected = await MediaTools.DecodeFrameAsync(path, time, width, height, CancellationToken.None);
            if (!actual.AsSpan().SequenceEqual(expected)) throw new InvalidOperationException($"VFR frame at {time:F3}s differs from DecodeFrameAsync.");
        }
        var startCount = decoder.ProcessStartCount;
        _ = await decoder.ReadFrameAsync(.34);
        if (decoder.ProcessStartCount != startCount) throw new InvalidOperationException("Reverse VFR cache hit restarted FFmpeg.");
        var last = await decoder.ReadFrameAsync(info.Duration);
        var expectedLast = await MediaTools.DecodeFrameAsync(path, pts[^1], width, height, CancellationToken.None);
        if (!last.AsSpan().SequenceEqual(expectedLast)) throw new InvalidOperationException("VFR tail frame differs from DecodeFrameAsync.");
        report.Add($"VFR: frames={pts.Length}, pts interval variation=verified, requests={requests.Length}, exact matches, reverse cache hit, tail=passed, starts={decoder.ProcessStartCount}, cacheBytes={decoder.CacheBytes:N0}");

        var shiftedPath = Path.Combine(root, "testsrc2-start-offset-5s.mp4");
        await MediaTools.RunAsync(MediaTools.Ffmpeg,
            ["-v", "error", "-itsoffset", "5", "-f", "lavfi", "-i", $"testsrc2=size={width}x{height}:rate={nominalRate}:duration=4",
             "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-y", shiftedPath]);
        var shiftedInfo = await MediaTools.ProbeAsync(shiftedPath);
        await using var shiftedDecoder = new ScrubFrameDecoder(shiftedPath, width, height, nominalRate, shiftedInfo.Duration);
        foreach (var time in new[] { 0d, .1, 2.5 })
        {
            var actual = await shiftedDecoder.ReadFrameAsync(time);
            var expected = await MediaTools.DecodeFrameAsync(shiftedPath, time, width, height, CancellationToken.None);
            if (!actual.AsSpan().SequenceEqual(expected)) throw new InvalidOperationException($"Shifted-PTS frame at {time:F3}s differs from DecodeFrameAsync.");
        }
        if (shiftedDecoder.ProcessStartCount != 2) throw new InvalidOperationException("PTS normalization failed to reuse the shifted-start stream for nearby requests.");
        report.Add($"PTS start offset: ffprobe start_time=5s, requests=3, exact matches, starts={shiftedDecoder.ProcessStartCount}");
    }

    private static async Task ProbeRateAsync(string root, int frameRate, List<string> report)
    {
        const int width = 320, height = 180;
        const double duration = 4;
        var path = Path.Combine(root, $"testsrc2-{frameRate}fps.mp4");
        await MediaTools.RunAsync(MediaTools.Ffmpeg,
        ["-v", "error", "-f", "lavfi", "-i", $"testsrc2=size={width}x{height}:rate={frameRate}:duration={duration}",
         "-an", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "18", "-pix_fmt", "yuv420p", "-y", path]);
        var info = await MediaTools.ProbeAsync(path);
        if (Math.Abs(info.FrameRate - frameRate) > .001) throw new InvalidOperationException($"Expected {frameRate} fps; ffprobe found {info.FrameRate}.");

        await using var decoder = new ScrubFrameDecoder(path, width, height, frameRate, info.Duration);
        var samples = new[] { 0, 5, 10, 11, 12, 20, 21, 22, 40, 41, 42, 90, 91, 92, 40, 5 };
        var coldMs = 0d;
        var warmMs = 0d;
        foreach (var frameIndex in samples)
        {
            var elapsed = Stopwatch.StartNew();
            var actual = await decoder.ReadFrameAsync(frameIndex / (double)frameRate);
            elapsed.Stop();
            if (frameIndex == samples[0]) coldMs = elapsed.Elapsed.TotalMilliseconds;
            else warmMs += elapsed.Elapsed.TotalMilliseconds;
            var expected = await MediaTools.DecodeFrameAsync(path, frameIndex / (double)frameRate, width, height, CancellationToken.None);
            if (!actual.AsSpan().SequenceEqual(expected))
                throw new InvalidOperationException($"{frameRate} fps frame {frameIndex} differs from DecodeFrameAsync.");
        }

        var beforeReverseHit = decoder.ProcessStartCount;
        var reverseCacheFrame = await decoder.ReadFrameAsync(40 / (double)frameRate);
        var reverseExpected = await MediaTools.DecodeFrameAsync(path, 40 / (double)frameRate, width, height, CancellationToken.None);
        if (!reverseCacheFrame.AsSpan().SequenceEqual(reverseExpected) || decoder.ProcessStartCount != beforeReverseHit)
            throw new InvalidOperationException("A reverse cache hit changed pixels or restarted FFmpeg.");

        var cacheHitCount = decoder.CacheHits;
        var beforeFarSeek = decoder.ProcessStartCount;
        var jump = await decoder.ReadFrameAsync(3.8);
        var expectedJump = await MediaTools.DecodeFrameAsync(path, 3.8, width, height, CancellationToken.None);
        if (!jump.AsSpan().SequenceEqual(expectedJump)) throw new InvalidOperationException($"{frameRate} fps far seek differs from DecodeFrameAsync.");
        if (decoder.ProcessStartCount <= beforeFarSeek) throw new InvalidOperationException("A far seek did not restart the decoder.");

        var lastTime = Math.Max(0, info.Duration - 1d / frameRate);
        var last = await decoder.ReadFrameAsync(info.Duration);
        var expectedLast = await MediaTools.DecodeFrameAsync(path, lastTime, width, height, CancellationToken.None);
        if (!last.AsSpan().SequenceEqual(expectedLast)) throw new InvalidOperationException($"{frameRate} fps last frame differs from DecodeFrameAsync.");
        var beyondEnd = await decoder.ReadFrameAsync(info.Duration + 1);
        if (!beyondEnd.AsSpan().SequenceEqual(expectedLast)) throw new InvalidOperationException($"{frameRate} fps request beyond duration did not clamp to the actual last frame.");

        // A non-frame-aligned input seek can start on the next actual PTS. The run must
        // still cover the seek request and reuse that PTS instead of restarting into fallback.
        await using (var seekCoverage = new ScrubFrameDecoder(path, width, height, frameRate, info.Duration))
        {
            const double nonAlignedTime = 3.81;
            var nonAligned = await seekCoverage.ReadFrameAsync(nonAlignedTime);
            var expectedNonAligned = await MediaTools.DecodeFrameAsync(path, nonAlignedTime, width, height, CancellationToken.None);
            if (!nonAligned.AsSpan().SequenceEqual(expectedNonAligned))
                throw new InvalidOperationException($"{frameRate} fps non-frame-aligned seek differs from DecodeFrameAsync.");
            var startsAfterFirstRead = seekCoverage.ProcessStartCount;
            var nearby = await seekCoverage.ReadFrameAsync(nonAlignedTime + .005);
            if (!nearby.AsSpan().SequenceEqual(nonAligned) || seekCoverage.ProcessStartCount != startsAfterFirstRead)
                throw new InvalidOperationException($"{frameRate} fps nearby request did not reuse the first PTS after seek.");
            if (seekCoverage.ProcessStartCount != 1 || seekCoverage.OneShotFallbackActive)
                throw new InvalidOperationException($"{frameRate} fps non-frame-aligned seek restarted or entered one-shot fallback.");
            report.Add($"{frameRate} fps non-aligned seek: requested={nonAlignedTime:F3}s, exact match, nearby reuse, starts={seekCoverage.ProcessStartCount}, fallback={seekCoverage.OneShotFallbackActive}");
        }

        await using (var cancelDecoder = new ScrubFrameDecoder(path, 3840, 2160, frameRate, info.Duration))
        {
            await cancelDecoder.ReadFrameAsync(0);
            await cancelDecoder.ReadFrameAsync(.5);
            await cancelDecoder.ReadFrameAsync(1.0);
            await cancelDecoder.ReadFrameAsync(1.5);
            await cancelDecoder.ReadFrameAsync(0); // the first 4K frame must have been evicted
            var safeBytes = cancelDecoder.CacheBytes;
            var threeFrameBytes = 3L * 3840 * 2160 * 4;
            if (safeBytes != threeFrameBytes) throw new InvalidOperationException("The 4K LRU did not evict to its byte budget.");
            if (safeBytes > 96L * 1024 * 1024) throw new InvalidOperationException("The 4K frame cache exceeded its byte budget.");
            using var canceled = new CancellationTokenSource();
            var priorStarts = cancelDecoder.ProcessStartCount;
            var pending = cancelDecoder.ReadFrameAsync(2.0, canceled.Token);
            var wait = Stopwatch.StartNew();
            while (cancelDecoder.ProcessStartCount == priorStarts && wait.Elapsed < TimeSpan.FromSeconds(2)) await Task.Yield();
            canceled.Cancel();
            try
            {
                await pending;
                throw new InvalidOperationException("Cancellation request completed before it could interrupt FFmpeg output.");
            }
            catch (OperationCanceledException)
            {
                if (cancelDecoder.CacheBytes < safeBytes) throw new InvalidOperationException("Cancellation discarded previously cached frames.");
                if (cancelDecoder.ActiveProcessId is not null) throw new InvalidOperationException("Cancellation left an FFmpeg process running.");
                var afterCancel = await cancelDecoder.ReadFrameAsync(2.0);
                var expectedAfterCancel = await MediaTools.DecodeFrameAsync(path, 2.0, 3840, 2160, CancellationToken.None);
                if (!afterCancel.AsSpan().SequenceEqual(expectedAfterCancel)) throw new InvalidOperationException("The decoder did not recover after cancellation.");
                report.Add($"{frameRate} fps cancellation: interrupted FFmpeg, preserved {cancelDecoder.CacheBytes:N0} cache bytes and recovered");
            }
        }

        var recovery = await decoder.ReadFrameAsync(1.5);
        var recoveryExpected = await MediaTools.DecodeFrameAsync(path, 1.5, width, height, CancellationToken.None);
        if (!recovery.AsSpan().SequenceEqual(recoveryExpected)) throw new InvalidOperationException($"{frameRate} fps decode did not recover after cache tests.");
        var processId = decoder.ActiveProcessId;
        if (processId is null) throw new InvalidOperationException("The decoder did not retain its sequential FFmpeg process.");
        var starts = decoder.ProcessStartCount;
        var hit = await decoder.ReadFrameAsync(1.5);
        if (!hit.AsSpan().SequenceEqual(recovery)) throw new InvalidOperationException("A cache read returned different pixels.");
        if (decoder.CacheHits <= cacheHitCount) throw new InvalidOperationException("A repeated read was not served from cache.");
        if (decoder.ProcessStartCount != starts) throw new InvalidOperationException("A cached read started another FFmpeg process.");
        if (decoder.CacheBytes > 96L * 1024 * 1024) throw new InvalidOperationException("The cache exceeded its 96 MiB budget.");

        // Caller-owned pixels must never mutate an entry held by the cache.
        Array.Fill(hit, (byte)0);
        var immutable = await decoder.ReadFrameAsync(1.5);
        if (!immutable.AsSpan().SequenceEqual(recoveryExpected)) throw new InvalidOperationException("Caller mutation changed cached pixels.");

        report.Add($"{frameRate} fps: samples={samples.Length}, exact matches, cold={coldMs:F2} ms, later requests total={warmMs:F2} ms, cacheHits={decoder.CacheHits}, cacheBytes={decoder.CacheBytes:N0}, starts={decoder.ProcessStartCount}, forward/reverse/far/tail/beyond-end/cache/recovery=passed, activePid={processId}");
        await decoder.DisposeAsync();
        if (decoder.CacheBytes != 0 || decoder.ActiveProcessId is not null)
            throw new InvalidOperationException("Disposal did not release cached frames and the active FFmpeg process.");
        try
        {
            using var process = Process.GetProcessById(processId.Value);
            if (!process.HasExited) throw new InvalidOperationException($"FFmpeg process {processId} remained alive after disposal.");
        }
        catch (ArgumentException) { }
    }
}
