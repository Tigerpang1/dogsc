using System.IO;
using System.Text.Json;
using FrameTrail;
using FrameTrail.Media;

namespace FrameTrail.Render.Probe;

/// <summary>Native playback mapping, compositor parity and session release.</summary>
internal static class NativePlaybackProbe
{
    internal static async Task RunAsync(string? outputDirectory = null)
    {
        var root = Path.GetFullPath(outputDirectory ?? Path.Combine("artifacts", "validation", "native-playback-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "native-playback-source.mkv");
        var projectPath = Path.Combine(root, "native-playback.frametrail");
        var report = Path.Combine(root, "result.json");
        try
        {
            await MediaTools.RunAsync(MediaTools.Ffmpeg,
                ["-v", "error", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=30:duration=3", "-an", "-c:v", "ffv1", "-y", source]);

            var project = new FrameTrailProject
            {
                MediaPath = Path.GetFileName(source),
                Media = new() { Width = 160, Height = 90, Duration = 3 },
                Timeline = new()
                {
                    Segments =
                    [
                        new() { SourceStart = 2, SourceDuration = .5, PlaybackRate = .5 },
                        new() { SourceStart = 0, SourceDuration = 1, PlaybackRate = 2 },
                        new() { SourceStart = 1, SourceDuration = .5, PlaybackRate = 1 }
                    ]
                },
                Settings = new() { CanvasWidth = 160, CanvasHeight = 90, AutoZoom = false, CursorVisible = false, Padding = 0, CornerRadius = 0, Shadow = 0 }
            };
            FrameTrailProjectStore.Save(projectPath, project);
            project = FrameTrailProjectStore.Load(projectPath);
            var streamingBufferParity = await VerifyStreamingBufferParity(source);

            var nativeFrame = new byte[160 * 90 * 4];
            var referenceFrame = new byte[nativeFrame.Length];
            var samples = new[] { .2, .3, 1.2, 1.1, 1.7 };
            var sourceTimes = new[] { 2.1, 2.15, .4, .2, 1.2 };
            var maxPixelDelta = 0;
            int nativeSessions;
            long processStarts;
            string backend;
            await using (var native = await PreviewPlaybackSession.CreateAsync(project, source, projectPath, 160, 90, 30, preferNativeDecoder: true))
            await using (var reference = await PreviewPlaybackSession.CreateAsync(project, source, projectPath, 160, 90, 30))
            {
                for (var i = 0; i < samples.Length; i++)
                {
                    var info = await native.ReadFrameAsync(samples[i], nativeFrame);
                    var referenceInfo = await reference.ReadFrameAsync(samples[i], referenceFrame);
                    Check(Math.Abs(info.SourceTime - sourceTimes[i]) < 1e-6, $"output {samples[i]} mapped to source {info.SourceTime}, expected {sourceTimes[i]}");
                    Check(Math.Abs(info.SourceTime - referenceInfo.SourceTime) < 1e-9, "native and reference scene mapping differs");
                    maxPixelDelta = Math.Max(maxPixelDelta, PixelDelta(nativeFrame, referenceFrame));

                    // Re-reading a frame models a pause before that frame is presented.
                    if (i == 1)
                    {
                        await native.ReadFrameAsync(samples[i], nativeFrame);
                        Check(PixelDelta(nativeFrame, referenceFrame) == 0, "repeated paused frame changed pixels");
                    }
                }
                nativeSessions = native.NativeSessionCount;
                processStarts = native.ProcessStartCount;
                backend = native.DecoderBackend;
            }
            Check(maxPixelDelta == 0, $"native/reference pixel mismatch: max channel delta {maxPixelDelta}");
            Check(nativeSessions == 1, $"native decoder session count was {nativeSessions}, expected one");
            Check(processStarts == 0, $"native path started {processStarts} FFmpeg process(es)");
            Check(backend.StartsWith("libav/", StringComparison.Ordinal), $"native decoder did not remain active: {backend}");
            // Both the libav context and fallback process must release the source.
            using (File.Open(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }

            var checks = new { passed = true, backend, nativeSessions, processStarts, maxPixelDelta, samples, sourceTimes, streamingBufferParity, sourceReleasedAfterDispose = true };
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("通过：native 播放重排/变速时间映射、暂停帧重读、向前与反向 seek、同渲染器逐像素一致；" + root);
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { passed = false, error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            throw;
        }
    }

    private static async Task<object> VerifyStreamingBufferParity(string source)
    {
        const int width = 162, height = 92;
        var times = new[] { .2, .3, 1.2, .7, .4 };
        await using var cached = new ScrubFrameDecoder(source, width, height, 30, 3, preferNative: true);
        await using var streaming = new ScrubFrameDecoder(source, width, height, 30, 3, preferNative: true);
        var reusable = new byte[width * height * 4];
        var matches = 0;
        foreach (var time in times)
        {
            var expected = await cached.ReadFrameAsync(time);
            await streaming.ReadFrameIntoAsync(time, reusable);
            Check(expected.AsSpan().SequenceEqual(reusable), $"caller-buffer decoder differs at {time:F3}s, stride {width * 4}");
            matches++;
        }

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            await streaming.ReadFrameIntoAsync(.9, reusable, canceled.Token);
            throw new InvalidOperationException("caller-buffer decoder accepted a pre-canceled request");
        }
        catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
        var expectedAfterCancel = await cached.ReadFrameAsync(.9);
        await streaming.ReadFrameIntoAsync(.9, reusable);
        Check(expectedAfterCancel.AsSpan().SequenceEqual(reusable), "caller-buffer decoder failed to recover after cancellation");
        matches++;
        Check(cached.DecoderBackend.StartsWith("libav/", StringComparison.Ordinal)
            && streaming.DecoderBackend.StartsWith("libav/", StringComparison.Ordinal),
            $"caller-buffer parity did not exercise libav: cached={cached.DecoderBackend}; streaming={streaming.DecoderBackend}");
        return new { width, height, strideBytes = width * 4, exactMatches = matches, requests = times.Length + 1, cancellationRecovery = true };
    }

    private static int PixelDelta(byte[] left, byte[] right)
    {
        var delta = 0;
        for (var i = 0; i < left.Length; i++) delta = Math.Max(delta, Math.Abs(left[i] - right[i]));
        return delta;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Native playback probe failed: " + message);
    }
}
