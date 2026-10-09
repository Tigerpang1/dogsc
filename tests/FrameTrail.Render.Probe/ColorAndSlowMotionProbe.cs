using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FrameTrail;
using FrameTrail.Media;

// Color-patch round-trip validation is inspired by dogscwindows selftest.html
// at 3bdcf00d4eb12a107c093e98aac7a894aedd2843. Unlike its reporting-only checks,
// this probe fails on color, timing, frame-rate, pitch or metadata regressions.
internal static class ColorAndSlowMotionProbe
{
    internal static async Task Run(string? outputDirectory = null)
    {
        var root = Path.GetFullPath(outputDirectory ?? Path.Combine("artifacts", "validation", "color-slow-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(root);
        var results = new List<object>();
        try
        {
            await ColorRoundTrip(root, results);
            await SlowMotion(root, results);
            await File.WriteAllTextAsync(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { passed = true, createdUtc = DateTimeOffset.UtcNow, checks = results }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("通过：带阈值颜色往返、BT.709矩阵/sRGB传递标记、慢放音调与预览/导出时间映射；" + root);
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { passed = false, error = ex.ToString(), checks = results }, new JsonSerializerOptions { WriteIndented = true }));
            throw;
        }
    }

    private static async Task ColorRoundTrip(string root, List<object> results)
    {
        const int width = 560, height = 280;
        (byte R, byte G, byte B)[] colors = [(255, 0, 0), (0, 255, 0), (0, 0, 255), (128, 128, 128), (255, 255, 255), (224, 160, 112), (0, 0, 0)];
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var color = colors[x / 80]; var i = (y * width + x) * 4;
            pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = 255;
        }
        var png = Path.Combine(root, "color-patches.png");
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(png)) encoder.Save(file);
        var source = Path.Combine(root, "color-source.mp4");
        await MediaTools.RunAsync(MediaTools.Ffmpeg,
            ["-v", "error", "-loop", "1", "-framerate", "30", "-i", png, "-t", "1.2", "-vf", "scale=in_range=pc:out_range=tv:out_color_matrix=bt709,format=yuv420p",
            "-color_range", "tv", "-colorspace", "bt709", "-color_trc", "iec61966-2-1", "-color_primaries", "bt709", "-c:v", "libx264", "-crf", "10", "-an", "-y", source]);
        var project = new FrameTrailProject
        {
            MediaPath = Path.GetFileName(source), Media = new() { Width = width, Height = height, Duration = 1.2 },
            Timeline = new() { Segments = [new() { SourceDuration = 1.2 }] },
            Settings = new() { CanvasWidth = width, CanvasHeight = height, AutoZoom = false, CursorVisible = false, Padding = 0, CornerRadius = 0, Shadow = 0 }
        };
        var projectFile = Path.Combine(root, "color.frametrail");
        FrameTrailProjectStore.Save(projectFile, project);
        var preview = new byte[pixels.Length];
        await using (var session = await PreviewPlaybackSession.CreateAsync(project, source, projectFile, width, height, 30))
            await session.ReadFrameAsync(.4, preview);
        var previewError = PatchError(preview, colors, width, height);
        Check(previewError <= 4, $"preview patch channel deviation {previewError} exceeds 4/255");
        foreach (var hardware in new[] { false, true })
        {
            var output = Path.Combine(root, hardware ? "color-hardware.mp4" : "color-software.mp4");
            var result = await new ExportEngine().ExportAsync(project, source, output, new(width, height, 30, hardware, false, projectFile));
            var decoded = await MediaTools.DecodeFrameAsync(output, .4, width, height, CancellationToken.None);
            var error = PatchError(decoded, colors, width, height);
            Check(error <= 6, $"{result.Encoder} patch channel deviation {error} exceeds 6/255");
            using var tags = JsonDocument.Parse(await MediaTools.RunAsync(MediaTools.Ffprobe,
                ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=color_range,color_space,color_transfer,color_primaries", "-of", "json", output]));
            var stream = tags.RootElement.GetProperty("streams")[0];
            foreach (var key in new[] { "color_space", "color_primaries" })
                Check(stream.TryGetProperty(key, out var tag) && tag.GetString() == "bt709", $"{result.Encoder} missing BT.709 {key}");
            Check(stream.GetProperty("color_transfer").GetString() == "iec61966-2-1", "screen RGB transfer must be tagged sRGB");
            Check(stream.GetProperty("color_range").GetString() == "tv", "video range must match limited-range YUV conversion");
            results.Add(new { check = "color-round-trip", hardwareRequested = hardware, encoder = result.Encoder, previewMaxChannelError = previewError, exportMaxChannelError = error, previewLimit = 4, exportLimit = 6, primaries = "bt709", transfer = "iec61966-2-1", output });
        }
    }

    private static int PatchError(byte[] frame, (byte R, byte G, byte B)[] colors, int width, int height)
    {
        var maximum = 0;
        for (var patch = 0; patch < colors.Length; patch++)
        for (var dy = -2; dy <= 2; dy++)
        for (var dx = -2; dx <= 2; dx++)
        {
            var i = ((height / 2 + dy) * width + patch * 80 + 40 + dx) * 4;
            maximum = Math.Max(maximum, Math.Max(Math.Abs(frame[i] - colors[patch].B), Math.Max(Math.Abs(frame[i + 1] - colors[patch].G), Math.Abs(frame[i + 2] - colors[patch].R))));
        }
        return maximum;
    }

    private static async Task SlowMotion(string root, List<object> results)
    {
        var source = Path.Combine(root, "slow-source.mp4");
        await MediaTools.RunAsync(MediaTools.Ffmpeg,
            ["-v", "error", "-f", "lavfi", "-i", "color=c=red:size=160x90:rate=30:duration=1", "-f", "lavfi", "-i", "color=c=green:size=160x90:rate=30:duration=1",
             "-f", "lavfi", "-i", "color=c=blue:size=160x90:rate=30:duration=1", "-f", "lavfi", "-i", "sine=frequency=440:duration=1:sample_rate=48000",
             "-f", "lavfi", "-i", "sine=frequency=880:duration=1:sample_rate=48000", "-f", "lavfi", "-i", "sine=frequency=1320:duration=1:sample_rate=48000",
             "-filter_complex", "[0:v][1:v][2:v]concat=n=3:v=1:a=0[v];[3:a][4:a][5:a]concat=n=3:v=0:a=1[a]", "-map", "[v]", "-map", "[a]", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", "-y", source]);
        var project = new FrameTrailProject
        {
            MediaPath = Path.GetFileName(source), Media = new() { Width = 160, Height = 90, Duration = 3 },
            Timeline = new() { Segments = [new() { SourceStart = 2, SourceDuration = 1, PlaybackRate = .25 }, new() { SourceStart = 0, SourceDuration = 1, PlaybackRate = .5 }, new() { SourceStart = 1, SourceDuration = .5, PlaybackRate = 2 }] },
            Settings = new() { CanvasWidth = 160, CanvasHeight = 90, AutoZoom = false, CursorVisible = false, Padding = 0, Shadow = 0, CornerRadius = 0 }
        };
        var file = Path.Combine(root, "slow.frametrail");
        FrameTrailProjectStore.Save(file, project); project = FrameTrailProjectStore.Load(file);
        var output = Path.Combine(root, "slow-reordered.mp4");
        var result = await new ExportEngine().ExportAsync(project, source, output, new(160, 90, 60, false, false, file));
        var info = await MediaTools.ProbeAsync(output);
        Check(result.FrameCount == 375 && Math.Abs(info.Duration - 6.25) < .08 && info.HasAudio, "0.25x/0.5x/2x output must contain 375 frames and 6.25s with audio");
        await using (var session = await PreviewPlaybackSession.CreateAsync(project, source, file, 160, 90, 60))
        {
            var preview = new byte[160 * 90 * 4];
            foreach (var (time, sourceTime, channel) in new[] { (1d, 2.25, 0), (5d, .5, 2), (6.1, 1.2, 1) })
            {
                var frame = await session.ReadFrameAsync(time, preview);
                var rendered = await MediaTools.DecodeFrameAsync(output, time, 160, 90, CancellationToken.None);
                var i = (45 * 160 + 80) * 4;
                Check(Math.Abs(frame.SourceTime - sourceTime) < 1e-6 && preview[i + channel] > 90 && rendered[i + channel] > 90,
                    $"slow preview/export mapping must match source color at output {time}");
            }
        }
        await CheckAudioPitch(root, output, "slow-main");
        // Exercise AudioTrackFilter separately: no main audio, the source's AAC is an independent track.
        project.Settings.MainAudioMuted = true;
        project.AudioTracks = [new() { MediaPath = Path.GetFileName(source), SourceStart = 0, Duration = 3, OffsetSeconds = 0 }];
        FrameTrailProjectStore.Save(file, project);
        var m4a = Path.Combine(root, "slow-independent.m4a");
        await new ExportEngine().ExportAsync(project, source, m4a, new(160, 90, 60, false, true, file));
        var audioInfo = await MediaTools.ProbeAsync(m4a);
        Check(audioInfo.HasAudio && Math.Abs(audioInfo.Duration - 6.25) < .08, "slow independent audio must follow the final timeline length");
        await CheckAudioPitch(root, m4a, "slow-independent");
        results.Add(new { check = "slow-motion", rates = new[] { .25, .5, 2 }, duration = info.Duration, expected = 6.25, frames = result.FrameCount, independentAudioDuration = audioInfo.Duration, pitchPreserved = true, previewSourceMapping = true });
    }

    private static async Task CheckAudioPitch(string root, string media, string name)
    {
        var raw = Path.Combine(root, name + ".f32");
        await MediaTools.RunAsync(MediaTools.Ffmpeg, ["-v", "error", "-i", media, "-map", "0:a:0", "-ac", "1", "-ar", "48000", "-f", "f32le", "-y", raw]);
        var bytes = await File.ReadAllBytesAsync(raw); var samples = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
        foreach (var (start, end, frequency, wrongFrequency) in new[] { (1d, 2d, 1320d, 330d), (4.3, 5.3, 440d, 220d), (6.05, 6.2, 880d, 1760d) })
            Check(TonePower(samples, start, end, frequency) > Math.Max(1e-10, TonePower(samples, start, end, wrongFrequency)) * 5,
                $"{name}: source tone {frequency}Hz must retain pitch after retiming");
    }

    private static double TonePower(float[] samples, double start, double end, double frequency)
    {
        var a = (int)(start * 48000); var b = Math.Min(samples.Length, (int)(end * 48000)); var real = 0d; var imaginary = 0d;
        for (var i = a; i < b; i++) { var phase = 2 * Math.PI * frequency * i / 48000; real += samples[i] * Math.Cos(phase); imaginary += samples[i] * Math.Sin(phase); }
        return (real * real + imaginary * imaginary) / Math.Max(1, (b - a) * (double)(b - a));
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Color/slow-motion probe failed: " + message); }
}
