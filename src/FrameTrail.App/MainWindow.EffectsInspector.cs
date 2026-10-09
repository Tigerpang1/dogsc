using System.IO;
using System.Windows;
using Microsoft.Win32;
using FrameTrail.Media;
namespace FrameTrail.App;
public partial class MainWindow
{
 private void AddMotionClip()
 {
  if(project==null)return;var id=Guid.NewGuid();var start=Math.Min(SourceNow,Math.Max(0,project.Media.Duration-.1));selectedEffect=new("motion",id);inspectorTab="zoom";motionTab="motion";ApplyEdit(p=>p.ScreenMotions.Add(new(){Id=id,Start=start,Duration=Math.Min(3,p.Media.Duration-start),Target=new(){Scale=.94,RotationY=-8,RotationX=5}}));
 }
 private void AddManualZoom()
 {
  if(project==null)return;var id=Guid.NewGuid();var start=Math.Min(SourceNow,Math.Max(0,project.Media.Duration-.1));selectedEffect=new("zoom",id);ApplyEdit(p=>p.ZoomClips.Add(new(){Id=id,Start=start,End=Math.Min(p.Media.Duration,start+2),Scale=1.5,Focus=new(.5,.5),Automatic=false}));
 }
 private static IEnumerable<(ZoomEasing,string)> Easings()=>new[]{(ZoomEasing.Linear,"线性"),(ZoomEasing.Cubic,"平滑"),(ZoomEasing.Quintic,"柔和"),(ZoomEasing.Spring,"弹簧")};
 private void BuildMotionInspector()
 {
  var p=project!;Section("运镜类型");Tabs(motionTab,("zoom","⌕  缩放",()=>{motionTab="zoom";BuildInspector();}),("motion","⬡  屏幕 3D",()=>{motionTab="motion";BuildInspector();}));
  if(motionTab=="zoom")
  {
   var z=p.ZoomClips.FirstOrDefault(x=>x.Id==selectedEffect?.Id)??p.ZoomClips.FirstOrDefault(x=>SourceNow>=x.Start&&SourceNow<=x.End);
   ActionButton("根据点击生成自动运镜",()=>AutoZoomClick(this,new()));ActionButton("添加手动缩放",AddManualZoom);
   if(z==null){Note("选择缩放片段以调整构图与动画。录制鼠标后可以根据点击自动生成。",true);return;}
   var id=z.Id;ZoomClip Target(FrameTrailProject x)=>x.ZoomClips.First(a=>a.Id==id);
   Section("构图");PointPad("缩放焦点",z.Automatic?ToCropLocalFocus(p,z.Focus):z.Focus,v=>ApplyEdit(x=>{Target(x).Focus=v;Target(x).Automatic=false;}),centered:true);Number("目标大小",z.Scale,1,6,v=>ApplyEdit(x=>Target(x).Scale=v),"×",inline:true);Toggle("点击画面调整焦点",ManualFocus.IsChecked==true,v=>ManualFocus.IsChecked=v);
   Section("片段与动画");Number("开始",z.Start,0,Math.Max(0,p.Media.Duration-.1),v=>ApplyEdit(x=>{var a=Target(x);var d=a.End-a.Start;a.Start=v;a.End=Math.Min(x.Media.Duration,v+d);})," s");Number("长度",z.End-z.Start,.1,Math.Max(.1,p.Media.Duration-z.Start),v=>ApplyEdit(x=>Target(x).End=Target(x).Start+v)," s");Number("进入",z.EnterDuration,0,5,v=>ApplyEdit(x=>Target(x).EnterDuration=v)," s");Number("退出",z.ExitDuration,0,5,v=>ApplyEdit(x=>Target(x).ExitDuration=v)," s");Choices("曲线",z.Easing,Easings(),v=>ApplyEdit(x=>Target(x).Easing=v));ActionButton("清除自动运镜",()=>ApplyEdit(x=>x.ZoomClips.RemoveAll(a=>a.Automatic)));
  }
  else
  {
   var clip=p.ScreenMotions.FirstOrDefault(x=>x.Id==selectedEffect?.Id)??p.ScreenMotions.FirstOrDefault(x=>SourceNow>=x.Start&&SourceNow<=x.End);
   if(clip==null){ActionButton("添加屏幕 3D 片段",AddMotionClip);Note("添加或选择屏幕 3D 片段，然后调整空间姿态。",true);return;}var id=clip.Id;ScreenMotionClip Target(FrameTrailProject x)=>x.ScreenMotions.First(a=>a.Id==id);
   Section("构图");PointPad("缩放焦点",clip.Target.Position,v=>ApplyEdit(x=>Target(x).Target.Position=v),centered:true,hint:"放大时画面向焦点的反方向展开；缩小时会向焦点收拢，因此 1× 两侧的移动方向会自然相反。");Number("目标大小",clip.Target.Scale,.3,3,v=>ApplyEdit(x=>Target(x).Target.Scale=v),"×",inline:true);
   Section("空间姿态");PointPad("3D 倾斜",new((clip.Target.RotationY+45)/90,(clip.Target.RotationX+45)/90),v=>ApplyEdit(x=>{Target(x).Target.RotationX=v.Y*90-45;Target(x).Target.RotationY=v.X*90-45;}),centered:true,tilt:true,hint:"上下控制 X · 左右控制 Y");Number("X 倾斜",clip.Target.RotationX,-65,65,v=>ApplyEdit(x=>Target(x).Target.RotationX=v),"°");Number("Y 倾斜",clip.Target.RotationY,-65,65,v=>ApplyEdit(x=>Target(x).Target.RotationY=v),"°");Number("旋转",clip.Target.RotationZ,-180,180,v=>ApplyEdit(x=>Target(x).Target.RotationZ=v),"°");Number("透视",clip.Target.Perspective,0,2,v=>ApplyEdit(x=>Target(x).Target.Perspective=v));
   Section("片段与动画");Number("开始",clip.Start,0,Math.Max(0,p.Media.Duration-.1),v=>ApplyEdit(x=>{Target(x).Start=v;Target(x).Duration=Math.Min(Target(x).Duration,x.Media.Duration-v);})," s");Number("长度",clip.Duration,.1,Math.Max(.1,p.Media.Duration-clip.Start),v=>ApplyEdit(x=>Target(x).Duration=v)," s");Number("进入",clip.EnterDuration,0,3,v=>ApplyEdit(x=>Target(x).EnterDuration=v)," s");Number("回落",clip.ReturnDuration,0,3,v=>ApplyEdit(x=>Target(x).ReturnDuration=v)," s");Choices("曲线",clip.Easing,Easings(),v=>ApplyEdit(x=>Target(x).Easing=v));
   Section("聚焦虚化");Toggle("启用聚焦",clip.Focus!=null,v=>ApplyEdit(x=>Target(x).Focus=v?new():null));
   if(clip.Focus is { } focus){FocusEffect F(FrameTrailProject x)=>Target(x).Focus!;Choices("形状",focus.Shape,new[]{(FocusEffectShape.Radial,"径向"),(FocusEffectShape.Linear,"线性")},v=>ApplyEdit(x=>F(x).Shape=v));Choices("目标",focus.Target,new[]{(FocusEffectTarget.Animation,"随运镜"),(FocusEffectTarget.Pointer,"随鼠标"),(FocusEffectTarget.Fixed,"固定")},v=>ApplyEdit(x=>F(x).Target=v));PointPad("焦点",focus.Center,v=>ApplyEdit(x=>F(x).Center=v));Number("范围",focus.Size,.1,1,v=>ApplyEdit(x=>F(x).Size=v));Number("虚化",focus.Blur,0,1,v=>ApplyEdit(x=>F(x).Blur=v));Number("暗化",focus.Dimming,0,.75,v=>ApplyEdit(x=>F(x).Dimming=v));Number("柔和度",focus.Softness,0,1,v=>ApplyEdit(x=>F(x).Softness=v));if(focus.Shape==FocusEffectShape.Linear)Number("焦面角度",focus.AngleDegrees,-180,180,v=>ApplyEdit(x=>F(x).AngleDegrees=v),"°");}
  }
 }
 private void BuildCursorInspector()
 {
  var p=project!;var c=p.Settings.Cursor??new(){Visible=p.Settings.CursorVisible};Section("光标显示");Toggle("显示光标",c.Visible,v=>ApplyEdit(x=>{CursorSettingsFor(x).Visible=v;x.Settings.CursorVisible=v;}));Choices("样式",c.Style,new[]{(CursorStyle.SystemArrow,"箭头"),(CursorStyle.Dot,"圆点"),(CursorStyle.Ring,"圆环")},v=>ApplyEdit(x=>CursorSettingsFor(x).Style=v));Number("大小",c.Scale,.3,4,v=>ApplyEdit(x=>CursorSettingsFor(x).Scale=v),"×");Toggle("静止后隐藏",c.HideWhenIdle,v=>ApplyEdit(x=>CursorSettingsFor(x).HideWhenIdle=v));if(c.HideWhenIdle)Number("隐藏等待",c.IdleSeconds,.2,10,v=>ApplyEdit(x=>CursorSettingsFor(x).IdleSeconds=v)," s");
  Section("点击反馈");Toggle("显示点击效果",c.ClickEffectsEnabled,v=>ApplyEdit(x=>CursorSettingsFor(x).ClickEffectsEnabled=v));Choices("效果",c.ClickEffect,new[]{(ClickEffectStyle.Pulse,"脉冲"),(ClickEffectStyle.Ring,"涟漪"),(ClickEffectStyle.None,"无")},v=>ApplyEdit(x=>CursorSettingsFor(x).ClickEffect=v));TextField("颜色",c.ClickColor,v=>ApplyEdit(x=>CursorSettingsFor(x).ClickColor=ValidColor(v)));Number("持续时间",c.ClickDuration,.05,2,v=>ApplyEdit(x=>CursorSettingsFor(x).ClickDuration=v)," s");
  Section("独立时间校准");Number("时间偏移",p.MouseClock.OffsetSeconds*1000,-10000,10000,v=>ApplyEdit(x=>{x.MouseClock=x.MouseClock with{OffsetSeconds=v/1000};if(x.ZoomClips.All(z=>z.Automatic))x.ZoomClips=AutoZoomPlanner.Plan(x.PointerEvents.Select(a=>a with{Time=x.MouseClock.ToSourceTime(a.Time)}));})," ms","0");Note("只调整光标与点击的时间，画面和声音不会移动。");
 }
 private void AddOverlayClick(object sender,RoutedEventArgs e)=>ImportImageAsset(relative=>{if(project==null)return;var id=Guid.NewGuid();var t=Math.Min(SourceNow,Math.Max(0,project.Media.Duration-.1));selectedEffect=new("overlay",id);inspectorTab="overlay";ApplyEdit(p=>p.Overlays.Add(new(){Id=id,MediaPath=relative,Start=t,Duration=Math.Min(3,p.Media.Duration-t)}));});
 private void AddBlurClip(){if(project==null)return;var id=Guid.NewGuid();var t=Math.Min(SourceNow,Math.Max(0,project.Media.Duration-.1));selectedEffect=new("blur",id);inspectorTab="blur";ApplyEdit(p=>p.BlurClips.Add(new(){Id=id,Start=t,Duration=Math.Min(3,p.Media.Duration-t)}));}
 private void RectControls(NormalizedRect rect,Action<NormalizedRect> apply)
 {
  PointPad("位置",new(rect.X+rect.Width/2,rect.Y+rect.Height/2),v=>apply(rect with{X=v.X-rect.Width/2,Y=v.Y-rect.Height/2}));Number("宽度",rect.Width,.02,1,v=>apply(rect with{Width=v}));Number("高度",rect.Height,.02,1,v=>apply(rect with{Height=v}));
 }
 private void BuildOverlayInspector()
 {
  var p=project!;ActionButton("添加图片贴图…",()=>AddOverlayClick(this,new()));var clip=p.Overlays.FirstOrDefault(x=>x.Id==selectedEffect?.Id)??p.Overlays.LastOrDefault();if(clip==null)return;var id=clip.Id;OverlayClip Target(FrameTrailProject x)=>x.Overlays.First(a=>a.Id==id);Section("贴图布局");Note(System.IO.Path.GetFileName(clip.MediaPath));RectControls(clip.Rect,v=>ApplyEdit(x=>Target(x).Rect=ConstrainRect(v)));Number("旋转",clip.Rotation,-180,180,v=>ApplyEdit(x=>Target(x).Rotation=v),"°");Number("不透明度",clip.Opacity,0,1,v=>ApplyEdit(x=>Target(x).Opacity=v));Number("圆角",clip.CornerRadius,0,.5,v=>ApplyEdit(x=>Target(x).CornerRadius=v));Number("贴图模糊",clip.Blur,0,1,v=>ApplyEdit(x=>Target(x).Blur=v));Number("图层",clip.Layer,-10,10,v=>ApplyEdit(x=>Target(x).Layer=(int)Math.Round(v)),"","0");
  Section("片段与动画");Number("开始",clip.Start,0,Math.Max(0,p.Media.Duration-.1),v=>ApplyEdit(x=>Target(x).Start=v)," s");Number("长度",clip.Duration,.1,p.Media.Duration,v=>ApplyEdit(x=>Target(x).Duration=v)," s");Choices("入场",clip.EnterMotion,Enum.GetValues<OverlayMotion>().Select(x=>(x,MotionName(x))),v=>ApplyEdit(x=>Target(x).EnterMotion=v));Choices("出场",clip.ExitMotion,Enum.GetValues<OverlayMotion>().Select(x=>(x,MotionName(x))),v=>ApplyEdit(x=>Target(x).ExitMotion=v));Number("入场时长",clip.EnterDuration,0,3,v=>ApplyEdit(x=>Target(x).EnterDuration=v)," s");Number("出场时长",clip.ExitDuration,0,3,v=>ApplyEdit(x=>Target(x).ExitDuration=v)," s");
 }
 private static string MotionName(OverlayMotion x)=>x switch{OverlayMotion.None=>"无",OverlayMotion.Fade=>"淡入淡出",OverlayMotion.Scale=>"缩放",OverlayMotion.SlideUp=>"向上",OverlayMotion.SlideDown=>"向下",OverlayMotion.SlideLeft=>"向左",_=>"向右"};
 private void BuildBlurInspector()
 {
  var p=project!;ActionButton("添加柔化片段",AddBlurClip);var clip=p.BlurClips.FirstOrDefault(x=>x.Id==selectedEffect?.Id)??p.BlurClips.LastOrDefault();if(clip==null)return;var id=clip.Id;BlurClip Target(FrameTrailProject x)=>x.BlurClips.First(a=>a.Id==id);Section("柔化区域");RectControls(clip.Rect,v=>ApplyEdit(x=>Target(x).Rect=ConstrainRect(v)));Choices("处理方式",clip.Kind,new[]{(BlurKind.Blur,"柔化"),(BlurKind.Pixelate,"马赛克")},v=>ApplyEdit(x=>Target(x).Kind=v));Number("强度",clip.Strength,0,1,v=>ApplyEdit(x=>Target(x).Strength=v));Number("圆角",clip.CornerRadius,0,.3,v=>ApplyEdit(x=>Target(x).CornerRadius=v));Section("片段");Number("开始",clip.Start,0,Math.Max(0,p.Media.Duration-.1),v=>ApplyEdit(x=>Target(x).Start=v)," s");Number("长度",clip.Duration,.1,p.Media.Duration,v=>ApplyEdit(x=>Target(x).Duration=v)," s");Number("进入",clip.EnterDuration,0,2,v=>ApplyEdit(x=>Target(x).EnterDuration=v)," s");Number("退出",clip.ExitDuration,0,2,v=>ApplyEdit(x=>Target(x).ExitDuration=v)," s");
 }
 private void BuildCameraInspector()
 {
  var p=project!;ActionButton("导入摄像头素材…",()=>_=Guard(ImportCamera));if(p.Camera is not { } camera){Note("可以在录制工具中同时录制摄像头，或导入独立视频素材。",true);return;}
  Section("摄像头布局");Choices("形状",camera.Shape,new[]{(CameraShape.RoundedRectangle,"圆角方形"),(CameraShape.Circle,"圆形")},v=>ApplyEdit(x=>x.Camera!.Shape=v));PointPad("位置",camera.Position,v=>ApplyEdit(x=>x.Camera!.Position=v));Number("大小",camera.Size,.08,.9,v=>ApplyEdit(x=>x.Camera!.Size=v));Number("圆角 / 圆形",camera.CornerRadius,0,.5,v=>ApplyEdit(x=>x.Camera!.CornerRadius=v));Number("描边",camera.BorderWidth,0,18,v=>ApplyEdit(x=>x.Camera!.BorderWidth=v),"","0");Number("不透明度",camera.Opacity,0,1,v=>ApplyEdit(x=>x.Camera!.Opacity=v));Toggle("水平镜像",camera.Mirrored,v=>ApplyEdit(x=>x.Camera!.Mirrored=v));Section("素材裁切");RectControls(camera.Crop,v=>ApplyEdit(x=>x.Camera!.Crop=ConstrainRect(v)));ActionButton("恢复完整摄像头画面",()=>ApplyEdit(x=>x.Camera!.Crop=NormalizedRect.Full));Section("素材同步");if(camera.SyncSegments is {Count:>1} segments)Note($"录制素材含 {segments.Count} 段同步区间；暂停间隙不显示相机画面。");Number("时间偏移",camera.TimelineStart,-10,10,v=>ApplyEdit(x=>CameraTimelineOperations.Move(x.Camera!,v-x.Camera!.TimelineStart))," s");Number("素材起点",camera.SourceStart,0,Math.Max(.04,AvailableAssetDuration(camera.MediaPath,camera.SourceStart+camera.Duration)-.04),ChangeCameraSourceStart," s");
  ActionButton("添加摄像头运动",()=>{var id=Guid.NewGuid();var start=Math.Min(SourceNow,Math.Max(0,p.Media.Duration-.1));selectedEffect=new("cameraMotion",id);ApplyEdit(x=>x.CameraMotions.Add(new(){Id=id,Start=start,Duration=Math.Min(2,x.Media.Duration-start),TargetPosition=camera.Position,TargetSize=Math.Min(.9,camera.Size*1.2)}));});
  var motion=p.CameraMotions.FirstOrDefault(x=>x.Id==selectedEffect?.Id);if(motion!=null){var id=motion.Id;CameraMotionClip M(FrameTrailProject x)=>x.CameraMotions.First(a=>a.Id==id);Section("摄像运动");PointPad("目标位置",motion.TargetPosition,v=>ApplyEdit(x=>M(x).TargetPosition=v));Number("目标大小",motion.TargetSize,.08,.9,v=>ApplyEdit(x=>M(x).TargetSize=v));Number("时长",motion.Duration,.1,p.Media.Duration,v=>ApplyEdit(x=>M(x).Duration=v)," s");Number("进入",motion.EnterDuration,0,3,v=>ApplyEdit(x=>M(x).EnterDuration=v)," s");Number("回落",motion.ReturnDuration,0,3,v=>ApplyEdit(x=>M(x).ReturnDuration=v)," s");Choices("曲线",motion.Easing,Easings(),v=>ApplyEdit(x=>M(x).Easing=v));}
 }
 private async Task ImportCamera()
 {
  if(projectFile==null)return;var d=new OpenFileDialog{Filter="视频|*.mp4;*.mov;*.mkv;*.avi"};if(d.ShowDialog(this)!=true)return;var info=await MediaTools.ProbeAsync(d.FileName);if(info.Width<1||info.Duration<=0)throw new InvalidDataException("视频没有可用画面。");var relative="media/camera-"+Guid.NewGuid().ToString("N")+System.IO.Path.GetExtension(d.FileName);var path=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(projectFile)!,relative);Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);File.Copy(d.FileName,path);auxiliaryDurations[relative]=info.Duration;ApplyEdit(p=>p.Camera=new(){MediaPath=relative,Duration=info.Duration});
 }
 private void BuildAudioInspector()
 {
  var p=project!;Section("屏幕录制声音");Toggle("静音",p.Settings.MainAudioMuted,v=>ApplyEdit(x=>x.Settings.MainAudioMuted=v));Number("音量",p.Settings.MainAudioVolume*100,0,200,v=>ApplyEdit(x=>x.Settings.MainAudioVolume=v/100),"%","0");
  if(p.Camera!=null){Section("摄像头声音");Toggle("摄像头静音",p.Camera.Muted,v=>ApplyEdit(x=>x.Camera!.Muted=v));Number("音量",p.Camera.Volume*100,0,200,v=>ApplyEdit(x=>x.Camera!.Volume=v/100),"%","0");}
  for(var i=0;i<p.AudioTracks.Count;i++){var index=i;var a=p.AudioTracks[i];Section($"独立音轨 {i+1}");Note(System.IO.Path.GetFileName(a.MediaPath));Toggle("静音",a.Muted,v=>ApplyEdit(x=>x.AudioTracks[index].Muted=v));Number("音量",a.Volume*100,0,200,v=>ApplyEdit(x=>x.AudioTracks[index].Volume=v/100),"%","0");Number("时间偏移",a.OffsetSeconds,-10,10,v=>ApplyEdit(x=>x.AudioTracks[index].OffsetSeconds=v)," s");ActionButton("移除音轨",()=>ApplyEdit(x=>x.AudioTracks.RemoveAt(index)));}
  ActionButton("导入音频…",()=>_=Guard(ImportAudio));
 }
 private async Task ImportAudio()
 {
  if(projectFile==null)return;var d=new OpenFileDialog{Filter="音频/视频|*.wav;*.mp3;*.m4a;*.aac;*.flac;*.mp4;*.mov"};if(d.ShowDialog(this)!=true)return;var info=await MediaTools.ProbeAsync(d.FileName);if(!info.HasAudio)throw new InvalidDataException("素材没有音轨。");var relative="media/audio-"+Guid.NewGuid().ToString("N")+System.IO.Path.GetExtension(d.FileName);var path=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(projectFile)!,relative);Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);File.Copy(d.FileName,path);auxiliaryDurations[relative]=info.Duration;ApplyEdit(p=>p.AudioTracks.Add(new(){MediaPath=relative,Duration=info.Duration}));
 }
 private void BuildOpeningInspector()
 {
  var o=project!.Settings.Opening??new OpeningSettings();var values=o.ResolvePreset();Section("开场动效");Choices("预设",o.Preset,new[]{(OpeningPreset.Custom,"自定"),(OpeningPreset.ZoomUp,"放大入场"),(OpeningPreset.Fade,"淡入"),(OpeningPreset.SlideUp,"上升"),(OpeningPreset.RotateIn,"旋转入场"),(OpeningPreset.Converge,"汇聚"),(OpeningPreset.SideSlide,"侧向滑入"),(OpeningPreset.Light3D,"轻 3D")},v=>ApplyEdit(x=>(x.Settings.Opening??=new()).Preset=v));Toggle("启用开场",o.Enabled,v=>ApplyEdit(x=>(x.Settings.Opening??=new()).Enabled=v));Number("时长",o.Duration,.1,5,v=>ApplyEdit(x=>(x.Settings.Opening??=new()).Duration=v)," s");
  Section("元素与顺序");foreach(var element in o.ElementOrder.Concat(Enum.GetValues<OpeningElement>()).Distinct()){var current=element;var label=element switch{OpeningElement.Screen=>"屏幕",OpeningElement.Camera=>"摄像头",_=>"贴图"};Toggle(label+"参与开场",o.IncludedElements.Contains(element),v=>ApplyEdit(x=>{var opening=x.Settings.Opening??=new();if(v&&!opening.IncludedElements.Contains(current))opening.IncludedElements.Add(current);else if(!v)opening.IncludedElements.Remove(current);}));var row=new System.Windows.Controls.StackPanel{Orientation=System.Windows.Controls.Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};foreach(var direction in new[]{-1,1}){var d=direction;var button=new System.Windows.Controls.Button{Content=d<0?"提前 ↑":"延后 ↓",FontSize=10,Padding=new(10,3,10,3)};button.Click+=(_,_)=>ApplyEdit(x=>{var opening=x.Settings.Opening??=new();var order=opening.ElementOrder.Concat(Enum.GetValues<OpeningElement>()).Distinct().ToList();var index=order.IndexOf(current);var next=Math.Clamp(index+d,0,order.Count-1);(order[index],order[next])=(order[next],order[index]);opening.ElementOrder=order;});row.Children.Add(button);}InspectorHost.Children.Add(row);}Number("元素间隔",o.Stagger,0,1,v=>ApplyEdit(x=>(x.Settings.Opening??=new()).Stagger=v)," s");
  Section("起始姿态");Number("初始大小",values.ScaleFrom,.1,2,v=>EditOpeningCustom(x=>x.ScaleFrom=v),"×");Number("水平位移",values.OffsetX,-1,1,v=>EditOpeningCustom(x=>x.OffsetX=v));Number("垂直位移",values.OffsetY,-1,1,v=>EditOpeningCustom(x=>x.OffsetY=v));Number("初始旋转",values.RotationFrom,-180,180,v=>EditOpeningCustom(x=>x.RotationFrom=v),"°");Number("初始不透明度",values.OpacityFrom,0,1,v=>EditOpeningCustom(x=>x.OpacityFrom=v));Choices("曲线",o.Easing,Easings(),v=>ApplyEdit(x=>(x.Settings.Opening??=new()).Easing=v));ActionButton("从开头预览",()=>{Playhead.Value=0;PlayClick(this,new());});
 }
 private void EditOpeningCustom(Action<OpeningSettings> edit)=>ApplyEdit(p=>{var value=(p.Settings.Opening??new()).ResolvePreset() with{Preset=OpeningPreset.Custom};edit(value);p.Settings.Opening=value;});
}
