using FrameTrail;

internal static class OverlayBackdropProbe
{
    public static void Run()
    {
        AdjacentHiddenHandoffKeepsScreenSuppressed();
        AdjacentTreatmentsBlendOnTheSwiftCurve();
        OverlapKeepsSharedBackdropTreatmentAtFullStrength();
        ARealGapDoesNotBridgeTreatments();
        BlurIsIndependentOfStickerOpacityAndHonorsOpeningSuppression();
        OverlayTreatmentFieldsSurvivePersistence();
    }

    private static void AdjacentHiddenHandoffKeepsScreenSuppressed()
    {
        var outgoing = Clip(start: 0, duration: 2, hidesScreen: true, hidesCamera: true);
        var incoming = Clip(start: 2, duration: 2, hidesScreen: true, hidesCamera: true);
        var track = new PreparedOverlayTrack([outgoing, incoming]);
        foreach (var time in new[] { 1.5, 1.75, 2, 2.25, 2.499999 })
        {
            var backdrop = track.Evaluate(time).Backdrop;
            Near(backdrop.ScreenSuppression, 1, "adjacent hidden sticker handoff keeps screen suppression full");
            Near(backdrop.CameraSuppression, 1, "adjacent hidden sticker handoff keeps camera suppression full");
        }
    }

    private static void AdjacentTreatmentsBlendOnTheSwiftCurve()
    {
        var outgoing = Clip(start: 0, duration: 2, hidesScreen: true, curve: ElementMotionCurve.Smooth);
        var incoming = Clip(start: 2, duration: 2, backdropBlur: 40, includesCamera: true, curve: ElementMotionCurve.Smooth);
        var track = new PreparedOverlayTrack([outgoing, incoming]);
        var before = track.Evaluate(1.75).Backdrop;
        var midpoint = track.Evaluate(2).Backdrop;
        var after = track.Evaluate(2.25).Backdrop;

        // Swift ElementMotionEvaluator uses the quintic smoothstep at x=.25/.5/.75.
        Near(midpoint.ScreenSuppression, .5, "adjacent screen suppression uses the independently derived smoothstep midpoint");
        Near(midpoint.ScreenBlur, 20, "adjacent screen blur blends to half strength at the midpoint");
        Near(midpoint.CameraBlur, 20, "adjacent camera blur blends to half strength at the midpoint");
        Check(before.ScreenSuppression > midpoint.ScreenSuppression && midpoint.ScreenSuppression > after.ScreenSuppression,
            "adjacent suppression transfers monotonically from outgoing to incoming treatment");
        Check(before.ScreenBlur < midpoint.ScreenBlur && midpoint.ScreenBlur < after.ScreenBlur,
            "adjacent blur transfers monotonically from outgoing to incoming treatment");
    }

    private static void OverlapKeepsSharedBackdropTreatmentAtFullStrength()
    {
        var outgoing = Clip(start: 0, duration: 2, hidesScreen: true, hidesCamera: true, backdropBlur: 28, includesCamera: true);
        var incoming = Clip(start: 1.8, duration: 2, hidesScreen: true, hidesCamera: true, backdropBlur: 28, includesCamera: true);
        var track = new PreparedOverlayTrack([outgoing, incoming]);
        var sample = track.Evaluate(2.1);
        Near(sample.Backdrop.ScreenSuppression, 1, "overlapping stickers keep shared screen suppression full while each image fades");
        Near(sample.Backdrop.CameraSuppression, 1, "overlapping stickers keep shared camera suppression full while each image fades");
        Near(sample.Backdrop.ScreenBlur, 0, "hiding the screen disables screen blur during an overlap bridge");
        Near(sample.Backdrop.CameraBlur, 28, "overlap bridge preserves shared camera blur at full strength");

        var blurOutgoing = Clip(start: 0, duration: 2, backdropBlur: 28, includesCamera: true);
        var blurIncoming = Clip(start: 1.8, duration: 2, backdropBlur: 28, includesCamera: true);
        var blurBridge = new PreparedOverlayTrack([blurOutgoing, blurIncoming]).Evaluate(2.1).Backdrop;
        Near(blurBridge.ScreenSuppression, 0, "non-hidden overlap does not suppress the screen");
        Near(blurBridge.ScreenBlur, 28, "overlap bridge keeps shared screen blur at full strength");
        Near(blurBridge.CameraBlur, 28, "non-hidden overlap bridge keeps shared camera blur at full strength");
    }

