using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FrameTrail.App;
using FrameTrail.App.VisualProbe;

internal static class MicrophoneAccessRecoveryProbe
{
    internal static void Verify(string output, Action<bool, string, object?> check)
    {
        var owner = new MainWindow();
        try
        {
            foreach (var theme in new[] { "light", "dark" })
            {
                if (theme == "dark") typeof(MainWindow).GetMethod("ThemeClick", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(owner, [owner, new RoutedEventArgs()]);
                foreach (var action in new[] { "accept", "later", "close" })
                {
                    var result = MessageBoxResult.Cancel;
                    var calls = 0;
                    // WPF owners must already have been shown; render the same surface unowned
                    // so this check never opens a native window or claims owner/focus activation coverage.
                    var dialog = MicrophoneAccessRecovery.Create(null, value => { result = value; calls++; });
                    var root = (FrameworkElement)dialog.Content;
                    root.Measure(new Size(dialog.Width, double.PositiveInfinity));
                    root.Arrange(new Rect(0, 0, dialog.Width, root.DesiredSize.Height));
                    root.UpdateLayout();
                    var buttons = Descendants(root).OfType<Button>().ToArray();
                    var accept = buttons.Single(b => AutomationProperties.GetAutomationId(b) == "DialogOK");
                    var cancel = buttons.Single(b => AutomationProperties.GetAutomationId(b) == "DialogCancel");
                    var texts = Descendants(root).OfType<TextBlock>().Select(t => t.Text).ToArray();
                    check(texts.Any(t => t.Contains("允许桌面应用访问麦克风") && t.Contains("重新开始录制")),
                        $"{theme}/{action}: explanation includes the desktop permission and return path", texts);
                    check(accept.Content?.ToString() == "打开麦克风设置" && cancel.Content?.ToString() == "稍后" &&
                          cancel.IsCancel && cancel.IsDefault && !accept.IsDefault,
                        $"{theme}/{action}: explicit settings action and safe default dismissal", null);
                    var acceptBounds = accept.TransformToAncestor(root).TransformBounds(new Rect(accept.RenderSize));
                    var cancelBounds = cancel.TransformToAncestor(root).TransformBounds(new Rect(cancel.RenderSize));
                    check(acceptBounds.Right <= root.ActualWidth && acceptBounds.Bottom <= root.ActualHeight &&
                          cancelBounds.Left >= 0 && !acceptBounds.IntersectsWith(cancelBounds),
                        $"{theme}/{action}: buttons are inside the dialog without overlap", new { acceptBounds, cancelBounds });
                    var labelsFit = new[] { accept, cancel }.All(button => Descendants(button).OfType<TextBlock>().All(text =>
                    {
                        var bounds = text.TransformToAncestor(button).TransformBounds(new Rect(text.RenderSize));
                        return bounds.Left >= -.5 && bounds.Right <= button.ActualWidth + .5;
                    }));
                    check(labelsFit, $"{theme}/{action}: complete action labels fit their buttons", null);
                    check(new WindowInteropHelper(dialog).Handle == IntPtr.Zero,
                        $"{theme}/{action}: layout remains offscreen without HWND", null);
                    if (action == "accept")
                    {
                        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(root);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using (var stream = File.Create(Path.Combine(output, theme + "-microphone-access.png"))) encoder.Save(stream);
                        accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    else if (action == "later") cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else dialog.Close();
                    check(action == "accept" ? result == MessageBoxResult.OK && calls == 1 :
                            result == MessageBoxResult.Cancel && calls == (action == "later" ? 1 : 0),
                        $"{theme}/{action}: only accepting requests settings; dismissals stay in the app", new { result, calls });
                }
            }
            foreach (var mode in new[] { MessageBoxButton.OK, MessageBoxButton.OKCancel, MessageBoxButton.YesNo, MessageBoxButton.YesNoCancel })
            {
                var dialog = SurfaceDialog.Create(null, "Existing dialog", "Regression", mode, _ => { });
                var root = (FrameworkElement)dialog.Content;
                root.Measure(new Size(480, double.PositiveInfinity));
                root.Arrange(new Rect(0, 0, 480, root.DesiredSize.Height));
                root.UpdateLayout();
                var buttons = Descendants(root).OfType<Button>().Where(b => AutomationProperties.GetAutomationId(b).StartsWith("Dialog")).ToArray();
                var expectedDefault = mode is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel ? "DialogNo" : "DialogOK";
                check(buttons.Count(b => b.IsDefault) == 1 && buttons.Single(b => b.IsDefault).GetValue(AutomationProperties.AutomationIdProperty).Equals(expectedDefault),
                    $"existing {mode}: default action is unchanged", null);
                dialog.Close();
            }
        }
        finally { MainWindowProbeLifetime.Close(owner); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
