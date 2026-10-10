using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace FrameTrail.App;

/// <summary>Edits time/value handles; commits one undo operation when a drag ends.</summary>
public sealed class MotionCurveEditor : FrameworkElement
{
    private const double Inset = 16;
    private ZoomBezierCurve curve = ZoomBezierCurve.Cubic;
    private ZoomBezierCurve? beforeDrag;
    private int dragging;
    public event Action<ZoomBezierCurve>? CurveCommitted;
    public ZoomBezierCurve Curve
    {
        get => curve;
        set { curve = value with { }; InvalidateVisual(); }
    }

    public MotionCurveEditor()
    {
        Height = 154;
        MinWidth = 140;
        Focusable = true;
        SetResourceReference(FocusVisualStyleProperty, "SurfaceKeyboardFocus");
        Cursor = Cursors.Cross;
        ToolTip = "拖动控制点调整曲线；Esc 取消。下方数值可精确调整。";
        System.Windows.Automation.AutomationProperties.SetName(this, "自定义运动曲线图");
    }

    private Point At(double x, double y) => new(Inset + x * Math.Max(1, ActualWidth - 2 * Inset), ActualHeight - Inset - y * Math.Max(1, ActualHeight - 2 * Inset));
    private Brush Color(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var ink = Color("Ink", Brushes.DimGray);
        var accent = Color("Accent", Brushes.SeaGreen);
        var thin = new Pen(Color("Hairline", Brushes.LightGray), 1);
        dc.DrawRoundedRectangle(Color("ControlSurface", Brushes.WhiteSmoke), null, new Rect(RenderSize), 12, 12);
        for (var i = 0; i <= 4; i++)
        {
            dc.DrawLine(thin, At(i / 4d, 0), At(i / 4d, 1));
            dc.DrawLine(thin, At(0, i / 4d), At(1, i / 4d));
        }
        var first = At(curve.X1, curve.Y1);
        var second = At(curve.X2, curve.Y2);
        dc.DrawLine(new Pen(accent, 1), At(0, 0), first);
        dc.DrawLine(new Pen(accent, 1), At(1, 1), second);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(At(0, 0), false, false);
            context.BezierTo(first, second, At(1, 1), true, false);
        }
        dc.DrawGeometry(null, new Pen(ink, 2), geometry);
        dc.DrawEllipse(Color("Chrome", Brushes.White), new Pen(accent, 2), first, 5, 5);
        dc.DrawEllipse(Color("Chrome", Brushes.White), new Pen(accent, 2), second, 5, 5);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var p = e.GetPosition(this);
        var d1 = (p - At(curve.X1, curve.Y1)).Length;
        var d2 = (p - At(curve.X2, curve.Y2)).Length;
        if (Math.Min(d1, d2) > 18) return;
        dragging = d1 <= d2 ? 1 : 2;
        beforeDrag = curve with { };
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging == 0 || !IsMouseCaptured) return;
        var p = e.GetPosition(this);
        var x = Math.Clamp((p.X - Inset) / Math.Max(1, ActualWidth - 2 * Inset), 0, 1);
        var y = Math.Clamp((ActualHeight - Inset - p.Y) / Math.Max(1, ActualHeight - 2 * Inset), 0, 1);
        Curve = dragging == 1 ? curve with { X1 = x, Y1 = y } : curve with { X2 = x, Y2 = y };
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (dragging == 0) return;
        var previous = beforeDrag;
        dragging = 0;
        beforeDrag = null;
        ReleaseMouseCapture();
        if (previous != curve) CurveCommitted?.Invoke(curve with { });
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && beforeDrag != null)
        {
            Curve = beforeDrag.Value;
            dragging = 0;
            beforeDrag = null;
            ReleaseMouseCapture();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (dragging != 0 && beforeDrag != null) Curve = beforeDrag.Value;
        dragging = 0;
        beforeDrag = null;
        base.OnLostMouseCapture(e);
    }
}
