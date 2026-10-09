using FrameTrail;
using FrameTrail.Media;

/// <summary>Independent GPU pixel probe; call Run from the render-probe host.</summary>
internal static class ZoomFocusRenderProbe
{
    internal static void Run()
    {
        const int size = 128;
        var source = Checker(size, 8);
        var baseline = new byte[size * size * 4];
        var focused = new byte[size * size * 4];
        var project = new FrameTrailProject
        {
            MediaPath = "screen.mp4", Media = new() { Width = size, Height = size, Duration = 3 },
            Timeline = new() { Segments = [new() { SourceDuration = 3 }] },
            Settings = new() { CanvasWidth = size, CanvasHeight = size, AutoZoom = false, CursorVisible = false, Padding = 0, CornerRadius = 0, Shadow = 0 },
            ZoomClips = [new() { Start = 0, End = 1.5, Scale = 2, EnterDuration = .5, ExitDuration = .5 }]
        };
        var scene = new PreparedFrameSceneEvaluator(project).Evaluate(1);
        var effect = new FocusEffect
        {
            Shape = FocusEffectShape.Radial,
            Target = FocusEffectTarget.Fixed,
            Center = new(.5, .5),
            Size = .32,
            Blur = .9,
            Softness = .28,
            Dimming = .45
        };
        using var renderer = new GpuFrameRenderer(size, size, size, size);
        renderer.Render(source, scene, baseline);
        project.ZoomClips[0].FocusEffect = effect;
        var treated = new PreparedFrameSceneEvaluator(project).Evaluate(1);
        if (treated.Focus is null || treated.FocusProgress != 1) throw new InvalidOperationException("Zoom focus did not reach the renderer from the project evaluator.");
        renderer.Render(source, treated, focused);
        var changedPixels = 0;
        for (var i = 0; i < baseline.Length; i += 4)
            if (baseline[i] != focused[i] || baseline[i + 1] != focused[i + 1] || baseline[i + 2] != focused[i + 2]) changedPixels++;
        if (changedPixels < 300) throw new InvalidOperationException($"Zoom focus GPU treatment changed too few pixels: {changedPixels}.");
        Console.WriteLine($"通过：zoom focus GPU pixels ({renderer.Backend}, changed={changedPixels})");
    }

    private static byte[] Checker(int size, int cell)
    {
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var value = ((x / cell + y / cell) & 1) == 0 ? (byte)24 : (byte)232;
            var offset = (y * size + x) * 4;
            pixels[offset] = value; pixels[offset + 1] = value; pixels[offset + 2] = value; pixels[offset + 3] = 255;
        }
        return pixels;
    }
}
