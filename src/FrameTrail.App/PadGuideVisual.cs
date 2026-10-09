using System.Windows;
using System.Windows.Media;

namespace FrameTrail.App;

/// <summary>Subtle reference axes for the normalized position and tilt controls.</summary>
public sealed class PadGuideVisual : FrameworkElement
{
    public PadGuideVisual() { IsHitTestVisible = false; }
    protected override void OnRender(DrawingContext dc)
    {
        const double inset = 12;
        var brush = (Brush?)TryFindResource("Ink") ?? Brushes.Black;
        var minor = new Pen(brush, .75) { DashStyle = new DashStyle([3, 4], 0) };
        dc.PushOpacity(.08);
        foreach (var fraction in new[] { 1d / 3d, 2d / 3d })
        {
            var x = inset + (ActualWidth - inset * 2) * fraction;
            var y = inset + (ActualHeight - inset * 2) * fraction;
            dc.DrawLine(minor, new(x, inset), new(x, ActualHeight - inset));
            dc.DrawLine(minor, new(inset, y), new(ActualWidth - inset, y));
        }
        dc.Pop();

        var center = new Pen(brush, .75) { DashStyle = new DashStyle([3, 4], 0) };
        dc.PushOpacity(.14);
        dc.DrawLine(center, new(ActualWidth / 2, inset), new(ActualWidth / 2, ActualHeight - inset));
        dc.DrawLine(center, new(inset, ActualHeight / 2), new(ActualWidth - inset, ActualHeight / 2));
        dc.Pop();
    }
}
