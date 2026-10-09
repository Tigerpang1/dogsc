using FrameTrail;
using FrameTrail.Media;

internal static class RecordedCursorRenderProbe
{
    internal static void Run()
    {
        const int sourceWidth = 320, sourceHeight = 180, outputWidth = 640, outputHeight = 360;
        var source = new byte[sourceWidth * sourceHeight * 4];
        for (var i = 0; i < source.Length; i += 4)
        {
            source[i] = 72; source[i + 1] = 88; source[i + 2] = 108; source[i + 3] = 255;
        }
        var outputLength = outputWidth * outputHeight * 4;
        var baseScene = new FrameScene(0, 0, new(0, 0), new(1, 1), 1, null, 0, "#202124", 0, 0,
            outputWidth, outputHeight, 0, SourceWidth: sourceWidth, SourceHeight: sourceHeight)
        {
            Cursor = new(.5, .5),
            CursorAppearance = new CursorSettings { Visible = true, Style = CursorStyle.FollowRecorded, Scale = 1.2, ClickEffect = ClickEffectStyle.None }
        };
        using var renderer = new GpuFrameRenderer(sourceWidth, sourceHeight, outputWidth, outputHeight);
        var rasters = SystemCursorAssetLibrary.All;
        foreach (var id in new[] { "system.arrow", "system.hand", "system.ibeam" })
        {
            var raster = rasters[id];
            Require(raster.Width > 0 && raster.Height > 0 && raster.HotspotX >= 0 && raster.HotspotX < raster.Width
                && raster.HotspotY >= 0 && raster.HotspotY < raster.Height, $"{id} has a valid cropped hotspot");
            var hasAlpha = false;
            for (var i = 3; i < raster.Bgra.Length; i += 4) hasAlpha |= raster.Bgra[i] > 8;
            Require(hasAlpha, $"{id} raster has visible alpha pixels");
        }
        var plain = new byte[outputLength];
        renderer.Render(source, baseScene with { Cursor = null }, plain);

        var arrow = Draw("system.arrow");
        var hand = Draw("system.hand");
        var beam = Draw("system.ibeam");
        var fixedHand = DrawFixed(CursorStyle.SystemHand);
        var fixedText = DrawFixed(CursorStyle.SystemText);
        Require(DifferentPixels(plain, arrow) > 30, "recorded system arrow raster is drawn");
        Require(DifferentPixels(plain, hand) > 30, "recorded system hand raster is drawn");
        Require(DifferentPixels(plain, beam) > 30, "recorded vector I-beam is drawn");
        Require(DifferentPixels(arrow, hand) > 20, "cursor shape changes while pointer location stays fixed");
        Require(DifferentPixels(arrow, beam) > 20 && DifferentPixels(hand, beam) > 20, "all three recorded cursor kinds remain distinct");
        Require(DifferentPixels(hand, fixedHand) == 0, "fixed system-hand style uses the native hand cursor raster");
        Require(DifferentPixels(beam, fixedText) == 0, "fixed system-text style uses the I-beam raster");

        var legacyNull = Draw(null);
        Require(DifferentPixels(arrow, legacyNull) == 0, "legacy pointer events without a cursor asset fall back to the system arrow");
        var legacyHandAlias = Draw("hand");
        Require(DifferentPixels(hand, legacyHandAlias) == 0, "legacy importer hand identifier resolves to the system hand");

        var tilted = Draw("system.hand", rotation: .22);
        Require(DifferentPixels(hand, tilted) > 8, "recorded cursor still follows motion tilt around its hotspot");
        var click = Draw("system.hand", clickProgress: .25, clickStyle: ClickEffectStyle.Ripple);
        Require(DifferentPixels(hand, click) > 12, "recorded cursor still composes click feedback");

        byte[] Draw(string? asset, double rotation = 0, double? clickProgress = null, ClickEffectStyle clickStyle = ClickEffectStyle.None)
        {
            var pixels = new byte[outputLength];
            renderer.Render(source, baseScene with
            {
                CursorAsset = asset,
                CursorRotationRadians = rotation,
                ClickProgress = clickProgress,
                CursorAppearance = baseScene.CursorAppearance! with { ClickEffect = clickStyle }
            }, pixels);
            return pixels;
        }

        byte[] DrawFixed(CursorStyle style)
        {
            var pixels = new byte[outputLength];
            renderer.Render(source, baseScene with
            {
                CursorAsset = null,
                CursorAppearance = baseScene.CursorAppearance! with { Style = style }
            }, pixels);
            return pixels;
        }
    }

    private static int DifferentPixels(byte[] a, byte[] b)
    {
        var count = 0;
        for (var i = 0; i < a.Length; i += 4)
            if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2]) count++;
        return count;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Recorded cursor render probe failed: " + message);
    }
}
