using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FrameTrail.App;

public partial class MainWindow
{
    private void BuildOpeningInspector()
    {
        var opening = project!.Settings.Opening ?? new OpeningSettings();
        Section("全局开场");
        Note("让所有元素有序进入画面。");
        var enabled = new CheckBox
        {
            Content = "启用全局开场",
            IsChecked = opening.Enabled,
            Style = (Style)FindResource("InspectorToggle"),
            Margin = new Thickness(0, 4, 0, 10)
        };
        AutomationProperties.SetName(enabled, "启用全局开场");
        RoutedEventHandler enabledChanged = (_, _) =>
        {
            if (!buildingInspector) ApplyEdit(p => (p.Settings.Opening ??= new()).Enabled = enabled.IsChecked == true);
        };
        enabled.Checked += enabledChanged;
        enabled.Unchecked += enabledChanged;
        InspectorHost.Children.Add(enabled);

        if (!opening.Enabled) return;

        var presets = new[]
        {
            (OpeningPreset.Custom, "自定", "sparkles"),
            (OpeningPreset.ZoomUp, "放大入场", "zoom"),
            (OpeningPreset.Fade, "淡入", "eye"),
            (OpeningPreset.SlideUp, "上升", "arrow-up"),
            (OpeningPreset.RotateIn, "旋转入场", "rotate"),
            (OpeningPreset.Converge, "汇聚", "merge"),
            (OpeningPreset.SideSlide, "侧向滑入", "arrow-right"),
            (OpeningPreset.Light3D, "轻 3D", "cube")
        };
        ChoiceCards("开场方式", opening.Preset, presets,
            value => ApplyEdit(p => (p.Settings.Opening ??= new()).Preset = value),
            columns: 3, preview: BuildOpeningPresetPreview);

        Section("节奏");
        Number("总时长", opening.Duration, .1, 5,
            value => ApplyEdit(p => (p.Settings.Opening ??= new()).Duration = value), " s");
        ChoiceCards("动效曲线", opening.Easing,
            Easings().Select(x => (x.Item1, x.Item2, x.Item1 switch
            {
                ZoomEasing.Linear => "sliders",
                ZoomEasing.Cubic => "zoom",
                ZoomEasing.Quintic => "preset",
                _ => "wave"
            })),
            value => ApplyEdit(p => (p.Settings.Opening ??= new()).Easing = value), columns: 3);

        InspectorDisclosure("参与元素与顺序", () =>
        {
            var order = opening.ElementOrder.Concat(Enum.GetValues<OpeningElement>()).Distinct().ToArray();
            foreach (var element in order)
            {
                var item = element;
                var label = OpeningElementLabel(element);
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var toggle = new CheckBox { Content = label, IsChecked = opening.IncludedElements.Contains(element), VerticalAlignment = VerticalAlignment.Center };
                AutomationProperties.SetName(toggle, label + "参与开场");
                toggle.Click += (_, _) => ApplyEdit(p =>
                {
                    var settings = p.Settings.Opening ??= new();
                    if (toggle.IsChecked == true && !settings.IncludedElements.Contains(item)) settings.IncludedElements.Add(item);
                    else if (toggle.IsChecked != true) settings.IncludedElements.Remove(item);
                });
                row.Children.Add(toggle);
                var up = DisclosureOrderButton(label + "提前", "↑", () => MoveOpeningElement(item, -1));
                Grid.SetColumn(up, 1); row.Children.Add(up);
                var down = DisclosureOrderButton(label + "延后", "↓", () => MoveOpeningElement(item, 1));
                Grid.SetColumn(down, 2); row.Children.Add(down);
                InspectorHost.Children.Add(row);
            }
            Number("元素间隔", opening.Stagger, 0, 1,
                value => ApplyEdit(p => (p.Settings.Opening ??= new()).Stagger = value), " s");
        });

        var start = opening.ResolvePreset();
        InspectorDisclosure("高级起始姿态", () =>
        {
            Number("初始大小", start.ScaleFrom, .1, 2, value => EditOpeningCustom(x => x.ScaleFrom = value), "×");
            Number("水平位移", start.OffsetX, -1, 1, value => EditOpeningCustom(x => x.OffsetX = value));
            Number("垂直位移", start.OffsetY, -1, 1, value => EditOpeningCustom(x => x.OffsetY = value));
            Number("初始旋转", start.RotationFrom, -180, 180, value => EditOpeningCustom(x => x.RotationFrom = value), "°");
            Number("初始不透明度", start.OpacityFrom, 0, 1, value => EditOpeningCustom(x => x.OpacityFrom = value));
        });

        var previewButton = new Button
        {
            Content = "从片头预览",
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 4),
            Style = (Style)FindResource("PrimaryButton")
        };
        AutomationProperties.SetName(previewButton, "从片头预览");
        previewButton.Click += (_, _) => { Playhead.Value = 0; PlayClick(this, new()); };
        InspectorHost.Children.Add(previewButton);
    }

    private static string OpeningElementLabel(OpeningElement element) => element switch
    {
        OpeningElement.Screen => "屏幕",
        OpeningElement.Camera => "摄像头",
        _ => "贴图"
    };

    private Button DisclosureOrderButton(string name, string glyph, Action action)
    {
        var button = new Button
        {
            Content = glyph,
            Width = 30,
            Height = 26,
            Padding = new Thickness(0),
            Margin = new Thickness(2),
            Style = (Style)FindResource("PlainButton")
        };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => action();
        return button;
    }

    private void MoveOpeningElement(OpeningElement element, int direction)
    {
        ApplyEdit(p =>
        {
            var settings = p.Settings.Opening ??= new();
            var order = settings.ElementOrder.Concat(Enum.GetValues<OpeningElement>()).Distinct().ToList();
            var index = order.IndexOf(element);
            var target = Math.Clamp(index + direction, 0, order.Count - 1);
            if (target == index) return;
            (order[index], order[target]) = (order[target], order[index]);
            settings.ElementOrder = order;
        });
    }

    private FrameworkElement BuildOpeningPresetPreview(OpeningPreset preset)
    {
        var preview = new Grid { Width = 56, Height = 36 };
        preview.Children.Add(new Border
        {
            Background = Brush("Hairline"),
            CornerRadius = new CornerRadius(4),
            BorderBrush = Brush("Secondary"),
            BorderThickness = new Thickness(.5)
        });
        var content = new Border
        {
            Width = 29,
            Height = 18,
            CornerRadius = new CornerRadius(2),
            Background = Brush("Ink"),
            RenderTransformOrigin = new Point(.5, .5),
            Opacity = preset == OpeningPreset.Fade ? .36 : 1
        };
        var scale = preset switch { OpeningPreset.ZoomUp => .65, OpeningPreset.Converge => .62, OpeningPreset.Light3D => .8, _ => 1 };
        var angle = preset == OpeningPreset.RotateIn ? -14 : 0;
        var (x, y) = preset switch
        {
            OpeningPreset.SlideUp => (0d, 8d),
            OpeningPreset.Converge => (-9d, 7d),
            OpeningPreset.SideSlide or OpeningPreset.Light3D => (-12d, 0d),
            _ => (0d, 0d)
        };
        var transforms = new TransformGroup();
        transforms.Children.Add(new ScaleTransform(scale, scale));
        transforms.Children.Add(new RotateTransform(angle));
        if (preset == OpeningPreset.Light3D) transforms.Children.Add(new SkewTransform(-12, 3));
        transforms.Children.Add(new TranslateTransform(x, y));
        content.RenderTransform = transforms;
        preview.Children.Add(content);
        return preview;
    }

    private void EditOpeningCustom(Action<OpeningSettings> edit) => ApplyEdit(p =>
    {
        var value = (p.Settings.Opening ?? new()).ResolvePreset() with { Preset = OpeningPreset.Custom };
        edit(value);
        p.Settings.Opening = value;
    });
}
