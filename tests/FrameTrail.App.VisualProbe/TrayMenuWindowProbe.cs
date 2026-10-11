using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FrameTrail.App;

namespace FrameTrail.App.VisualProbe;

internal static class TrayMenuWindowProbe
{
    internal static void Verify(string output, Action<bool, string, object?> check)
    {
        Directory.CreateDirectory(output);
        var commandCount = 0;
        var entries = new ContextMenu();
        entries.Items.Add(new MenuItem { Header = "打开编辑器" });
        var recent = new MenuItem { Header = "最近项目" };
        recent.Items.Add(new MenuItem { Header = "项目 A", Tag = "project-a.frametrail" });
        recent.Items.Add(new MenuItem { Header = "项目 B", Tag = "project-b.frametrail" });
        entries.Items.Add(recent);
        entries.Items.Add(new Separator());
        var action = new MenuItem { Header = "执行测试命令" };
        action.Click += (_, _) => commandCount++;
        entries.Items.Add(action);

        var window = new TrayMenuWindow(entries);
        try
        {
            var retainedRecent = window.Content is Grid grid && grid.Children[0] is Border border && border.Child is Menu menu
                ? menu.Items.OfType<MenuItem>().SingleOrDefault(item => Equals(item.Header, "最近项目"))
                : null;
            check(retainedRecent is not null && ReferenceEquals(retainedRecent, recent) &&
                  Equals(retainedRecent.Items[0] is MenuItem first ? first.Tag : null, "project-a.frametrail") &&
                  Equals(retainedRecent.Items[1] is MenuItem second ? second.Tag : null, "project-b.frametrail"),
                "moving entries into the window retains recent-project submenu entries and identity",
                new { RootItems = ((Border)((Grid)window.Content).Children[0]).Child is Menu rootMenu ? rootMenu.Items.Count : -1, RecentItems = retainedRecent?.Items.Count });
            check(entries.Items.Count == 0, "source ContextMenu releases entries after the move", entries.Items.Count);

            byte[]? lightPixels = null;
            for (var themeIndex = 0; themeIndex < 2; themeIndex++)
            {
                var theme = themeIndex == 0 ? "light" : "dark";
                SetTheme(themeIndex == 1);
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(900, 900));
                content.Arrange(new Rect(new Point(0, 0), content.DesiredSize));
                content.UpdateLayout();
                var menuRoot = (Menu)((Border)((Grid)window.Content).Children[0]).Child;
                var topItems = menuRoot.Items.OfType<UIElement>().ToArray();
                var bounds = topItems.Select(item => item.TransformToAncestor(content).TransformBounds(new Rect(item.RenderSize))).ToArray();
                var contained = content.ActualWidth > 0 && content.ActualHeight > 0 && bounds.Length >= 4 && bounds.All(rect =>
                    rect.Width > 0 && rect.Height > 0 && rect.Left >= -.5 && rect.Top >= -.5 &&
                    rect.Right <= content.ActualWidth + .5 && rect.Bottom <= content.ActualHeight + .5);
                check(contained, $"{theme}: menu rows have visible, unclipped layout bounds", new { content.ActualWidth, content.ActualHeight, Bounds = bounds });

                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                check(pixels.Where((_, index) => index % 4 == 3).Any(alpha => alpha > 0), $"{theme}: offscreen rendering contains visible surface pixels", new { bitmap.PixelWidth, bitmap.PixelHeight });
                if (theme == "light") lightPixels = pixels;
                else
                {
                    var changed = lightPixels is { } old && old.Length == pixels.Length
                        ? Enumerable.Range(0, pixels.Length / 4).Count(index =>
                            Math.Abs(old[index * 4] - pixels[index * 4]) > 8 ||
                            Math.Abs(old[index * 4 + 1] - pixels[index * 4 + 1]) > 8 ||
                            Math.Abs(old[index * 4 + 2] - pixels[index * 4 + 2]) > 8)
                        : 0;
                    check(changed > 100, "dark: existing menu surface responds to dynamic theme brushes", new { changed, bitmap.PixelWidth, bitmap.PixelHeight });
                }
                Save(bitmap, Path.Combine(output, theme + "-tray-menu.png"));
            }

            action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, action));
            check(commandCount == 1, "a leaf command executes exactly once through the moved menu item", commandCount);
            check(new System.Windows.Interop.WindowInteropHelper(window).Handle == IntPtr.Zero && !window.IsVisible,
                "content probe does not create or show a native window", new { window.IsVisible, Handle = new System.Windows.Interop.WindowInteropHelper(window).Handle.ToInt64() });
        }
        finally { window.Close(); }

        VerifyFit(check);
    }

    private static void VerifyFit(Action<bool, string, object?> check)
    {
        var work = new MainWindowPlacement.RectPx(0, 0, 1920, 1080);
        var bottomRight = TrayMenuPlacement.Fit(1900, 1060, 300, 180, work);
        check(bottomRight == new MainWindowPlacement.RectPx(1600, 880, 1900, 1060),
            "physical placement flips left and up at the bottom-right work-area edge", bottomRight);

        var negativeWork = new MainWindowPlacement.RectPx(-1920, -100, 0, 980);
        var negative = TrayMenuPlacement.Fit(-1910, -90, 320, 200, negativeWork);
        check(negative.Left >= -1916 && negative.Top >= -96 && negative.Right <= -4 && negative.Bottom <= 976,
            "physical placement clamps a negative-origin monitor inside its work area", negative);

        var dimensions = new[] { (Width: 180, Height: 90), (Width: 420, Height: 260) };
        foreach (var size in dimensions)
        {
            var fit = TrayMenuPlacement.Fit(700, 500, size.Width, size.Height, new(0, 0, 1600, 1000));
            check(fit.Width == size.Width && fit.Height == size.Height && fit.Left == 700 && fit.Top == 500,
                $"physical placement preserves requested {size.Width}×{size.Height} pixel dimensions when they fit", fit);
        }
    }

    private static void SetTheme(bool dark)
    {
        var palette = dark
            ? new[] { "#252829", "#303334", "#393D3E", "#F1F2F2", "#A6ABAD", "#484D4F", "#3B3F40", "#35584B", "#62B693", "#4B5052" }
            : new[] { "#F4F6F6", "#FCFCFC", "#FFFFFF", "#303232", "#707579", "#E6E7E8", "#F2F2F4", "#E1F2EA", "#46A986", "#D9DDDC" };
        var keys = new[] { "Workspace", "Surface", "Chrome", "Ink", "Secondary", "Hairline", "ControlSurface", "Selection", "Accent", "Dot" };
        for (var index = 0; index < keys.Length; index++)
            Application.Current.Resources[keys[index]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette[index]));
        Application.Current.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => { }));
    }

    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
