using FrameTrail;

internal static class CameraMotionEvaluationProbe
{
    internal static void Run()
    {
        const double canvasAspect = 16d / 9;
        var project = new FrameTrailProject
        {
            MediaPath = "screen.mp4",
            Media = new() { Width = 1920, Height = 1080, Duration = 5 },
            Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 5 }] },
            Camera = new()
            {
                MediaPath = "camera.mp4", SourceStart = 0, Duration = 5, TimelineStart = 0,
                AssetAspectRatio = 16d / 9, Crop = new(.1, 0, .8, 1),
                Position = new(.8, .75), Size = .3, CornerRadius = .06,
                Layout = CameraMotionLayoutKind.RoundedRectangle
            },
            CameraMotions =
            [
                new()
                {
                    Start = 1, Duration = 2, EnterDuration = 1, ReturnDuration = .5,
                    Easing = ZoomEasing.Linear, TargetPosition = new(.25, .4), TargetSize = .5,
                    TargetLayout = CameraMotionLayoutKind.Original, TargetRoundness = .7, TargetOpacity = .4
                }
            ]
        };

        var evaluator = new PreparedFrameSceneEvaluator(project);
        var before = RequireCamera(evaluator.Evaluate(.5));
        Near(before.Rect.Width / before.Rect.Height * canvasAspect, 1, "base rounded-square layout keeps its square aspect");
        Near(before.Opacity, 1, "base camera starts visible");

        var roundnessFallbackProject = new FrameTrailProject
        {
            MediaPath = "screen.mp4", Media = new() { Width = 1920, Height = 1080, Duration = 2 },
            Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 2 }] },
            Camera = new() { MediaPath = "camera.mp4", Duration = 2, Size = .3, Layout = CameraMotionLayoutKind.RoundedRectangle, CornerRadius = .75 },
            CameraMotions = [new() { Start = .2, Duration = 1, EnterDuration = .2, TargetLayout = CameraMotionLayoutKind.Horizontal }]
        };
        var roundnessFallback = RequireCamera(new PreparedFrameSceneEvaluator(roundnessFallbackProject).Evaluate(.5));
        Near(roundnessFallback.CornerRadius, .105, "camera motion without an authored target roundness inherits the explicit track value");

        var entering = RequireCamera(evaluator.Evaluate(1.5));
        var basePose = CameraMotionEvaluator.Base(project.Camera, project.Camera.Layout, (16d / 9) * .8, canvasAspect)!.Value;
        var targetPose = CameraMotionEvaluator.Target(project.Camera, new(.25, .4), .5,
            CameraMotionLayoutKind.Original, .7, .4, (16d / 9) * .8, canvasAspect)!.Value;
        var expected = CameraMotionEvaluator.Interpolate(basePose, targetPose, .5)!.Value;
        var expectedRect = expected.ToNormalizedRect(canvasAspect);
        Near(entering.Rect.X, expectedRect.X, "prepared evaluation applies interpolated target position");
        Near(entering.Rect.Width, expectedRect.Width, "prepared evaluation applies continuous aspect geometry");
        Near(entering.Opacity, .7, "prepared evaluation applies opacity during entrance");
        Near(entering.CornerRadius, expected.ToNormalizedCornerRadius(canvasAspect), "prepared evaluation applies interpolated roundness");
        Check(entering.Crop == project.Camera.Crop, "camera motion preserves authored source crop");

        var target = RequireCamera(evaluator.Evaluate(2));
        Near(target.Rect.Width / target.Rect.Height * canvasAspect, (16d / 9) * .8, "original target layout uses cropped source pixel aspect");
        Near(target.Rect.X, .25 - target.Rect.Width / 2, "target layout remains centered on authored position");
        Near(target.Opacity, .4, "target opacity reaches authored value");

        project.CameraMotions[0].TargetLayout = CameraMotionLayoutKind.Fullscreen;
        project.CameraMotions[0].TargetOpacity = 1;
        var fullscreen = RequireCamera(new PreparedFrameSceneEvaluator(project).Evaluate(2));
        Near(fullscreen.Rect.X, 0, "fullscreen camera reaches left canvas edge");
        Near(fullscreen.Rect.Y, 0, "fullscreen camera reaches top canvas edge");
        Near(fullscreen.Rect.Width, 1, "fullscreen camera fills canvas width");
        Near(fullscreen.Rect.Height, 1, "fullscreen camera fills canvas height");
        Near(fullscreen.Opacity, 1, "fullscreen camera reaches target opacity");

        var legacyProject = new FrameTrailProject
        {
            MediaPath = "screen.mp4", Media = new() { Width = 1920, Height = 1080, Duration = 2 },
            Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 2 }] },
            Settings = new() { Canvas = new() { AspectWidth = 1080, AspectHeight = 1920 } },
            Camera = new() { MediaPath = "camera.mp4", Duration = 2, TimelineStart = 0, Size = .25, Shape = CameraShape.Circle }
        };
        var legacy = RequireCamera(new PreparedFrameSceneEvaluator(legacyProject).Evaluate(.5));
        Near(legacy.Rect.Height, .25, "legacy portrait size remains a fraction of canvas height");
        Near(legacy.Rect.Width, .25 / (.5625), "legacy portrait width preserves the old height-based dimensions");
        Near(legacy.Rect.Width / legacy.Rect.Height * (.5625), 1, "legacy circle remains pixel-circular on a portrait canvas");
        Check(legacy.CornerRadius > 0, "legacy circle remains rounded in the shared scene output");

        legacyProject.Camera.Layout = CameraMotionLayoutKind.Circle;
        var explicitLayout = RequireCamera(new PreparedFrameSceneEvaluator(legacyProject).Evaluate(.5));
        Near(explicitLayout.Rect.Width, .25, "explicit circle layout measures size against the portrait short edge");
        Near(explicitLayout.Rect.Height, .25 * .5625, "explicit circle layout applies short-edge size to portrait height");
        Near(explicitLayout.Rect.Width / explicitLayout.Rect.Height * .5625, 1, "explicit portrait circle remains physically circular");
    }

    private static FrameCameraScene RequireCamera(FrameScene scene) => scene.Camera ?? throw new InvalidOperationException("Expected an evaluated camera scene.");
    private static void Near(double actual, double expected, string name)
    {
        if (!double.IsFinite(actual) || Math.Abs(actual - expected) > 1e-8)
            throw new InvalidOperationException($"Camera evaluation probe failed ({name}): expected {expected}, got {actual}.");
    }
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("Camera evaluation probe failed: " + name);
    }
}
