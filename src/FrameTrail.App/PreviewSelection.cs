using System.Windows;
using System.Windows.Media;
namespace FrameTrail.App;
public sealed class PreviewSelection : FrameworkElement
{
 public Rect Selection {get;set;}=Rect.Empty;
 public IReadOnlyList<Point>? Quad {get;set;}
 public PreviewSelection(){IsHitTestVisible=false;SizeChanged+=(_,_)=>InvalidateVisual();}
 protected override void OnRender(DrawingContext dc)
 {
  if(Quad is {Count:4} quad){var outline=new Pen(new SolidColorBrush(Color.FromRgb(83,87,87)),1.2);for(var i=0;i<4;i++)dc.DrawLine(outline,quad[i],quad[(i+1)%4]);dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(35,70,75,70)),null,quad[2],10,10);dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(214,218,210)),new Pen(Brushes.White,2.5),quad[2],7.5,7.5);return;}
  if(Selection.IsEmpty)return;var pen=new Pen(new SolidColorBrush(Color.FromRgb(70,169,134)),1.2);dc.DrawRectangle(null,pen,Selection);foreach(var p in new[]{Selection.TopLeft,Selection.TopRight,Selection.BottomLeft,Selection.BottomRight})dc.DrawRoundedRectangle(Brushes.White,pen,new Rect(p.X-4,p.Y-4,8,8),2,2);
 }
}
