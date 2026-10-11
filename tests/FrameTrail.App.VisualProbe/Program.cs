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
        var textBoxEditMenuOnly = args.FirstOrDefault() == "--textbox-edit-menu";
        var saveFailureOnly = args.FirstOrDefault() == "--save-failure";
        var preferencesExportOnly = args.FirstOrDefault() == "--preferences-export";
        var recordingHotKeyOnly = args.FirstOrDefault() == "--recording-hotkey";
        var windowSourceFilterOnly = args.FirstOrDefault() == "--window-source-filter";
        var microphoneAccessOnly = args.FirstOrDefault() == "--microphone-access-recovery";
        var bundledResourcesOnly = args.FirstOrDefault() == "--bundled-resources";
        var errorRecoveryOnly = args.FirstOrDefault() == "--error-recovery";
        var desktopInstanceOnly = args.FirstOrDefault() == "--desktop-instance";
        var desktopSurfacesOnly = args.FirstOrDefault() == "--desktop-surfaces";
        var outputArgument = textBoxEditMenuOnly || saveFailureOnly || preferencesExportOnly || recordingHotKeyOnly || windowSourceFilterOnly || microphoneAccessOnly || bundledResourcesOnly || errorRecoveryOnly || desktopInstanceOnly || desktopSurfacesOnly ? args.Skip(1).SingleOrDefault() : args.SingleOrDefault();
        var output = Path.GetFullPath(outputArgument ?? (preferencesExportOnly
            ? "artifacts/validation/noncamera-closeout-v4/preferences-export"
            : saveFailureOnly
            ? "artifacts/validation/save-failure-offscreen"
            : errorRecoveryOnly ? "artifacts/validation/error-recovery-offscreen"
            : bundledResourcesOnly ? "artifacts/validation/bundled-resource-locator-offscreen"
            : windowSourceFilterOnly ? "artifacts/validation/window-source-filter-offscreen"
            : textBoxEditMenuOnly ? "artifacts/validation/textbox-edit-menu-offscreen" : "artifacts/validation/theme-offscreen"));
        if (Directory.Exists(output)) throw new IOException("Use a fresh directory for visual evidence.");
        Directory.CreateDirectory(output);
        var checks = new List<object>();
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        // Do not call Application.Run: StartupUri must never open the desktop window.
        MainWindow? window = null;
        try
        {
            if (desktopSurfacesOnly)
            {
                DesktopSurfacesProbe.Verify(output, Check);
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
                {
                    Passed = true,
                    Scope = "独立录制浮窗真实工厂的浅深布局、入口、无Owner与无HWND检查；不代表托盘或真实录制验收",
                    Checks = checks
                }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"PASS: {checks.Count} desktop surface checks; {output}");
                return 0;
            }
            if (desktopInstanceOnly)
            {
                DesktopInstanceProbe.Verify(Check);
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
                {
                    Passed = true,
                    Scope = "隔离会话标识的单实例 IPC、参数与确认、无效请求超时和恢复；不创建托盘或桌面窗口",
                    Checks = checks
                }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"PASS: {checks.Count} desktop IPC checks; {output}");
                return 0;
            }
            if (errorRecoveryOnly)
            {
                AppErrorPresentationProbe.Verify(output, Check);
                MemoSaveRecoveryProbe.Verify(output, Check);
                MemoTeleprompterProbe.Verify(output, Check);
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
                {
                    Passed = true,
                    Scope = "全局错误壳与重入保护、备忘录写盘失败和恢复、浅深主题像素透明度及隐藏 HWND 合成配置；只使用临时文稿，不显示或激活窗口，不替代实机关闭重开或录屏排除验收",
                    Checks = checks
                }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"PASS: {checks.Count} error recovery checks; {output}");
                return 0;
            }
            if (bundledResourcesOnly)
            {
                BundledResourceLocatorProbe.Verify(Check);
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
                {
                    Passed = true,
                    Scope = "仅验证安装版与便携包资源路径、缺失和空许可证处理；使用临时夹具，不打开文件管理器或系统 UI",
                    Checks = checks
                }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"PASS: {checks.Count} bundled resource locator checks; {output}");
                return 0;
            }
            if (microphoneAccessOnly)
            {
                MicrophoneAccessRecoveryProbe.Verify(output, Check);
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
                {
                    Passed = true,
                    Scope = "麦克风权限恢复提示：浅深主题离屏布局、动作与默认按钮；未打开系统设置或音频设备，不替代实机拒绝/恢复验收",
                    Checks = checks
                }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"PASS: {checks.Count} microphone recovery checks; {output}");
                return 0;
            }
            if (windowSourceFilterOnly)
            {
                WindowCaptureSourceFilterProbe.Verify(Check);
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
                {
                    Passed = true,
                    Scope = "窗口来源过滤纯函数：按当前进程 ID 和主窗口句柄排除 FrameTrail 自身浮窗，支持主编辑器开关并保留外部同名窗口",
                    Checks = checks
                }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"PASS: {checks.Count} window capture source filter checks; {output}");
                return 0;
            }
            if (recordingHotKeyOnly)
            {
                RecordingMarkerHotKeyProbe.Verify(Check);
                RecordingBarPresentationProbe.Verify(output, Check);
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
                {
                    Passed = true,
                    Scope = "快捷键冲突、备用键、释放与重试；录制条三种快捷键状态的离屏布局，不代表实机热键验收",
                    Checks = checks
                }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"PASS: {checks.Count} recording hotkey and offscreen bar checks; {output}");
                return 0;
            }
            MainMediaCoverageProbe.Verify(Check);
            TextBoxEditMenuProbe.Verify(app, Check);
            if (textBoxEditMenuOnly)
            {
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
                {
                    Passed = true,
                    Scope = "TextBox edit-menu resources and command-target binding; unshown WPF controls; no HWND or desktop automation",
                    Checks = checks
                }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"PASS: {checks.Count} offscreen TextBox edit-menu checks; {output}");
                return 0;
            }
            VerifyPlacementGeometry();
            window = new MainWindow();
            if (preferencesExportOnly)
            {
                PreferencesExportProbe.Verify(window, output, Check);
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
                {
                    Passed = true,
                    Scope = "真实 App 偏好应用路径与 ExportEngine 成片 framemd5 对照；隐藏窗口，不创建 HWND，不写用户设置",
                    Checks = checks
                }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"PASS: {checks.Count} offscreen preferences/export checks; {output}");
                return 0;
            }
            PreferenceValidationProbe.Verify(window, Check);
            if (saveFailureOnly)
            {
                SaveFailureProbe.Verify(window, Check);
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
                {
                    Passed = true,
                    Scope = "Save failure model, control, history, and project-file state; unshown WPF window; no HWND or desktop automation",
                    Checks = checks
                }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"PASS: {checks.Count} offscreen save-failure checks; {output}");
                return 0;
            }
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
