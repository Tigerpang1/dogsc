using FrameTrail;
using FrameTrail.Media;

internal static class CameraMotionRenderProbe
{
    internal static void Run()
    {
        var sourceWidth = 16;
        var sourceHeight = 8;
        var outputWidth = 96;
        var outputHeight = 96;
        var cameraPixels = new byte[sourceWidth * sourceHeight * 4];
        var source = new byte[cameraPixels.Length];
        for (var y = 0; y < sourceHeight; y++)
        for (var x = 0; x < sourceWidth; x++)
        {
            var at = (y * sourceWidth + x) * 4;
            source[at] = 24; source[at + 1] = 24; source[at + 2] = 24; source[at + 3] = 255;
            cameraPixels[at] = 24;
            cameraPixels[at + 1] = (byte)(y * 24);
            cameraPixels[at + 2] = (byte)(x * 16);
            cameraPixels[at + 3] = 255;
        }

        var crop = SceneAssetLoader.ComputeCameraFillCrop(NormalizedRect.Full, 2, 1);
        Near(crop.X, .25, "circle camera source crop x");
        Near(crop.Width, .5, "circle camera source crop width");
        Near(crop.Y, 0, "circle camera source crop y");
        Near(crop.Height, 1, "circle camera source crop height");

        var scene = new FrameScene(0, 0, new(0, 0), new(1, 1), 1, null, 0, "#202124", 0, 0, outputWidth, outputHeight, 0,
            SourceWidth: sourceWidth, SourceHeight: sourceHeight);
        var backgroundOnly = new byte[outputWidth * outputHeight * 4];
        var rendered = new byte[backgroundOnly.Length];
        using var renderer = new GpuFrameRenderer(sourceWidth, sourceHeight, outputWidth, outputHeight);
        renderer.Render(source, scene, backgroundOnly);
        var camera = new FrameLayerInput(sourceWidth, sourceHeight, cameraPixels, new(.25, .25, .5, .5), Opacity: .75,
            CornerRadius: .22, SourceCrop: crop, Shape: CameraShape.RoundedRectangle);
        renderer.Render(source, scene, rendered, [camera]);

        var center = Pixel(rendered, outputWidth, 48, 48);
        var baseCenter = Pixel(backgroundOnly, outputWidth, 48, 48);
        Check(center != baseCenter, "camera layer changes center pixels in the shared GPU scene renderer");
        Check(center.R is >= 95 and <= 160, "fill crop samples the centered source region instead of stretching the full image");
        var roundedCorner = Pixel(rendered, outputWidth, 25, 25);
        var baseCorner = Pixel(backgroundOnly, outputWidth, 25, 25);
        Check(roundedCorner == baseCorner, "rounded camera corner leaves the underlying scene visible");

        var opaque = new byte[rendered.Length];
        renderer.Render(source, scene, opaque, [camera with { Opacity = 1 }]);
        var opaqueCenter = Pixel(opaque, outputWidth, 48, 48);
        Check(opaqueCenter != center, "camera opacity changes composited output pixels");
    }

    private static (byte B, byte G, byte R) Pixel(byte[] image, int width, int x, int y)
    {
        var at = (y * width + x) * 4;
        return (image[at], image[at + 1], image[at + 2]);
    }

    private static void Near(double actual, double expected, string name)
    {
        if (!double.IsFinite(actual) || Math.Abs(actual - expected) > 1e-8)
            throw new InvalidOperationException($"Camera render probe failed ({name}): expected {expected}, got {actual}.");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("Camera render probe failed: " + name);
    }
}
