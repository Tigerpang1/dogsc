namespace FrameTrail.App;

internal static class PreviewSizing
{
    // Zero means fit the actual physical viewport, including per-monitor DPI.
    internal static (int Width, int Height) Output(int configuredWidth, int canvasWidth, int canvasHeight,
        double viewportWidthPixels, double viewportHeightPixels)
    {
        var aspect = Math.Max(1, canvasWidth) / (double)Math.Max(1, canvasHeight);
        var width = configuredWidth > 0 ? configuredWidth :
            viewportWidthPixels > 0 && viewportHeightPixels > 0
                ? Math.Min(viewportWidthPixels, viewportHeightPixels * aspect) : 1440;
        var scale = Math.Min(1, Math.Min(7680 / width, 7680 * aspect / width));
        width *= scale;
        return (Even(width), Even(width / aspect));
    }

    private static int Even(double value) => Math.Max(2, (int)Math.Ceiling(value / 2) * 2);
}
