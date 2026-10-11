using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using FrameTrail.Media;

namespace FrameTrail.Render.Probe;

internal static class NativeSeekDecoderProbe
{
    private sealed record Timeline(double StartSeconds, double DurationSeconds, double TimeBaseSeconds, double[] RelativeFrameTimes);
    private sealed record PixelComparison(int DifferentBytes, int MaximumChannelDifference, double MeanAbsoluteDifference, string FirstDifference);

    internal static async Task RunAsync(string? outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory ?? Path.Combine("artifacts", "validation", "native-seek-decoder-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")));
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new IOException($"Refusing to reuse non-empty native seek probe output: {root}");
        Directory.CreateDirectory(root);
        var results = new List<object>();

        foreach (var frameRate in new[] { 30, 60 })
        {
            var path = Path.Combine(root, $"cfr-{frameRate}fps-long-gop-bframes.mp4");
            await CreateCfrClip(path, frameRate, 160, 90, 3);
            var timeline = await ProbeTimeline(path);
            if (frameRate == 30) results.Add(await CompareD3D11VAWithCli(path, 160, 90, 30, timeline, root));
            results.Add(await VerifyNativeAndWrapper(path, 160, 90, frameRate, timeline,
                [0, .137, .733, 1.217, 2.31], $"CFR {frameRate} fps, GOP 120, B-frames"));
            if (frameRate == 60)
                results.Add(await VerifySequentialReads(path, 160, 90, frameRate, timeline, false));
        }

        var vfrPath = Path.Combine(root, "vfr-30fps.mp4");
        await CreateVfrClip(vfrPath, 160, 90, 60, 3);
        results.Add(await VerifyNativeAndWrapper(vfrPath, 160, 90, 60, await ProbeTimeline(vfrPath),
            [.11, .47, 1.01, 1.61, 2.11, 2.47], "VFR source with 60-to-20 fps cadence"));
        results.Add(await VerifySequentialReads(vfrPath, 160, 90, 60, await ProbeTimeline(vfrPath), true));

        foreach (var range in new[] { (Name: "limited", Tag: "tv", OutputRange: "tv"), (Name: "full", Tag: "pc", OutputRange: "pc") })
        {
            var colorPath = Path.Combine(root, $"bt709-{range.Name}-range-scaled.mp4");
            await CreateTaggedColorClip(colorPath, range.Tag, range.OutputRange);
            results.Add(await VerifyTaggedColorAndScale(colorPath, range.Name, root));
        }

        var rotatedPath = Path.Combine(root, "displaymatrix-90.mp4");
        await CreateRotatedClip(rotatedPath);
        results.Add(await VerifyDisplayMatrixFallback(rotatedPath, root));

        var shiftedPath = Path.Combine(root, "cfr-start-offset-5s.mp4");
        await CreateShiftedClip(shiftedPath, 160, 90, 30, 3);
        var shiftedTimeline = await ProbeTimeline(shiftedPath);
        results.Add(await VerifyNativeAndWrapper(shiftedPath, 160, 90, 30, shiftedTimeline,
            [0, .137, 1.113, 2.31], "CFR with 5-second stream PTS offset"));

        var cancelPath = Path.Combine(root, "cfr-cancellation-recovery.mp4");
        await CreateCfrClip(cancelPath, 30, 640, 360, 5);
        results.Add(await VerifyCancellationAndDisposal(cancelPath, 640, 360, 30, await ProbeTimeline(cancelPath)));

        var reportPath = Path.Combine(root, "result.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
        {
            probe = "native-libav-seek-frame-regression",
            results,
            limitations = "Synthetic FFmpeg sources only. Pixel comparisons are exact; mismatch diagnostics report byte and channel deltas without a permissive threshold."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Native seek decoder regression passed. Cases={results.Count}; report: {reportPath}");
    }

    private static async Task CreateCfrClip(string path, int frameRate, int width, int height, int durationSeconds)
    {
        await MediaTools.RunAsync(MediaTools.Ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", $"testsrc2=size={width}x{height}:rate={frameRate}:duration={durationSeconds}",
             "-an", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "18", "-g", "120", "-bf", "3", "-pix_fmt", "yuv420p", "-y", path]);
    }

    private static async Task CreateVfrClip(string path, int width, int height, int frameRate, int durationSeconds)
    {
        await MediaTools.RunAsync(MediaTools.Ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", $"testsrc2=size={width}x{height}:rate={frameRate}:duration={durationSeconds}",
             "-vf", "select='if(lt(t,1.5),1,not(mod(n,3)))'", "-fps_mode", "vfr", "-an", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "18", "-g", "120", "-bf", "3", "-pix_fmt", "yuv420p", "-y", path]);
    }

    private static async Task CreateShiftedClip(string path, int width, int height, int frameRate, int durationSeconds)
    {
        await MediaTools.RunAsync(MediaTools.Ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-itsoffset", "5", "-f", "lavfi", "-i", $"testsrc2=size={width}x{height}:rate={frameRate}:duration={durationSeconds}",
             "-an", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "18", "-g", "120", "-bf", "3", "-pix_fmt", "yuv420p", "-y", path]);
    }

    private static async Task CreateTaggedColorClip(string path, string inputRange, string outputRange)
    {
        await MediaTools.RunAsync(MediaTools.Ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30:duration=1",
             "-vf", $"scale=in_range={inputRange}:out_range={outputRange}:in_color_matrix=bt709:out_color_matrix=bt709,format=yuv420p",
             "-an", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "18", "-g", "30", "-bf", "3",
             "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", outputRange,
             "-x264-params", $"colorprim=bt709:transfer=bt709:colormatrix=bt709:fullrange={(outputRange == "pc" ? "on" : "off")}",
             "-y", path]);
    }

    private static async Task CreateRotatedClip(string path)
    {
        var basePath = path + ".base.mp4";
        await MediaTools.RunAsync(MediaTools.Ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30:duration=1",
             "-an", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "18", "-g", "30", "-bf", "3",
             "-y", basePath]);
        await MediaTools.RunAsync(MediaTools.Ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-display_rotation:v:0", "90", "-i", basePath, "-c", "copy", "-y", path]);
        File.Delete(basePath);
    }

    private static async Task<object> VerifySequentialReads(string path, int width, int height, int frameRate, Timeline timeline, bool vfr)
    {
        var points = vfr
            ? timeline.RelativeFrameTimes.Skip(2).Take(20)
                .Concat(timeline.RelativeFrameTimes.Skip(90).Take(20))
                .Concat(timeline.RelativeFrameTimes.Skip(98).Take(11).Reverse()).ToArray()
            : timeline.RelativeFrameTimes.Skip(24).Take(36).Concat(timeline.RelativeFrameTimes.Skip(52).Take(12).Reverse()).ToArray();
        Check(points.Length >= 30, $"Sequential regression expected at least 30 timestamps; got {points.Length}.");
        var deltas = points.Zip(points.Skip(1), (a, b) => Math.Abs(b - a)).ToArray();
        Check(deltas.Any(delta => delta > 0) && (vfr ? deltas.Any(delta => delta < 1.0 / 30 - timeline.TimeBaseSeconds) : true),
            "Sequential regression did not include the requested close/adjacent timestamp steps.");
        using var direct = new NativeSeekFrameDecoder(path, width, height, frameRate, timeline.DurationSeconds);
        await using var wrapper = new ScrubFrameDecoder(path, width, height, frameRate, timeline.DurationSeconds, preferNative: true);
        Check(direct.BackendName.StartsWith("libav/", StringComparison.Ordinal), $"Sequential {frameRate} fps backend was {direct.BackendName}.");
        var comparisons = new List<PixelComparison>(points.Length * 2);
        for (var i = 0; i < points.Length; i++)
        {
            var target = points[i];
            var expectedPts = FirstFrameAtOrAfter(timeline, target);
            var native = direct.ReadFrame(target, CancellationToken.None);
            var timestampError = Math.Abs(direct.LastTimestampSeconds - expectedPts);
            Check(timestampError <= Math.Max(1e-7, timeline.TimeBaseSeconds * .51),
                $"Sequential point {i}/{points.Length}, target {target:F9}s: got PTS {direct.LastTimestampSeconds:F9}s, expected {expectedPts:F9}s.");
            var reference = await MediaTools.DecodeFrameAsync(path, target, width, height, CancellationToken.None);
            await SavePixelDiagnosticsIfDifferent(Path.GetDirectoryName(path)!, $"sequential-{(vfr ? "vfr" : "cfr")}-{i:D2}", target, width, height, native, reference);
            CheckPixels(native, reference, $"Sequential native {(vfr ? "VFR" : "CFR")} read {i}/{points.Length} at {target:F6}s");
            comparisons.Add(ComparePixels(native, reference));

            var wrapped = await wrapper.ReadFrameAsync(target);
            AssertNativeBackend(wrapper, $"Sequential wrapper read {i}/{points.Length}");
            CheckPixels(wrapped, reference, $"Sequential wrapper {(vfr ? "VFR" : "CFR")} read {i}/{points.Length} at {target:F6}s");
        }
        return new { label = vfr ? "VFR contiguous/short-step and reverse-adjacent reads" : "CFR 60 fps contiguous and reverse-adjacent reads", frameRate, vfr, points = points.Length, minimumRequestStepSeconds = deltas.Min(), maximumRequestStepSeconds = deltas.Max(), allNativeAndWrapperPixelsExact = true };
    }

    private static async Task<object> VerifyTaggedColorAndScale(string path, string range, string root)
    {
        const int outputWidth = 240;
        const int outputHeight = 136;
        var timeline = await ProbeTimeline(path);
        using var decoder = new NativeSeekFrameDecoder(path, outputWidth, outputHeight, 30, timeline.DurationSeconds);
        Check(decoder.InputWidth == 320 && decoder.InputHeight == 180, $"BT.709 {range} source dimensions unexpected: {decoder.InputWidth}x{decoder.InputHeight}.");
        var native = decoder.ReadFrame(.47, CancellationToken.None);
        var cli = await MediaTools.DecodeFrameAsync(path, .47, outputWidth, outputHeight, CancellationToken.None);
        await SavePixelDiagnosticsIfDifferent(root, $"bt709-{range}-scaled-240x136", .47, outputWidth, outputHeight, native, cli);
        CheckPixels(native, cli, $"BT.709 {range} range 320x180 to 240x136 at 0.47s");
        var probe = await MediaTools.RunAsync(MediaTools.Ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=color_space,color_primaries,color_transfer,color_range,width,height", "-of", "json", path]);
        using var doc = JsonDocument.Parse(probe);
        var stream = doc.RootElement.GetProperty("streams")[0];
        var expectedRange = range == "full" ? "pc" : "tv";
        Check(stream.GetProperty("color_space").GetString() == "bt709", $"BT.709 {range} source did not retain BT.709 colorspace metadata.");
        Check(stream.GetProperty("color_primaries").GetString() == "bt709", $"BT.709 {range} source did not retain BT.709 primaries metadata.");
        Check(stream.GetProperty("color_transfer").GetString() == "bt709", $"BT.709 {range} source did not retain BT.709 transfer metadata.");
        Check(stream.GetProperty("color_range").GetString() == expectedRange, $"BT.709 {range} source range metadata was {stream.GetProperty("color_range").GetString()}.");
        return new { label = $"BT.709 {range} range tagged source resized to 240x136", backend = decoder.BackendName, inputWidth = decoder.InputWidth, inputHeight = decoder.InputHeight, outputWidth, outputHeight, sourceColorSpace = stream.GetProperty("color_space").GetString(), sourceColorRange = stream.GetProperty("color_range").GetString(), pixelComparison = ComparePixels(native, cli) };
    }

    private static async Task<object> VerifyDisplayMatrixFallback(string path, string root)
    {
        var probe = await MediaTools.RunAsync(MediaTools.Ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height:stream_side_data=side_data_type,rotation", "-of", "json", path]);
        using var doc = JsonDocument.Parse(probe);
        var stream = doc.RootElement.GetProperty("streams")[0];
        Check(stream.GetProperty("width").GetInt32() == 320 && stream.GetProperty("height").GetInt32() == 180,
            "Rotated source did not retain expected 320x180 coded dimensions.");
        Check(stream.TryGetProperty("side_data_list", out var sideData) && sideData.EnumerateArray().Any(item => item.GetProperty("side_data_type").GetString() == "Display Matrix"),
            "FFmpeg did not write a display matrix for the rotated source.");
        var timeline = await ProbeTimeline(path);
        await using var decoder = new ScrubFrameDecoder(path, 180, 320, 30, timeline.DurationSeconds, preferNative: true);
        var nativeAttempt = await decoder.ReadFrameAsync(.47);
        Check(decoder.DecoderBackend == "ffmpeg/process",
            $"90-degree display matrix must use autorotating FFmpeg fallback; backend={decoder.DecoderBackend}, reason={decoder.NativeFailureReason ?? "none"}.");
        var cli = await MediaTools.DecodeFrameAsync(path, .47, 180, 320, CancellationToken.None);
        await SavePixelDiagnosticsIfDifferent(root, "displaymatrix-90-fallback-autorotate", .47, 180, 320, nativeAttempt, cli);
        CheckPixels(nativeAttempt, cli, "90-degree display matrix FFmpeg autorotate fallback");
        return new { label = "90-degree display matrix falls back to FFmpeg autorotate", backend = decoder.DecoderBackend, failureReason = decoder.NativeFailureReason, codedWidth = 320, codedHeight = 180, displayWidth = 180, displayHeight = 320, pixelComparison = ComparePixels(nativeAttempt, cli) };
    }

    private static async Task<object> VerifyNativeAndWrapper(string path, int width, int height, int frameRate,
        Timeline timeline, IReadOnlyList<double> requests, string label)
    {
        var direct = new NativeSeekFrameDecoder(path, width, height, frameRate, timeline.DurationSeconds);
        await using var wrapper = new ScrubFrameDecoder(path, width, height, frameRate, timeline.DurationSeconds, preferNative: true);
        try
        {
            Check(direct.BackendName.StartsWith("libav/", StringComparison.Ordinal), $"{label}: direct decoder backend was {direct.BackendName}.");
            var directChecks = new List<object>();
            var wrapperChecks = new List<object>();
            foreach (var target in requests)
            {
                var expectedPts = FirstFrameAtOrAfter(timeline, target);
                var pixels = direct.ReadFrame(target, CancellationToken.None);
                var timestampDelta = Math.Abs(direct.LastTimestampSeconds - expectedPts);
                var maxTimestampError = Math.Max(1e-7, timeline.TimeBaseSeconds * .51);
                Check(timestampDelta <= maxTimestampError,
                    $"{label} target {target:F6}s selected PTS {direct.LastTimestampSeconds:F9}s; expected {expectedPts:F9}s (delta {timestampDelta:F9}s).");
                var reference = await MediaTools.DecodeFrameAsync(path, target, width, height, CancellationToken.None);
                await SavePixelDiagnosticsIfDifferent(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path), target, width, height, pixels, reference);
                CheckPixels(pixels, reference, $"{label} target {target:F6}s PTS {direct.LastTimestampSeconds:F9}s");
                directChecks.Add(new { targetSeconds = target, actualTimestampSeconds = direct.LastTimestampSeconds, timestampErrorSeconds = timestampDelta, pixelComparison = ComparePixels(pixels, reference) });
                var wrappedPixels = await wrapper.ReadFrameAsync(target);
                AssertNativeBackend(wrapper, $"{label} wrapper target {target:F6}s");
                CheckPixels(wrappedPixels, reference, $"{label} wrapper target {target:F6}s");
                wrapperChecks.Add(new { targetSeconds = target, pixelComparison = ComparePixels(wrappedPixels, reference) });
            }
            if (label.StartsWith("CFR 30 fps", StringComparison.Ordinal))
            {
                var lastPts = timeline.RelativeFrameTimes[^1];
                var lastPixels = await wrapper.ReadFrameAsync(lastPts);
                AssertNativeBackend(wrapper, "last-frame read");
                var lastReference = await MediaTools.DecodeFrameAsync(path, lastPts, width, height, CancellationToken.None);
                CheckPixels(lastPixels, lastReference, "last actual frame");
                var beyondPixels = await wrapper.ReadFrameAsync(timeline.DurationSeconds + 1);
                AssertNativeBackend(wrapper, "read beyond duration");
                CheckPixels(beyondPixels, lastReference, "read beyond duration should clamp to actual tail frame");
                wrapperChecks.Add(new { targetSeconds = timeline.DurationSeconds, label = "tail frame", pixelComparison = ComparePixels(lastPixels, lastReference) });
                wrapperChecks.Add(new { targetSeconds = timeline.DurationSeconds + 1, label = "beyond duration", pixelComparison = ComparePixels(beyondPixels, lastReference) });
            }
            return new { label, path, frameRate, frameCount = timeline.RelativeFrameTimes.Length, timeline.StartSeconds, timeline.DurationSeconds, directChecks, wrapperChecks, wrapperNativeSessionCount = wrapper.NativeSessionCount };
        }
        finally { direct.Dispose(); }
    }

    private static async Task<object> VerifyCancellationAndDisposal(string path, int width, int height, int frameRate, Timeline timeline)
    {
        var decoder = new ScrubFrameDecoder(path, width, height, frameRate, timeline.DurationSeconds, preferNative: true);
        var initial = await decoder.ReadFrameAsync(.73);
        AssertNativeBackend(decoder, "initial cancellation case read");
        var expected = await MediaTools.DecodeFrameAsync(path, .73, width, height, CancellationToken.None);
        CheckPixels(initial, expected, "cancellation case initial read");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            _ = await decoder.ReadFrameAsync(4.37, cancellation.Token);
            throw new InvalidOperationException("Native cancellation case accepted an already-canceled read.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }

        AssertNativeBackend(decoder, "after canceled seek");
        var recovered = await decoder.ReadFrameAsync(1.91);
        AssertNativeBackend(decoder, "after recovery read");
        var recoveredReference = await MediaTools.DecodeFrameAsync(path, 1.91, width, height, CancellationToken.None);
        var recoveryComparison = ComparePixels(recovered, recoveredReference);
        CheckPixels(recovered, recoveredReference, "native recovery after cancellation");
        var cacheCopy = await decoder.ReadFrameAsync(1.91);
        Check(decoder.CacheHits > 0, "Repeated native read was not served from cache.");
        Check(!ReferenceEquals(recovered, cacheCopy), "Native cache returned the same caller-owned array instance.");
        CheckPixels(cacheCopy, recoveredReference, "native cache copy before caller mutation");
        Array.Fill(recovered, (byte)0);
        var afterMutation = await decoder.ReadFrameAsync(1.91);
        CheckPixels(afterMutation, recoveredReference, "native cache after caller buffer mutation");
        AssertNativeBackend(decoder, "after cache mutation check");

        var sessionCount = decoder.NativeSessionCount;
        var cacheBytesBeforeDispose = decoder.CacheBytes;
        await decoder.DisposeAsync();
        Check(decoder.CacheBytes == 0, "Dispose did not clear native frame cache bytes.");
        Check(decoder.NativeSessionCount == sessionCount && sessionCount == 1, "Native decoder opened more than one native session or lost its session count.");
        try
        {
            _ = await decoder.ReadFrameAsync(.5);
            throw new InvalidOperationException("A disposed native decoder accepted another read.");
        }
        catch (ObjectDisposedException) { }
        return new { label = "cancellation, recovery, cache ownership and disposal", frameRate, durationSeconds = timeline.DurationSeconds, nativeSessionCount = sessionCount, cacheBytesBeforeDispose, cacheBytesAfterDispose = decoder.CacheBytes, recoveryComparison };
    }

    private static async Task<object> CompareD3D11VAWithCli(string path, int width, int height, int frameRate, Timeline timeline, string root)
    {
        var wasEnabled = NativeSeekFrameDecoder.EnableD3D11VA;
        NativeSeekFrameDecoder.EnableD3D11VA = true;
        try
        {
            using var decoder = new NativeSeekFrameDecoder(path, width, height, frameRate, timeline.DurationSeconds);
            if (!decoder.BackendName.Equals("libav/d3d11va", StringComparison.Ordinal))
                return new { label = "D3D11VA parity diagnostic", available = false, backend = decoder.BackendName };
            var native = decoder.ReadFrame(0, CancellationToken.None);
            var cliPath = Path.Combine(root, "cfr-30fps-d3d11va-cli.bgra");
            var logPath = Path.Combine(root, "cfr-30fps-d3d11va-cli.log");
            using var process = MediaTools.Start(MediaTools.Ffmpeg,
                ["-hide_banner", "-loglevel", "verbose", "-hwaccel", "d3d11va", "-hwaccel_output_format", "d3d11", "-ss", "0", "-i", path,
                 "-frames:v", "1", "-vf", $"hwdownload,format=nv12,scale={width}:{height}", "-pix_fmt", "bgra", "-f", "rawvideo", "-y", cliPath]);
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var log = await error;
            await File.WriteAllTextAsync(logPath, log);
            Check(process.ExitCode == 0, $"FFmpeg CLI D3D11VA parity decode failed (exit {process.ExitCode}): {MediaTools.Tail(log)}");
            Check(log.Contains("requested hwaccel method d3d11va", StringComparison.OrdinalIgnoreCase), "FFmpeg CLI did not confirm D3D11VA decoder selection.");
            var cli = await File.ReadAllBytesAsync(cliPath);
            await SavePixelDiagnosticsIfDifferent(root, "cfr-30fps-d3d11va-vs-cli", 0, width, height, native, cli);
            return new { label = "D3D11VA parity diagnostic", available = true, backend = decoder.BackendName, nativeTimestampSeconds = decoder.LastTimestampSeconds, pixelComparison = ComparePixels(native, cli), cliLogPath = logPath };
        }
        finally { NativeSeekFrameDecoder.EnableD3D11VA = wasEnabled; }
    }

    private static void AssertNativeBackend(ScrubFrameDecoder decoder, string context)
    {
        Check(decoder.DecoderBackend.StartsWith("libav/", StringComparison.Ordinal),
            $"{context}: expected native libav backend, actual={decoder.DecoderBackend}, fallbackReason={decoder.NativeFailureReason ?? "none"}.");
        Check(decoder.NativeSessionCount == 1, $"{context}: expected exactly one native session, actual={decoder.NativeSessionCount}.");
    }

    private static async Task<Timeline> ProbeTimeline(string path)
    {
        var json = await MediaTools.RunAsync(MediaTools.Ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-show_streams", "-show_frames", "-show_format", "-show_entries", "stream=start_time,start_pts,time_base:format=start_time,duration:frame=best_effort_timestamp,best_effort_timestamp_time", "-of", "json", path]);
        using var document = JsonDocument.Parse(json);
        var stream = document.RootElement.GetProperty("streams")[0];
        var timeBase = stream.GetProperty("time_base").GetString()!.Split('/');
        var tick = double.Parse(timeBase[0], CultureInfo.InvariantCulture) / double.Parse(timeBase[1], CultureInfo.InvariantCulture);
        var startPts = stream.TryGetProperty("start_pts", out var startPtsValue)
            ? startPtsValue.GetInt64()
            : (long)Math.Round(ReadOptionalDouble(stream, "start_time", 0) / tick);
        var start = startPts * tick;
        var pts = document.RootElement.GetProperty("frames").EnumerateArray()
            .Select(frame => frame.TryGetProperty("best_effort_timestamp", out var framePts)
                ? (framePts.GetInt64() - startPts) * tick
                : double.Parse(frame.GetProperty("best_effort_timestamp_time").GetString()!, CultureInfo.InvariantCulture) - start)
            .ToArray();
        var format = document.RootElement.GetProperty("format");
        var duration = ReadOptionalDouble(format, "duration", pts[^1] + tick);
        Check(pts.Length > 1, $"Expected multiple video frames in {path}.");
        Check(pts.Zip(pts.Skip(1), (a, b) => b - a).All(delta => delta >= 0), $"ffprobe reported non-monotonic frame PTS in {path}.");
        return new Timeline(start, duration, tick, pts);
    }

    private static double ReadOptionalDouble(JsonElement element, string property, double fallback) =>
        element.TryGetProperty(property, out var value) && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static double FirstFrameAtOrAfter(Timeline timeline, double time)
    {
        var expected = timeline.RelativeFrameTimes.FirstOrDefault(pts => pts + timeline.TimeBaseSeconds * .51 >= time, double.NaN);
        if (double.IsNaN(expected)) throw new InvalidOperationException($"No frame PTS at or after {time:F6}s in source with duration {timeline.DurationSeconds:F6}s.");
        return expected;
    }

    private static PixelComparison ComparePixels(byte[] actual, byte[] expected)
    {
        if (actual.Length != expected.Length)
            return new(actual.Length != expected.Length ? Math.Max(actual.Length, expected.Length) : 0, 255, double.NaN, $"length actual={actual.Length}, expected={expected.Length}");
        var different = 0;
        var maximum = 0;
        long total = 0;
        var first = "none";
        for (var i = 0; i < actual.Length; i++)
        {
            var delta = Math.Abs(actual[i] - expected[i]);
            if (delta == 0) continue;
            different++;
            total += delta;
            maximum = Math.Max(maximum, delta);
            if (first == "none") first = $"byte={i}, actual={actual[i]}, expected={expected[i]}";
        }
        return new(different, maximum, actual.Length == 0 ? 0 : (double)total / actual.Length, first);
    }

    private static async Task SavePixelDiagnosticsIfDifferent(string directory, string label, double target,
        int width, int height, byte[] native, byte[] cli)
    {
        var diff = ComparePixels(native, cli);
        if (diff.DifferentBytes == 0) return;
        var suffix = target.ToString("F6", CultureInfo.InvariantCulture).Replace('.', 'p');
        var prefix = Path.Combine(directory, $"pixel-diagnostic-{label}-{suffix}");
        await File.WriteAllBytesAsync(prefix + "-native.bgra", native);
        await File.WriteAllBytesAsync(prefix + "-ffmpeg-cli.bgra", cli);
        await File.WriteAllTextAsync(prefix + ".json", JsonSerializer.Serialize(new { label, targetSeconds = target, width, height, pixelFormat = "BGRA", diff }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void CheckPixels(byte[] actual, byte[] expected, string context)
    {
        if (actual.AsSpan().SequenceEqual(expected)) return;
        var diff = ComparePixels(actual, expected);
        throw new InvalidOperationException($"{context}: native and FFmpeg CLI pixels differ. bytesDifferent={diff.DifferentBytes}/{Math.Max(actual.Length, expected.Length)}, maxChannelDelta={diff.MaximumChannelDifference}, meanAbsoluteDelta={diff.MeanAbsoluteDifference:F6}; first={diff.FirstDifference}.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
