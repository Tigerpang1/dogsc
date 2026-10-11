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

internal static class AppErrorPresentationProbe
{
    internal static void Verify(string output, Action<bool, string, object?> check)
    {
        var errors = new List<Exception>();
        var presentations = 0;
        UnhandledErrorPresenter? presenter = null;
        presenter = new(errors.Add, _ =>
        {
            presentations++;
            presenter!.Report(new IOException("nested error from dialog event"));
        });
        presenter.Report(new IOException("initial operation error"));
        check(errors.Count == 2 && presentations == 1,
            "nested dialog errors are logged without opening recursive dialogs", new { Logs = errors.Count, presentations });
        presenter.Report(new IOException("later operation error"));
        check(errors.Count == 4 && presentations == 2,
            "dismissing one error allows a later independent error to be presented", new { Logs = errors.Count, presentations });

        errors.Clear();
        var failedPresentations = 0;
        var failingPresenter = new UnhandledErrorPresenter(errors.Add, _ =>
        {
            failedPresentations++;
            throw new InvalidOperationException("dialog layout failed");
        });
        failingPresenter.Report(new IOException("first failure"));
        failingPresenter.Report(new IOException("retry failure"));
        check(errors.Count == 4 && failedPresentations == 2 && errors[1].Message == "dialog layout failed",
            "presentation failure is contained, logged, and releases the presentation guard", new { Logs = errors.Count, failedPresentations });

        var owner = new MainWindow();
        try
        {
            foreach (var theme in new[] { "light", "dark" })
            {
                if (theme == "dark") typeof(MainWindow).GetMethod("ThemeClick", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(owner, [owner, new RoutedEventArgs()]);
                var dialog = UnhandledErrorPresenter.Create(null, new IOException("文件暂时无法写入，请稍后重试。"));
                try
                {
                    var root = (FrameworkElement)dialog.Content;
                    root.Measure(new Size(dialog.Width, double.PositiveInfinity));
                    root.Arrange(new Rect(0, 0, dialog.Width, root.DesiredSize.Height));
                    root.UpdateLayout();
                    var button = Descendants(root).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "DialogOK");
                    var bounds = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
                    check(bounds.Left >= 0 && bounds.Right <= root.ActualWidth && bounds.Bottom <= root.ActualHeight && button.IsDefault,
                        theme + ": recovery action is visible and remains the default action", bounds);
                    check(Descendants(root).OfType<TextBlock>().Any(t => t.Text.Contains("检查当前内容，再重试")),
                        theme + ": error surface explains the return path without claiming the operation succeeded", null);
                    check(new WindowInteropHelper(dialog).Handle == IntPtr.Zero && !dialog.IsVisible,
                        theme + ": shared error surface renders without creating a native window", null);
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(output, theme + "-app-error.png"));
                    encoder.Save(stream);
                }
                finally { dialog.Close(); }
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
