using FrameTrail;

internal static class AutoFollowProbe
{
    internal static void Run()
    {
        GoldenSpringAndSafetyArea();
        FollowResumesAfterSilentIntervals();
        CropClockAndReorder();
        AdjacentSegmentsStayContinuous();
        LongSilentRangeIsBoundedAndRepeatable();
        ExtremeSpringConvergesWithStableSubsteps();
        ExcessiveContinuousActivityIsRejected();
        Console.WriteLine("通过：prepared automatic camera follow");
    }

    private static void FollowResumesAfterSilentIntervals()
    {
        var opening = BaseProject(2);
        opening.PointerEvents = [new(0, new(.2, .5), PointerEventKind.LeftClick), new(.5, new(.8, .5))];
        opening.ZoomClips = [new() { Start = 0, End = 1.5, Scale = 2, Focus = new(.2, .5), EnterDuration = .5, ExitDuration = 0, Easing = ZoomEasing.Linear, Automatic = true }];
        var openingEvaluator = new PreparedFrameSceneEvaluator(opening);
        var beforeFollow = openingEvaluator.Evaluate(.3);
        var entering = openingEvaluator.Evaluate(.5);
        var moving = openingEvaluator.Evaluate(.75);
        Near(beforeFollow.CropOrigin.X + beforeFollow.CropSize.X / 2, .3125, 1e-10,
            "zoom opening keeps its target anchor; the 1.6x crop is clamped at the left canvas edge");
        Check(moving.CropOrigin.X + .25 > entering.CropOrigin.X + .25 + .02,
            "automatic follow continues integrating pointer motion after the opening interval");

        var resumed = BaseProject(5);
        resumed.PointerEvents =
        [
            new(0, new(.4, .5), PointerEventKind.LeftClick), new(.1, new(.4, .5)),
            new(3, new(.4, .5)), new(3.1, new(.8, .5))
        ];
        resumed.ZoomClips = [new() { Start = 0, End = 4.5, Scale = 2, Focus = new(.4, .5), EnterDuration = .1, ExitDuration = 0, Easing = ZoomEasing.Linear, Automatic = true }];
        var resumedEvaluator = new PreparedFrameSceneEvaluator(resumed);
        var settled = resumedEvaluator.Evaluate(2);
        var movingAgain = resumedEvaluator.Evaluate(3.3);
        Near(settled.CropOrigin.X + settled.CropSize.X / 2, .4, 1e-10,
            "long constant pointer interval settles at its current focus");
        Check(movingAgain.CropOrigin.X + movingAgain.CropSize.X / 2 > .42,
            "follow resumes integrating when pointer movement occurs after a long silent interval");
    }

    private static void GoldenSpringAndSafetyArea()
    {
        var project = BaseProject(2);
        project.PointerEvents =
        [
            new(0, new(.2, .5)),
            new(.2, new(.2, .5), PointerEventKind.LeftClick),
            new(.5, new(.8, .5))
        ];
        project.ZoomClips = [new() { Start = 0, End = 1, Scale = 2, Focus = new(.2, .5), EnterDuration = .2, ExitDuration = 0, Automatic = true }];
        var evaluator = new PreparedFrameSceneEvaluator(project);
        var scene = evaluator.Evaluate(.5);
        var focusX = scene.CropOrigin.X + .25;
        var focusY = scene.CropOrigin.Y + .25;
        Near(focusX, .4065377783046487, 1e-9,
            "120 Hz spring follow matches the independent DogSC pointer.js golden at 0.5s");
        Near(focusY, .5, 1e-10, "follow preserves an in-range axis when the pointer does not move on it");
        Check(focusX > .2 && focusX < .8,
            "camera follows beyond the 56%-of-visible-half-span rest area without snapping to the pointer");

        var staticProject = BaseProject(2);
        staticProject.PointerEvents = [new(0, new(.4, .6), PointerEventKind.LeftClick)];
        staticProject.ZoomClips = [new() { Start = 0, End = 1, Scale = 2, Focus = new(.4, .6), EnterDuration = 0, ExitDuration = 0, Automatic = true }];
        var still = new PreparedFrameSceneEvaluator(staticProject).Evaluate(.8);
        Near(still.CropOrigin.X + .25, .4, 1e-10, "stationary cursor remains fixed at its automatic focus");
        Near(still.CropOrigin.Y + .25, .6, 1e-10, "stationary cursor remains fixed vertically");
    }

