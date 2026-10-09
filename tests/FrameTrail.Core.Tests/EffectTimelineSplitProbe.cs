using System.Text.Json;
using FrameTrail;

namespace FrameTrail.Core.Tests;

/// <summary>Independent regression cases for source-clock effect splitting.</summary>
public static class EffectTimelineSplitProbe
{
    public static void Run()
    {
        ZoomSplitKeepsTransitionAndStyle();
        MotionSplitBakesSeamAndKeepsRightTarget();
        CameraSplitPreservesLayoutAndRoundness();
        CameraSplitWithDifferentLayoutRemainsFinite();
        SimpleEffectSplitCopiesSettingsAndRoundTrips();
        MotionSplitDuringHoldRemainsFinite();
        InvalidSplitDoesNotMutate();
        ZoomSplitKeepsPhaseAnchorsAndHoldContinuity();
        CameraMissingAndInvalidTimesDoNotMutate();
        Console.WriteLine("通过：缩放、屏幕运动、摄像头运动、贴图和模糊效果分割回归。");
    }

    private static void ZoomSplitKeepsTransitionAndStyle()
    {
        var focusEffect = new FocusEffect { Size = .31, Blur = .7 };
        var zoom = new ZoomClip { Start = 1, End = 4, Scale = 1.8, Focus = new(.3, .6), EnterDuration = .6, ExitDuration = .4,
            Easing = ZoomEasing.Custom, CustomCurve = new(.2, .1, .8, .9), Automatic = true, FocusEffect = focusEffect };
        var project = Project(); project.ZoomClips.Add(zoom);
        var rightId = EffectTimelineOperations.Split(project, TimelineEffectKind.Zoom, zoom.Id, 2.5);
        var left = project.ZoomClips.Single(x => x.Id == zoom.Id);
        var right = project.ZoomClips.Single(x => x.Id == rightId);
        Check(left.End == 2.5 && left.ExitDuration == 0 && right.Start == 2.5 && right.End == 4,
            "zoom split keeps adjacent timing and disables the left return");
        Check(right.EnterDuration >= .15 && right.Scale == zoom.Scale && right.Focus == zoom.Focus
            && right.Automatic && right.Easing == zoom.Easing && right.CustomCurve == zoom.CustomCurve,
            "zoom split preserves authored target, easing, and automatic-follow behavior");
        Check(left.Id == zoom.Id && right.Id != zoom.Id && left.FocusEffect is not null && right.FocusEffect is not null
            && !ReferenceEquals(left.FocusEffect, right.FocusEffect), "zoom split assigns independent identities and focus settings");
    }

    private static void MotionSplitBakesSeamAndKeepsRightTarget()
    {
        var screen = new ScreenMotionClip
        {
            Start = 1, Duration = 4, EnterDuration = 1.2, ReturnDuration = .8, Easing = ZoomEasing.Linear,
            Target = new() { Position = new(.72, .38), Scale = 1.35, RotationY = 14, Perspective = .7 },
            Focus = new() { Size = .27 }, GroupId = Guid.NewGuid()
        };
        var project = Project(); project.ScreenMotions.Add(screen);
        var preSplit = new PreparedFrameSceneEvaluator(project).Evaluate(1.5).ScreenMotion!;
        var rightId = EffectTimelineOperations.Split(project, TimelineEffectKind.ScreenMotion, screen.Id, 1.5);
        var left = project.ScreenMotions.Single(x => x.Id == screen.Id);
        var right = project.ScreenMotions.Single(x => x.Id == rightId);
        var seam = new PreparedFrameSceneEvaluator(project).Evaluate(1.5).ScreenMotion!;
        Check(Near(left.Target.Position.X, preSplit.Position.X) && Near(left.Target.Position.Y, preSplit.Position.Y)
            && Near(left.Target.Scale, preSplit.Scale) && Near(left.Target.RotationY, preSplit.RotationY),
            "screen motion left target is baked at the cut from source-time evaluator state");
        Check(Near(seam.Position.X, left.Target.Position.X) && Near(seam.Position.Y, left.Target.Position.Y)
            && Near(seam.Scale, left.Target.Scale), "screen motion has no seam jump after split");
        Check(right.Target.Position == screen.Target.Position && right.Target.Scale == screen.Target.Scale
            && left.GroupId == screen.GroupId && right.GroupId == screen.GroupId
            && left.Focus is not null && right.Focus is not null && !ReferenceEquals(left.Focus, right.Focus),
            "screen motion right target, group, and independently copied focus survive split");
        Check(left.ReturnDuration == 0 && right.EnterDuration > 0 && Near(right.Duration, 3.5),
            "screen split removes left return and gives right a bounded continuation lead-in");
    }

