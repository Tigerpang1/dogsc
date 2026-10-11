using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FrameTrail.App;

internal static class MemoTeleprompterProbe
{
    private static readonly string[] ThemeKeys = ["Workspace", "Surface", "Chrome", "Ink", "Secondary", "Hairline", "ControlSurface", "Selection", "Accent", "Dot"];

    internal static void Verify(string output, Action<bool, string, object?> check)
    {
        var legacy = new[] { 22d, .92, 390, 420, 20, 40 };
        var oldSettings = MemoStorage.ReadTeleprompterSettings(legacy, defaultEditing: true, defaultSpeed: 45);
        check(oldSettings.IsEditing && oldSettings.ScrollSpeed == 45,
            "legacy six-value memo settings load with safe teleprompter defaults", oldSettings);

        var extended = MemoStorage.CreateSettings(24, .8, 390, 420, 20, 40, isEditing: false, scrollSpeed: 76.5);
        var restored = MemoStorage.ReadTeleprompterSettings(extended, defaultEditing: true, defaultSpeed: 45);
        check(extended.Take(6).SequenceEqual(new[] { 24d, .8, 390d, 420d, 20d, 40d })
              && !restored.IsEditing && restored.ScrollSpeed == 76.5,
            "extended memo settings append mode and speed without shifting the legacy six values", new { extended, restored });

        var normalStep = MemoAutoScroll.Advance(120, 600, 80, .25);
        check(Math.Abs(normalStep.Offset - 140) < .001 && !normalStep.ReachedEnd,
            "teleprompter advances by elapsed time multiplied by speed", normalStep);
        var finalStep = MemoAutoScroll.Advance(590, 600, 80, .25);
        check(finalStep.Offset == 600 && finalStep.ReachedEnd,
            "teleprompter stops exactly at the end of a long script", finalStep);
        var pausedStep = MemoAutoScroll.Advance(180, 600, 80, 0);
        check(pausedStep.Offset == 180 && !pausedStep.ReachedEnd,
            "paused teleprompter makes no progress", pausedStep);
        var bounded = MemoAutoScroll.Advance(-10, 600, double.NaN, double.PositiveInfinity);
        check(bounded.Offset == 0 && !bounded.ReachedEnd,
            "invalid timing values cannot produce a runaway scroll step", bounded);

        VerifyThemeLayouts(output, check);
        VerifyHiddenHandle(output, check);
    }