    private static void CropClockAndReorder()
    {
        var cropProject = BaseProject(2);
        cropProject.Settings.Canvas = new CanvasSettings { Crop = new(.25, 0, .5, 1) };
        cropProject.PointerEvents = [new(0, new(.4, .5), PointerEventKind.LeftClick)];
        cropProject.ZoomClips = [new() { Start = 0, End = 1, Scale = 2, Focus = new(.4, .5), EnterDuration = .2, ExitDuration = 0, Easing = ZoomEasing.Linear, Automatic = true }];
        var cropped = new PreparedFrameSceneEvaluator(cropProject).Evaluate(.25);
        // Independent coordinate conversion: (full-source x .4 - crop origin .25) / crop width .5 = .3.
        // At 2x, the visible width is .5 and the unclamped origin is .3 - .25 = .05,
        // so the viewport center must be .05 + .25 = .3 (not full-source .4).
        var expectedCropLocalFocus = (.4 - .25) / .5;
        Near(cropped.CropOrigin.X + cropped.CropSize.X / 2, expectedCropLocalFocus, 1e-10,
            "automatic follow uses the crop-local anchor after the zoom entry has completed");

        var calibrated = BaseProject(2);
        calibrated.MouseClock = new(.2, 2);
        calibrated.PointerEvents = [new(.2, new(.4, .5), PointerEventKind.LeftClick), new(.4, new(.8, .5))];
        calibrated.ZoomClips = [new() { Start = .6, End = 1.4, Scale = 2, Focus = new(.4, .5), EnterDuration = .2, ExitDuration = 0, Automatic = true }];
        var calibratedEvaluator = new PreparedFrameSceneEvaluator(calibrated);
        var calibratedScene = calibratedEvaluator.Evaluate(.9);
        Check(calibratedScene.SourceTime == .9 && calibratedScene.Cursor is not null,
            "automatic follow and cursor use the same independently calibrated mouse clock");
        Check(calibratedScene.CropOrigin.X > .15,
            "clock calibration advances the follow target along calibrated source time");

        var reordered = BaseProject(4);
        reordered.Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 2 }, new() { SourceStart = 10, SourceDuration = 2 }] };
        reordered.PointerEvents = [new(10, new(.2, .5), PointerEventKind.LeftClick), new(11, new(.8, .5))];
        reordered.ZoomClips = [new() { Start = 10, End = 12, Scale = 2, Focus = new(.2, .5), EnterDuration = .2, ExitDuration = 0, Automatic = true }];
        var before = new PreparedFrameSceneEvaluator(reordered).Evaluate(3);
        reordered.Timeline.Reorder(reordered.Timeline.Segments[1].Id, 0);
        var after = new PreparedFrameSceneEvaluator(reordered).Evaluate(1);
        Check(before.SourceTime == after.SourceTime && before.CropOrigin == after.CropOrigin,
            "source-time follow is stateless across timeline reorder and maps to the same source frame");

        var randomAccessEvaluator = new PreparedFrameSceneEvaluator(cropProject);
        var expectedMiddle = randomAccessEvaluator.Evaluate(.55).CropOrigin;
        _ = randomAccessEvaluator.Evaluate(.95);
        _ = randomAccessEvaluator.Evaluate(.15);
        Check(randomAccessEvaluator.Evaluate(.55).CropOrigin == expectedMiddle,
            "random-access preview/export evaluation does not accumulate spring state from prior frames");

        var betweenEvents = BaseProject(2);
        betweenEvents.PointerEvents = [new(0, new(.2, .5)), new(1.2, new(.8, .5))];
        betweenEvents.ZoomClips = [new() { Start = .5, End = 1, Scale = 2, Focus = new(.1, .5), EnterDuration = .1, ExitDuration = 0, Easing = ZoomEasing.Linear, Automatic = true }];
        var betweenEventsScene = new PreparedFrameSceneEvaluator(betweenEvents).Evaluate(.6);
        Near(betweenEventsScene.CropOrigin.X + betweenEventsScene.CropSize.X / 2, .45, 1e-10,
            "automatic entry interpolates from the nearest pointer sample before clip.Start");
    }

    private static void AdjacentSegmentsStayContinuous()
    {
        var project = BaseProject(3);
        project.PointerEvents =
        [
            new(0, new(.2, .5), PointerEventKind.LeftClick), new(.9, new(.7, .5)),
            new(1.1, new(.8, .5), PointerEventKind.LeftClick), new(1.9, new(.3, .5))
        ];
        project.ZoomClips =
        [
            new() { Start = 0, End = 1, Scale = 2, Focus = new(.2, .5), EnterDuration = .2, ExitDuration = 0, Automatic = true },
            new() { Start = 1, End = 2, Scale = 2, Focus = new(.8, .5), EnterDuration = .25, ExitDuration = .2, Automatic = true }
        ];
        var evaluator = new PreparedFrameSceneEvaluator(project);
        var before = evaluator.Evaluate(1 - 1e-7);
        var at = evaluator.Evaluate(1);
        var after = evaluator.Evaluate(1 + 1e-7);
        Near(at.CropOrigin.X, before.CropOrigin.X, 2e-6, "adjacent auto clips inherit the previous spring focus at the handoff");
        Near(after.CropOrigin.X, at.CropOrigin.X, 2e-6, "adjacent spring focus has no visible boundary jump");
    }

    private static void LongSilentRangeIsBoundedAndRepeatable()
    {
        const double day = 24 * 60 * 60;
        var project = BaseProject(day);
        project.PointerEvents = [new(0, new(.25, .45), PointerEventKind.LeftClick), new(5, new(.75, .45))];
        project.ZoomClips = [new() { Start = 0, End = day, Scale = 2, Focus = new(.25, .45), EnterDuration = .2, ExitDuration = 0, Automatic = true }];
        var evaluator = new PreparedFrameSceneEvaluator(project);
        var first = evaluator.Evaluate(day * .75);
        var repeated = evaluator.Evaluate(day * .75);
        Check(first.CropOrigin == repeated.CropOrigin && first.CropOrigin.IsFinite,
            "24-hour silent tail uses bounded cached samples and supports deterministic random-access evaluation");
        Check(first.CropOrigin.X > .1, "long silent tail settles at the final activity target");

        const double silentGap = 3 * 60 * 60;
        var silentProject = BaseProject(silentGap);
        silentProject.PointerEvents = [new(0, new(.4, .5), PointerEventKind.LeftClick), new(silentGap, new(.4, .5))];
        silentProject.ZoomClips = [new() { Start = 0, End = silentGap, Scale = 2, Focus = new(.4, .5), EnterDuration = .1, ExitDuration = 0, Automatic = true }];
        var silent = new PreparedFrameSceneEvaluator(silentProject).Evaluate(silentGap * .7);
        Near(silent.CropOrigin.X + silent.CropSize.X / 2, .4, 1e-10,
            "a multi-hour constant pointer interval is skipped after the spring settles instead of exhausting the sample budget");
    }

    private static void ExtremeSpringConvergesWithStableSubsteps()
    {
        var project = BaseProject(60);
        project.Settings.Spring = new() { Mass = .01, Stiffness = 10000, Damping = 100 };
        project.PointerEvents = [new(0, new(.2, .5), PointerEventKind.LeftClick), new(.5, new(.8, .5))];
        project.ZoomClips = [new() { Start = 0, End = 60, Scale = 2, Focus = new(.2, .5), EnterDuration = .1, ExitDuration = 0, Automatic = true }];
        var settled = new PreparedFrameSceneEvaluator(project).Evaluate(30);
        Near(settled.CropOrigin.X + .25, .75, 2e-4,
            "extreme spring values converge stably while the zoom viewport clamps focus to its visible edge");
    }

    private static void ExcessiveContinuousActivityIsRejected()
    {
        const double duration = 9_000;
        var project = BaseProject(duration);
        project.PointerEvents = [new(0, new(.1, .5), PointerEventKind.LeftClick), new(duration, new(.9, .5))];
        project.ZoomClips = [new() { Start = 0, End = duration, Scale = 2, Focus = new(.1, .5), EnterDuration = .1, ExitDuration = 0, Automatic = true }];
        try
        {
            _ = new PreparedFrameSceneEvaluator(project);
            throw new InvalidOperationException("Expected an explicit error when active follow exceeds the bounded cache.");
        }
        catch (InvalidDataException ex)
        {
            Check(ex.Message.Contains("安全采样上限", StringComparison.Ordinal),
                "excessive continuous pointer activity reports the sampling limit instead of degrading cadence");
        }
    }

    private static FrameTrailProject BaseProject(double duration) => new()
    {
        MediaPath = "screen.mp4",
        Media = new() { Width = 1920, Height = 1080, Duration = duration },
        Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = duration }] },
        Settings = new() { AutoZoom = false, Spring = new() { Mass = 2.4, Stiffness = 210, Damping = 42 } }
    };

    private static void Near(double actual, double expected, double tolerance, string message) =>
        Check(Math.Abs(actual - expected) <= tolerance, $"{message}; expected {expected:R}, got {actual:R}");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Automatic follow probe failed: " + message);
    }
}
