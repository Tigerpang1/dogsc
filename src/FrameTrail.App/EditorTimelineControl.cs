using System.Globalization;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
namespace FrameTrail.App;
public sealed record TimelineSelection(string Kind,Guid Id);
public sealed record TimelineChange(TimelineSelection Selection,string Operation,double SourceStart,double SourceDuration,int NewIndex=-1);
public sealed class EditorTimelineControl : FrameworkElement
{
 protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer()=>new System.Windows.Automation.Peers.FrameworkElementAutomationPeer(this);
 internal FrameTrailProject? Project {get;set;}
 internal TimelineAssetData? Assets {get;set;}
 public TimelineSelection? Selection {get;set;}
 public HashSet<TimelineSelection> MultiSelection {get;}=[];
 public HashSet<string> HiddenTracks {get;}=[];
 public double? RangeStart {get;set;}
 public double? RangeEnd {get;set;}
 public event Action? TrackMenuRequested;
 public double Position {get;set;}
 public double Zoom {get;set;}=1;
 public double Offset {get;set;}
 public bool Filmstrip {get;set;}=true;
 public bool Waveform {get;set;}=true;
 public bool Snap {get;set;}=true;
 public bool HoverEnabled {get;set;}=true;
 public double? HoverTime {get;private set;}
 public event Action<double>? SeekRequested;
 public event Action<TimelineSelection>? SelectionRequested;
 public event Action<TimelineChange>? ChangeRequested;
 public event Action<double?>? HoverRequested;
 public event Action<double>? ZoomRequested;
 public event Action<double>? ScrollRequested;
 public event Action? ContextRequested;
 record Hit(Rect Rect,TimelineSelection Selection,double SourceStart,double SourceDuration,double Rate,int Index);
 readonly List<Hit> hits=[];
 Hit? drag; Point down; double dragDelta; string dragMode=""; bool seeking;
 const double LabelWidth=152, RulerHeight=40, PrimaryHeight=90, LaneHeight=52;
 static readonly Brush EffectLabelBrush=CreateEffectLabelBrush();
 bool isHighContrast;
 public double DocumentWidth=>Math.Max(1,ActualWidth-LabelWidth)*Zoom;
 double Scale=>DocumentWidth/Math.Max(Project?.Timeline.Duration??1,.01);
 double X(double t)=>LabelWidth+t*Scale-Offset;
 double Time(double x)=>Math.Clamp((x-LabelWidth+Offset)/Scale,0,Project?.Timeline.Duration??0);
 bool IsHighContrast=>isHighContrast;
 Brush B(string name)
 {
  if(IsHighContrast)return name switch
  {
   "Surface" or "Chrome" or "ControlSurface" or "Workspace"=>SystemColors.WindowBrush,
   "Ink" or "Secondary"=>SystemColors.WindowTextBrush,
   "Hairline" or "Dot"=>SystemColors.GrayTextBrush,
   "Accent" or "Selection"=>SystemColors.HighlightBrush,
   _=>SystemColors.WindowTextBrush
  };
  return (Brush?)TryFindResource(name)??Brushes.Gray;
 }
 public EditorTimelineControl()
 {
  Focusable=true;MinHeight=234;SizeChanged+=(_,_)=>InvalidateVisual();
  if(Application.Current?.TryFindResource("TransientPreferences") is TransientPreferences preferences)
  {
   isHighContrast=preferences.HighContrast;
   PropertyChangedEventManager.AddHandler(preferences,OnPreferencesChanged,string.Empty);
  }
 }
 void OnPreferencesChanged(object? sender,PropertyChangedEventArgs e)
 {
  isHighContrast=(sender as TransientPreferences)?.HighContrast==true;
  InvalidateVisual();
 }
 static Brush CreateEffectLabelBrush(){var brush=new SolidColorBrush(Color.FromRgb(48,50,50));brush.Freeze();return brush;}
 public void Refresh(){var lanes=LaneNames().Count();Height=RulerHeight+PrimaryHeight+lanes*LaneHeight;InvalidateVisual();}
 void Text(DrawingContext dc,string text,Point p,double size=11,Brush? brush=null,FontWeight? weight=null,double maxWidth=double.PositiveInfinity,TextAlignment alignment=TextAlignment.Left)
 { var family=(FontFamily?)TryFindResource("InterfaceFont")??new FontFamily("Microsoft YaHei UI");var f=new FormattedText(text,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface(family,FontStyles.Normal,weight??FontWeights.Normal,FontStretches.Normal),size,brush??B("Secondary"),VisualTreeHelper.GetDpi(this).PixelsPerDip){TextAlignment=alignment};if(double.IsFinite(maxWidth)){f.MaxTextWidth=Math.Max(1,maxWidth);f.Trimming=TextTrimming.CharacterEllipsis;}dc.DrawText(f,p);}
 protected override void OnRender(DrawingContext dc)
 {
  dc.DrawRectangle(B("Surface"),null,new Rect(0,0,ActualWidth,ActualHeight));hits.Clear();var pen=new Pen(B("Hairline"),.65);
  EditorIcon.DrawVector(dc,"layers",new Rect(16,9,16,16),B("Ink"));Text(dc,"轨道",new(38,12),11,B("Ink"));EditorIcon.DrawVector(dc,"chevron",new Rect(LabelWidth-25,10,12,12),B("Secondary"));dc.DrawLine(pen,new(LabelWidth,0),new(LabelWidth,ActualHeight));
  if(Project==null){Text(dc,"录制或导入视频后，片段与效果将在此显示",new(LabelWidth+25,70),12);return;}
  var s=Scale;var step=new[]{.05,.1,.25,.5,1,2,5,10,15,30,60,120,300,600}.FirstOrDefault(x=>x*s>=100);if(step==0)step=1200;
  dc.PushClip(new RectangleGeometry(new Rect(LabelWidth,0,Math.Max(0,ActualWidth-LabelWidth),ActualHeight)));
  for(var t=Math.Floor(Time(LabelWidth)/step)*step;t<=Project.Timeline.Duration;t+=step){var x=X(t);dc.DrawLine(pen,new(x,RulerHeight-12),new(x,ActualHeight));Text(dc,TimeSpan.FromSeconds(t).ToString(@"m\:ss\.ff"),new(x+4,10),10);for(var n=1;n<4;n++)dc.DrawLine(pen,new(x+step*s*n/4,RulerHeight-6),new(x+step*s*n/4,RulerHeight));}
  var elapsed=0d;
  for(var i=0;i<Project.Timeline.Segments.Count;i++)
  {
   var segment=Project.Timeline.Segments[i];var rect=new Rect(X(elapsed)+2,RulerHeight+3,Math.Max(2,segment.OutputDuration*s-4),PrimaryHeight-7);var selected=Selection?.Kind=="screen"&&Selection.Id==segment.Id||MultiSelection.Contains(new("screen",segment.Id));
   dc.DrawRoundedRectangle(B("Chrome"),new Pen(selected?B("Ink"):B("Hairline"),selected?1.6:1),rect,10,10);
   dc.PushClip(new RectangleGeometry(rect,9,9));
   if(Filmstrip&&Assets?.Thumbnails.Count>0){for(double px=rect.X;px<rect.Right;px+=90){var sourceTime=segment.SourceStart+(px-rect.X)/s*segment.PlaybackRate;var image=Assets.Thumbnails.OrderBy(z=>Math.Abs(z.SourceTime-sourceTime)).First().Image;dc.DrawImage(image,new Rect(px,rect.Y,90,58));}}
   else dc.DrawRectangle(IsHighContrast?B("Chrome"):new SolidColorBrush(Color.FromRgb(228,232,230)),null,new Rect(rect.X,rect.Y,rect.Width,58));
   if(Waveform&&Assets?.Peaks.Count>0&&Assets.Duration>0){var peak=Math.Max(.001,Assets.Peaks.Max());var geometry=new StreamGeometry();var mid=rect.Bottom-11d;var left=rect.Left;var right=rect.Right;var waveStep=2d;double Amplitude(double px){var st=segment.SourceStart+(px-rect.X)/s*segment.PlaybackRate;var k=Math.Clamp((int)(st/Assets.Duration*Assets.Peaks.Count),0,Assets.Peaks.Count-1);return Math.Clamp(Assets.Peaks[k]/peak*8,.5,8);}using(var ctx=geometry.Open()){ctx.BeginFigure(new Point(left,mid),true,true);for(var px=left;px<=right;px+=waveStep)ctx.LineTo(new Point(px,mid-Amplitude(px)),true,false);ctx.LineTo(new Point(right,mid),true,false);for(var px=right;px>=left;px-=waveStep)ctx.LineTo(new Point(px,mid+Amplitude(px)),true,false);ctx.LineTo(new Point(left,mid),true,false);}geometry.Freeze();dc.DrawGeometry(IsHighContrast?B("Ink"):new SolidColorBrush(Color.FromRgb(120,125,125)),null,geometry);}
   if(IsHighContrast){dc.DrawRoundedRectangle(B("Chrome"),new Pen(B("Ink"),.8),new Rect(Math.Max(rect.Left+4,rect.Right-60),rect.Top+5,55,19),8,8);Text(dc,TimeSpan.FromSeconds(segment.OutputDuration).ToString(@"m\:ss\.ff"),new(Math.Max(rect.Left+8,rect.Right-55),rect.Top+7),10,B("Ink"));}
   else{dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(145,30,33,33)),null,new Rect(Math.Max(rect.Left+4,rect.Right-60),rect.Top+5,55,19),8,8);Text(dc,TimeSpan.FromSeconds(segment.OutputDuration).ToString(@"m\:ss\.ff"),new(Math.Max(rect.Left+8,rect.Right-55),rect.Top+7),10,Brushes.White);}
   dc.Pop();hits.Add(new(rect,new("screen",segment.Id),segment.SourceStart,segment.SourceDuration,segment.PlaybackRate,i));elapsed+=segment.OutputDuration;
  }
  var y=RulerHeight+PrimaryHeight;
  void Lane(string kind,IEnumerable<(Guid,double,double,string)> clips,Color color){if(HiddenTracks.Contains(kind))return;DrawEffects(dc,kind,clips,y,color);y+=LaneHeight;}
  Lane("zoom",Project.ZoomClips.Select(z=>(z.Id,z.Start,z.End-z.Start,$"缩放 {z.Scale:0.#}× · {(z.Automatic?"自动":"手动")}")),Color.FromRgb(231,214,183));
  Lane("motion",Project.ScreenMotions.Select((z,i)=>(z.Id,z.Start,z.Duration,$"过渡 {i+1}  {TimeSpan.FromSeconds(z.Duration):m\\:ss\\.ff}")),Color.FromRgb(224,162,83));
  if(Project.Camera is {} camera)Lane("camera",new[]{(Guid.Empty,camera.TimelineStart,camera.Duration,"摄像头")},Color.FromRgb(179,200,219));
  if(Project.CameraMotions.Count>0)Lane("cameraMotion",Project.CameraMotions.Select(z=>(z.Id,z.Start,z.Duration,"摄像头运动")),Color.FromRgb(152,181,206));
  if(Project.Overlays.Count>0)Lane("overlay",Project.Overlays.Select(z=>(z.Id,z.Start,z.Duration,"贴图 · "+System.IO.Path.GetFileNameWithoutExtension(z.MediaPath))),Color.FromRgb(188,216,192));
  if(Project.BlurClips.Count>0)Lane("blur",Project.BlurClips.Select(z=>(z.Id,z.Start,z.Duration,z.Kind switch{BlurKind.Blur=>"柔化",BlurKind.Spotlight=>"聚光",_=>"马赛克"})),Color.FromRgb(208,192,220));
  if(Project.AudioTracks.Count>0)Lane("audio",Project.AudioTracks.Select((z,i)=>(AudioId(i),z.OffsetSeconds,z.Duration,z.Muted?"音频 · 静音":"音频 · "+(i+1))),Color.FromRgb(198,200,228));
  foreach(var marker in Project.Markers){var t=Project.Timeline.MapSourceToOutput(marker.SourceTime);if(t is null)continue;var x=X(t.Value);dc.DrawLine(new Pen(IsHighContrast?B("Accent"):new SolidColorBrush(Color.FromRgb(220,159,73)),1.5),new(x,0),new(x,ActualHeight));Text(dc,"⚑",new(x+2,0),14,B("Ink"));}
  if(HoverTime.HasValue){var x=X(HoverTime.Value);dc.DrawLine(new Pen(B("Secondary"),1){DashStyle=DashStyles.Dash},new(x,0),new(x,ActualHeight));}
  var playX=X(Position);dc.DrawLine(new Pen(B("Ink"),1.25),new(playX,5),new(playX,ActualHeight));dc.DrawEllipse(B("Ink"),null,new(playX,7),4,4);
  if(RangeStart.HasValue&&RangeEnd>RangeStart){var a=X(RangeStart.Value);var b=X(RangeEnd.Value);var rangeColor=IsHighContrast?SystemColors.HighlightColor:Color.FromRgb(70,169,134);dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb((byte)(IsHighContrast?70:50),rangeColor.R,rangeColor.G,rangeColor.B)),new Pen(B("Accent"),1),new Rect(a,3,b-a,30),4,4);}
  if(drag!=null&&Math.Abs(dragDelta)>3){var r=drag.Rect;r.Offset(dragDelta,0);dc.DrawRoundedRectangle(null,new Pen(B("Accent"),2),r,8,8);}
  dc.Pop();dc.DrawRectangle(B("Surface"),null,new Rect(0,RulerHeight,LabelWidth,Math.Max(0,ActualHeight-RulerHeight)));
  EditorIcon.DrawVector(dc,"trackScreen",new Rect(16,RulerHeight+31,18,18),B("Ink"));Text(dc,"屏幕录制",new(40,RulerHeight+35),11,B("Ink"));y=RulerHeight+PrimaryHeight;
  foreach(var lane in LaneNames()){dc.DrawLine(pen,new(0,y),new(ActualWidth,y));EditorIcon.DrawVector(dc,lane.Icon,new Rect(17,y+16,17,17),B("Secondary"));Text(dc,lane.Label,new(41,y+18),11,B("Ink"));DrawHideButton(dc,new Rect(LabelWidth-30,y+12,24,28),B("Secondary"));y+=LaneHeight;}
  dc.DrawLine(pen,new(0,RulerHeight),new(ActualWidth,RulerHeight));dc.DrawLine(pen,new(LabelWidth,0),new(LabelWidth,ActualHeight));
 }
 IEnumerable<(string Key,string Icon,string Label)> LaneNames(){if(!HiddenTracks.Contains("zoom"))yield return ("zoom","trackZoom","缩放");if(!HiddenTracks.Contains("motion"))yield return ("motion","track3d","屏幕 3D");if(Project?.Camera!=null&&!HiddenTracks.Contains("camera"))yield return ("camera","trackCamera","摄像头");if(Project?.CameraMotions.Count>0&&!HiddenTracks.Contains("cameraMotion"))yield return ("cameraMotion","trackCameraMotion","摄像运动");if(Project?.Overlays.Count>0&&!HiddenTracks.Contains("overlay"))yield return ("overlay","trackOverlay","贴图");if(Project?.BlurClips.Count>0&&!HiddenTracks.Contains("blur"))yield return ("blur","trackBlur","柔化");if(Project?.AudioTracks.Count>0&&!HiddenTracks.Contains("audio"))yield return ("audio","trackAudio","声音");}
 static void DrawHideButton(DrawingContext dc,Rect bounds,Brush brush){EditorIcon.DrawVector(dc,"eye",new Rect(bounds.X+6,bounds.Y+8,12,12),brush);dc.DrawLine(new Pen(brush,.85),new(bounds.X+6,bounds.Y+21),new(bounds.X+18,bounds.Y+7));}
 public static Guid AudioId(int i)=>new(i+1,0,0,new byte[8]);
 void DrawEffects(DrawingContext dc,string kind,IEnumerable<(Guid Id,double Start,double Duration,string Label)> clips,double y,Color color)
 {
  if(Project==null)return;
  foreach(var c in clips){var outputStart=0d;foreach(var seg in Project.Timeline.Segments){var a=Math.Max(c.Start,seg.SourceStart);var b=Math.Min(c.Start+c.Duration,seg.SourceEnd);if(b>a){var r=new Rect(X(outputStart+(a-seg.SourceStart)/seg.PlaybackRate)+1,y+8,Math.Max(4,(b-a)/seg.PlaybackRate*Scale-2),LaneHeight-15);var selected=Selection?.Kind==kind&&Selection.Id==c.Id||MultiSelection.Contains(new(kind,c.Id));var fill=IsHighContrast?selected?B("Accent"):B("Chrome"):new SolidColorBrush(color);var label=IsHighContrast?selected?SystemColors.HighlightTextBrush: B("Ink"):EffectLabelBrush;var outline=IsHighContrast?selected?B("Accent"):B("Ink"):selected?B("Ink"):new SolidColorBrush(Color.FromArgb(30,60,60,60));dc.DrawRoundedRectangle(fill,new Pen(outline,selected?1.4:.6),r,9,9);if(r.Width>30)Text(dc,c.Label,new(r.Left+6,r.Top+9),11,label,FontWeights.Medium,r.Width-12,TextAlignment.Center);hits.Add(new(r,new(kind,c.Id),c.Start,c.Duration,seg.PlaybackRate,-1));}outputStart+=seg.OutputDuration;}}
 }
 protected override void OnMouseDown(MouseButtonEventArgs e)
 {
  base.OnMouseDown(e);Focus();if(Project==null)return;var p=e.GetPosition(this);if(p.X<LabelWidth){if(e.ChangedButton==MouseButton.Left&&p.Y<RulerHeight){TrackMenuRequested?.Invoke();return;}if(e.ChangedButton==MouseButton.Left&&p.Y>=RulerHeight+PrimaryHeight&&p.X>=LabelWidth-30){var lanes=LaneNames().ToArray();var index=(int)((p.Y-(RulerHeight+PrimaryHeight))/LaneHeight);if((uint)index<(uint)lanes.Length){HiddenTracks.Add(lanes[index].Key);Refresh();e.Handled=true;}}return;}
  var hit=hits.LastOrDefault(h=>h.Rect.Contains(p));
  if(e.ChangedButton==MouseButton.Right){if(hit!=null){Selection=hit.Selection;SelectionRequested?.Invoke(hit.Selection);}ContextRequested?.Invoke();e.Handled=true;return;}
  if(e.ChangedButton!=MouseButton.Left)return;
  down=p;drag=hit;dragDelta=0;
  if(hit!=null){if(Keyboard.Modifiers.HasFlag(ModifierKeys.Control)){if(!MultiSelection.Add(hit.Selection))MultiSelection.Remove(hit.Selection);}else if(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)&&Selection?.Kind==hit.Selection.Kind){var previous=hits.Find(h=>h.Selection==Selection);if(previous!=null)foreach(var h in hits.Where(h=>h.Selection.Kind==hit.Selection.Kind&&h.Rect.X>=Math.Min(previous.Rect.X,hit.Rect.X)&&h.Rect.X<=Math.Max(previous.Rect.X,hit.Rect.X)))MultiSelection.Add(h.Selection);}else{MultiSelection.Clear();MultiSelection.Add(hit.Selection);}Selection=hit.Selection;SelectionRequested?.Invoke(hit.Selection);dragMode=p.X-hit.Rect.Left<8?"trim-start":hit.Rect.Right-p.X<8?"trim-end":"move";}
  else {seeking=true;SeekRequested?.Invoke(Time(p.X));}
  CaptureMouse();InvalidateVisual();e.Handled=true;
 }
 protected override void OnMouseMove(MouseEventArgs e)
 {
  base.OnMouseMove(e);var p=e.GetPosition(this);if(Project==null)return;
  if(IsMouseCaptured){if(seeking)SeekRequested?.Invoke(Snapped(Time(p.X)));else if(drag!=null){dragDelta=p.X-down.X;InvalidateVisual();}return;}
  if(p.X<LabelWidth&&p.Y>=RulerHeight+PrimaryHeight&&p.X>=LabelWidth-30){var lanes=LaneNames().ToArray();var index=(int)((p.Y-(RulerHeight+PrimaryHeight))/LaneHeight);if((uint)index<(uint)lanes.Length){Cursor=Cursors.Hand;HoverTime=null;InvalidateVisual();return;}}
  var hit=hits.LastOrDefault(h=>h.Rect.Contains(p));Cursor=hit!=null&&(p.X-hit.Rect.Left<8||hit.Rect.Right-p.X<8)?Cursors.SizeWE:hit!=null?Cursors.Hand:Cursors.Arrow;
  HoverTime=HoverEnabled&&p.X>=LabelWidth?Time(p.X):null;HoverRequested?.Invoke(HoverTime);InvalidateVisual();
 }
 double Snapped(double t){if(!Snap||Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)||Project==null)return t;var boundaries=new List<double>{0};var end=0d;foreach(var s in Project.Timeline.Segments)boundaries.Add(end+=s.OutputDuration);var nearest=boundaries.OrderBy(x=>Math.Abs(x-t)).First();return Math.Abs(nearest-t)*Scale<9?nearest:t;}
 protected override void OnMouseUp(MouseButtonEventArgs e)
 {
  base.OnMouseUp(e);if(!IsMouseCaptured)return;ReleaseMouseCapture();seeking=false;
  if(drag!=null&&Project!=null){if(Math.Abs(dragDelta)>3){var delta=dragDelta/Scale*drag.Rate;
   if(drag.Selection.Kind=="screen"&&dragMode=="move"){var time=Time(e.GetPosition(this).X);var cumulative=0d;var index=0;foreach(var seg in Project.Timeline.Segments){if(time<cumulative+seg.OutputDuration/2)break;cumulative+=seg.OutputDuration;index++;}ChangeRequested?.Invoke(new(drag.Selection,"reorder",drag.SourceStart,drag.SourceDuration,Math.Clamp(index,0,Project.Timeline.Segments.Count-1)));}
   else{var start=drag.SourceStart;var duration=drag.SourceDuration;if(dragMode=="trim-start"){var end=start+duration;start=Math.Clamp(start+delta,0,end-.04);duration=end-start;}else if(dragMode=="trim-end")duration=Math.Clamp(duration+delta,.04,Project.Media.Duration-start);else start=Math.Clamp(start+delta,0,Math.Max(0,Project.Media.Duration-duration));if(Snap&&!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)&&drag.Selection.Kind!="screen"){var anchors=Project.ZoomClips.Where(x=>x.Id!=drag.Selection.Id).SelectMany(x=>new[]{x.Start,x.End}).Concat(Project.ScreenMotions.Where(x=>x.Id!=drag.Selection.Id).SelectMany(x=>new[]{x.Start,x.End})).ToArray();if(anchors.Length>0){var near=anchors.OrderBy(x=>Math.Abs(x-start)).First();if(Math.Abs(near-start)*Scale/drag.Rate<9)start=near;}}ChangeRequested?.Invoke(new(drag.Selection,dragMode,start,duration));}}
   else if(drag.Selection.Kind=="screen")SeekRequested?.Invoke(Time(e.GetPosition(this).X));}
  drag=null;dragDelta=0;InvalidateVisual();e.Handled=true;
 }
 protected override void OnMouseLeave(MouseEventArgs e){base.OnMouseLeave(e);if(IsMouseCaptured)return;HoverTime=null;HoverRequested?.Invoke(null);InvalidateVisual();}
 protected override void OnMouseWheel(MouseWheelEventArgs e){if(Keyboard.Modifiers.HasFlag(ModifierKeys.Control)){ZoomRequested?.Invoke(Math.Clamp(Zoom*(e.Delta>0?1.25:.8),1,12));e.Handled=true;}else if(Zoom>1){ScrollRequested?.Invoke(Math.Clamp(Offset-e.Delta,0,DocumentWidth-Math.Max(1,ActualWidth-LabelWidth)));e.Handled=true;}base.OnMouseWheel(e);}
}