    private static void VerifyHiddenHandle(string output, Action<bool, string, object?> check)
    {
        var fixture = Path.Combine(Path.GetTempPath(), "FrameTrail-Teleprompter-Hwnd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var scriptPath = Path.Combine(fixture, "memo.txt");
        File.WriteAllText(scriptPath, "仅用于隐藏 HWND 分层透明诊断的临时文稿。");
        MemoWindow? memo = null;
        var errors = new List<Exception>();
        try
        {
            memo = new MemoWindow(scriptPath, (_, _, _) => { }, errors.Add) { Width = 410, Height = 400 };
            var root = (FrameworkElement)memo.Content;
            root.Measure(new Size(memo.Width, memo.Height));
            root.Arrange(new Rect(0, 0, memo.Width, memo.Height));
            root.UpdateLayout();

            var handle = new WindowInteropHelper(memo).EnsureHandle();
            var source = HwndSource.FromHwnd(handle) ?? throw new InvalidOperationException("EnsureHandle did not create an HwndSource.");
            var backgroundNodes = DescribeBackgrounds(memo, (Visual)memo.Content);
            var composition = source.CompositionTarget;
            check(!memo.IsVisible && handle != IntPtr.Zero && memo.AllowsTransparency && source.UsesPerPixelOpacity,
                "hidden memo HWND uses per-pixel opacity without showing or activating the window",
                new { memo.IsVisible, memo.AllowsTransparency, source.UsesPerPixelOpacity, HandleCreated = handle != IntPtr.Zero, memo.WindowStyle });
            check(composition is not null && composition.BackgroundColor.A == 0,
                "hidden memo HwndTarget composition background preserves transparent pixels",
                new { BackgroundColor = composition?.BackgroundColor.ToString(), CompositionTarget = composition?.GetType().FullName });
            check(backgroundNodes.Any(node => node.StartsWith("Window=#00", StringComparison.Ordinal))
                  && backgroundNodes.Any(node => node.StartsWith("Control:TextBox=#00", StringComparison.Ordinal))
                  && backgroundNodes.Any(node => node.StartsWith("Border=#FF", StringComparison.Ordinal)),
                "hidden memo visual tree background configuration is captured for the HWND diagnostic", backgroundNodes);
            check(errors.Count == 0, "hidden HWND diagnostic did not touch a user memo or report fixture errors", new { Errors = errors.Count });
        }
        finally
        {
            if (memo is null)
            {
                Directory.Delete(fixture, recursive: true);
            }
            else
            {
                try
                {
                    var handle = new WindowInteropHelper(memo).Handle;
                    memo.Close();
                    check(new WindowInteropHelper(memo).Handle == IntPtr.Zero,
                        "hidden diagnostic HWND is destroyed after closing the temporary memo", new { WasCreated = handle != IntPtr.Zero });
                }
                finally { if (Directory.Exists(fixture)) Directory.Delete(fixture, recursive: true); }
            }
        }
    }

    private static string[] DescribeBackgrounds(params DependencyObject[] roots)
    {
        var nodes = new List<string>();
        foreach (var root in roots) Visit(root);
        return nodes.ToArray();

        void Visit(DependencyObject node)
        {
            if (node is Window window) nodes.Add($"Window={DescribeBrush(window.Background)};Opacity={window.Opacity:0.##}");
            if (node is Border border) nodes.Add($"Border={DescribeBrush(border.Background)};Opacity={border.Opacity:0.##}");
            if (node is Panel panel) nodes.Add($"Panel={DescribeBrush(panel.Background)};Opacity={panel.Opacity:0.##}");
            if (node is Control control) nodes.Add($"Control:{control.GetType().Name}={DescribeBrush(control.Background)};Opacity={control.Opacity:0.##}");
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Visit(VisualTreeHelper.GetChild(node, i));
        }

        static string DescribeBrush(Brush? brush) => brush switch
        {
            SolidColorBrush solid => $"{solid.Color}*{solid.Opacity:0.##}",
            null => "null",
            _ => brush.GetType().Name
        };
    }

    private static void VerifyThemeLayouts(string output, Action<bool, string, object?> check)
    {
        var app = Application.Current ?? throw new InvalidOperationException("The teleprompter probe requires initialized WPF resources.");
        var priorTheme = ThemeKeys.ToDictionary(key => key, key => app.Resources.Contains(key) ? app.Resources[key] : null);
        MainWindow? themeOwner = null;
        try
        {
            themeOwner = new MainWindow();
            InvokeThemeClick(themeOwner);
            InvokeThemeClick(themeOwner);
            VerifyThemeLayout(output, check, dark: false);
            InvokeThemeClick(themeOwner);
            VerifyThemeLayout(output, check, dark: true);
        }
        finally
        {
            themeOwner?.Close();
            foreach (var (key, value) in priorTheme)
            {
                if (value is null) app.Resources.Remove(key);
                else app.Resources[key] = value;
            }
        }
    }

    private static void VerifyThemeLayout(string output, Action<bool, string, object?> check, bool dark)
    {
        var fixture = Path.Combine(Path.GetTempPath(), "FrameTrail-Teleprompter-Layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var scriptPath = Path.Combine(fixture, "memo.txt");
        File.WriteAllText(scriptPath, string.Join(Environment.NewLine,
            Enumerable.Range(1, 80).Select(index => $"第 {index} 段：这是用于检查长稿滚动、阅读排版和控制区布局的示例口播文案。")));
        MemoWindow? memo = null;
        var saveErrors = new List<Exception>();
        try
        {
            memo = new MemoWindow(scriptPath, (_, _, _) => { }, saveErrors.Add) { Width = 410, Height = 400 };
            memo.SetEditingMode(false);
            var savedValues = System.Text.Json.JsonSerializer.Deserialize<double[]>(File.ReadAllText(MemoStorage.SettingsPath(scriptPath)))!;
            var savedSettings = MemoStorage.ReadTeleprompterSettings(savedValues, true, 45);
            check(saveErrors.Count == 0 && savedValues.All(double.IsFinite) && !savedSettings.IsEditing && savedSettings.ScrollSpeed == 45,
                $"{ThemeName(dark)}: changing mode before first show saves a finite snapshot without errors", new { errors = saveErrors.Count, savedSettings, savedValues });
            var root = (FrameworkElement)memo.Content;
            root.Measure(new Size(memo.Width, memo.Height));
            root.Arrange(new Rect(0, 0, memo.Width, memo.Height));
            root.UpdateLayout();
            var footer = memo.FooterForProbe;
            var footerBounds = footer.TransformToAncestor(root).TransformBounds(new Rect(footer.RenderSize));
            var controls = new FrameworkElement[] { memo.ScrollButtonForProbe, memo.RewindButtonForProbe, memo.SpeedSliderForProbe, memo.SpeedLabelForProbe };
            var controlBounds = controls.Select(control => control.TransformToAncestor(footer).TransformBounds(new Rect(control.RenderSize))).ToArray();
            var fitsFooter = FooterFits(memo, root);
            check(memo.MinWidth >= 390 && memo.MinHeight >= 330 && memo.Title == "FrameTrail · 口播提词器",
                $"{ThemeName(dark)}: teleprompter has an explicit minimum size and product title", new { memo.MinWidth, memo.MinHeight, memo.Title });
            check(fitsFooter && footerBounds.Left >= 0 && footerBounds.Right <= root.ActualWidth + .5 && footerBounds.Bottom <= root.ActualHeight + .5,
                $"{ThemeName(dark)}: two-row playback footer fits at default size without clipping", new { footer.ActualWidth, footer.ActualHeight, footer.DesiredSize, footerBounds, controlBounds });
            check(memo.ReaderForProbe.ActualHeight > 0 && memo.ReaderForProbe.ScrollableHeight > 0 && memo.SpeedSliderForProbe.ActualWidth >= 100,
                $"{ThemeName(dark)}: long script remains scrollable and speed slider keeps a usable width", new { memo.ReaderForProbe.ActualHeight, memo.ReaderForProbe.ScrollableHeight, memo.SpeedSliderForProbe.ActualWidth });
            check(memo.Opacity == 1 && memo.AllowsTransparency && memo.WindowStyle == WindowStyle.None
                  && !memo.IsScrollingForProbe && new WindowInteropHelper(memo).Handle == IntPtr.Zero,
                $"{ThemeName(dark)}: text opacity stays one, scrolling starts paused, and render creates no HWND", null);

            foreach (var editing in new[] { true, false })
            {
                memo.SetEditingMode(editing);
                root.UpdateLayout();
                memo.SetBackgroundOpacityForProbe(.55);
                root.UpdateLayout();
                var translucentAlpha = RenderPixel(root, Math.Min(350, (int)root.ActualWidth - 8), Math.Min(200, (int)root.ActualHeight - 8)).A;
                memo.SetBackgroundOpacityForProbe(1);
                root.UpdateLayout();
                var opaqueAlpha = RenderPixel(root, Math.Min(350, (int)root.ActualWidth - 8), Math.Min(200, (int)root.ActualHeight - 8)).A;
                check(translucentAlpha < opaqueAlpha && translucentAlpha < 240 && opaqueAlpha > 245,
                    $"{ThemeName(dark)}: {(editing ? "editing" : "reading")} backdrop alpha follows the setting in rendered pixels", new { translucentAlpha, opaqueAlpha });
            }
            memo.SetBackgroundOpacityForProbe(.92);

            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, dark ? "teleprompter-dark.png" : "teleprompter-light.png"));
            encoder.Save(stream);

            memo.Height = memo.MinHeight;
            root.Measure(new Size(memo.Width, memo.Height));
            root.Arrange(new Rect(0, 0, memo.Width, memo.Height));
            root.UpdateLayout();
            check(FooterFits(memo, root),
                $"{ThemeName(dark)}: playback controls remain unclipped at the minimum window height", new { memo.Height, footer.ActualWidth, footer.ActualHeight, footer.DesiredSize });
        }
        finally
        {
            if (memo is null)
            {
                Directory.Delete(fixture, recursive: true);
            }
            else
            {
                var canClose = memo.TryCloseForProbe();
                check(canClose && saveErrors.Count == 0,
                    $"{ThemeName(dark)}: offscreen memo closes cleanly and fixture errors stay empty", new { canClose, errors = saveErrors.Count });
                if (canClose)
                {
                    var closed = false;
                    memo.Closed += (_, _) => closed = true;
                    memo.Close();
                    check(closed, $"{ThemeName(dark)}: temporary memo window reaches Closed before fixture cleanup", closed);
                    if (closed) Directory.Delete(fixture, recursive: true);
                }
            }
        }
    }

