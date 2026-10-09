using System.IO;
using System.Text.Json;
using FrameTrail;
using FrameTrail.Media;

internal static class CameraClockMediaProbe
{
    private sealed record CheckResult(double OutputTime, double ScreenSourceTime, double CameraSourceTime,
        string ExpectedColor, string PreviewColor, string ExportColor, int[] PreviewCenterBgra, int[] ExportCenterBgra);

    internal static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine("artifacts", "validation", "camera-clock-media-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(root);
        var screenPath = Path.Combine(root, "screen.mp4");
        var cameraPath = Path.Combine(root, "camera.mp4");
        var projectPath = Path.Combine(root, "clock.frametrail");
        var exportPath = Path.Combine(root, "reordered-camera.mp4");
        await MediaTools.RunAsync(MediaTools.Ffmpeg,
            ["-v", "error", "-f", "lavfi", "-i", "color=c=blue:size=320x180:rate=30:duration=3", "-an", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-y", screenPath]);
        await MediaTools.RunAsync(MediaTools.Ffmpeg,
            ["-v", "error", "-f", "lavfi", "-i", "color=c=red:size=320x180:rate=30:duration=2",
                "-f", "lavfi", "-i", "color=c=lime:size=320x180:rate=30:duration=2",
                "-f", "lavfi", "-i", "color=c=yellow:size=320x180:rate=30:duration=2",
                "-filter_complex", "[0:v][1:v][2:v]concat=n=3:v=1:a=0,format=yuv420p[outv]", "-map", "[outv]", "-an", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-y", cameraPath]);

        var project = new FrameTrailProject
        {
            MediaPath = "screen.mp4",
            Media = new() { Width = 320, Height = 180, Duration = 3, FrameRate = 30 },
            Timeline = new()
            {
                Segments =
                [
                    new() { SourceStart = 1, SourceDuration = 1, PlaybackRate = 2 },
                    new() { SourceStart = 0, SourceDuration = 1, PlaybackRate = .5 },
                    new() { SourceStart = 2, SourceDuration = 1, PlaybackRate = 1 }
                ]
            },
            Settings = new() { AutoZoom = false, CanvasWidth = 16, CanvasHeight = 9, Padding = 0, Shadow = 0, CornerRadius = 0 },
            Camera = new()
            {
                MediaPath = "camera.mp4", SourceStart = 0, TimelineStart = 0, Duration = 3,
                Layout = CameraMotionLayoutKind.Fullscreen, AssetAspectRatio = 16d / 9,
                SyncSegments =
                [
                    new(0, 0, 1, 2),
                    new(2, 1, 1, 2),
                    new(4, 2, 1, 2)
                ],
                Muted = true, Opacity = 1, Crop = NormalizedRect.Full
            }
        };
        FrameTrailProjectStore.Save(projectPath, project);
        var source = FrameTrailProjectStore.ResolveMediaPath(projectPath, project);
        var outputTimes = new[] { .25, 1.5, 3.0 };
        var expectedColors = new[] { "green", "red", "yellow" };
        var evaluator = new PreparedFrameSceneEvaluator(project);
        var export = await new ExportEngine().ExportAsync(project, source, exportPath,
            new ExportOptions(320, 180, 30, HardwarePreferred: false, AudioOnly: false, ProjectFilePath: projectPath));
        var exportInfo = await MediaTools.ProbeAsync(export.Path);
        if (exportInfo.Width != 320 || exportInfo.Height != 180 || export.FrameCount != 105 || Math.Abs(exportInfo.Duration - 3.5) > .12)
            throw new InvalidOperationException($"Camera clock export has unexpected output geometry or duration: {exportInfo.Width}x{exportInfo.Height}, {export.FrameCount} frames, {exportInfo.Duration:F3}s");

        var checks = new List<CheckResult>();
        await using (var preview = await PreviewPlaybackSession.CreateAsync(project, source, projectPath, 320, 180, 30))
        {
            foreach (var (time, expected) in outputTimes.Zip(expectedColors))
            {
                var scene = evaluator.Evaluate(time);
                var camera = scene.Camera ?? throw new InvalidOperationException($"No camera scene at output {time}.");
                var expectedSource = time switch { .25 => 3, 1.5 => 1, _ => 5 };
                if (Math.Abs(camera.SourceTime - expectedSource) > 1e-8)
                    throw new InvalidOperationException($"Evaluator camera mapping at {time:F2}s expected {expectedSource:F2}s, got {camera.SourceTime:F6}s.");
                var previewPixels = new byte[320 * 180 * 4];
                var previewFrame = await preview.ReadFrameAsync(time, previewPixels);
                if (Math.Abs(previewFrame.SourceTime - scene.SourceTime) > 1e-8)
                    throw new InvalidOperationException("Preview session source clock differs from the prepared evaluator.");
                var exportPixels = await MediaTools.DecodeFrameAsync(export.Path, time, 320, 180, CancellationToken.None);
                var previewCenter = Pixel(previewPixels, 320, 160, 90);
                var exportCenter = Pixel(exportPixels, 320, 160, 90);
                var previewColor = Classify(previewCenter);
                var exportColor = Classify(exportCenter);
                if (previewColor != expected || exportColor != expected)
                    throw new InvalidOperationException($"Camera clock color mismatch at output {time:F2}s: expected {expected}, preview={previewColor}, export={exportColor}.");
                checks.Add(new(time, scene.SourceTime, camera.SourceTime, expected, previewColor, exportColor, previewCenter, exportCenter));
            }
        }

        var report = new
        {
            passed = true,
            timelineOutputDuration = project.Timeline.Duration,
            cameraSyncSegments = project.Camera?.SyncSegments,
            checks,
            previewBackend = "PreviewPlaybackSession",
            exportRenderer = export.Renderer,
            exportPath,
            exportFrames = export.FrameCount
        };
        var reportPath = Path.Combine(root, "result.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"通过：重排与变速时间线上的摄像头SourceRate在预览及导出中保持一致；{reportPath}");
    }

    private static int[] Pixel(byte[] bgra, int width, int x, int y)
    {
        var offset = checked((y * width + x) * 4);
        return [bgra[offset], bgra[offset + 1], bgra[offset + 2], bgra[offset + 3]];
    }

    private static string Classify(int[] bgra)
    {
        var b = bgra[0]; var g = bgra[1]; var r = bgra[2];
        if (g > 100 && g > b * 1.35 && g > r * 1.35) return "green";
        if (r > 150 && g > 130 && b < 100) return "yellow";
        if (r > 150 && g < 100 && b < 100) return "red";
        return $"unknown(B={b},G={g},R={r})";
    }
}
