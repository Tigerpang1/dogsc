namespace FrameTrail;

public enum ElementMotionCurve { Smooth, Swift, Gentle }

/// <summary>Port of DogSC ElementMotion.swift and FrameScene.openingSample, in canvas-normalized units.</summary>
public static class OpeningMotionEvaluator
{
    public static double Progress(double linear, ElementMotionCurve curve)
    {
        var x = Math.Clamp(double.IsFinite(linear) ? linear : 0, 0, 1);
        return curve switch
        {
            ElementMotionCurve.Smooth => x * x * x * (x * (x * 6 - 15) + 10),
            ElementMotionCurve.Swift => 1 - Math.Pow(1 - x, 4),
            ElementMotionCurve.Gentle => 1 - Math.Pow(1 - x, 3),
            _ => throw new ArgumentOutOfRangeException(nameof(curve))
        };
    }

    public static FrameOpeningState Sample(OpeningPreset preset, OpeningElement element, double linear, ElementMotionCurve curve)
    {
        var t = Math.Clamp(double.IsFinite(linear) ? linear : 0, 0, 1);
        var eased = Progress(t, curve);
        var opacity = Progress(Math.Min(t * 1.65, 1), curve);
        var remaining = 1 - eased;
        var rotationRemaining = Math.Pow(1 - t, 1.5);
        var overshoot = Math.Clamp(1 + 1.35 * Math.Pow(t - 1, 3) + .35 * Math.Pow(t - 1, 2), 0, 1.04);
        var start = (preset, element) switch
        {
            (OpeningPreset.Converge, OpeningElement.Screen) => (.72, -1.05, -.62),
            (OpeningPreset.Converge, OpeningElement.Camera) => (.58, 1.12, .72),
            (OpeningPreset.Converge, _) => (.62, -1.30, -.70),
            (OpeningPreset.SideSlide, OpeningElement.Screen) => (.94, -1.12, 0d),
            (OpeningPreset.SideSlide, OpeningElement.Camera) => (.88, 1.15, 0d),
            (OpeningPreset.SideSlide, _) => (.9, -1.35, 0d),
            (OpeningPreset.Light3D, OpeningElement.Screen) => (.8, -1.05, .08),
            (OpeningPreset.Light3D, OpeningElement.Camera) => (.72, 1.10, .16),
            (OpeningPreset.Light3D, _) => (.76, -1.30, -.12),
            _ => throw new ArgumentOutOfRangeException(nameof(preset), "该预设使用自定开场求值。")
        };
        var screen3D = preset == OpeningPreset.Light3D && element == OpeningElement.Screen;
        return new FrameOpeningState(start.Item1 + (overshoot - start.Item1) * eased,
            start.Item2 * remaining, start.Item3 * remaining, 0, opacity,
            screen3D ? 9 * rotationRemaining : 0, screen3D ? -22 * rotationRemaining : 0,
            screen3D ? .72 * rotationRemaining : 0);
    }
}
