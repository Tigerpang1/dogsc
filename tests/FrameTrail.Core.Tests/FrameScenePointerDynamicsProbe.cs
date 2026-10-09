using FrameTrail;

internal static class FrameScenePointerDynamicsProbe
{
    internal static void Run()
    {
        var clicks = new[]
        {
            new PointerEvent(0, new(.1, .8)),
            new PointerEvent(.1, new(.3, .8)),
            new PointerEvent(.15, new(.4, .8), PointerEventKind.RightClick),
            new PointerEvent(.2, new(.6, .8))
        };
        var settings = new FrameTrailSettings
        {
            AutoZoom = false,
            PointerSmoothing = PointerSmoothing.Rapid,
            Cursor = new CursorSettings { MotionTiltEnabled = true, MotionTiltStrength = 3, ClickDuration = .2 }
        };
        var project = new FrameTrailProject
        {
            MediaPath = "screen.mp4",
            Media = new() { Width = 1920, Height = 1080, Duration = 2 },
            Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 2 }] },
            PointerEvents = clicks.ToList(),
            Settings = settings
        };
        var evaluator = new PreparedFrameSceneEvaluator(project);
        var active = evaluator.Evaluate(.2);
        Check(active.ActiveClickKind == PointerEventKind.RightClick && Near(active.ClickProgress, .25)
            && Near(active.ClickStrength, Math.Sin(Math.PI * .25)),
            "FrameScene exposes the active right-click kind and duration-normalized pulse strength");
        Check(active.CursorRotationRadians < 0,
            "FrameScene applies enabled motion tilt to the rendered cursor pose");

        var disabledTiltProject = Clone(project);
        disabledTiltProject.Settings.Cursor!.MotionTiltEnabled = false;
        var noTilt = new PreparedFrameSceneEvaluator(disabledTiltProject).Evaluate(.2);
        Check(noTilt.CursorRotationRadians == 0 && noTilt.ClickProgress == active.ClickProgress,
            "disabling tilt removes cursor rotation without changing click timing");

        var calibrated = Clone(project);
        calibrated.MouseClock = new MouseClockCalibration(.1, 1.5);
        var calibratedEvaluator = new PreparedFrameSceneEvaluator(calibrated);
        // source .4 maps to pointer .2: 50ms after the .15 click, or 25% of 200ms.
        var atCalibratedClick = calibratedEvaluator.Evaluate(.4);
        Check(atCalibratedClick.ActiveClickKind == PointerEventKind.RightClick && Near(atCalibratedClick.ClickProgress, .25)
            && atCalibratedClick.Cursor is not null,
            "mouse offset and rate map the FrameScene click and pointer sample on the independent pointer clock");

        calibrated.Settings.Cursor!.ClickDuration = .1;
        var shortDuration = new PreparedFrameSceneEvaluator(calibrated).Evaluate(.4);
        Check(Near(shortDuration.ClickProgress, .5) && shortDuration.ClickStrength > atCalibratedClick.ClickStrength,
            "changing click duration changes FrameScene animation progress and strength");
        var expired = new PreparedFrameSceneEvaluator(project).Evaluate(.36);
        Check(expired.ClickProgress is null && expired.ActiveClickKind is null && expired.ClickStrength == 0,
            "FrameScene clears click kind and strength after duration instead of reusing the pointer track pulse");

        Console.WriteLine("通过：PreparedFrameSceneEvaluator pointer dynamics integration");
    }

    private static FrameTrailProject Clone(FrameTrailProject project) =>
        System.Text.Json.JsonSerializer.Deserialize<FrameTrailProject>(System.Text.Json.JsonSerializer.Serialize(project))!;

    private static bool Near(double? value, double expected) => value is { } actual && Math.Abs(actual - expected) < 1e-10;
    private static bool Near(double value, double expected) => Math.Abs(value - expected) < 1e-10;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FrameScene pointer dynamics probe failed: " + message);
    }
}
