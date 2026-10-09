using FrameTrail;
using System.Text.Json;

internal static class ZoomFocusEffectProbe
{
    internal static void Run()
    {
        SourceCropAnchorAndProgress();
        PointerAndScreenPriority();
        LegacyAndPersistenceValidation();
        Console.WriteLine("通过：zoom focus effect evaluation");
    }

    private static void LegacyAndPersistenceValidation()
    {
        var legacy = JsonSerializer.Deserialize<ZoomClip>("{}")!;
        Check(legacy.FocusEffect is null, "old project JSON keeps zoom focus treatment disabled by default");
        var clip = new ZoomClip { FocusEffect = new() { Shape = FocusEffectShape.Linear, Target = FocusEffectTarget.Fixed, Center = new(.2, .3), AngleDegrees = 35 } };
        var roundTrip = JsonSerializer.Deserialize<ZoomClip>(JsonSerializer.Serialize(clip))!;
        Check(roundTrip.FocusEffect is { Shape: FocusEffectShape.Linear, Target: FocusEffectTarget.Fixed, Center: { X: .2, Y: .3 }, AngleDegrees: 35 },
            "zoom focus settings survive the JSON clone used by persistence, history and effect copy");

        var invalid = BaseProject();
        invalid.ZoomClips = [new() { Start = 0, End = 1, FocusEffect = new() { Size = 1.01 } }];
        var path = Path.Combine(Path.GetTempPath(), "frametrail-invalid-zoom-focus-" + Guid.NewGuid().ToString("N") + ".frametrail");
        try
        {
            FrameTrailProjectStore.Save(path, invalid);
            throw new InvalidOperationException("Invalid zoom focus effect was accepted by project validation.");
        }
        catch (InvalidDataException) { }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void SourceCropAnchorAndProgress()
    {
        var project = BaseProject();
        project.Settings.Canvas = new CanvasSettings { Crop = new(.25, 0, .5, 1) };
        project.ZoomClips = [new()
        {
            Start = .5, End = 1.5, Scale = 2, Focus = new(.6, .5), EnterDuration = 1, ExitDuration = 1,
            Easing = ZoomEasing.Linear,
            FocusEffect = new() { Shape = FocusEffectShape.Radial, Target = FocusEffectTarget.Animation, Size = .4, Blur = .8, Softness = .3, Dimming = .2 }
        }];

        var evaluator = new PreparedFrameSceneEvaluator(project);
        var entering = evaluator.Evaluate(.75);
        Check(entering.Focus?.Shape == FocusEffectShape.Radial, "zoom-attached focus effect is present during entry");
        Near(entering.FocusProgress, .25, 1e-9, "focus effect follows the zoom clip's eased entry progress");
        // Swift ZoomAnimation keeps the authored target focus fixed through entry;
        // it does not interpolate from canvas center. Crop-local .6 maps to source .25 + .6*.5.
        Near(entering.Focus!.Center.X, .25 + .6 * .5, 1e-9,
            "animation focus follows the fixed target anchor transformed through the project canvas crop");

        var held = evaluator.Evaluate(1.5);
        Near(held.FocusProgress, 1, 1e-9, "focus effect remains fully active during the zoom hold");
        var exiting = evaluator.Evaluate(1.75);
        Near(exiting.FocusProgress, .75, 1e-9, "focus effect follows the zoom clip's exit progress");
        Check(evaluator.Evaluate(2.6).Focus is null, "focus effect ends after its configured exit interval");

        var target = evaluator.EvaluateTargetForZoom(project.ZoomClips[0].Id, .1);
        Check(target.Focus?.Shape == FocusEffectShape.Radial && target.FocusProgress == 1,
            "paused selected-zoom preview applies the target focus effect even outside its timeline interval");
        Near(target.Focus!.Center.X, .25 + .6 * .5, 1e-9, "selected-zoom preview anchors its effect at the selected target crop");
    }

    private static void PointerAndScreenPriority()
    {
        var pointer = BaseProject();
        pointer.PointerEvents = [new(0, new(.72, .63), PointerEventKind.LeftClick)];
        pointer.ZoomClips = [new() { Start = 0, End = 1, Scale = 2, Focus = new(.4, .5), EnterDuration = .2, ExitDuration = .2,
            FocusEffect = new() { Shape = FocusEffectShape.Radial, Target = FocusEffectTarget.Pointer } }];
        var pointerScene = new PreparedFrameSceneEvaluator(pointer).Evaluate(.5);
        Check(pointerScene.Focus?.Target == FocusEffectTarget.Pointer, "pointer-targeted zoom focus effect remains pointer-relative");
        Near(pointerScene.Focus!.Center.X, .72, 1e-9, "pointer-targeted focus resolves to the calibrated pointer x coordinate");
        Near(pointerScene.Focus.Center.Y, .63, 1e-9, "pointer-targeted focus resolves to the calibrated pointer y coordinate");

        pointer.ScreenMotions = [new()
        {
            Start = 0, Duration = 1, EnterDuration = .5, ReturnDuration = .2, Easing = ZoomEasing.Linear,
            Target = new() { Position = new(.8, .25) },
            Focus = new() { Shape = FocusEffectShape.Linear, Target = FocusEffectTarget.Fixed, Center = new(.1, .2), AngleDegrees = 25, Blur = .4 }
        }];
        var priority = new PreparedFrameSceneEvaluator(pointer).Evaluate(.25);
        Check(priority.Focus?.Shape == FocusEffectShape.Linear && priority.Focus.Target == FocusEffectTarget.Fixed,
            "screen-motion focus takes precedence when a screen and zoom effect overlap");
        Near(priority.Focus!.Center.X, .1, 1e-9, "higher-priority fixed screen focus preserves its own center");
    }

    private static FrameTrailProject BaseProject() => new()
    {
        MediaPath = "screen.mp4",
        Media = new() { Width = 1920, Height = 1080, Duration = 3 },
        Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 3 }] },
        Settings = new() { AutoZoom = false }
    };

    private static void Near(double actual, double expected, double tolerance, string message) =>
        Check(Math.Abs(actual - expected) <= tolerance, $"{message}; expected {expected:R}, got {actual:R}");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Zoom focus probe failed: " + message);
    }
}
