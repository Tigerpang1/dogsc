using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FrameTrail.App;

/// <summary>Switch feedback follows the committed value, including interrupted clicks.</summary>
public static class ControlMotion
{
    public static readonly DependencyProperty AnimateSwitchProperty = DependencyProperty.RegisterAttached(
        "AnimateSwitch", typeof(bool), typeof(ControlMotion), new PropertyMetadata(false, Changed));
    public static bool GetAnimateSwitch(DependencyObject value) => (bool)value.GetValue(AnimateSwitchProperty);
    public static void SetAnimateSwitch(DependencyObject value, bool enabled) => value.SetValue(AnimateSwitchProperty, enabled);

    private static void Changed(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is not CheckBox control) return;
        if ((bool)args.NewValue)
        {
            control.Loaded += Loaded;
            control.Checked += Toggled;
            control.Unchecked += Toggled;
            control.Indeterminate += Toggled;
        }
        else
        {
            control.Loaded -= Loaded;
            control.Checked -= Toggled;
            control.Unchecked -= Toggled;
            control.Indeterminate -= Toggled;
        }
    }

    private static void Loaded(object sender, RoutedEventArgs args) => Position((CheckBox)sender, false);
    private static void Toggled(object sender, RoutedEventArgs args) => Position((CheckBox)sender, true);

    private static void Position(CheckBox control, bool animate)
    {
        control.ApplyTemplate();
        if (control.Template?.FindName("SwitchThumb", control) is not FrameworkElement thumb
            || thumb.RenderTransform is not TranslateTransform transform) return;
        // WPF freezes template Freezables. Each switch needs its own mutable
        // transform, otherwise a checked setting can fail before the dialog opens.
        if (transform.IsFrozen)
        {
            transform = transform.CloneCurrentValue();
            thumb.RenderTransform = transform;
        }
        var target = control.IsChecked == true ? 16d : 0d;
        var current = transform.X;
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.X = target;
        if (!animate || !control.IsLoaded || !SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast) return;
        transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop
        }, HandoffBehavior.SnapshotAndReplace);
    }
}
