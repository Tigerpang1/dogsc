using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FrameTrail.App.VisualProbe;

internal static class EditorControlProbe
{
    private const int GalleryWidth = 640;
    private const int GalleryHeight = 280;

    public static void Verify(Window owner, string theme, string output, Action<bool, string, object?> check)
    {
        var originalContent = owner.Content;
        var gallery = new StackPanel
        {
            Width = 600,
            Background = (Brush)owner.FindResource("Surface"),
            Margin = new Thickness(12)
        };
        TextElement.SetForeground(gallery, (Brush)owner.FindResource("Ink"));
        owner.Content = gallery; // Use the real Window resource/foreground inheritance without creating a HWND.
        try
        {
            var title = new TextBlock { Text = $"控件离屏样本 · {theme}", Margin = new Thickness(0, 0, 0, 8) };
            gallery.Children.Add(title);
            var stateRow = new StackPanel { Orientation = Orientation.Horizontal };
            var states = new (string Name, bool? Value, bool Enabled)[]
            {
                ("unchecked", false, true),
                ("checked", true, true),
                ("mixed", null, true),
                ("disabled", true, false)
            };
            var boxes = new List<CheckBox>();
            foreach (var state in states)
            {
                var box = CreateCheckBox(state.Value, state.Enabled);
                boxes.Add(box);
                stateRow.Children.Add(box);
            }
            gallery.Children.Add(stateRow);

            var inspectorToggle = new CheckBox
            {
                Content = "设置开关",
                IsChecked = true,
                Style = (Style)owner.FindResource("InspectorToggle"),
                Width = 360,
                Height = 34,
                Margin = new Thickness(2, 6, 2, 2)
            };
            gallery.Children.Add(inspectorToggle);

            var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 8) };
            var primary = new Button
            {
                Content = "主要操作",
                Style = (Style)owner.FindResource("PrimaryButton"),
                Width = 150,
                Height = 42,
                Margin = new Thickness(2)
            };
            actionRow.Children.Add(primary);
            var slider = new Slider { Width = 250, Minimum = 0, Maximum = 1, Value = .25, Margin = new Thickness(10, 2, 2, 2) };
            actionRow.Children.Add(slider);
            gallery.Children.Add(actionRow);

            var horizontal = new ScrollBar
            {
                Orientation = Orientation.Horizontal,
                Minimum = 0,
                Maximum = 100,
                Value = 50,
                ViewportSize = 10,
                SmallChange = 1,
                LargeChange = 20,
                Width = 360,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(2, 6, 2, 2)
            };
            var scrollbarHost = new Grid { Width = 364, Height = 32, Background = (Brush)owner.FindResource("Surface") };
            scrollbarHost.Children.Add(horizontal);
            gallery.Children.Add(scrollbarHost);

