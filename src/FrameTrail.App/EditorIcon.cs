using System.Windows;
using System.Windows.Media;
namespace FrameTrail.App;
public sealed class EditorIcon : FrameworkElement
{
 public static readonly DependencyProperty KindProperty=DependencyProperty.Register(nameof(Kind),typeof(string),typeof(EditorIcon),new FrameworkPropertyMetadata("frame",FrameworkPropertyMetadataOptions.AffectsRender));
 public static readonly DependencyProperty StrokeProperty=DependencyProperty.Register(nameof(Stroke),typeof(Brush),typeof(EditorIcon),new FrameworkPropertyMetadata(null,FrameworkPropertyMetadataOptions.AffectsRender));
 public string Kind { get=>(string)GetValue(KindProperty);set=>SetValue(KindProperty,value); }
 public Brush? Stroke { get=>(Brush?)GetValue(StrokeProperty);set=>SetValue(StrokeProperty,value); }
 public EditorIcon(){Width=19;Height=19;IsHitTestVisible=false;}
 static readonly Dictionary<string,string> Paths=new(){
 ["folder"]="M2,6 L8,6 10,8 22,8 22,20 2,20 Z M2,6 L2,4 9,4 11,6 20,6",
 ["frame"]="M3,3 L18,3 18,6 M7,7 L22,7 22,21 7,21 Z M8,18 L12,13 16,17 18,14 21,18 M3,3 L3,17 5,17",
 ["opening"]="M4,6 L20,6 20,21 4,21 Z M4,10 L20,10 M7,2 L17,2 M12,12 L12,18 M9,15 L15,15",
 ["zoom"]="M3,9 L3,3 9,3 M15,3 L21,3 21,9 M21,15 L21,21 15,21 M9,21 L3,21 3,15",
 ["cursor"]="M4,2 L4,21 9,16 13,23 16,21 12,15 20,15 Z",
 ["camera"]="M3,6 L15,6 15,19 3,19 Z M15,10 L22,6 22,19 15,15 Z",
 ["audio"]="M3,9 L7,9 13,4 13,21 7,16 3,16 Z M17,8 C21,12 21,13 17,17 M20,4 C26,10 26,15 20,21",
 ["screen"]="M2,3 L22,3 22,17 2,17 Z M8,22 L16,22 M12,17 L12,22",
 ["window"]="M3,3 L21,3 21,21 3,21 Z M3,8 L21,8 M6,6 L6.1,6 M9,6 L9.1,6",
 ["region"]="M3,8 L3,3 8,3 M12,3 L16,3 M20,3 L22,3 22,8 M22,12 L22,16 M22,20 L22,22 17,22 M13,22 L9,22 M5,22 L3,22 3,17 M3,13 L3,10",
 ["device"]="M7,2 L17,2 17,22 7,22 Z M10,5 L14,5 M11,19 L13,19",
 ["mic"]="M8,6 C8,0 16,0 16,6 L16,13 C16,18 8,18 8,13 Z M5,11 L5,14 C5,23 19,23 19,14 L19,11 M12,21 L12,24 M8,24 L16,24",
 ["export"]="M12,15 L12,1 M7,6 L12,1 17,6 M7,9 L4,9 4,23 20,23 20,9 17,9",
 ["trash"]="M3,6 L21,6 M6,6 L7,22 17,22 18,6 M8,3 L16,3 M10,10 L10,18 M14,10 L14,18",
 ["undo"]="M8,4 L2,10 8,16 M3,10 L15,10 C24,10 24,22 15,22",
 ["redo"]="M16,4 L22,10 16,16 M21,10 L9,10 C0,10 0,22 9,22",
 ["preset"]="M2,3 L11,3 11,13 2,13 Z M15,3 L22,3 22,9 15,9 Z M2,17 L11,17 11,22 2,22 Z M15,13 L22,13 22,22 15,22 Z",
 ["settings"]="M9,2 L15,2 16,6 20,6 23,11 20,14 20,18 15,21 12,19 8,21 3,18 4,14 1,11 4,6 8,6 Z M16,12 A4,4 0 1 1 8,12 A4,4 0 1 1 16,12",
 ["sun"]="M16,12 A4,4 0 1 1 8,12 A4,4 0 1 1 16,12 M12,1 L12,4 M12,20 L12,23 M1,12 L4,12 M20,12 L23,12 M4,4 L6,6 M18,18 L20,20 M4,20 L6,18 M18,6 L20,4",
 ["crop"]="M6,1 L6,18 23,18 M1,6 L18,6 18,23",["plus"]="M12,3 L12,21 M3,12 L21,12",["minus"]="M3,12 L21,12",
 ["sliders"]="M2,5 L22,5 M2,12 L22,12 M2,19 L22,19 M7,2 L7,8 M17,9 L17,15 M9,16 L9,22",
 ["split"]="M7,6 A3,3 0 1 1 1,6 A3,3 0 1 1 7,6 M7,18 A3,3 0 1 1 1,18 A3,3 0 1 1 7,18 M7,8 L22,20 M7,16 L22,4",
 ["play"]="M7,3 L21,12 7,21 Z",["pause"]="M7,3 L7,21 M17,3 L17,21",["previous"]="M5,4 L5,20 M20,4 L8,12 20,20 Z",["next"]="M19,4 L19,20 M4,4 L16,12 4,20 Z",
 ["eye"]="M1,12 Q12,-1 23,12 Q12,25 1,12 M16,12 A4,4 0 1 1 8,12 A4,4 0 1 1 16,12",
 ["magnet"]="M4,3 L4,14 C4,26 20,26 20,14 L20,3 15,3 15,14 C15,19 9,19 9,14 L9,3 Z",
 ["close"]="M4,4 L20,20 M20,4 L4,20",["chevron"]="M5,9 L12,16 19,9",["pencil"]="M3,17 L17,3 21,7 7,21 2,22 Z M14,6 L18,10",
 ["memo"]="M3,3 L21,3 21,21 3,21 Z M7,7 L17,7 M7,11 L17,11 M7,15 L14,15",
 ["cube"]="M12,1 L23,7 23,18 12,24 1,18 1,7 Z M1,7 L12,13 23,7 M12,13 L12,24 M6,4 L18,10",
 ["flag"]="M5,23 L5,2 20,2 16,8 20,14 5,14",["reset"]="M3,2 L3,9 10,9 M3,9 C4,-1 23,0 23,12 C23,24 5,27 2,16",
 ["wave"]="M2,10 L2,15 M6,6 L6,19 M10,2 L10,23 M14,7 L14,18 M18,4 L18,21 M22,9 L22,15",["aspect"]="M2,4 L22,4 22,20 2,20 Z M7,4 L7,20 M17,4 L17,20",["layers"]="M12,2 L23,8 12,14 1,8 Z M2,13 L12,19 22,13 M2,18 L12,24 22,18"
 };
 protected override void OnRender(DrawingContext dc){var b=Stroke??(Brush?)TryFindResource("Ink")??Brushes.DimGray;var p=new Pen(b,1.65){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round,LineJoin=PenLineJoin.Round};if(!Paths.TryGetValue(Kind,out var path))path=Paths["frame"];dc.PushTransform(new ScaleTransform(ActualWidth/24,ActualHeight/24));dc.DrawGeometry(Kind=="play"?b:null,p,Geometry.Parse(path));dc.Pop();}
}
public sealed class DotGrid:FrameworkElement
{
 public DotGrid(){IsHitTestVisible=false;}
 protected override void OnRender(DrawingContext dc){var b=(Brush?)TryFindResource("Dot")??Brushes.LightGray;for(double x=12;x<ActualWidth;x+=22)for(double y=12;y<ActualHeight;y+=22)dc.DrawEllipse(b,null,new Point(x,y),.55,.55);}
}