    private static void CameraSplitPreservesLayoutAndRoundness()
    {
        var camera = new CameraTrack { MediaPath = "camera.mp4", Duration = 8, TimelineStart = 0, SourceStart = 0,
            Layout = CameraMotionLayoutKind.RoundedRectangle, CornerRadius = .3, Size = .25, Position = new(.75, .72), AssetAspectRatio = 16d / 9 };
        var motion = new CameraMotionClip { Start = 1, Duration = 4, EnterDuration = 1, ReturnDuration = .5, Easing = ZoomEasing.Linear,
            TargetPosition = new(.65, .35), TargetSize = .4, TargetLayout = CameraMotionLayoutKind.RoundedRectangle,
            TargetRoundness = .22, TargetOpacity = .8 };
        var project = Project(); project.Camera = camera; project.CameraMotions.Add(motion);
        var before = new PreparedFrameSceneEvaluator(project).Evaluate(1.4).Camera!;
        var rightId = EffectTimelineOperations.Split(project, TimelineEffectKind.CameraMotion, motion.Id, 1.4);
        var left = project.CameraMotions.Single(x => x.Id == motion.Id);
        var right = project.CameraMotions.Single(x => x.Id == rightId);
        var atCut = new PreparedFrameSceneEvaluator(project).Evaluate(1.4).Camera!;
        Check(left.TargetLayout == motion.TargetLayout && left.TargetRoundness == motion.TargetRoundness,
            "camera split keeps the authored target layout and roundness on the baked left target");
        Check(Near(left.TargetPosition.X, before.Rect.X + before.Rect.Width / 2)
            && Near(left.TargetPosition.Y, before.Rect.Y + before.Rect.Height / 2)
            && Near(left.TargetOpacity!.Value, before.Opacity),
            "camera split samples position and opacity from the resolved pose at the cut");
        Check(Near(atCut.Rect.X, before.Rect.X) && Near(atCut.Rect.Y, before.Rect.Y)
            && Near(atCut.Rect.Width, before.Rect.Width) && Near(atCut.Rect.Height, before.Rect.Height),
            "same-layout camera split retains geometry continuously at the cut");
        Check(right.TargetPosition == motion.TargetPosition && right.TargetLayout == motion.TargetLayout
            && right.TargetRoundness == motion.TargetRoundness && right.TargetOpacity == motion.TargetOpacity,
            "camera split leaves the right target unchanged");
    }

    private static void SimpleEffectSplitCopiesSettingsAndRoundTrips()
    {
        var overlay = new OverlayClip { MediaPath = "assets/card.png", Start = 1, Duration = 3, Rect = new(.1, .2, .3, .4),
            Rotation = 12, Layer = 4, HidesCamera = true, BackdropBlur = .4 };
        var blur = new BlurClip { Start = 2, Duration = 4, Rect = new(.2, .1, .5, .6), Strength = .8, Kind = BlurKind.Pixelate, SourceSpace = true };
        var project = Project(); project.Overlays.Add(overlay); project.BlurClips.Add(blur);
        var overlayRight = EffectTimelineOperations.Split(project, TimelineEffectKind.Overlay, overlay.Id, 2.25);
        var blurRight = EffectTimelineOperations.Split(project, TimelineEffectKind.Blur, blur.Id, 4);
        Check(project.Overlays.Single(x => x.Id == overlay.Id).Duration == 1.25
            && project.Overlays.Single(x => x.Id == overlayRight).Duration == 1.75
            && project.Overlays.Single(x => x.Id == overlayRight).HidesCamera
            && project.Overlays.Single(x => x.Id == overlayRight).Rect == overlay.Rect,
            "overlay split preserves appearance and divides the source-time range");
        Check(project.BlurClips.Single(x => x.Id == blur.Id).Duration == 2
            && project.BlurClips.Single(x => x.Id == blurRight).Duration == 2
            && project.BlurClips.Single(x => x.Id == blurRight).Kind == BlurKind.Pixelate
            && project.BlurClips.Single(x => x.Id == blurRight).SourceSpace,
            "blur split preserves region behavior and divides the source-time range");
        var reopened = JsonSerializer.Deserialize<FrameTrailProject>(JsonSerializer.Serialize(project))!;
        Check(reopened.Overlays.Count == 2 && reopened.BlurClips.Count == 2
            && reopened.Overlays.Any(x => x.Id == overlayRight) && reopened.BlurClips.Any(x => x.Id == blurRight),
            "split effect IDs and settings survive project serialization");
    }

