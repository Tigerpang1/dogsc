using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FrameTrail.App;

namespace FrameTrail.App.VisualProbe;

internal static class DesktopSurfacesProbe
{
    internal static void Verify(string output, Action<bool, string, object?> check)
    {
        Directory.CreateDirectory(output);
        var editor = new MainWindow();
        var recorder = editor.CreateFloatingRecorderWindow();
        try
        {
            byte[]? lightPixels = null;
            var renderedWidth = 0;
            var renderedHeight = 0;
            foreach (var theme in new[] { "light", "dark" })
            {
                if (theme == "dark")
                {
                    typeof(MainWindow).GetMethod("ThemeClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(editor, [editor, new RoutedEventArgs()]);
                }

                var root = (FrameworkElement)recorder.Content;
                root.Measure(new Size(818, 80));
                root.Arrange(new Rect(0, 0, 818, 80));
                root.UpdateLayout();
                // Dynamic resources invalidate drawing asynchronously. Let WPF apply
                // those changes before comparing the already-created surface.
                root.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => { }));

                var buttons = Descendants(root).OfType<Button>().ToArray();
                var bounds = buttons.Select(button => new
                {
                    Name = AutomationProperties.GetName(button),
                    ToolTip = button.ToolTip?.ToString(),
                    Rect = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize))
                }).ToArray();
                var contained = buttons.Length > 0 && bounds.All(item =>
                    item.Rect.Left >= -.5 && item.Rect.Top >= -.5 &&
                    item.Rect.Right <= root.ActualWidth + .5 && item.Rect.Bottom <= root.ActualHeight + .5 &&
                    item.Rect.Width > 0 && item.Rect.Height > 0);
                check(contained, $"{theme}: every interactive recorder control fits inside the 818×80 surface", bounds);

                var requiredNames = new[] { "口播提词器", "打开编辑器", "FrameTrail 菜单", "收起录制工具" };
                var names = buttons.Select(AutomationProperties.GetName).ToArray();
                check(requiredNames.All(names.Contains), $"{theme}: teleprompter, editor, menu, and collapse actions exist", names);
                check(recorder.Owner is null, $"{theme}: recorder window has no editor owner", recorder.Owner?.Title);
                check(new WindowInteropHelper(recorder).Handle == IntPtr.Zero &&
                      new WindowInteropHelper(editor).Handle == IntPtr.Zero,
                    $"{theme}: factory and offscreen layout do not create a native window handle", null);
                check(recorder.MinHeight >= 80 && root.DesiredSize.Height <= 80.5 &&
                      buttons.All(button => button.ActualHeight <= 80),
                    $"{theme}: default minimum-height layout keeps controls on the compact row",
                    new { recorder.MinHeight, root.DesiredSize, root.ActualWidth, root.ActualHeight });

                var width = (int)Math.Ceiling(root.ActualWidth);
                var height = (int)Math.Ceiling(root.ActualHeight);
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var pixels = new byte[width * height * 4];
                bitmap.CopyPixels(pixels, width * 4, 0);
                if (theme == "light")
                {
                    lightPixels = pixels;
                    renderedWidth = width;
                    renderedHeight = height;
                }
                else
                {
                    var changedPixels = lightPixels is { Length: > 0 } &&
                        lightPixels.Length == pixels.Length
                        ? Enumerable.Range(0, pixels.Length / 4).Count(index =>
                            Math.Abs(lightPixels[index * 4] - pixels[index * 4]) > 8 ||
                            Math.Abs(lightPixels[index * 4 + 1] - pixels[index * 4 + 1]) > 8 ||
                            Math.Abs(lightPixels[index * 4 + 2] - pixels[index * 4 + 2]) > 8)
                        : 0;
                    check(changedPixels > 100,
                        "dark: re-rendering the same recorder after ThemeClick updates dynamic colors",
                        new { changedPixels, renderedWidth, renderedHeight, width, height });
                }

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(output, $"{theme}-recorder-surface.png"));
                encoder.Save(stream);
            }
        }
        finally
        {
            recorder.Close();
            // The editor is intentionally left unshown and unclosed: closing a MainWindow
            // follows the application's normal shutdown path, which this probe does not test.
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