            var inputRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 8, 2, 2) };
            var textBox = new TextBox { Text = @"C:\User\Demo", Width = 220, Height = 36, Margin = new Thickness(0, 0, 8, 0) };
            var combo = new ComboBox { Width = 150, Height = 34, IsEditable = false };
            combo.Items.Add("常规");
            combo.Items.Add("高质量");
            combo.SelectedIndex = 0;
            inputRow.Children.Add(textBox);
            inputRow.Children.Add(combo);
            gallery.Children.Add(inputRow);

            Layout(gallery, GalleryWidth, GalleryHeight);
            gallery.UpdateLayout();
            primary.ApplyTemplate();
            slider.ApplyTemplate();
            horizontal.ApplyTemplate();
            gallery.UpdateLayout();

            var fullGallery = Render(gallery, GalleryWidth, GalleryHeight);
            Save(fullGallery, Path.Combine(output, $"controls-{theme}-gallery.png"));
            var titleForeground = title.Foreground;
            var surface = GetSolidColor(owner.FindResource("Surface"));
            var titleContrast = titleForeground is SolidColorBrush titleBrush ? Contrast(titleBrush.Color, surface) : 0;
            var titlePixels = CountPixelsDistinctFrom(fullGallery, BoundsInBitmap(title, gallery), surface, 40);
            check(titleContrast >= 4.5 && titlePixels >= 10,
                $"{theme}: 控件画廊标题继承主题且有可读字形像素",
                new { Foreground = titleForeground?.ToString(), Background = surface.ToString(), Contrast = titleContrast, GlyphPixels = titlePixels });

            textBox.ApplyTemplate();
            combo.ApplyTemplate();
            gallery.UpdateLayout();
            var textForeground = textBox.Foreground is SolidColorBrush textBrush ? textBrush.Color : Colors.Transparent;
            var textBounds = BoundsInBitmap(textBox, gallery);
            var glyphBounds = new Rect(textBounds.Left + 12, textBounds.Top + 8, textBounds.Width - 20, textBounds.Height - 16);
            var inputGlyphPixels = CountPixelsDistinctFrom(fullGallery, glyphBounds, GetSolidColor(owner.FindResource("ControlSurface")), 35);
            check(Math.Abs(textBox.ActualHeight - 36) <= .25 && Contrast(textForeground, GetSolidColor(owner.FindResource("ControlSurface"))) >= 4.5 && inputGlyphPixels >= 20,
                $"{theme}: 36 DIP TextBox 中路径文字实际可读且未被裁切", new
                {
                    Height = textBox.ActualHeight,
                    Text = textBox.Text,
                    Foreground = textForeground.ToString(),
                    GlyphPixels = inputGlyphPixels,
                    TextBounds = textBounds
                });
            check(Math.Abs(combo.ActualHeight - 34) <= .25,
                $"{theme}: 非编辑 ComboBox 高度不被隐藏编辑框撑大", new { Height = combo.ActualHeight, RequestedHeight = combo.Height });
            Save(fullGallery, Path.Combine(output, $"controls-{theme}-inputs.png"));

            var stateBitmaps = new List<(string Name, BitmapSource Bitmap)>();
            for (var i = 0; i < states.Length; i++)
            {
                boxes[0].IsChecked = states[i].Value;
                boxes[0].IsEnabled = states[i].Enabled;
                gallery.UpdateLayout();
                var bitmap = Render(gallery, GalleryWidth, GalleryHeight);
                Save(bitmap, Path.Combine(output, $"controls-{theme}-checkbox-{states[i].Name}.png"));
                stateBitmaps.Add((states[i].Name, Crop(bitmap, BoundsInBitmap(boxes[0], gallery))));
            }
            for (var i = 0; i < stateBitmaps.Count; i++)
            for (var j = i + 1; j < stateBitmaps.Count; j++)
            {
                var different = CountDifferentPixels(stateBitmaps[i].Bitmap, stateBitmaps[j].Bitmap);
                check(different >= 3,
                    $"{theme}: 默认复选框 {stateBitmaps[i].Name}/{stateBitmaps[j].Name} 状态有可见像素差异", new { DifferingPixels = different });
            }
            var checkboxContent = FindDescendants(boxes[0]).OfType<ContentPresenter>().FirstOrDefault();
            var checkboxGlyph = checkboxContent is null ? null : FindTextVisual(checkboxContent);
            var checkboxTextBrush = checkboxGlyph is null ? null : ForegroundOf(checkboxGlyph);
            var checkboxContrast = checkboxTextBrush is SolidColorBrush checkboxBrush ? Contrast(checkboxBrush.Color, surface) : 0;
            var uncheckedImage = stateBitmaps[0].Bitmap;
            var checkboxGlyphPixels = checkboxGlyph is null ? 0 : CountPixelsDistinctFrom(uncheckedImage, BoundsInAncestor(checkboxGlyph, boxes[0]), surface, 35);
            check(checkboxContrast >= 4.5 && checkboxGlyphPixels >= 8,
                $"{theme}: 复选框 ContentPresenter 文字有实际可读像素", new { Foreground = checkboxTextBrush?.ToString(), Contrast = checkboxContrast, GlyphPixels = checkboxGlyphPixels });

            inspectorToggle.ApplyTemplate();
            gallery.UpdateLayout();
            inspectorToggle.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var toggleThumb = inspectorToggle.Template?.FindName("SwitchThumb", inspectorToggle) as FrameworkElement;
            var checkedThumbX = (toggleThumb?.RenderTransform as TranslateTransform)?.X ?? double.NaN;
            var toggleChecked = Crop(Render(gallery, GalleryWidth, GalleryHeight), BoundsInBitmap(inspectorToggle, gallery));
            inspectorToggle.IsChecked = false;
            gallery.UpdateLayout();
            var uncheckedThumbX = (toggleThumb?.RenderTransform as TranslateTransform)?.X ?? double.NaN;
            var toggleUnchecked = Crop(Render(gallery, GalleryWidth, GalleryHeight), BoundsInBitmap(inspectorToggle, gallery));
            Save(toggleChecked, Path.Combine(output, $"controls-{theme}-inspector-toggle-checked.png"));
            Save(toggleUnchecked, Path.Combine(output, $"controls-{theme}-inspector-toggle-unchecked.png"));
            var toggleDifference = CountDifferentPixels(toggleChecked, toggleUnchecked);
            check(inspectorToggle.IsChecked == false && toggleDifference >= 16 && Math.Abs(checkedThumbX - 16) <= .25 && Math.Abs(uncheckedThumbX) <= .25,
                $"{theme}: InspectorToggle 从已选切到未选会更新实际模板像素与滑块位置", new
                {
                    IsChecked = inspectorToggle.IsChecked,
                    DifferingPixels = toggleDifference,
                    CheckedThumbX = checkedThumbX,
                    UncheckedThumbX = uncheckedThumbX
                });

            var primaryForeground = primary.Foreground is SolidColorBrush foregroundBrush ? foregroundBrush.Color : Colors.Transparent;
            var primaryContent = FindDescendants(primary).OfType<ContentPresenter>()
                .FirstOrDefault(p => string.Equals(p.Content?.ToString(), "主要操作", StringComparison.Ordinal));
            var effectiveTextBrush = primaryContent is { } presenter
                ? FindTextVisual(presenter) is { } textVisual ? ForegroundOf(textVisual) : TextElement.GetForeground(presenter)
                : null;
            var primaryGlyph = primaryContent is null ? null : FindTextVisual(primaryContent);
            var galleryImage = Render(gallery, GalleryWidth, GalleryHeight);
            var buttonBounds = BoundsInBitmap(primary, gallery);
            var backgroundPoint = new Point(buttonBounds.Right - 5, buttonBounds.Top + primary.ActualHeight / 2);
            var actualBackground = GetPixelColor(galleryImage, (int)backgroundPoint.X, (int)backgroundPoint.Y);
            var actualTextColor = effectiveTextBrush is SolidColorBrush effectiveBrush ? effectiveBrush.Color : primaryForeground;
            var actualContrast = Contrast(actualTextColor, actualBackground);
            var contentPixels = primaryGlyph is null ? 0 : CountPixelsDistinctFrom(galleryImage, BoundsInAncestor(primaryGlyph, gallery), actualBackground, 35);
            check(actualContrast >= 4.5 && contentPixels >= 12,
                $"{theme}: PrimaryButton 实际内容字色对比充足且文字确实绘出", new
                {
                    ButtonForeground = primaryForeground.ToString(),
                    ContentForeground = effectiveTextBrush?.ToString(),
                    BackgroundProperty = primary.Background?.ToString(),
                    PixelBackground = actualBackground.ToString(),
                    Contrast = actualContrast,
                    GlyphPixels = contentPixels,
                    GlyphElement = primaryGlyph?.GetType().Name
                });

            var track = horizontal.Template?.FindName("PART_Track", horizontal) as Track;
            check(Math.Abs(horizontal.ActualHeight - 10) <= .25,
                $"{theme}: 横向 ScrollBar 保留 10 DIP 命中带", new { ActualHeight = horizontal.ActualHeight });
            check(track is not null, $"{theme}: 横向 ScrollBar 模板创建实际 Track", new { TrackFound = track is not null });
            if (track is not null)
            {
                check(!track.IsDirectionReversed, $"{theme}: 横向 Track 未反向", new { track.IsDirectionReversed });
                check(ReferenceEquals(track.DecreaseRepeatButton.Command, ScrollBar.PageLeftCommand),
                    $"{theme}: 横向减小按钮绑定 PageLeft", new { Command = track.DecreaseRepeatButton.Command?.ToString() });
                check(ReferenceEquals(track.IncreaseRepeatButton.Command, ScrollBar.PageRightCommand),
                    $"{theme}: 横向增加按钮绑定 PageRight", new { Command = track.IncreaseRepeatButton.Command?.ToString() });
                horizontal.Value = 50;
                ScrollBar.PageLeftCommand.Execute(null, horizontal);
                var afterPageLeft = horizontal.Value;
                horizontal.Value = 50;
                ScrollBar.PageRightCommand.Execute(null, horizontal);
                var afterPageRight = horizontal.Value;
                check(afterPageLeft < 50 && afterPageRight > 50,
                    $"{theme}: PageLeft/PageRight 实际按水平值方向翻页", new { afterPageLeft, afterPageRight });

                var trackThumb = track.Thumb;
                check(trackThumb is not null, $"{theme}: 横向 Track 有可视 Thumb", new { ThumbFound = trackThumb is not null });
                if (trackThumb is not null)
                {
                    gallery.UpdateLayout();
                    var scrollbarBitmap = Render(gallery, GalleryWidth, GalleryHeight);
                    Save(scrollbarBitmap, Path.Combine(output, $"controls-{theme}-scrollbar.png"));
                    var thumbBounds = BoundsInBitmap(trackThumb, gallery);
                    var sampleX = (int)Math.Round(thumbBounds.Left + thumbBounds.Width / 2);
                    var topY = (int)Math.Floor(thumbBounds.Top);
                    var baseline = GetPixelColor(scrollbarBitmap, sampleX, topY);
                    var visibleThickness = CountColumnPixelsDifferentFrom(scrollbarBitmap, sampleX, topY, (int)Math.Ceiling(thumbBounds.Bottom), baseline, 25);
                    check(thumbBounds.Height >= 9.75 && visibleThickness is >= 3 and <= 6,
                        $"{theme}: 横向滚动条拇指的实际可见厚度约为 4 DIP", new
                        {
                            VisiblePixels = visibleThickness,
                            HitTargetHeight = thumbBounds.Height,
                            Bounds = thumbBounds,
                            SampleX = sampleX,
                            Baseline = baseline.ToString()
                        });
                }
            }

            var sliderTrack = slider.Template?.FindName("PART_Track", slider) as Track;
            check(sliderTrack is not null && sliderTrack.ActualWidth >= 200,
                $"{theme}: Slider 创建可用宽度的实际 Track", new { TrackWidth = sliderTrack?.ActualWidth ?? 0 });
            slider.Value = .25;
            var sliderAtQuarter = Render(gallery, GalleryWidth, GalleryHeight);
            slider.Value = .75;
            var sliderAtThreeQuarters = Render(gallery, GalleryWidth, GalleryHeight);
            var sliderDifference = CountDifferentPixels(sliderAtQuarter, sliderAtThreeQuarters);
            Save(sliderAtQuarter, Path.Combine(output, $"controls-{theme}-slider-25.png"));
            Save(sliderAtThreeQuarters, Path.Combine(output, $"controls-{theme}-slider-75.png"));
            check(sliderDifference >= 8, $"{theme}: Slider 值变化会移动实际绘制的拇指", new { DifferingPixels = sliderDifference });

        }
        finally
        {
            owner.Content = originalContent;
            owner.UpdateLayout();
        }
    }


    private static CheckBox CreateCheckBox(bool? value, bool enabled) => new()
    {
        Content = "状态样本",
        IsThreeState = true,
        IsChecked = value,
        IsEnabled = enabled,
        Width = 142,
        Height = 34,
        Margin = new Thickness(2)
    };

    private static void Layout(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static RenderTargetBitmap Render(FrameworkElement element, int width, int height)
    {
        element.ApplyTemplate();
        Layout(element, width, height);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        return bitmap;
    }

    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static IEnumerable<DependencyObject> FindDescendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var nested in FindDescendants(child)) yield return nested;
        }
    }

    private static FrameworkElement? FindTextVisual(DependencyObject parent) => FindDescendants(parent)
        .OfType<FrameworkElement>()
        .FirstOrDefault(element => element is AccessText or TextBlock);

    private static Brush? ForegroundOf(FrameworkElement textVisual) => textVisual is TextBlock textBlock
        ? textBlock.Foreground
        : TextElement.GetForeground(textVisual);

    private static Rect BoundsInAncestor(FrameworkElement element, FrameworkElement ancestor)
    {
        var origin = element.TranslatePoint(new Point(0, 0), ancestor);
        return new Rect(origin, new Size(element.ActualWidth, element.ActualHeight));
    }

    private static Rect BoundsInBitmap(FrameworkElement element, FrameworkElement bitmapRoot)
    {
        var local = BoundsInAncestor(element, bitmapRoot);
        var rootOffset = VisualTreeHelper.GetOffset(bitmapRoot);
        return new Rect(local.X + rootOffset.X, local.Y + rootOffset.Y, local.Width, local.Height);
    }

    private static BitmapSource Crop(BitmapSource bitmap, Rect bounds)
    {
        var left = Math.Clamp((int)Math.Floor(bounds.Left), 0, bitmap.PixelWidth - 1);
        var top = Math.Clamp((int)Math.Floor(bounds.Top), 0, bitmap.PixelHeight - 1);
        var right = Math.Clamp((int)Math.Ceiling(bounds.Right), left + 1, bitmap.PixelWidth);
        var bottom = Math.Clamp((int)Math.Ceiling(bounds.Bottom), top + 1, bitmap.PixelHeight);
        var crop = new CroppedBitmap(bitmap, new Int32Rect(left, top, right - left, bottom - top));
        crop.Freeze();
        return crop;
    }

    private static int CountDifferentPixels(BitmapSource first, BitmapSource second)
    {
        if (first.PixelWidth != second.PixelWidth || first.PixelHeight != second.PixelHeight) return int.MaxValue;
        var stride = first.PixelWidth * 4;
        var a = new byte[stride * first.PixelHeight];
        var b = new byte[stride * second.PixelHeight];
        first.CopyPixels(a, stride, 0);
        second.CopyPixels(b, stride, 0);
        var count = 0;
        for (var i = 0; i < a.Length; i += 4)
            if (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) + Math.Abs(a[i + 3] - b[i + 3]) > 20)
                count++;
        return count;
    }

    private static int CountColumnPixelsDifferentFrom(BitmapSource bitmap, int x, int top, int bottom, Color baseline, int threshold)
    {
        x = Math.Clamp(x, 0, bitmap.PixelWidth - 1);
        top = Math.Clamp(top, 0, bitmap.PixelHeight - 1);
        bottom = Math.Clamp(bottom, top + 1, bitmap.PixelHeight);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        var rows = 0;
        for (var y = top; y < bottom; y++)
        {
            var index = (y * bitmap.PixelWidth + x) * 4;
            if (pixels[index + 3] < 200) continue;
            var actual = Color.FromArgb(pixels[index + 3], pixels[index + 2], pixels[index + 1], pixels[index]);
            var difference = Math.Abs(actual.R - baseline.R) + Math.Abs(actual.G - baseline.G) + Math.Abs(actual.B - baseline.B);
            if (difference >= threshold) rows++;
        }
        return rows;
    }

    private static int CountPixelsDistinctFrom(BitmapSource bitmap, Rect bounds, Color background, int threshold)
    {
        var left = Math.Clamp((int)Math.Floor(bounds.Left), 0, bitmap.PixelWidth - 1);
        var top = Math.Clamp((int)Math.Floor(bounds.Top), 0, bitmap.PixelHeight - 1);
        var right = Math.Clamp((int)Math.Ceiling(bounds.Right), left + 1, bitmap.PixelWidth);
        var bottom = Math.Clamp((int)Math.Ceiling(bounds.Bottom), top + 1, bitmap.PixelHeight);
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        var count = 0;
        for (var y = top; y < bottom; y++)
        for (var x = left; x < right; x++)
        {
            var i = y * stride + x * 4;
            if (pixels[i + 3] < 220) continue;
            var delta = Math.Abs(pixels[i + 2] - background.R) + Math.Abs(pixels[i + 1] - background.G) + Math.Abs(pixels[i] - background.B);
            if (delta >= threshold) count++;
        }
        return count;
    }

    private static Color GetPixelColor(BitmapSource bitmap, int x, int y)
    {
        x = Math.Clamp(x, 0, bitmap.PixelWidth - 1);
        y = Math.Clamp(y, 0, bitmap.PixelHeight - 1);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        var i = (y * bitmap.PixelWidth + x) * 4;
        var alpha = pixels[i + 3];
        if (alpha == 0) return Colors.Transparent;
        byte Unpremultiply(byte value) => (byte)Math.Clamp((value * 255 + alpha / 2) / alpha, 0, 255);
        return Color.FromArgb(alpha, Unpremultiply(pixels[i + 2]), Unpremultiply(pixels[i + 1]), Unpremultiply(pixels[i]));
    }

    private static Color GetSolidColor(object brush) => brush is SolidColorBrush solid ? solid.Color : Colors.Transparent;

    private static double Contrast(Color foreground, Color background)
    {
        static double Linear(byte value) { var c = value / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
        static double Luma(Color color) => .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        var a = Luma(foreground); var b = Luma(background);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
}