    private static void CameraSplitWithDifferentLayoutRemainsFinite()
    {
        var project = Project();
        project.Camera = new CameraTrack { MediaPath = "camera.mp4", Duration = 8, Layout = CameraMotionLayoutKind.RoundedRectangle,
            AssetAspectRatio = 16d / 9, Size = .3, CornerRadius = .16, Position = new(.7, .7) };
        var motion = new CameraMotionClip { Start = 1, Duration = 4, EnterDuration = 1, ReturnDuration = .5,
            Easing = ZoomEasing.Linear, TargetPosition = new(.2, .3), TargetSize = .42,
            TargetLayout = CameraMotionLayoutKind.Circle, TargetRoundness = .37, TargetOpacity = .65 };
        project.CameraMotions.Add(motion);
        var rightId = EffectTimelineOperations.Split(project, TimelineEffectKind.CameraMotion, motion.Id, 1.45);
        var left = project.CameraMotions.Single(x => x.Id == motion.Id);
        var right = project.CameraMotions.Single(x => x.Id == rightId);
        var scene = new PreparedFrameSceneEvaluator(project).Evaluate(1.45).Camera!;
        Check(left.TargetLayout == CameraMotionLayoutKind.Circle && left.TargetRoundness == .37
            && right.TargetLayout == CameraMotionLayoutKind.Circle && right.TargetRoundness == .37,
            "different-layout camera split retains the authored layout and roundness on both pieces");
        Check(double.IsFinite(left.TargetPosition.X + left.TargetPosition.Y + left.TargetSize + left.TargetOpacity!.Value)
            && double.IsFinite(scene.Rect.X + scene.Rect.Y + scene.Rect.Width + scene.Rect.Height + scene.CornerRadius + scene.Opacity),
            "different-layout camera split remains finite; the interpolated mid-transition shape is approximate");
    }

    private static void MotionSplitDuringHoldRemainsFinite()
    {
        var project = Project();
        var screen = new ScreenMotionClip { Start = 1, Duration = 4, EnterDuration = .4, ReturnDuration = .5,
            Target = new() { Position = new(.68, .31), Scale = 1.3, RotationX = -8, RotationY = 11 } };
        project.ScreenMotions.Add(screen);
        var rightId = EffectTimelineOperations.Split(project, TimelineEffectKind.ScreenMotion, screen.Id, 3);
        var left = project.ScreenMotions.Single(x => x.Id == screen.Id);
        var right = project.ScreenMotions.Single(x => x.Id == rightId);
        Check(Near(left.Target.Position.X, screen.Target.Position.X) && Near(left.Target.Scale, screen.Target.Scale)
            && right.Target.Position == screen.Target.Position && right.Target.Scale == screen.Target.Scale,
            "screen motion split during hold samples the held state and keeps the right target");
        var eval = new PreparedFrameSceneEvaluator(project);
        var before = eval.Evaluate(2.999).ScreenMotion!;
        var seam = eval.Evaluate(3).ScreenMotion!;
        var after = eval.Evaluate(3.001).ScreenMotion!;
        Check(Finite(before) && Finite(seam) && Finite(after)
            && Near(seam.Position.X, left.Target.Position.X) && Near(seam.Scale, left.Target.Scale)
            && Near(before.Position.X, seam.Position.X) && Near(after.Position.X, seam.Position.X),
            "hold-phase motion split stays finite and continuous on both sides of the cut");
    }

