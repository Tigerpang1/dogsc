using System.Windows;
using System.Windows.Media;
namespace FrameTrail.App;
public sealed class TiltPadVisual:FrameworkElement
{
 public NormalizedPoint Position{get;set;}=new(.5,.5);
 public TiltPadVisual(){IsHitTestVisible=false;}
 protected override void OnRender(DrawingContext dc)
 {
  var rx=(Position.Y-.5)*Math.PI/2;var ry=(Position.X-.5)*Math.PI/2;
  Point Project(double x,double y){var z=-x*Math.Sin(ry)+y*Math.Sin(rx)*Math.Cos(ry);var px=x*Math.Cos(ry)+y*Math.Sin(rx)*Math.Sin(ry);var py=y*Math.Cos(rx);var k=1/(1+z*.35);return new(ActualWidth/2+px*k*ActualWidth*.37,ActualHeight/2+py*k*ActualHeight*.34);}
  var geometry=new StreamGeometry();using(var c=geometry.Open()){c.BeginFigure(Project(-1,-1),true,true);c.LineTo(Project(1,-1),true,false);c.LineTo(Project(1,1),true,false);c.LineTo(Project(-1,1),true,false);}dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(22,255,255,255)),new Pen(new SolidColorBrush(Color.FromArgb(75,75,77,79)),1.2),geometry);
 }
}
