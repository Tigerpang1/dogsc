using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FrameTrail.App;

public partial class MainWindow
{
    private void BuildCursorInspector()
    {
        var p = project!;
        var cursor = p.Settings.Cursor ?? new() { Visible = p.Settings.CursorVisible };
        Section("光标外观");
        ChoiceCards("样式", cursor.Style,
        [
            (CursorStyle.SystemArrow, "箭头", "cursor"),
            (CursorStyle.Dot, "圆点", "dot"),
            (CursorStyle.Ring, "圆环", "ring"),
            (CursorStyle.FollowRecorded, "跟随录制", "cursor"),
            (CursorStyle.SystemHand, "手型", "hand"),
            (CursorStyle.SystemText, "文本", "text")
        ], value => ApplyEdit(x => CursorSettingsFor(x).Style = value), columns: 3, preview: BuildCursorStylePreview);

        Toggle("显示光标", cursor.Visible, value => ApplyEdit(x =>
        {
            CursorSettingsFor(x).Visible = value;
            x.Settings.CursorVisible = value;
        }));
        Number("大小", cursor.Scale, .3, 4, value => ApplyEdit(x => CursorSettingsFor(x).Scale = value), "×");
        Choices("移动平滑", p.Settings.PointerSmoothing,
            new[] { (PointerSmoothing.Smooth, "平滑"), (PointerSmoothing.Medium, "适中"), (PointerSmoothing.Rapid, "灵敏") },
            value => ApplyEdit(x => x.Settings.PointerSmoothing = value));
        Toggle("移动时倾斜", cursor.MotionTiltEnabled, value => ApplyEdit(x => CursorSettingsFor(x).MotionTiltEnabled = value));
        if (cursor.MotionTiltEnabled)
            Choices("倾斜强度", cursor.MotionTiltStrength,
                new[] { (2d, "自然"), (3d, "明显"), (4d, "强烈") },
                value => ApplyEdit(x => CursorSettingsFor(x).MotionTiltStrength = value));
        Toggle("静止后隐藏", cursor.HideWhenIdle, value => ApplyEdit(x => CursorSettingsFor(x).HideWhenIdle = value));
        if (cursor.HideWhenIdle)
            Number("静止隐藏延迟", cursor.IdleSeconds, .2, 10, value => ApplyEdit(x => CursorSettingsFor(x).IdleSeconds = value), " s");

        Section("点击反馈");
        Toggle("显示点击效果", cursor.ClickEffectsEnabled, value => ApplyEdit(x => CursorSettingsFor(x).ClickEffectsEnabled = value));
        Choices("效果", cursor.ClickEffect,
            new[] { (ClickEffectStyle.Pulse, "脉冲"), (ClickEffectStyle.Ring, "柔光"), (ClickEffectStyle.Ripple, "涟漪"), (ClickEffectStyle.Burst, "迸发"), (ClickEffectStyle.None, "无") },
            value => ApplyEdit(x => CursorSettingsFor(x).ClickEffect = value));
        if (cursor.ClickEffectsEnabled && cursor.ClickEffect != ClickEffectStyle.None)
        {
            TextField("颜色", cursor.ClickColor, value => ApplyEdit(x => CursorSettingsFor(x).ClickColor = ValidColor(value)));
            Number("持续时间", cursor.ClickDuration, .05, 2, value => ApplyEdit(x => CursorSettingsFor(x).ClickDuration = value), " s");
            Number("点击范围", cursor.ClickScale, .4, 3, value => ApplyEdit(x => CursorSettingsFor(x).ClickScale = value), "×");
            Number("点击不透明度", cursor.ClickOpacity * 100, 5, 100, value => ApplyEdit(x => CursorSettingsFor(x).ClickOpacity = value / 100), "%", "0");
        }

        InspectorDisclosure("时间对齐", () =>
        {
            var offsetMs = p.MouseClock.OffsetSeconds * 1000;
            Number("光标时间偏移", offsetMs, -10000, 10000, SetMouseOffsetMilliseconds, " ms", "0");
            var row = new UniformGrid { Columns = 3, Margin = new Thickness(-3, 2, -3, 4) };
            row.Children.Add(TimingStepButton("光标提前 10 毫秒", "提前 10 ms", () => SetMouseOffsetMilliseconds(offsetMs - 10)));
            row.Children.Add(TimingStepButton("光标延后 10 毫秒", "延后 10 ms", () => SetMouseOffsetMilliseconds(offsetMs + 10)));
            row.Children.Add(TimingStepButton("归零光标时间偏移", "归零", () => SetMouseOffsetMilliseconds(0)));
            InspectorHost.Children.Add(row);
            Note("负值让光标与点击提前，正值让它们延后；不改变移动手感，也不移动画面或声音。");
        });
    }