    private static void InvalidSplitDoesNotMutate()
    {
        var zoom = new ZoomClip { Start = 1, End = 2, Scale = 1.7 };
        var project = Project(); project.ZoomClips.Add(zoom);
        try { EffectTimelineOperations.Split(project, TimelineEffectKind.Zoom, zoom.Id, 1.1); throw new Exception("short-left split was accepted"); }
        catch (ArgumentOutOfRangeException) { }
        Check(project.ZoomClips.Count == 1 && project.ZoomClips[0] == zoom && zoom.Start == 1 && zoom.End == 2,
            "rejected split leaves the project unchanged");
        try { EffectTimelineOperations.Split(project, TimelineEffectKind.Zoom, Guid.NewGuid(), 1.5); throw new Exception("missing effect ID was accepted"); }
        catch (KeyNotFoundException) { }
        try { EffectTimelineOperations.Split(project, TimelineEffectKind.CameraMotion, Guid.NewGuid(), 1.5); throw new Exception("missing camera clip was accepted"); }
        catch (KeyNotFoundException) { }
    }

    private static void ZoomSplitKeepsPhaseAnchorsAndHoldContinuity()
    {
        var zoom = new ZoomClip { Start = 1, End = 5, Scale = 1.85, Focus = new(.34, .62), EnterDuration = .7, ExitDuration = .8,
            EnterProgressOffset = .23, ExitProgressOffset = .41, Easing = ZoomEasing.Linear };
        var project = Project(); project.ZoomClips.Add(zoom);
        var rightId = EffectTimelineOperations.Split(project, TimelineEffectKind.Zoom, zoom.Id, 3);
        var left = project.ZoomClips.Single(x => x.Id == zoom.Id);
        var right = project.ZoomClips.Single(x => x.Id == rightId);
        Check(left.EnterProgressOffset == .23 && right.EnterProgressOffset == .23
            && right.ExitProgressOffset == .41 && left.ExitProgressOffset == 0,
            "zoom split preserves active phase anchors and clears the disabled left return anchor");
        var evaluator = new PreparedFrameSceneEvaluator(project);
        var before = evaluator.Evaluate(2.999);
        var seam = evaluator.Evaluate(3);
        var after = evaluator.Evaluate(3.001);
        Check(double.IsFinite(before.Zoom + seam.Zoom + after.Zoom)
            && before.CropOrigin.IsFinite && seam.CropOrigin.IsFinite && after.CropOrigin.IsFinite
            && Near(before.Zoom, seam.Zoom) && Near(seam.Zoom, after.Zoom)
            && Near(before.CropOrigin.X, seam.CropOrigin.X) && Near(seam.CropOrigin.X, after.CropOrigin.X),
            "zoom split during hold stays finite and continuous across the boundary");
    }

    private static void CameraMissingAndInvalidTimesDoNotMutate()
    {
        var cameraMotion = new CameraMotionClip { Start = 1, Duration = 4, TargetLayout = CameraMotionLayoutKind.Original };
        var cameraLess = Project(); cameraLess.CameraMotions.Add(cameraMotion);
        var before = JsonSerializer.Serialize(cameraLess);
        try { EffectTimelineOperations.Split(cameraLess, TimelineEffectKind.CameraMotion, cameraMotion.Id, 2); throw new Exception("camera-less motion split was accepted"); }
        catch (InvalidOperationException) { }
        Check(JsonSerializer.Serialize(cameraLess) == before, "camera-less split rejects without changing the project");

        var screen = new ScreenMotionClip { Start = 1, Duration = 3 };
        var project = Project(); project.ScreenMotions.Add(screen);
        before = JsonSerializer.Serialize(project);
        try { EffectTimelineOperations.Split(project, TimelineEffectKind.ScreenMotion, screen.Id, double.NaN); throw new Exception("NaN split time was accepted"); }
        catch (ArgumentOutOfRangeException) { }
        try { EffectTimelineOperations.Split(project, TimelineEffectKind.ScreenMotion, screen.Id, 4); throw new Exception("outside split time was accepted"); }
        catch (ArgumentOutOfRangeException) { }
        Check(JsonSerializer.Serialize(project) == before, "NaN and out-of-range splits leave every project field unchanged");
    }

    private static FrameTrailProject Project() => new()
    {
        Media = new() { Width = 1920, Height = 1080, Duration = 10 },
        Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 10 }] },
        Settings = new() { AutoZoom = false }
    };

    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;
    private static bool Finite(ScreenMotionState x) => x.Position.IsFinite && double.IsFinite(x.Scale + x.RotationX + x.RotationY + x.RotationZ + x.Perspective);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
