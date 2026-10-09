using FrameTrail;
using System.Text.Json;

internal static class OverlayStyleProbe
{
    internal static void Run()
    {
        var clip = new OverlayClip { MediaPath = "sticker.png", Start = 0, Duration = 3, Rect = new(.3, .35, .4, .3), Opacity = .6,
            EnterMotion = OverlayMotion.Scale, ExitMotion = OverlayMotion.SlideBottomRight, EnterDuration = 1, ExitDuration = .5,
            AnimationCurve = ElementMotionCurve.Gentle, BorderWidth = 3, BorderColor = "#FF5533", ShadowOpacity = .4, ShadowRadius = 24, ShadowOffsetX = -4, ShadowOffsetY = 9 };
        var quarter = OverlayMotionEvaluator.Sample(clip, .25);
        Near(quarter.Opacity, .6 * (1 - .75 * .75 * .75), "gentle entry alpha uses the original cubic ease-out");
        Near(quarter.Rect.Width, .4 * (.72 + .578125 * .28), "pop scale follows the original .72..1 path");
        var beforeEntry = OverlayMotionEvaluator.Transform(clip.Rect, OverlayMotion.SlideTopLeft, 0);
        Check(beforeEntry.X + beforeEntry.Width < 0 && beforeEntry.Y + beforeEntry.Height < 0, "diagonal entry starts fully outside the canvas");
        var exit = OverlayMotionEvaluator.Sample(clip, 2.75);
        Check(exit.Rect.X > clip.Rect.X && exit.Rect.Y > clip.Rect.Y, "diagonal exit travels toward its independent exit direction");
        Near(exit.Opacity, .6 * .875, "exit evaluates remaining time through the same element curve");
        Check(OverlayMotionEvaluator.AutomaticExit(OverlayMotion.SlideTopLeft) == OverlayMotion.SlideBottomRight, "automatic exit is the complementary direction");

        var shortClip = clip with { Duration = .4, EnterDuration = 1, ExitDuration = 1 };
        Near(OverlayMotionEvaluator.Sample(shortClip, .2).Opacity, .6, "short clip budgets both transitions within its duration and reaches full opacity");
        var project = new FrameTrailProject { MediaPath = "screen.mp4", Media = new() { Width = 320, Height = 180, Duration = 3 },
            Timeline = new() { Segments = [new() { SourceDuration = 3 }] }, Settings = new() { AutoZoom = false }, Overlays = [clip] };
        var scene = new PreparedFrameSceneEvaluator(project).Evaluate(1.5).Overlays!.Single();
        Check(scene.BorderWidth == 3 && scene.BorderColor == "#FF5533" && scene.ShadowOpacity == .4 && scene.ShadowRadius == 24
            && scene.ShadowOffsetX == -4 && scene.ShadowOffsetY == 9, "border and shadow reach the shared rendered scene");
        var old = JsonSerializer.Deserialize<OverlayClip>("{}")!;
        Check(old.AnimationCurve is null && old.ShadowOpacity == 0 && old.BorderWidth == 0, "legacy projects retain their prior transition and no shadow/border");
        var path = Path.Combine(Path.GetTempPath(), "frametrail-overlay-style-" + Guid.NewGuid().ToString("N") + ".frametrail");
        try
        {
            FrameTrailProjectStore.Save(path, project);
            var reloaded = FrameTrailProjectStore.Load(path).Overlays.Single();
            Check(reloaded == clip, "advanced overlay style and independent diagonal exit survive save/reopen");
            project.Overlays[0].ShadowRadius = double.NaN;
            try { FrameTrailProjectStore.Save(path, project); throw new Exception("non-finite shadow was accepted"); }
            catch (InvalidDataException) { }
        }
        finally { if (File.Exists(path)) File.Delete(path); if (File.Exists(path + ".bak")) File.Delete(path + ".bak"); }
        Console.WriteLine("通过：贴图原版曲线、斜向进出场、边框阴影场景与持久化");
    }
    private static void Near(double a, double b, string message) => Check(Math.Abs(a - b) < 1e-9, message);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("Overlay style: " + message); }
}