    private static bool FooterFits(MemoWindow memo, FrameworkElement root)
    {
        var footer = memo.FooterForProbe;
        var bounds = new FrameworkElement[] { memo.ScrollButtonForProbe, memo.RewindButtonForProbe, memo.SpeedSliderForProbe, memo.SpeedLabelForProbe }
            .Select(control => control.TransformToAncestor(footer).TransformBounds(new Rect(control.RenderSize)));
        return footer.ActualWidth > 0 && footer.ActualHeight > 0
            && footer.DesiredSize.Width <= footer.ActualWidth + footer.Margin.Left + footer.Margin.Right + 1
            && footer.DesiredSize.Height <= footer.ActualHeight + footer.Margin.Top + footer.Margin.Bottom + .5
            && bounds.All(rect => rect.Left >= -.5 && rect.Top >= -.5
                && rect.Right <= footer.ActualWidth + .5 && rect.Bottom <= footer.ActualHeight + .5)
            && footer.TransformToAncestor(root).TransformBounds(new Rect(footer.RenderSize)).Bottom <= root.ActualHeight + .5;
    }

    private static (byte B, byte G, byte R, byte A) RenderPixel(FrameworkElement root, int x, int y)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return (pixel[0], pixel[1], pixel[2], pixel[3]);
    }

    private static void InvokeThemeClick(MainWindow owner) =>
        typeof(MainWindow).GetMethod("ThemeClick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(owner, [owner, new RoutedEventArgs()]);

    private static string ThemeName(bool dark) => dark ? "dark" : "light";
}
