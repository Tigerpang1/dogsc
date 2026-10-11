using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media.Imaging;
using FrameTrail;
using FrameTrail.Media;

namespace FrameTrail.Render.Probe;

/// <summary>Sequential playback throughput and allocation probe. Runs one workload at a time.</summary>
internal static class PlaybackThroughputProbe
{
    internal static async Task RunAsync(string? projectFile = null, TextWriter? output = null, int measuredFrames = 48)
    {
        if (string.IsNullOrWhiteSpace(projectFile))
            throw new ArgumentException("Usage: FrameTrail.Render.Probe --playback-throughput <project.frametrail>", nameof(projectFile));
        if (measuredFrames < 12) throw new ArgumentOutOfRangeException(nameof(measuredFrames));
        output ??= Console.Out;
        projectFile = Path.GetFullPath(projectFile);
        var projectSha256Before = await ComputeSha256Async(projectFile);
        var project = FrameTrailProjectStore.Load(projectFile);
        var source = FrameTrailProjectStore.ResolveMediaPath(projectFile, project);
        var mediaSha256Before = await ComputeSha256Async(source);
        var result = new List<CaseResult>();

        foreach (var (width, height) in new[] { (2560, 1440), (3840, 2160) })
        foreach (var fps in new[] { 30, 60 })
        {
            var pixels = GC.AllocateUninitializedArray<byte>(checked(width * height * 4));
            await using var session = await PreviewPlaybackSession.CreateAsync(project, source, projectFile,
                width, height, fps, preferNativeDecoder: true, preserveSourceResolution: width == 3840);
            // Prime shader/device/decoder state before measuring. Every measured call advances one frame.
            var warmup = Math.Min(8, measuredFrames / 2);
            for (var i = 0; i < warmup; i++)
                await RenderOne(session, pixels, width, height, i / (double)fps);

            var samples = new long[measuredFrames];
            var allocatedBefore = GC.GetTotalAllocatedBytes(true);
            var started = Stopwatch.GetTimestamp();
            for (var i = 0; i < measuredFrames; i++)
            {
                var frameStarted = Stopwatch.GetTimestamp();
                await RenderOne(session, pixels, width, height, (warmup + i) / (double)fps);
                samples[i] = Stopwatch.GetTimestamp() - frameStarted;
            }
            var elapsed = Stopwatch.GetElapsedTime(started);
            var allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
            Array.Sort(samples);
            var entry = new CaseResult("project", width, height, fps, measuredFrames,
                Math.Round(measuredFrames / elapsed.TotalSeconds, 2),
                Math.Round(elapsed.TotalMilliseconds / measuredFrames, 2),
                Math.Round(samples[(int)Math.Floor((samples.Length - 1) * .50)] * 1000d / Stopwatch.Frequency, 2),
                Math.Round(samples[(int)Math.Floor((samples.Length - 1) * .95)] * 1000d / Stopwatch.Frequency, 2),
                allocated, Math.Round(allocated / (double)measuredFrames, 0), session.DecoderBackend,
                session.Backend, session.NativeSessionCount, session.ProcessStartCount);
            result.Add(entry);
            await output.WriteLineAsync(JsonSerializer.Serialize(entry));
            await output.FlushAsync();
        }

        foreach (var (width, height) in new[] { (2560, 1440), (3840, 2160) })
        foreach (var fps in new[] { 30, 60 })
        {
            using var renderer = new GpuFrameRenderer(width, height, width, height);
            var sourcePixels = GC.AllocateUninitializedArray<byte>(checked(width * height * 4));
            var outputPixels = GC.AllocateUninitializedArray<byte>(sourcePixels.Length);
            FillSynthetic(sourcePixels);
            var scene = new FrameScene(0, 0, new(0, 0), new(1, 1), 1, null, 0, "#202124", 0, 0,
                width, height, 0, SourceWidth: width, SourceHeight: height);
            for (var i = 0; i < 4; i++)
                RenderSynthetic(renderer, sourcePixels, outputPixels, scene with { OutputTime = i / (double)fps }, width, height);

            var samples = new long[measuredFrames];
            var allocatedBefore = GC.GetTotalAllocatedBytes(true);
            var started = Stopwatch.GetTimestamp();
            for (var i = 0; i < measuredFrames; i++)
            {
                var frameStarted = Stopwatch.GetTimestamp();
                RenderSynthetic(renderer, sourcePixels, outputPixels, scene with { OutputTime = (i + 4) / (double)fps }, width, height);
                samples[i] = Stopwatch.GetTimestamp() - frameStarted;
            }
            var elapsed = Stopwatch.GetElapsedTime(started);
            var allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
            Array.Sort(samples);
            var entry = new CaseResult("synthetic-render", width, height, fps, measuredFrames,
                Math.Round(measuredFrames / elapsed.TotalSeconds, 2),
                Math.Round(elapsed.TotalMilliseconds / measuredFrames, 2),
                Math.Round(samples[(int)Math.Floor((samples.Length - 1) * .50)] * 1000d / Stopwatch.Frequency, 2),
                Math.Round(samples[(int)Math.Floor((samples.Length - 1) * .95)] * 1000d / Stopwatch.Frequency, 2),
                allocated, Math.Round(allocated / (double)measuredFrames, 0), "synthetic", renderer.Backend, 0, 0);
            result.Add(entry);
            await output.WriteLineAsync(JsonSerializer.Serialize(entry));
            await output.FlushAsync();
        }

        var projectSha256After = await ComputeSha256Async(projectFile);
        var mediaSha256After = await ComputeSha256Async(source);
        var inputHashesMatch = projectSha256Before == projectSha256After && mediaSha256Before == mediaSha256After;
        var reportRoot = Path.GetFullPath(Path.Combine("artifacts", "validation",
            "playback-throughput-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")));
        Directory.CreateDirectory(reportRoot);
        var reportPath = Path.Combine(reportRoot, "result.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
        {
            probe = "sequential-playback-throughput",
            projectFile,
            source,
            inputsReadOnly = new
            {
                projectSha256Before,
                projectSha256After,
                mediaSha256Before,
                mediaSha256After,
                inputHashesMatch
            },
            measuredFramesPerCase = measuredFrames,
            warmupFrames = Math.Min(8, measuredFrames / 2),
            cases = result
        }, new JsonSerializerOptions { WriteIndented = true }));
        await output.WriteLineAsync($"report: {reportPath}");
        await output.FlushAsync();
        if (!inputHashesMatch)
            throw new IOException("Playback throughput probe detected that the project or media changed during measurement; see the hash evidence in the report.");
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static async Task RenderOne(PreviewPlaybackSession session, byte[] pixels, int width, int height, double time)
    {
        await session.ReadFrameAsync(time, pixels);
        var bitmap = BitmapSource.Create(width, height, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
    }

    private static void RenderSynthetic(GpuFrameRenderer renderer, byte[] source, byte[] output,
        FrameScene scene, int width, int height)
    {
        renderer.Render(source, scene, output);
        var bitmap = BitmapSource.Create(width, height, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, output, width * 4);
        bitmap.Freeze();
    }

    private static void FillSynthetic(byte[] pixels)
    {
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(i / 4);
            pixels[i + 1] = (byte)(i / 16);
            pixels[i + 2] = (byte)(i / 64);
            pixels[i + 3] = 255;
        }
    }

    private sealed record CaseResult(string workload, int width, int height, int fps, int frames,
        double throughputFps, double meanFrameMs, double p50FrameMs, double p95FrameMs,
        long allocatedBytes, double allocatedBytesPerFrame, string decoder, string renderer,
        int nativeSessions, long ffmpegProcessStarts);
}
