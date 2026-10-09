using System.Windows;
using System.Windows.Media;

namespace FrameTrail.App;

/// <summary>Subtle reference axes for the normalized position and tilt controls.</summary>
public sealed class PadGuideVisual : FrameworkElement
{
    public PadGuideVisual() { IsHitTestVisible = false; }
    protected override void OnRender(DrawingContext dc)
    {
        var brush = (Brush?)TryFindResource("Secondary") ?? Brushes.Gray;
        dc.PushOpacity(.17);
        foreach (var fraction in new[] { .25, .5, .75 })
        {
            var pen = new Pen(brush, fraction == .5 ? .8 : .55) { DashStyle = new DashStyle([2,4],0) };
            dc.DrawLine(pen,new(12,ActualHeight*fraction),new(ActualWidth-12,ActualHeight*fraction));
            dc.DrawLine(pen,new(ActualWidth*fraction,12),new(ActualWidth*fraction,ActualHeight-12));
        }
        dc.Pop();
    }
}
