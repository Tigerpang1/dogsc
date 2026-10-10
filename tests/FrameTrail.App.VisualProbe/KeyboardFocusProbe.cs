using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FrameTrail.App;

namespace FrameTrail.App.VisualProbe;

/// <summary>Renders the FocusVisualStyle templates resolved by real controls without opening a desktop window.</summary>
internal static class KeyboardFocusProbe
{
    public static void Verify(MainWindow window, EditorTimelineControl timeline, string theme, string output, Action<bool, string, object?> check)
    {
        var fallback = (Style)window.FindResource(SystemParameters.FocusVisualStyleKey);
        var fallbackFocus = RenderStyle(fallback, 120, 42);
        Save(fallbackFocus, Path.Combine(output, theme + "-focus-theme-fallback.png"));
        check(HasSolidHorizontalRun(fallbackFocus, 12, 108, 1, .78),
            theme + ": theme fallback for otherwise unstyled focus targets is a continuous solid line", null);
        var button = new Button { Content = "focus sample" };
        var defaultStyle = button.FocusVisualStyle;
        var buttonFocus = RenderStyle(defaultStyle, 120, 42);
        Save(buttonFocus, Path.Combine(output, theme + "-focus-default.png"));
        check(buttonFocus is not null && HasSolidHorizontalRun(buttonFocus, 12, buttonFocus.PixelWidth - 12, 1, .78),
            theme + ": actual Button focus style renders as a continuous solid line",
            new { Style = defaultStyle?.TargetType?.Name, Size = buttonFocus is null ? "unavailable" : $"{buttonFocus.PixelWidth}x{buttonFocus.PixelHeight}" });

        var dotted = RenderDottedNegativeControl(120, 42);
        Save(dotted, Path.Combine(output, theme + "-focus-dotted-negative.png"));
        check(!HasSolidHorizontalRun(dotted, 8, dotted.PixelWidth - 8, 2, .78),
            theme + ": negative control detects the old dotted focus appearance",
            new { SolidLineRatio = HorizontalRunRatio(dotted, 8, dotted.PixelWidth - 8, 2) });

        var timelineFocus = RenderStyle(timeline.FocusVisualStyle, (int)timeline.ActualWidth, (int)timeline.ActualHeight);
        Save(timelineFocus, Path.Combine(output, theme + "-focus-timeline.png"));
        var timelinePixelsInside = CountVisible(timelineFocus, new Rect(5, 2, 145, 40));
        var timelinePixelsOutside = CountVisible(timelineFocus, new Rect(155, 0, timelineFocus.PixelWidth - 155, timelineFocus.PixelHeight));
        check(timelinePixelsInside >= 20 && timelinePixelsOutside == 0,
            theme + ": timeline focus marks only the upper-left track heading, without a large perimeter",
            new { timelinePixelsInside, timelinePixelsOutside, Size = $"{timelineFocus.PixelWidth}x{timelineFocus.PixelHeight}" });

        var curve = new MotionCurveEditor();
        var curveFocus = RenderStyle(curve.FocusVisualStyle, 260, 154);
        Save(curveFocus, Path.Combine(output, theme + "-focus-curve.png"));
        check(CountVisible(curveFocus, new Rect(0, 0, curveFocus.PixelWidth, curveFocus.PixelHeight)) >= 20,
            theme + ": motion curve focus template produces visible focus pixels", null);

        var splitter = Descendants((DependencyObject)window.Content).OfType<GridSplitter>().First();
        var splitterFocus = RenderStyle(splitter.FocusVisualStyle, 500, 12);
        Save(splitterFocus, Path.Combine(output, theme + "-focus-resize.png"));
        check(CountVisible(splitterFocus, new Rect(210, 2, 80, 8)) >= 15 &&
              CountVisible(splitterFocus, new Rect(0, 0, 190, 12)) == 0,
            theme + ": timeline resize focus is a short centered grip marker", null);

        var scroll = (ScrollViewer)window.FindName("InspectorScroll");
        check(!scroll.Focusable && !scroll.IsTabStop,
            theme + ": inspector ScrollViewer does not take an extra keyboard tab stop",
            new { scroll.Focusable, scroll.IsTabStop });
    }

    private static BitmapSource RenderStyle(Style? style, int width, int height)
    {
        var template = FindTemplate(style);
        if (template is null) throw new InvalidOperationException("FocusVisualStyle has no ControlTemplate.");
        var host = new Control { Template = template, Width = width, Height = height };
        host.ApplyTemplate();
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        bitmap.Freeze();
        return bitmap;
    }

    private static ControlTemplate? FindTemplate(Style? style)
    {
        for (var current = style; current is not null; current = current.BasedOn)
            if (current.Setters.OfType<Setter>().FirstOrDefault(setter => setter.Property == Control.TemplateProperty)?.Value is ControlTemplate template)
                return template;
        return null;
    }

    private static BitmapSource RenderDottedNegativeControl(int width, int height)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var pen = new Pen(Brushes.Black, 1.5) { DashStyle = DashStyles.Dot };
            context.DrawLine(pen, new Point(1, 2), new Point(width - 1, 2));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static bool HasSolidHorizontalRun(BitmapSource bitmap, int left, int right, int y, double minimumRatio) =>
        HorizontalRunRatio(bitmap, left, right, y) >= minimumRatio;

    private static double HorizontalRunRatio(BitmapSource bitmap, int left, int right, int y)
    {
        left = Math.Clamp(left, 0, bitmap.PixelWidth - 1);
        right = Math.Clamp(right, left + 1, bitmap.PixelWidth);
        y = Math.Clamp(y, 0, bitmap.PixelHeight - 1);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        var visible = 0;
        for (var x = left; x < right; x++)
        {
            var i = (y * bitmap.PixelWidth + x) * 4;
            if (pixels[i + 3] >= 180) visible++;
        }
        return visible / (double)(right - left);
    }

    private static int CountVisible(BitmapSource bitmap, Rect region)
    {
        var left = Math.Clamp((int)Math.Floor(region.Left), 0, bitmap.PixelWidth);
        var top = Math.Clamp((int)Math.Floor(region.Top), 0, bitmap.PixelHeight);
        var right = Math.Clamp((int)Math.Ceiling(region.Right), left, bitmap.PixelWidth);
        var bottom = Math.Clamp((int)Math.Ceiling(region.Bottom), top, bitmap.PixelHeight);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        var count = 0;
        for (var y = top; y < bottom; y++)
        for (var x = left; x < right; x++)
            if (pixels[(y * bitmap.PixelWidth + x) * 4 + 3] >= 180) count++;
        return count;
    }

    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
