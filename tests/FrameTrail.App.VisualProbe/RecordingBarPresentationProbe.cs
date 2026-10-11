using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FrameTrail.App;
using FrameTrail.App.VisualProbe;

internal static class RecordingBarPresentationProbe
{
    private sealed record ShortcutCase(string Name, string? Shortcut, bool IsFallback, string ExpectedText);

    public static void Verify(string output, Action<bool, string, object?> check)
    {
        Directory.CreateDirectory(output);
        var app = Application.Current ?? throw new InvalidOperationException("WPF application must be initialized before this probe.");
        var main = new MainWindow();
        try
        {
            var switchTheme = typeof(MainWindow).GetMethod("ThemeClick", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(typeof(MainWindow).FullName, "ThemeClick");
            var dark = false;
            foreach (var theme in new[] { "light", "dark" })
            {
                if (dark != (theme == "dark"))
                {
                    switchTheme.Invoke(main, [main, new RoutedEventArgs()]);
                    dark = !dark;
                }

                var noMenuBar = new RecordingBar(() => { }, () => Task.CompletedTask, () => Task.CompletedTask);
                try
                {
                    var noMenuButtons = Descendants((DependencyObject)noMenuBar.Content).OfType<Button>().ToArray();
                    check(noMenuButtons.All(button => AutomationProperties.GetAutomationId(button) != "RecordingBarMenu"),
                        $"{theme}: menu button is omitted when no callback is supplied",
                        new { AutomationIds = noMenuButtons.Select(AutomationProperties.GetAutomationId).ToArray() });
                }
                finally { noMenuBar.CloseAfterRecording(); }

                foreach (var shortcut in new[]
                         {
                             new ShortcutCase("default", "Ctrl+Alt+M", false, "Ctrl+Alt+M"),
                             new ShortcutCase("fallback", "Ctrl+Alt+Shift+M", true, "Ctrl+Alt+Shift+M"),
                             new ShortcutCase("unavailable", null, false, "快捷键不可用")
                         })
                {
                    var callbackCount = 0;
                    var menuCallbackCount = 0;
                    Button? callbackButton = null;
                    var bar = new RecordingBar(
                        () => callbackCount++,
                        () => Task.CompletedTask,
                        () => Task.CompletedTask,
                        markerShortcut: shortcut.Shortcut,
                        markerShortcutIsFallback: shortcut.IsFallback,
                        showMenu: button => { menuCallbackCount++; callbackButton = button; });
                    try
                    {
                        var barHandle = new System.Windows.Interop.WindowInteropHelper(bar).Handle;
                        check(barHandle == IntPtr.Zero,
                            $"{theme}/{shortcut.Name}: recording bar remains an unshown window without an HWND",
                            new { Handle = barHandle.ToInt64() });

                        var root = (FrameworkElement)bar.Content;
                        Layout(root, bar.Width, bar.Height);
                        var buttons = Descendants(root).OfType<Button>().ToArray();
                        var mark = buttons.SingleOrDefault(button => AutomationProperties.GetAutomationId(button) == "RecordingAddMarker");
                        var menu = buttons.SingleOrDefault(button => AutomationProperties.GetAutomationId(button) == "RecordingBarMenu");
                        var pause = buttons.FirstOrDefault(button => button.ToolTip?.ToString() == "暂停录制");
                        var end = buttons.FirstOrDefault(button => button.Content?.ToString() == "结束录制");
                        check(mark is not null && menu is not null && pause is not null && end is not null,
                            $"{theme}/{shortcut.Name}: marker, pause, end, and menu controls exist",
                            new { ButtonCount = buttons.Length, AutomationIds = buttons.Select(AutomationProperties.GetAutomationId).ToArray() });

                        if (mark is null || menu is null || pause is null || end is null)
                            continue;

                        check(menu.Width == 38 && menu.Height == 38 && menu.ToolTip?.ToString() == "DogSC 菜单" &&
                              AutomationProperties.GetName(menu) == "DogSC 菜单",
                            $"{theme}/{shortcut.Name}: menu button has the expected size and accessible label",
                            new { menu.Width, menu.Height, ToolTip = menu.ToolTip?.ToString(), Name = AutomationProperties.GetName(menu) });

                        var text = Descendants(mark).OfType<TextBlock>().Select(block => block.Text).ToArray();
                        check(text.Contains("标记") && text.Contains(shortcut.ExpectedText),
                            $"{theme}/{shortcut.Name}: marker button shows the label and active shortcut state",
                            new { Text = text, Expected = shortcut.ExpectedText });
                        var markerBounds = BoundsIn(root, mark);
                        var textBounds = Descendants(mark).OfType<TextBlock>()
                            .Select(block => new { block.Text, Bounds = BoundsIn(mark, block) }).ToArray();
                        var textInsideButton = textBounds.All(entry => entry.Bounds.Left >= 0 && entry.Bounds.Top >= 0 &&
                            entry.Bounds.Right <= mark.ActualWidth + .5 && entry.Bounds.Bottom <= mark.ActualHeight + .5);
                        check(textInsideButton,
                            $"{theme}/{shortcut.Name}: each marker text line fits inside the button bounds",
                            new { Button = markerBounds, ButtonWidth = mark.ActualWidth, ButtonHeight = mark.ActualHeight, Text = textBounds });

                        var rects = buttons.Select(button => (Button: button, Bounds: BoundsIn(root, button))).ToArray();
                        var inside = rects.All(entry => entry.Bounds.Left >= 0 && entry.Bounds.Top >= 0 &&
                            entry.Bounds.Right <= root.ActualWidth + .5 && entry.Bounds.Bottom <= root.ActualHeight + .5);
                        var noOverlap = true;
                        for (var i = 0; i < rects.Length; i++)
                        for (var j = i + 1; j < rects.Length; j++)
                            if (rects[i].Bounds.IntersectsWith(rects[j].Bounds)) noOverlap = false;
                        check(inside && noOverlap,
                            $"{theme}/{shortcut.Name}: all buttons fit within the recording bar without overlap",
                            new { Width = root.ActualWidth, Height = root.ActualHeight, Buttons = rects.Select(entry => new { Name = AutomationProperties.GetName(entry.Button), entry.Bounds }).ToArray() });

                        var iconBounds = buttons.SelectMany(button => Descendants(button).OfType<EditorIcon>()
                                .Select(icon => new
                                {
                                    Button = AutomationProperties.GetAutomationId(button),
                                    ButtonWidth = button.ActualWidth,
                                    ButtonHeight = button.ActualHeight,
                                    Kind = icon.Kind,
                                    Bounds = BoundsIn(button, icon)
                                }))
                            .ToArray();
                        var iconsFit = iconBounds.Length >= 2 && iconBounds.All(icon =>
                            icon.Bounds.Left >= 0 && icon.Bounds.Top >= 0 &&
                            icon.Bounds.Right <= icon.ButtonWidth + .5 && icon.Bounds.Bottom <= icon.ButtonHeight + .5);
                        check(iconsFit,
                            $"{theme}/{shortcut.Name}: marker, pause, and menu icons fit inside their buttons",
                            new { Icons = iconBounds });

                        var foreground = FindTextColor(mark);
                        var background = FindBackgroundColor(root);
                        var ratio = Contrast(foreground, background);
                        check(ratio >= 4.5,
                            $"{theme}/{shortcut.Name}: marker text has readable contrast against the bar",
                            new { Foreground = foreground.ToString(), Background = background.ToString(), Ratio = ratio });

                        var imagePath = Path.Combine(output, $"{theme}-{shortcut.Name}.png");
                        Save(root, imagePath);
                        check(File.Exists(imagePath) && new FileInfo(imagePath).Length > 0,
                            $"{theme}/{shortcut.Name}: rendered recording bar image saved",
                            new { Path = imagePath, root.ActualWidth, root.ActualHeight });

                        bar.SetPaused(true);
                        check(!mark.IsEnabled,
                            $"{theme}/{shortcut.Name}: pause disables marker input",
                            new { mark.IsEnabled });
                        bar.SetPaused(false);
                        check(mark.IsEnabled,
                            $"{theme}/{shortcut.Name}: resume restores marker input",
                            new { mark.IsEnabled });

                        bar.SetMarkerCount(1);
                        var label = Descendants(root).OfType<TextBlock>().FirstOrDefault(block => block.Text.Contains("标记"));
                        check(label?.Text.Contains("1 标记", StringComparison.Ordinal) == true,
                            $"{theme}/{shortcut.Name}: marker count updates the live label",
                            new { Label = label?.Text });

                        var shortcutLabel = Descendants(mark).OfType<TextBlock>().Single(block => block.Text == shortcut.ExpectedText);
                        var preview = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                        {
                            RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
                            Source = shortcutLabel
                        };
                        var textClickReachedWindow = true;
                        try { shortcutLabel.RaiseEvent(preview); }
                        catch (InvalidOperationException) { textClickReachedWindow = false; }
                        check(textClickReachedWindow,
                            $"{theme}/{shortcut.Name}: clicking shortcut text does not invoke window dragging",
                            new { Shortcut = shortcut.ExpectedText });

                        mark.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, mark));
                        check(callbackCount == 1,
                            $"{theme}/{shortcut.Name}: clicking marker invokes its callback once",
                            new { callbackCount });

                        menu.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, menu));
                        check(menuCallbackCount == 1 && ReferenceEquals(callbackButton, menu) &&
                              new System.Windows.Interop.WindowInteropHelper(bar).Handle == IntPtr.Zero && !bar.IsVisible,
                            $"{theme}/{shortcut.Name}: clicking the unshown menu button passes that button to its callback exactly once",
                            new { menuCallbackCount, SameButton = ReferenceEquals(callbackButton, menu), bar.IsVisible,
                                Handle = new System.Windows.Interop.WindowInteropHelper(bar).Handle.ToInt64() });
                    }
                    finally
                    {
                        bar.CloseAfterRecording();
                        check(new System.Windows.Interop.WindowInteropHelper(bar).Handle == IntPtr.Zero,
                            $"{theme}/{shortcut.Name}: closing the unshown bar still leaves its HWND at zero",
                            new { Handle = new System.Windows.Interop.WindowInteropHelper(bar).Handle.ToInt64() });
                    }
                }
            }
        }
        finally
        {
            if (new System.Windows.Interop.WindowInteropHelper(main).Handle != IntPtr.Zero)
                throw new InvalidOperationException("The presentation probe unexpectedly created a native window.");
            MainWindowProbeLifetime.Close(main);
        }
    }

    private static void Layout(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static Rect BoundsIn(FrameworkElement root, FrameworkElement element) =>
        element.TransformToAncestor(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static Color FindTextColor(DependencyObject parent) => Descendants(parent)
        .OfType<TextBlock>()
        .Select(block => block.Foreground)
        .OfType<SolidColorBrush>()
        .Select(brush => brush.Color)
        .FirstOrDefault();

    private static Color FindBackgroundColor(FrameworkElement root)
    {
        var border = Descendants(root).OfType<Border>().FirstOrDefault(candidate => candidate.Background is SolidColorBrush);
        return (border?.Background as SolidColorBrush)?.Color ?? Colors.Transparent;
    }

    private static void Save(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static double Contrast(Color foreground, Color background)
    {
        static double Linear(byte value)
        {
            var component = value / 255d;
            return component <= .04045 ? component / 12.92 : Math.Pow((component + .055) / 1.055, 2.4);
        }

        static double Luminance(Color color) => .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        var a = Luminance(foreground);
        var b = Luminance(background);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
}