    private Button TimingStepButton(string automationName, string label, Action action)
    {
        var button = new Button
        {
            Content = label,
            Padding = new Thickness(5, 7, 5, 7),
            Margin = new Thickness(2),
            Background = Brush("ControlSurface")
        };
        AutomationProperties.SetName(button, automationName);
        button.Click += (_, _) => action();
        return button;
    }

    private void SetMouseOffsetMilliseconds(double milliseconds)
    {
        ApplyEdit(x =>
        {
            x.MouseClock = x.MouseClock with { OffsetSeconds = Math.Clamp(milliseconds / 1000, -10, 10) };
            if (x.ZoomClips.All(clip => clip.Automatic))
                x.ZoomClips = AutoZoomPlanner.Plan(x.PointerEvents.Select(pointer => pointer with { Time = x.MouseClock.ToSourceTime(pointer.Time) }));
        });
    }

    private FrameworkElement BuildCursorStylePreview(CursorStyle style)
    {
        var canvas = new Canvas { Width = 44, Height = 36, HorizontalAlignment = HorizontalAlignment.Center };
        if (style is CursorStyle.SystemArrow or CursorStyle.FollowRecorded)
        {
            var arrow = new Polygon
            {
                Points = new PointCollection { new(5, 3), new(5, 27), new(11, 21), new(17, 32), new(22, 29), new(16, 19), new(25, 18) },
                Fill = Brush("Ink"),
                Stroke = Brushes.White,
                StrokeThickness = 1.5
            };
            canvas.Children.Add(arrow);
        }
        else if (style == CursorStyle.SystemHand)
        {
            var hand = new TextBlock { Text = "☝", FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 29, Foreground = Brush("Ink"), Width = 44, TextAlignment = TextAlignment.Center };
            Canvas.SetTop(hand, -1); canvas.Children.Add(hand);
        }
        else if (style == CursorStyle.SystemText)
        {
            var beam = new Line { X1 = 22, X2 = 22, Y1 = 5, Y2 = 31, Stroke = Brush("Ink"), StrokeThickness = 2 };
            canvas.Children.Add(beam);
            canvas.Children.Add(new Line { X1 = 14, X2 = 30, Y1 = 6, Y2 = 6, Stroke = Brush("Ink"), StrokeThickness = 2 });
            canvas.Children.Add(new Line { X1 = 14, X2 = 30, Y1 = 30, Y2 = 30, Stroke = Brush("Ink"), StrokeThickness = 2 });
        }
        else if (style == CursorStyle.Dot)
        {
            var halo = new Ellipse { Width = 25, Height = 25, Fill = Brush("Selection"), Stroke = Brush("Accent"), StrokeThickness = 2 };
            Canvas.SetLeft(halo, 9.5); Canvas.SetTop(halo, 5.5); canvas.Children.Add(halo);
            var dot = new Ellipse { Width = 11, Height = 11, Fill = Brush("Ink"), Stroke = Brushes.White, StrokeThickness = 1.5 };
            Canvas.SetLeft(dot, 16.5); Canvas.SetTop(dot, 12.5); canvas.Children.Add(dot);
        }
        else
        {
            var ring = new Ellipse { Width = 25, Height = 25, Fill = Brushes.Transparent, Stroke = Brush("Accent"), StrokeThickness = 3 };
            Canvas.SetLeft(ring, 9.5); Canvas.SetTop(ring, 5.5); canvas.Children.Add(ring);
            var dot = new Ellipse { Width = 7, Height = 7, Fill = Brush("Ink") };
            Canvas.SetLeft(dot, 18.5); Canvas.SetTop(dot, 14.5); canvas.Children.Add(dot);
        }
        return canvas;
    }
}
