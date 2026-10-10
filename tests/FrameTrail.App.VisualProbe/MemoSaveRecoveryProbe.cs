using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FrameTrail.App;

internal static class MemoSaveRecoveryProbe
{
    private static readonly string[] PaletteKeys = ["Workspace", "Surface", "Chrome", "Ink", "Secondary", "Hairline", "ControlSurface", "Selection", "Accent", "Dot"];

    internal static void Verify(string output, Action<bool, string, object?> check)
    {
        var app = Application.Current ?? throw new InvalidOperationException("The visual probe requires the WPF application resources to be initialized.");
        var priorPalette = PaletteKeys.ToDictionary(key => key, key => app.Resources.Contains(key) ? app.Resources[key] : null);
        MainWindow? themeOwner = null;
        try
        {
            themeOwner = new MainWindow();
            InvokeThemeClick(themeOwner);
            InvokeThemeClick(themeOwner);
            VerifyTheme(output, check, dark: false);
            InvokeThemeClick(themeOwner);
            VerifyTheme(output, check, dark: true);
        }
        finally
        {
            themeOwner?.Close();
            foreach (var (key, value) in priorPalette)
            {
                if (value is null) app.Resources.Remove(key);
                else app.Resources[key] = value;
            }
        }
    }

    private static void VerifyTheme(string output, Action<bool, string, object?> check, bool dark)
    {
        var fixtureDirectory = Path.Combine(Path.GetTempPath(), "FrameTrail-MemoSaveRecovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureDirectory);
        var textPath = Path.Combine(fixtureDirectory, "memo.txt");
        var settingsPath = MemoStorage.SettingsPath(textPath);
        const string oldText = "original memo text";
        const string pendingText = "待保存的录制提纲\n1. 介绍主要功能\n2. 展示编辑过程";
        var oldSettings = new double[] { 18, .93, 410, 320, 120, 90 };
        var savedSettings = JsonSerializer.Serialize(oldSettings);
        File.WriteAllText(textPath, oldText);
        File.WriteAllText(settingsPath, savedSettings);

        var notifications = new List<(string Message, string Title)>();
        var loggedErrors = new List<Exception>();
        var memo = new MemoWindow(textPath, (_, message, title) => notifications.Add((message, title)), loggedErrors.Add)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 120,
            Top = 90
        };
        try
        {
            memo.EditorForProbe.Text = pendingText;
            using (new FileStream(textPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                memo.SaveTextForProbe();
                check(File.ReadAllText(textPath) == oldText,
                    $"{ThemeName(dark)}: locked body file remains unchanged after autosave failure", File.ReadAllText(textPath));
                check(memo.SaveStatusForProbe.Visibility == Visibility.Visible
                      && memo.SaveStatusForProbe.Text.Contains("自动保存失败", StringComparison.Ordinal)
                      && !memo.AutoSaveTimerEnabledForProbe,
                    $"{ThemeName(dark)}: autosave failure is shown inline and the timer stops", memo.SaveStatusForProbe.Text);
                check(loggedErrors.Count == 1,
                    $"{ThemeName(dark)}: autosave fixture errors stay in the probe's in-memory log", loggedErrors.Count);
                check(FindTemporaryFiles(fixtureDirectory).Length == 0,
                    $"{ThemeName(dark)}: autosave failure removes its temporary file", FindTemporaryFiles(fixtureDirectory));
            }

            MeasureAndCapture(memo, output, dark, check);

            using (new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                check(!memo.TryCloseForProbe(),
                    $"{ThemeName(dark)}: locked settings file cancels close and keeps the window retryable", null);
                check(memo.EditorForProbe.Text == pendingText,
                    $"{ThemeName(dark)}: pending editor text remains in the open window", memo.EditorForProbe.Text);
                check(File.ReadAllText(settingsPath) == savedSettings,
                    $"{ThemeName(dark)}: locked settings file remains unchanged", File.ReadAllText(settingsPath));
                check(notifications.Count == 1
                      && notifications[0].Title == "备忘录保存失败"
                      && notifications[0].Message.Contains("再次关闭窗口重试", StringComparison.Ordinal),
                    $"{ThemeName(dark)}: close failure uses one retry explanation", notifications);
                check(loggedErrors.Count == 2,
                    $"{ThemeName(dark)}: close fixture error also stays in the probe's in-memory log", loggedErrors.Count);
                check(FindTemporaryFiles(fixtureDirectory).Length == 0,
                    $"{ThemeName(dark)}: close failure removes its temporary file", FindTemporaryFiles(fixtureDirectory));
            }

            check(memo.TryCloseForProbe(),
                $"{ThemeName(dark)}: after unlocking, close save succeeds", null);
            check(File.ReadAllText(textPath) == pendingText,
                $"{ThemeName(dark)}: retry saves pending body text", File.ReadAllText(textPath));
            var actualSettings = JsonSerializer.Deserialize<double[]>(File.ReadAllText(settingsPath));
            var expectedSettings = new[] { memo.EditorForProbe.FontSize, memo.BackgroundOpacityForProbe, memo.Width, memo.Height, memo.Left, memo.Top, memo.IsEditingForProbe ? 1d : 0d, memo.ScrollSpeedForProbe };
            check(actualSettings is { Length: >= 8 } && actualSettings.SequenceEqual(expectedSettings),
                $"{ThemeName(dark)}: retry saves the current appearance and position", actualSettings);
            check(notifications.Count == 1,
                $"{ThemeName(dark)}: successful retry does not show another failure dialog", notifications.Count);
            check(FindTemporaryFiles(fixtureDirectory).Length == 0,
                $"{ThemeName(dark)}: successful retry leaves no temporary files", FindTemporaryFiles(fixtureDirectory));
            check(new WindowInteropHelper(memo).Handle == IntPtr.Zero,
                $"{ThemeName(dark)}: recovery probe creates no window handle", null);
        }
        finally
        {
            try { memo.Close(); } catch (InvalidOperationException) { }
            try { Directory.Delete(fixtureDirectory, recursive: true); } catch (IOException) { }
        }
    }

    private static void MeasureAndCapture(MemoWindow memo, string output, bool dark, Action<bool, string, object?> check)
    {
        var root = (FrameworkElement)memo.Content;
        root.Measure(new Size(memo.Width, memo.Height));
        root.Arrange(new Rect(0, 0, memo.Width, memo.Height));
        root.UpdateLayout();
        var status = memo.SaveStatusForProbe;
        var bounds = status.TransformToAncestor(root).TransformBounds(new Rect(status.RenderSize));
        check(status.Visibility == Visibility.Visible
              && !string.IsNullOrWhiteSpace(status.Text)
              && bounds.Left >= 0 && bounds.Top >= 0
              && bounds.Right <= root.ActualWidth + .5 && bounds.Bottom <= root.ActualHeight + .5,
            $"{ThemeName(dark)}: inline save failure status is readable and stays within the memo layout", new { status.Text, bounds, root.ActualWidth, root.ActualHeight });
        var background = status.Background as SolidColorBrush;
        var foreground = status.Foreground as SolidColorBrush;
        var contrast = background is not null && foreground is not null
            ? ContrastRatio(foreground.Color, background.Color)
            : 0;
        check(background is { Color.A: 255 } && contrast >= 4.5,
            $"{ThemeName(dark)}: inline save status has an opaque theme surface and at least 4.5:1 contrast", new { contrast, background = background?.Color, foreground = foreground?.Color });
        var untrimmed = new TextBlock
        {
            Text = status.Text,
            FontFamily = status.FontFamily,
            FontSize = status.FontSize,
            FontStyle = status.FontStyle,
            FontWeight = status.FontWeight,
            FontStretch = status.FontStretch,
            FlowDirection = status.FlowDirection,
            TextWrapping = TextWrapping.NoWrap
        };
        untrimmed.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        check(untrimmed.DesiredSize.Width <= status.ActualWidth + .5
              && untrimmed.DesiredSize.Height <= status.ActualHeight + .5,
            $"{ThemeName(dark)}: complete status text fits its laid-out text box", new { untrimmed.DesiredSize, status.ActualWidth, status.ActualHeight, status.Text });
        check(new WindowInteropHelper(memo).Handle == IntPtr.Zero,
            $"{ThemeName(dark)}: layout check remains offscreen without HWND", null);

        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, dark ? "dark-memo-save-failure.png" : "light-memo-save-failure.png"));
        encoder.Save(stream);
    }

    private static string[] FindTemporaryFiles(string directory) => Directory.GetFiles(directory, ".*.tmp", SearchOption.TopDirectoryOnly);

    private static string ThemeName(bool dark) => dark ? "dark" : "light";

    private static void InvokeThemeClick(MainWindow owner)
    {
        typeof(MainWindow).GetMethod("ThemeClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(owner, [owner, new RoutedEventArgs()]);
    }

    private static double ContrastRatio(Color first, Color second)
    {
        static double Luminance(Color color)
        {
            static double Linear(byte value)
            {
                var channel = value / 255d;
                return channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4);
            }
            return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        }
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
}
