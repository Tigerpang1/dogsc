using FrameTrail;
using FrameTrail.Media;

internal static class LayerShadowRenderProbe
{
    internal static void Run()
    {
        const int width = 320, height = 180;
        var source = Solid(width, height, 255, 255, 255);
        var red = Solid(8, 8, 255, 0, 0);
        var blue = Solid(8, 8, 0, 0, 255);
        var scene = new FrameScene(0, 0, new(0, 0), new(1, 1), 1, null, 0, "#FFFFFF", 0, 0,
            width, height, 0, SourceWidth: width, SourceHeight: height);
        using var renderer = new GpuFrameRenderer(width, height, width, height);

        var plainLayer = new FrameLayerInput(8, 8, red, new(.3, .25, .25, .35), Layer: 0,
            CornerRadius: .12);
        var shadowLayer = plainLayer with
        {
            ShadowOpacity = .7,
            ShadowRadius = 48,
            ShadowOffsetX = 60,
            ShadowOffsetY = 120
        };
        var baseline = Render([plainLayer]);
        var zeroOpacityShadow = Render([plainLayer with { ShadowOpacity = 0, ShadowRadius = 48, ShadowOffsetX = 60, ShadowOffsetY = 120 }]);
        Require(baseline.AsSpan().SequenceEqual(zeroOpacityShadow), "zero shadow opacity is pixel-identical to the legacy layer renderer");

        var projected = Render([shadowLayer]);
        Require(Pixel(projected, 130, 70).R > 240 && Pixel(projected, 130, 70).G < 10, "the source layer remains in its original rectangle");
        var sourceOnlyPixel = Pixel(baseline, 146, 115);
        var shadowOnlyPixel = Pixel(projected, 146, 115);
        Require(sourceOnlyPixel.R > 245 && sourceOnlyPixel.G > 245, "the comparison point is white without a shadow");
        Require(shadowOnlyPixel.R < sourceOnlyPixel.R - 80 && shadowOnlyPixel.G < sourceOnlyPixel.G - 80,
            "the shadow is offset below the source layer");

        var centerShadow = Pixel(projected, 146, 115).R;
        var roundedCornerShadow = Pixel(projected, 185, 122).R;
        Require(roundedCornerShadow > centerShadow + 30 && roundedCornerShadow < 250,
            "rounded-corner shadow coverage softens near the shifted corner");
        var softEdge = Pixel(projected, 146, 130).R;
        Require(softEdge > centerShadow + 20 && softEdge < 250, "the shadow radius creates a soft edge");

        var rotated = Render([shadowLayer with { Rotation = 90, ShadowOffsetX = 60, ShadowOffsetY = 0, ShadowRadius = 12 }]);
        var rotatedOffset = Pixel(rotated, 130, 123);
        Require(rotatedOffset.R < 180 && rotatedOffset.G < 180, "shadow offset rotates with its layer in canvas coordinates");
        var rotatedCard = Render([plainLayer with { Rotation = 90 }]);
        Require(Pixel(rotatedCard, 130, 111).R > 240 && Pixel(rotatedCard, 130, 111).G < 10
            && Pixel(rotatedCard, 173, 76).G > 240, "rotation moves the entire non-square card silhouette without clipping it to its old rectangle");

        var occluder = new FrameLayerInput(8, 8, blue, new(.55, .59, .12, .15), Layer: 1, Opacity: 1);
        var occluded = Render([shadowLayer, occluder]);
        var coveredShadow = Pixel(occluded, 185, 122);
        Require(coveredShadow.B > 240 && coveredShadow.R < 10 && coveredShadow.G < 10,
            "a higher layer composites over and occludes the lower layer shadow");

        byte[] Render(IReadOnlyList<FrameLayerInput> layers)
        {
            var pixels = new byte[width * height * 4];
            renderer.Render(source, scene, pixels, layers);
            return pixels;
        }
    }

    private static byte[] Solid(int width, int height, byte red, byte green, byte blue)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = blue;
            pixels[i + 1] = green;
            pixels[i + 2] = red;
            pixels[i + 3] = 255;
        }
        return pixels;
    }

    private static (byte R, byte G, byte B) Pixel(byte[] pixels, int x, int y)
    {
        var offset = checked((y * 320 + x) * 4);
        return (pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Layer shadow render probe failed: " + message);
    }
}
