using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FrameTrail;
using FrameTrail.App;
using FrameTrail.App.VisualProbe;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.SingleOrDefault() ?? "artifacts/validation/theme-offscreen");
        if (Directory.Exists(output)) throw new IOException("Use a fresh directory for visual evidence.");
        Directory.CreateDirectory(output);
        var checks = new List<object>();
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        // Do not call Application.Run: StartupUri must never open the desktop window.
        MainWindow? window = null;
        try
        {
            VerifyPlacementGeometry();
            window = new MainWindow();
            var project = new FrameTrailProject
            {
                MediaPath = "unused-synthetic.mp4",
                Media = new() { Width = 1920, Height = 1080, Duration = 10, FrameRate = 60 },
                Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 10 }] },
                ZoomClips = [new() { Start = 1, End = 4, Scale = 1.6 }]
            };
            typeof(MainWindow).GetField("project", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, project);
            Invoke(window, "RefreshEditorShell");
            var root = (FrameworkElement)window.Content;
            var timeline = (EditorTimelineControl)window.FindName("TimelineView");
            foreach (var theme in new[] { "light", "dark" })
            {
                if (theme == "dark") Invoke(window, "ThemeClick", window, new RoutedEventArgs());
                Layout(root, 1280, 900);
                Check(window.IsVisible == false && new WindowInteropHelper(window).Handle == IntPtr.Zero,
                    theme + ": offscreen only, no native window created");
                var surface = ((SolidColorBrush)window.FindResource("Surface")).Color;
                foreach (var name in new[] { "InspectorTitle", "TimeLabel" })
                {
                    var label = (TextBlock)window.FindName(name);
                    var foreground = ((SolidColorBrush)label.Foreground).Color;
                    var ratio = Contrast(foreground, surface);
                    Check(ratio >= 4.5, $"{theme}: {name} primary-text contrast >= 4.5", new { Foreground = foreground.ToString(), Background = surface.ToString(), Ratio = ratio });
                }
                foreach (var name in new[] { "SnapBoundaries", "HoverPreview" })
                {
                    var checkbox = (CheckBox)window.FindName(name);
                    var label = Descendants(checkbox).OfType<TextBlock>().Single();
                    var foreground = ((SolidColorBrush)label.Foreground).Color;
                    var background = ((SolidColorBrush)window.FindResource("ControlSurface")).Color;
                    var ratio = Contrast(foreground, background);
                    Check(ratio >= 4.5, $"{theme}: {name} rendered label contrast >= 4.5", new { Foreground = foreground.ToString(), Background = background.ToString(), Ratio = ratio });
                }
                var sectionLabels = Descendants((StackPanel)window.FindName("InspectorHost")).OfType<TextBlock>()
                    .Where(t => t.Style == window.FindResource("SectionTitle")).ToArray();
                Check(sectionLabels.Length > 0 && sectionLabels.All(t => Contrast(((SolidColorBrush)t.Foreground).Color, surface) >= 4.5),
                    theme + ": live inspector section labels readable", new { Labels = sectionLabels.Select(t => t.Text).ToArray() });
                Save(root, Path.Combine(output, theme + "-workspace.png"));
                var bitmap = Save(timeline, Path.Combine(output, theme + "-timeline.png"));
                // Check actual drawn glyph pixels well inside the pastel zoom clip, excluding its outline.
                var scale = (timeline.ActualWidth - 152) / 10;
                var left = (int)(152 + scale + 8);
                var right = (int)(152 + 4 * scale - 8);
                var darkPixels = CountDarkPixels(bitmap, left, 145, right, 169);
                Check(darkPixels >= 12, theme + ": pastel effect clip contains dark, readable glyph pixels", new { darkPixels, left, right });
                EditorControlProbe.Verify(window, theme, output, Check);
                KeyboardFocusProbe.Verify(window, timeline, theme, output, Check);
            }
            var currentStyle = window.Style;
            window.ClearValue(FrameworkElement.StyleProperty);
            Layout(root, 1280, 900);
            var missingThemeColor = ((SolidColorBrush)((TextBlock)window.FindName("InspectorTitle")).Foreground).Color;
            var darkSurface = ((SolidColorBrush)window.FindResource("Surface")).Color;
            Check(Contrast(missingThemeColor, darkSurface) < 4.5,
                "negative control: removing the root theme reproduces and detects unreadable dark text",
                new { Foreground = missingThemeColor.ToString(), Ratio = Contrast(missingThemeColor, darkSurface) });
            window.Style = currentStyle;
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { Passed = true, Scope = "WPF offscreen layout and rendered pixels; no desktop, capture devices, or media decoder", Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS: {checks.Count} offscreen theme checks; {output}");
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { Passed = false, Checks = checks, Error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally { window?.Close(); app.Shutdown(); }

        void Check(bool passed, string name, object? detail = null)
        {
            checks.Add(new { Name = name, Passed = passed, Detail = detail });
            if (!passed) throw new InvalidOperationException(name);
        }

        void VerifyPlacementGeometry()
        {
            var negativeMonitorWork = new MainWindowPlacement.RectPx(-1920, -40, 0, 1040);
            var oversized = new MainWindowPlacement.RectPx(-2100, -180, 200, 1180);
            var centered = MainWindowPlacement.CenterAndFit(oversized, negativeMonitorWork, 32);
            Check(centered.Left >= negativeMonitorWork.Left + 32 && centered.Right <= negativeMonitorWork.Right - 32 &&
                  centered.Top >= negativeMonitorWork.Top + 32 && centered.Bottom <= negativeMonitorWork.Bottom - 32,
                "main-window initial placement fits a negative-origin monitor in physical pixels", centered);
            Check(Math.Abs((centered.Left + centered.Right) - (negativeMonitorWork.Left + negativeMonitorWork.Right)) <= 1 &&
                  Math.Abs((centered.Top + centered.Bottom) - (negativeMonitorWork.Top + negativeMonitorWork.Bottom)) <= 1,
                "initial placement centers within the usable monitor work area", centered);

            var fitting = new MainWindowPlacement.RectPx(-1700, 180, -740, 780);
            var fittingClamped = MainWindowPlacement.ClampAndFit(fitting, negativeMonitorWork, 32);
            Check(fittingClamped == fitting,
                "DPI clamp preserves a user-sized window that already fits", fittingClamped);

            var outside = new MainWindowPlacement.RectPx(120, 40, 3920, 2400);
            var clamped = MainWindowPlacement.ClampAndFit(outside, new MainWindowPlacement.RectPx(0, 0, 3840, 2064), 32);
            Check(clamped.Left == 32 && clamped.Top == 32 && clamped.Right == 3808 && clamped.Bottom == 2032,
                "DPI clamp shrinks and repositions an overflowing window without leaving the work area", clamped);
        }
    }

    private static void Invoke(MainWindow window, string method, params object[] args) =>
        typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);

    private static void Layout(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
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

    private static RenderTargetBitmap Save(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        if (element is EditorTimelineControl)
        {
            // This control draws its entire contents in OnRender. Copy its local drawing
            // without the parent layout offset, so pixel assertions cannot sample the gutter.
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen()) context.DrawDrawing(VisualTreeHelper.GetDrawing(element));
            bitmap.Render(drawing);
        }
        else bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return bitmap;
    }

    private static int CountDarkPixels(BitmapSource bitmap, int left, int top, int right, int bottom)
    {
        var rectangle = new Int32Rect(Math.Max(0, left), top, Math.Min(bitmap.PixelWidth, right) - Math.Max(0, left), bottom - top);
        var stride = rectangle.Width * 4;
        var pixels = new byte[stride * rectangle.Height];
        bitmap.CopyPixels(rectangle, pixels, stride, 0);
        var count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
            if (pixels[i + 3] >= 250 && pixels[i] < 110 && pixels[i + 1] < 110 && pixels[i + 2] < 110) count++;
        return count;
    }

    private static double Contrast(Color foreground, Color background)
    {
        static double Linear(byte value) { var c = value / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
        static double Luma(Color color) => .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        var a = Luma(foreground); var b = Luma(background);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
}
