using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FrameTrail;
using FrameTrail.App;

namespace FrameTrail.App.VisualProbe;

internal static class TimelinePlayheadRetentionProbe
{
    private const int LabelWidth = 152;

    public static void Verify(Action<bool, string, object?> check)
    {
        var control = new EditorTimelineControl
        {
            Project = new FrameTrailProject
            {
                Media = new() { Width = 1280, Height = 720, Duration = 10, FrameRate = 30 },
                Timeline = new() { Segments = [new() { SourceDuration = 10 }] }
            }
        };
        control.Resources["Surface"] = Brushes.White;
        control.Resources["Chrome"] = Brushes.WhiteSmoke;
        control.Resources["Ink"] = Brushes.Black;
        control.Resources["Secondary"] = Brushes.DimGray;
        control.Resources["Hairline"] = Brushes.LightGray;
        control.Resources["Accent"] = Brushes.Blue;
        control.Width = 1152;
        control.Refresh();
        control.Measure(new Size(control.Width, 240));
        control.Arrange(new Rect(0, 0, control.Width, control.Height));
        control.UpdateLayout();

        var visual = new DrawingVisual();
        RenderContent(control, visual);
        var initialPasses = control.RenderPassCount;
        var initial = Capture(visual, control);
        var firstX = PlayheadX(control);

        control.Position = 7.25;
        var moved = Capture(visual, control);
        var secondX = PlayheadX(control);
        var movedBounds = DifferenceBounds(initial, moved, control.ActualWidth, control.ActualHeight);
        check(control.RenderPassCount == initialPasses,
            "Position 更新 retained playhead transform 时不重新执行整个时间线 OnRender",
            new { Before = initialPasses, After = control.RenderPassCount });
        check(movedBounds.Count > 0 && Math.Abs(movedBounds.MinX - firstX) < 8 && Math.Abs(movedBounds.MaxX - secondX) < 8,
            "只更新 Position 后输出像素中的播放头从旧位置移动到新位置",
            new { FirstX = firstX, SecondX = secondX, movedBounds });

        control.Position = 2.5;
        control.Zoom = 2;
        control.Offset = 35;
        control.InvalidateVisual();
        RenderContent(control, visual);
        var zoomed = Capture(visual, control);
        var zoomX = PlayheadX(control);
        control.Position = 100;
        var zoomWithoutPlayhead = Capture(visual, control);
        var zoomBounds = DifferenceBounds(zoomWithoutPlayhead, zoomed, control.ActualWidth, control.ActualHeight);
        control.Position = 2.5;
        check(control.RenderPassCount == initialPasses + 1 && NearPlayheadColumn(zoomBounds, zoomX),
            "静态重绘后 playhead 与缩放及水平偏移保持对齐",
            new { ExpectedX = zoomX, RenderPasses = control.RenderPassCount, PixelBounds = zoomBounds });

        control.Width = 900;
        control.Measure(new Size(control.Width, 240));
        control.Arrange(new Rect(0, 0, control.Width, control.Height));
        control.UpdateLayout();
        control.InvalidateVisual();
        RenderContent(control, visual);
        var resized = Capture(visual, control);
        var resizedX = PlayheadX(control);
        control.Position = 100;
        var resizedWithoutPlayhead = Capture(visual, control);
        var resizedBounds = DifferenceBounds(resizedWithoutPlayhead, resized, control.ActualWidth, control.ActualHeight);
        control.Position = 2.5;
        check(NearPlayheadColumn(resizedBounds, resizedX),
            "静态重绘后 playhead 使用 resize 后的控件宽度",
            new { ExpectedX = resizedX, Width = control.ActualWidth, PixelBounds = resizedBounds });

        control.Resources["Ink"] = Brushes.Red;
        control.InvalidateVisual();
        RenderContent(control, visual);
        var themed = Capture(visual, control);
        var redPixels = RedPixelsAtColumn(themed, control, resizedX);
        check(redPixels >= 20,
            "主题画笔更新后重绘使用新的 playhead 颜色",
            new { ExpectedX = resizedX, RedPixels = redPixels });
    }

    private static double PlayheadX(EditorTimelineControl control) =>
        LabelWidth + control.Position * Math.Max(1, control.ActualWidth - LabelWidth) * control.Zoom / Math.Max(control.Project?.Timeline.Duration ?? 1, .01) - control.Offset;

    private static void RenderContent(EditorTimelineControl control, DrawingVisual visual)
    {
        using var context = visual.RenderOpen();
        typeof(EditorTimelineControl).GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(control, [context]);
    }

    private static BitmapSource Capture(DrawingVisual visual, EditorTimelineControl control)
    {
        var bitmap = new RenderTargetBitmap((int)control.ActualWidth, (int)control.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static (int MinX, int MaxX, int Count) DifferenceBounds(BitmapSource before, BitmapSource after, double width, double height)
    {
        var stride = (int)width * 4;
        var a = new byte[stride * (int)height];
        var b = new byte[a.Length];
        before.CopyPixels(a, stride, 0);
        after.CopyPixels(b, stride, 0);
        var minX = (int)width;
        var maxX = -1;
        var count = 0;
        for (var y = 0; y < (int)height; y++)
            for (var x = 0; x < (int)width; x++)
            {
                var index = y * stride + x * 4;
                if (a[index] == b[index] && a[index + 1] == b[index + 1] && a[index + 2] == b[index + 2] && a[index + 3] == b[index + 3]) continue;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                count++;
            }
        return (minX, maxX, count);
    }

    private static bool NearPlayheadColumn((int MinX, int MaxX, int Count) bounds, double expectedX)
        => bounds.Count > 0 && Math.Abs(bounds.MinX - expectedX) < 8 && Math.Abs(bounds.MaxX - expectedX) < 8;

    private static int RedPixelsAtColumn(BitmapSource bitmap, EditorTimelineControl control, double x)
    {
        var red = 0;
        for (var y = 5; y < Math.Min(130, (int)control.ActualHeight); y++)
        {
            var pixel = PixelAt(bitmap, (int)Math.Round(x), y, control.ActualWidth);
            if (pixel.R > pixel.G * 1.5 && pixel.R > pixel.B * 1.5) red++;
        }
        return red;
    }

    private static (byte R, byte G, byte B) PixelAt(BitmapSource bitmap, int x, int y, double width)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(Math.Clamp(x, 0, (int)width - 1), y, 1, 1), pixel, 4, 0);
        return (pixel[2], pixel[1], pixel[0]);
    }
}
