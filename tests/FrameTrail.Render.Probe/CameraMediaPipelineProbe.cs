using System.IO;
using FrameTrail;
using FrameTrail.Media;

internal static class CameraMediaPipelineProbe
{
    internal static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine("artifacts", "validation", "camera-media-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(root);
        {
            var screenPath = Path.Combine(root, "screen.mp4");
            var cameraPath = Path.Combine(root, "camera.mp4");
            var projectPath = Path.Combine(root, "camera.frametrail");
        var originalExportPath = Path.Combine(root, "original-camera.mp4");
        var noShadowExportPath = Path.Combine(root, "original-camera-no-shadow.mp4");
            var exportPath = Path.Combine(root, "fullscreen-camera.mp4");
            await MediaTools.RunAsync(MediaTools.Ffmpeg,
            ["-v", "error", "-f", "lavfi", "-i", "color=c=blue:size=320x180:rate=30:duration=1", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-y", screenPath]);
            await MediaTools.RunAsync(MediaTools.Ffmpeg,
            ["-v", "error", "-f", "lavfi", "-i", "color=c=lime:size=1920x1080:rate=30:duration=1", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-y", cameraPath]);

            var project = new FrameTrailProject
            {
                MediaPath = "screen.mp4", Media = new() { Width = 320, Height = 180, Duration = 1, FrameRate = 30 },
                Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 1 }] },
                Settings = new() { AutoZoom = false },
                ZoomClips = [new() { Start = .1, End = .9, Scale = 2, Focus = new(.5, .5), EnterDuration = 0, ExitDuration = 0 }],
                Camera = new()
                {
                    MediaPath = "camera.mp4", Duration = 1, TimelineStart = 0, SourceStart = 0,
                    Position = new(.5, .5), Size = .45, Layout = CameraMotionLayoutKind.Original,
                    AssetAspectRatio = null, Crop = NormalizedRect.Full, ScaleDuringZoom = .62, ShadowStrength = .8, Muted = true
                },
            };
            FrameTrailProjectStore.Save(projectPath, project);

            var measuredAspect = await SceneAssetLoader.ProbeCameraAspectRatioAsync(project, projectPath);
            Near(measuredAspect ?? 0, 16d / 9, "camera media probe resolves actual 16:9 dimensions when the project has no ratio field");
            var evaluator = new PreparedFrameSceneEvaluator(project, measuredAspect);
            var originalScene = evaluator.Evaluate(.01);
            var originalCamera = originalScene.Camera ?? throw new InvalidOperationException("Camera media probe expected an active camera scene.");
            Near(originalCamera.Rect.Width * (16d / 9) / originalCamera.Rect.Height, 16d / 9, "original camera geometry uses measured pixel aspect");
            var zoomedCamera = evaluator.Evaluate(.5).Camera ?? throw new InvalidOperationException("Camera zoom probe expected an active camera scene.");
            Near(zoomedCamera.Rect.Width / originalCamera.Rect.Width, .62, "camera size follows the active 2x screen zoom using scaleDuringZoom");

            using (var assets = new SceneAssetLoader(projectPath, 2560, 1440))
            {
                var layers = await assets.LoadAsync(originalScene);
                var layer = layers.Single();
                Check(layer.Width == 1920 && layer.Height == 1080,
                    $"camera decoder retains source detail for fullscreen 2560x1440 output (got {layer.Width}x{layer.Height})");
                Near(layer.ShadowOpacity, .8, "camera shadow strength reaches the shared scene layer");
                Near(layer.ShadowRadius * 2560d / 1920, 14 * 1440d / 1080, "camera shadow radius scales from the upstream short-edge reference");
                Near(layer.ShadowOffsetY * 2560d / 1920, 8 * 1440d / 1080, "camera shadow offset scales from the upstream short-edge reference");
            }

            project.ZoomClips = [];
            var result = await new ExportEngine().ExportAsync(project, screenPath, originalExportPath,
                new ExportOptions(960, 540, 30, HardwarePreferred: false, AudioOnly: false, ProjectFilePath: projectPath));
            var originalMedia = await MediaTools.ProbeAsync(result.Path);
            Check(originalMedia.Width == 960 && originalMedia.Height == 540 && result.FrameCount == 30,
                "Original-camera pixel probe export has the expected 960x540 geometry");
            var originalPixels = await MediaTools.DecodeFrameAsync(result.Path, .5, 960, 540, CancellationToken.None);
            var greenBounds = FindGreenBounds(originalPixels, 960, 540);
            var greenAspect = greenBounds.Width / (double)greenBounds.Height;
            Check(greenBounds.Width > 300 && greenBounds.Height > 150 && Math.Abs(greenAspect - 16d / 9) < .12,
                $"decoded Original camera pixels preserve source 16:9 aspect (green bounds {greenBounds.Width}x{greenBounds.Height}, {greenAspect:0.###})");
            var cameraBottom = greenBounds.Bottom;
            var shadowPixelIndex = ((cameraBottom + 3) * 960 + (greenBounds.Left + greenBounds.Width / 2)) * 4;
            var shadowBlue = originalPixels[shadowPixelIndex];

            project.Camera!.ShadowStrength = 0;
            var noShadowResult = await new ExportEngine().ExportAsync(project, screenPath, noShadowExportPath,
                new ExportOptions(960, 540, 30, HardwarePreferred: false, AudioOnly: false, ProjectFilePath: projectPath));
            var noShadowPixels = await MediaTools.DecodeFrameAsync(noShadowResult.Path, .5, 960, 540, CancellationToken.None);
            var noShadowBlue = noShadowPixels[shadowPixelIndex];
            Check(noShadowBlue > shadowBlue + 12,
                $"camera shadow changes exported pixels outside the Original camera mask (shadow blue={shadowBlue}, no shadow={noShadowBlue})");

            project.Camera!.ShadowStrength = .8;
            project.CameraMotions = [new() { Start = 0, Duration = .55, EnterDuration = .25, ReturnDuration = 0,
                Easing = ZoomEasing.Linear, TargetPosition = new(.5, .5), TargetSize = .8,
                TargetLayout = CameraMotionLayoutKind.Fullscreen, TargetOpacity = 1 }];
            var fullscreenPathResult = await new ExportEngine().ExportAsync(project, screenPath, exportPath,
                new ExportOptions(2560, 1440, 30, HardwarePreferred: false, AudioOnly: false, ProjectFilePath: projectPath));
            var exported = await MediaTools.ProbeAsync(fullscreenPathResult.Path);
            var fullscreenPixels = await MediaTools.DecodeFrameAsync(fullscreenPathResult.Path, .4, 2560, 1440, CancellationToken.None);
            var fullscreenCenter = (720 * 2560 + 1280) * 4;
            var fullscreenCorner = (120 * 2560 + 120) * 4;
            Check(IsGreen(fullscreenPixels, fullscreenCenter) && IsGreen(fullscreenPixels, fullscreenCorner),
                "decoded fullscreen camera frame covers both center and corner pixels");
            Check(fullscreenPathResult.FrameCount == 30 && exported.Width == 2560 && exported.Height == 1440 && Math.Abs(exported.FrameRate - 30) < .1
                && exported.Duration is >= .9 and <= 1.1,
                $"ExportEngine renders the full camera source at 2560x1440/30 (frames={fullscreenPathResult.FrameCount}, media={exported.Width}x{exported.Height}@{exported.FrameRate:0.###}, {exported.Duration:0.###}s)");
            await File.WriteAllTextAsync(Path.Combine(root, "result.json"), System.Text.Json.JsonSerializer.Serialize(new { passed = true, measuredAspect,
                greenWidth = greenBounds.Width, greenHeight = greenBounds.Height, greenAspect, shadowBlue, noShadowBlue,
                originalExport = originalExportPath, fullscreenExport = exportPath, frames = fullscreenPathResult.FrameCount }));
            Console.WriteLine($"通过：真实相机素材宽高比/Original像素/缩放联动/阴影/全屏导出；{root}");
        }
    }

    private static void Near(double actual, double expected, string name)
    {
        if (!double.IsFinite(actual) || Math.Abs(actual - expected) > 1e-8)
            throw new InvalidOperationException($"Camera media pipeline probe failed ({name}): expected {expected}, got {actual}.");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("Camera media pipeline probe failed: " + name);
    }

    private static (int Left, int Top, int Width, int Height, int Bottom) FindGreenBounds(byte[] bgra, int width, int height)
    {
        var left = width; var top = height; var right = -1; var bottom = -1;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                if (!IsGreen(bgra, i)) continue;
                left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
            }
        if (right < left || bottom < top) throw new InvalidOperationException("Camera media pipeline probe found no green camera pixels in the decoded export.");
        return (left, top, right - left + 1, bottom - top + 1, bottom);
    }

    private static bool IsGreen(byte[] bgra, int i)
        => bgra[i + 1] > 100 && bgra[i + 1] > bgra[i] * 1.3 && bgra[i + 1] > bgra[i + 2] * 1.3;
}
