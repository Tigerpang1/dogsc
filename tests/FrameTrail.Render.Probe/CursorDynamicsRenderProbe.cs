using FrameTrail;
using FrameTrail.Media;

internal static class CursorDynamicsRenderProbe
{
    internal static void Run()
    {
        const int sourceWidth = 320, sourceHeight = 180, outputWidth = 640, outputHeight = 360;
        var source = new byte[sourceWidth * sourceHeight * 4];
        for (var y = 0; y < sourceHeight; y++)
        for (var x = 0; x < sourceWidth; x++)
        {
            var offset = (y * sourceWidth + x) * 4;
            var light = ((x / 12 + y / 12) & 1) == 0 ? (byte)205 : (byte)90;
            source[offset] = light;
            source[offset + 1] = (byte)(light * .92);
            source[offset + 2] = (byte)(light * .84);
            source[offset + 3] = 255;
        }

        var plain = new byte[outputWidth * outputHeight * 4];
        var still = new byte[plain.Length];
        var tilted = new byte[plain.Length];
        var baseScene = new FrameScene(0, 0, new(0, 0), new(1, 1), 1, null, 0, "#202124", .08, .03,
            outputWidth, outputHeight, .1, SourceWidth: sourceWidth, SourceHeight: sourceHeight);
        using var renderer = new GpuFrameRenderer(sourceWidth, sourceHeight, outputWidth, outputHeight);
        renderer.Render(source, baseScene, plain);
        var cursorSettings = new CursorSettings { Visible = true, MotionTiltEnabled = true, Scale = 1.25, ClickEffect = ClickEffectStyle.None };
        var cursorScene = baseScene with { Cursor = new(.5, .5), CursorAppearance = cursorSettings };
        renderer.Render(source, cursorScene, still);
        renderer.Render(source, cursorScene with { CursorRotationRadians = .22 }, tilted);
        Require(CountDifferent(still, tilted) > 20, "speed-derived cursor rotation changes raster pixels around the hotspot");
        var tiltDisabled = new byte[plain.Length];
        renderer.Render(source, cursorScene with
        {
            CursorRotationRadians = .22,
            CursorAppearance = cursorSettings with { MotionTiltEnabled = false }
        }, tiltDisabled);
        Require(CountDifferent(still, tiltDisabled) == 0, "the cursor setting disables velocity-driven rotation");

        foreach (var style in new[] { ClickEffectStyle.Pulse, ClickEffectStyle.Ring, ClickEffectStyle.Ripple, ClickEffectStyle.Burst })
        {
            var early = new byte[plain.Length];
            var late = new byte[plain.Length];
            var styleSettings = cursorSettings with { ClickEffect = style, ClickScale = 1.2, ClickOpacity = .8 };
            renderer.Render(source, cursorScene with { CursorAppearance = styleSettings, ClickProgress = .1 }, early);
            renderer.Render(source, cursorScene with { CursorAppearance = styleSettings, ClickProgress = .8 }, late);
            Require(CountDifferent(plain, early) > 20, $"{style} draws a visible click effect");
            Require(CountDifferent(early, late) > 5, $"{style} changes geometry or opacity as the click advances");
        }

        var smallClick = new byte[plain.Length];
        var largeClick = new byte[plain.Length];
        renderer.Render(source, cursorScene with
        {
            CursorAppearance = cursorSettings with { ClickEffect = ClickEffectStyle.Pulse, ClickScale = .5, ClickOpacity = 1 },
            ClickProgress = .35
        }, smallClick);
        renderer.Render(source, cursorScene with
        {
            CursorAppearance = cursorSettings with { ClickEffect = ClickEffectStyle.Pulse, ClickScale = 2, ClickOpacity = 1 },
            ClickProgress = .35
        }, largeClick);
        Require(CountDifferent(smallClick, largeClick) > 20, "click scale changes the rendered effect footprint");

        var dimClick = new byte[plain.Length];
        var brightClick = new byte[plain.Length];
        renderer.Render(source, cursorScene with
        {
            CursorAppearance = cursorSettings with { ClickEffect = ClickEffectStyle.Ripple, ClickOpacity = .05 },
            ClickProgress = .35
        }, dimClick);
        renderer.Render(source, cursorScene with
        {
            CursorAppearance = cursorSettings with { ClickEffect = ClickEffectStyle.Ripple, ClickOpacity = 1 },
            ClickProgress = .35
        }, brightClick);
        Require(CountDifferent(dimClick, brightClick) > 5, "click opacity changes the rendered effect intensity");

        var disabled = new byte[plain.Length];
        renderer.Render(source, cursorScene with
        {
            CursorAppearance = cursorSettings with { ClickEffect = ClickEffectStyle.None },
            ClickProgress = .2,
            ClickStrength = 1
        }, disabled);
        Require(CountDifferent(still, disabled) == 0, "None suppresses both the new click track and legacy click strength");
    }

    private static int CountDifferent(byte[] first, byte[] second)
    {
        var count = 0;
        for (var i = 0; i < first.Length; i += 4)
            if (first[i] != second[i] || first[i + 1] != second[i + 1] || first[i + 2] != second[i + 2]) count++;
        return count;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Cursor dynamics render probe failed: " + message);
    }
}