    private static void ARealGapDoesNotBridgeTreatments()
    {
        var outgoing = Clip(start: 0, duration: 2, hidesScreen: true, hidesCamera: true, backdropBlur: 30, includesCamera: true);
        var incoming = Clip(start: 2.05, duration: 2, hidesScreen: true, hidesCamera: true, backdropBlur: 30, includesCamera: true);
        var track = new PreparedOverlayTrack([outgoing, incoming]);
        var gap = track.Evaluate(2.025).Backdrop;
        Near(gap.ScreenSuppression, 0, "a real timeline gap restores screen between stickers");
        Near(gap.CameraSuppression, 0, "a real timeline gap restores camera between stickers");
        Near(gap.ScreenBlur, 0, "a real timeline gap has no screen blur bridge");
        Near(gap.CameraBlur, 0, "a real timeline gap has no camera blur bridge");
    }

    private static void BlurIsIndependentOfStickerOpacityAndHonorsOpeningSuppression()
    {
        var staticOverlay = Clip(start: 0, duration: 2, backdropBlur: 36, includesCamera: true) with { Opacity = .12 };
        var staticSample = new PreparedOverlayTrack([staticOverlay]).Evaluate(.5);
        Near(staticSample.Overlays.Single().Visibility, 1, "static overlay is fully present independent of authored opacity");
        Near(staticSample.Backdrop.ScreenBlur, 36, "backdrop blur does not scale with the sticker's authored opacity");
        Near(staticSample.Backdrop.CameraBlur, 36, "camera backdrop blur does not scale with the sticker's authored opacity");

        var opening = Clip(start: 0, duration: 2, enterDuration: .5, backdropBlur: 36, includesCamera: true);
        var suppressed = new PreparedOverlayTrack([opening]).Evaluate(.05, suppressInitialEntry: true, initialSourceTime: 0);
        Near(suppressed.Overlays.Single().Visibility, 1, "opening suppression skips initial sticker fade-in");
        Near(suppressed.Backdrop.ScreenBlur, 36, "opening suppression applies full backdrop treatment at the first frame");
        Near(suppressed.Backdrop.CameraBlur, 36, "opening suppression applies full camera treatment at the first frame");
    }

    private static void OverlayTreatmentFieldsSurvivePersistence()
    {
        var root = Path.Combine(Path.GetTempPath(), "frametrail-overlay-backdrop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var projectFile = Path.Combine(root, "backdrop.frametrail");
            var overlay = Clip(start: .25, duration: 1.75, hidesScreen: true, hidesCamera: true,
                backdropBlur: 42, includesCamera: true, curve: ElementMotionCurve.Gentle) with { Opacity = .23 };
            var project = new FrameTrailProject
            {
                MediaPath = "screen.mp4",
                Media = new MediaAsset { Width = 1920, Height = 1080, Duration = 4, FrameRate = 60 },
                Timeline = new ProjectTimeline { Segments = [new TimelineSegment { SourceStart = 0, SourceDuration = 4 }] },
                Overlays = [overlay]
            };
            FrameTrailProjectStore.Save(projectFile, project);
            var restored = FrameTrailProjectStore.Load(projectFile).Overlays.Single();
            Check(restored.Id == overlay.Id && restored.AnimationCurve == ElementMotionCurve.Gentle
                && restored.Opacity == .23 && restored.BackdropBlur == 42 && restored.BackdropBlurIncludesCamera
                && restored.HidesScreen && restored.HidesCamera,
                "overlay backdrop fields and transition curve survive save and reopen");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static OverlayClip Clip(double start, double duration, bool hidesScreen = false, bool hidesCamera = false,
        double backdropBlur = 0, bool includesCamera = false, ElementMotionCurve curve = ElementMotionCurve.Swift,
        double enterDuration = .5, double exitDuration = .5)
        => new()
        {
            MediaPath = "assets/sticker.png", Start = start, Duration = duration,
            Rect = new(.3, .3, .4, .4), EnterDuration = enterDuration, ExitDuration = exitDuration,
            EnterMotion = OverlayMotion.Fade, ExitMotion = OverlayMotion.Fade, AnimationCurve = curve,
            HidesScreen = hidesScreen, HidesCamera = hidesCamera, BackdropBlur = backdropBlur,
            BackdropBlurIncludesCamera = includesCamera
        };

    private static void Near(double actual, double expected, string message)
    {
        if (!double.IsFinite(actual) || Math.Abs(actual - expected) > 1e-8)
            throw new InvalidOperationException($"Overlay backdrop probe failed: {message}; expected {expected}, got {actual}.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Overlay backdrop probe failed: " + message);
    }
}
