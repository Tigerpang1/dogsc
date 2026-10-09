using FrameTrail;

internal static class OpeningMotionProbe
{
    private readonly record struct PresetGolden(OpeningPreset Preset, OpeningElement Element,
        double ScaleAt025, double XAt025, double YAt025, double ScaleAt050, double XAt050, double YAt050,
        double ScaleAt080, double XAt080, double YAt080);

    internal static void Run()
    {
        Check(Math.Abs(OpeningMotionEvaluator.Progress(.25, ElementMotionCurve.Smooth) - .103515625) < 1e-12
            && Math.Abs(OpeningMotionEvaluator.Progress(.5, ElementMotionCurve.Smooth) - .5) < 1e-12
            && Math.Abs(OpeningMotionEvaluator.Progress(.75, ElementMotionCurve.Smooth) - .896484375) < 1e-12,
            "Smooth opening curve matches Swift 6t^5-15t^4+10t^3 goldens");
        Check(Math.Abs(OpeningMotionEvaluator.Progress(.25, ElementMotionCurve.Swift) - .68359375) < 1e-12
            && Math.Abs(OpeningMotionEvaluator.Progress(.5, ElementMotionCurve.Swift) - .9375) < 1e-12
            && Math.Abs(OpeningMotionEvaluator.Progress(.75, ElementMotionCurve.Swift) - .99609375) < 1e-12,
            "Swift opening curve matches 1-(1-t)^4 goldens");
        Check(Math.Abs(OpeningMotionEvaluator.Progress(.25, ElementMotionCurve.Gentle) - .578125) < 1e-12
            && Math.Abs(OpeningMotionEvaluator.Progress(.5, ElementMotionCurve.Gentle) - .875) < 1e-12
            && Math.Abs(OpeningMotionEvaluator.Progress(.75, ElementMotionCurve.Gentle) - .984375) < 1e-12,
            "Gentle opening curve matches 1-(1-t)^3 goldens");

        PresetGolden[] samples =
        [
            new(OpeningPreset.Converge, OpeningElement.Screen, .6566607666015625, -.3322265625, -.196171875, .906328125, -.065625, -.03875, 1.00274688, -.00168, -.000992),
            new(OpeningPreset.Converge, OpeningElement.Camera, .6123638916015625, .354375, .2278125, .897578125, .07, .045, 1.00252288, .001792, .001152),
            new(OpeningPreset.Converge, OpeningElement.Overlays, .6250201416015625, -.411328125, -.221484375, .900078125, -.08125, -.04375, 1.00258688, -.00208, -.00112),
            new(OpeningPreset.SideSlide, OpeningElement.Screen, .7262701416015624, -.354375, 0, .920078125, -.07, 0, 1.00309888, -.001792, 0),
            new(OpeningPreset.SideSlide, OpeningElement.Camera, .7072857666015625, .3638671875, 0, .916328125, .071875, 0, 1.00300288, .00184, 0),
            new(OpeningPreset.SideSlide, OpeningElement.Overlays, .7136138916015625, -.4271484375, 0, .917578125, -.084375, 0, 1.00303488, -.00216, 0),
            new(OpeningPreset.Light3D, OpeningElement.Screen, .6819732666015625, -.3322265625, .0253125, .911328125, -.065625, .005, 1.00287488, -.00168, .000128),
            new(OpeningPreset.Light3D, OpeningElement.Camera, .6566607666015625, .348046875, .050625, .906328125, .06875, .01, 1.00274688, .00176, .000256),
            new(OpeningPreset.Light3D, OpeningElement.Overlays, .6693170166015625, -.411328125, -.03796875, .908828125, -.08125, -.0075, 1.00281088, -.00208, -.000192)
        ];
        foreach (var expected in samples)
        {
            var a = OpeningMotionEvaluator.Sample(expected.Preset, expected.Element, .25, ElementMotionCurve.Swift);
            var b = OpeningMotionEvaluator.Sample(expected.Preset, expected.Element, .5, ElementMotionCurve.Swift);
            var c = OpeningMotionEvaluator.Sample(expected.Preset, expected.Element, .8, ElementMotionCurve.Swift);
            Check(Near(a.Scale, expected.ScaleAt025) && Near(a.OffsetX, expected.XAt025) && Near(a.OffsetY, expected.YAt025)
                && Near(b.Scale, expected.ScaleAt050) && Near(b.OffsetX, expected.XAt050) && Near(b.OffsetY, expected.YAt050)
                && Near(c.Scale, expected.ScaleAt080) && Near(c.OffsetX, expected.XAt080) && Near(c.OffsetY, expected.YAt080),
                $"{expected.Preset}/{expected.Element} scale, offsets and end overshoot match Swift goldens");
        }

        var screenQuarter = OpeningMotionEvaluator.Sample(OpeningPreset.Light3D, OpeningElement.Screen, .25, ElementMotionCurve.Swift);
        var screenHalf = OpeningMotionEvaluator.Sample(OpeningPreset.Light3D, OpeningElement.Screen, .5, ElementMotionCurve.Swift);
        var cameraHalf = OpeningMotionEvaluator.Sample(OpeningPreset.Light3D, OpeningElement.Camera, .5, ElementMotionCurve.Swift);
        Check(Near(screenQuarter.RotationX, 9 * Math.Pow(.75, 1.5))
            && Near(screenQuarter.RotationY, -22 * Math.Pow(.75, 1.5))
            && Near(screenQuarter.Perspective, .72 * Math.Pow(.75, 1.5))
            && Near(screenHalf.RotationX, 9 * Math.Pow(.5, 1.5))
            && screenHalf.RotationY < 0 && cameraHalf.RotationX == 0 && cameraHalf.RotationY == 0 && cameraHalf.Perspective == 0,
            "Light3D screen tilt decays with linear-time power 1.5 independently from scale easing and camera pose");

        foreach (var curve in Enum.GetValues<ElementMotionCurve>())
            Check(OpeningMotionEvaluator.Progress(0, curve) == 0 && OpeningMotionEvaluator.Progress(1, curve) == 1
                && OpeningMotionEvaluator.Progress(double.NaN, curve) == 0
                && OpeningMotionEvaluator.Sample(OpeningPreset.Light3D, OpeningElement.Screen, 1, curve) is
                    { Scale: 1, OffsetX: 0, OffsetY: 0, RotationX: 0, RotationY: 0, Perspective: 0, Opacity: 1 },
                $"{curve} handles exact endpoints and non-finite input");
        Check(OpeningMotionEvaluator.Progress(double.NegativeInfinity, ElementMotionCurve.Swift) == 0
            && OpeningMotionEvaluator.Progress(double.PositiveInfinity, ElementMotionCurve.Gentle) == 0,
            "non-finite opening progress uses the defined safe initial state");

        Console.WriteLine("通过：OpeningMotionEvaluator Swift公式与预设 golden probe");
    }

    // Call after PreparedFrameSceneEvaluator wires ElementCurve to this source-time sampler.
    internal static void RunFrameSceneIntegration()
    {
        var project = new FrameTrailProject
        {
            MediaPath = "screen.mp4",
            Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 2 }] },
            Settings = new() { AutoZoom = false, Opening = new()
            {
                Enabled = true, Preset = OpeningPreset.Light3D, Duration = 1,
                ElementCurve = ElementMotionCurve.Swift, IncludedElements = [OpeningElement.Screen]
            } }
        };
        var scene = new PreparedFrameSceneEvaluator(project).Evaluate(.25);
        var expected = OpeningMotionEvaluator.Sample(OpeningPreset.Light3D, OpeningElement.Screen, .25, ElementMotionCurve.Swift);
        Check(scene.Opening is { } actual && Near(actual.Scale, expected.Scale) && Near(actual.OffsetX, expected.OffsetX)
            && Near(actual.RotationX, expected.RotationX) && Near(actual.RotationY, expected.RotationY)
            && Near(actual.Perspective, expected.Perspective),
            "prepared FrameScene opening matches the shared original DogSC ElementMotionEvaluator");
    }

    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) < 1e-10;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Opening motion probe failed: " + message);
    }
}
