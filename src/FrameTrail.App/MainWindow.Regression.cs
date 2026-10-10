using System.IO;
using System.Text.Json;
using FrameTrail.Media;
namespace FrameTrail.App;
public partial class MainWindow
{
 private async Task VerifyRecordingBarLifecycle(string report)
 {
  var checks=new List<string>();
  void Check(bool passed,string name){if(!passed)throw new InvalidOperationException(name);checks.Add(name);}
  RecordingBar? delayedBar=null;RecordingBar? retryBar=null;
  try
  {
   var requested=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
   var releaseStop=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
   var stopCalls=0;
   delayedBar=new RecordingBar(()=>{},async()=>{Interlocked.Increment(ref stopCalls);requested.TrySetResult();await releaseStop.Task;},()=>Task.CompletedTask,_=>{});
   delayedBar.Show();delayedBar.Close();delayedBar.Close();
   await requested.Task.WaitAsync(TimeSpan.FromSeconds(3));
   Check(stopCalls==1,"关闭请求在停止尚未完成时只调用一次stop");
   Check(delayedBar.IsVisible,"异步停止期间录制条仍可见");
   releaseStop.TrySetResult();
   await WaitUntilAsync(()=>!delayedBar.IsVisible,TimeSpan.FromSeconds(3));
   Check(!delayedBar.IsVisible,"停止成功后显式关闭录制条");delayedBar=null;

   var failures=0;var attempts=0;
   retryBar=new RecordingBar(()=>{},()=>Interlocked.Increment(ref attempts)==1?Task.FromException(new IOException("expected virtual stop failure")):Task.CompletedTask,()=>Task.CompletedTask,_=>Interlocked.Increment(ref failures));
   retryBar.Show();retryBar.Close();
   await WaitUntilAsync(()=>failures==1,TimeSpan.FromSeconds(3));
   Check(retryBar.IsVisible&&attempts==1,"停止失败时窗口保持可见并允许重试");
   retryBar.Close();
   await WaitUntilAsync(()=>!retryBar.IsVisible,TimeSpan.FromSeconds(3));
   Check(attempts==2&&!retryBar.IsVisible,"重试成功后关闭且停止回调恰好重试一次");retryBar=null;
   Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
   await File.WriteAllTextAsync(report,JsonSerializer.Serialize(new{Passed=true,Checks=checks},new JsonSerializerOptions{WriteIndented=true}));
  }
  catch(Exception ex)
  {
   delayedBar?.CloseAfterRecording();retryBar?.CloseAfterRecording();
   Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
   await File.WriteAllTextAsync(report,JsonSerializer.Serialize(new{Passed=false,Checks=checks,Error=ex.ToString()},new JsonSerializerOptions{WriteIndented=true}));
  }

  static async Task WaitUntilAsync(Func<bool> predicate,TimeSpan timeout)
  {
   var until=DateTime.UtcNow+timeout;
   while(!predicate())
   {
    if(DateTime.UtcNow>=until)throw new TimeoutException("录制条生命周期回归等待超时。");
    await Task.Delay(15);
   }
  }
 }

 private async Task VerifyEditorRegression(string report)
 {
  var checks=new List<string>();object result;
  void Check(bool passed,string name){if(!passed)throw new InvalidOperationException(name);checks.Add(name);}
  try
  {
   if(project==null||projectFile==null)throw new InvalidOperationException("需要 --open 测试工程。");
   var original=projectFile;var copy=Clone(project);var directory=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report))!,"editor-regression-"+Guid.NewGuid().ToString("N")[..6]);Directory.CreateDirectory(directory);var file=Path.Combine(directory,"project.frametrail");
   var assets=new[]{copy.MediaPath,copy.Camera?.MediaPath,copy.Settings.BackgroundStyle?.ImagePath,copy.Settings.BackgroundStyle?.VideoPath}.Concat(copy.AudioTracks.Select(x=>x.MediaPath)).Concat(copy.Overlays.Select(x=>x.MediaPath)).Where(x=>!string.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase);
   foreach(var asset in assets){var target=FrameTrailProjectStore.ResolveAssetPath(file,asset!);Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(FrameTrailProjectStore.ResolveAssetPath(original,asset!),target);}
   FrameTrailProjectStore.Save(file,copy);await OpenProject(file);
   var savedPadding=project!.Settings.Padding;var saveFailureMessage="";
   using(var blockingReader=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read))
   {
    PaddingSlider.Value=Math.Clamp(savedPadding+.02,.01,.9);
    try{Edit(p=>p.Settings.Padding=PaddingSlider.Value);}
    catch(IOException ex){saveFailureMessage=ex.Message;}
    Check(saveFailureMessage.Contains("本次编辑未应用")&&saveFailureMessage.Contains("重新执行该操作"),"保存失败明确提示编辑未应用并指导重试");
    Check(project.Settings.Padding==savedPadding&&Math.Abs(PaddingSlider.Value-savedPadding)<1e-9,"保存失败后编辑模型与控件恢复为已保存值");
    Check(Math.Abs(FrameTrailProjectStore.Load(file).Settings.Padding-savedPadding)<1e-9,"保存失败未改变磁盘项目");
   }
   Playhead.Value=.5;NextFrame(this,new());Check(Math.Abs(Playhead.Value-(.5+1/project!.Media.FrameRate))<1e-9,"逐帧前进使用素材实际帧率");PreviousFrame(this,new());Check(Math.Abs(Playhead.Value-.5)<1e-9,"逐帧后退恢复原位置");
   var fiveFrameStart=Playhead.Value;StepFrames(5);Check(Math.Abs(Playhead.Value-(fiveFrameStart+5/project!.Media.FrameRate))<1e-9,"Shift+方向键移动5个素材帧");StepFrames(-5);
   var baselineSegments=project!.Timeline.Segments.Select(x=>x.Copy()).ToList();var sourceSegment=project.Timeline.Segments[0];var effectCenter=sourceSegment.SourceStart+sourceSegment.SourceDuration/2;var zoomClip=new ZoomClip{Start=effectCenter-.5,End=effectCenter+.5,Scale=1.6};var originalZoomCount=project.ZoomClips.Count;
   Edit(p=>p.ZoomClips.Add(zoomClip));selectedEffect=new("zoom",zoomClip.Id);TimelineView.Selection=selectedEffect;Playhead.Value=project.Timeline.MapSourceToOutput(effectCenter)!.Value;await SplitAtPlayhead();
   Check(project.ZoomClips.Count==originalZoomCount+2&&project.Timeline.Segments.Select(x=>(x.Id,x.SourceStart,x.SourceDuration,x.PlaybackRate)).SequenceEqual(baselineSegments.Select(x=>(x.Id,x.SourceStart,x.SourceDuration,x.PlaybackRate))),"S拆分所选缩放效果而不修改主片段");var rightZoom=selectedEffect!.Id;UndoClick(this,new());Check(project.ZoomClips.Count==originalZoomCount+1,"效果分割可撤销");RedoClick(this,new());Check(project.ZoomClips.Count==originalZoomCount+2&&project.ZoomClips.Any(x=>x.Id==rightZoom),"效果分割可重做并选中右段");UndoClick(this,new());Check(selectedEffect?.Kind=="zoom"&&selectedEffect.Id==zoomClip.Id&&TimelineView.Selection==selectedEffect,"撤销效果分割后选中恢复的原效果");
   await SplitAtPlayhead();var repeatedRight=selectedEffect!.Id;Check(project.ZoomClips.Count==originalZoomCount+2&&project.Timeline.Segments.Select(x=>(x.Id,x.SourceStart,x.SourceDuration,x.PlaybackRate)).SequenceEqual(baselineSegments.Select(x=>(x.Id,x.SourceStart,x.SourceDuration,x.PlaybackRate))),"效果分割撤销后再次S仍分割效果而不切主视频");
   Edit(p=>p.ZoomClips.RemoveAll(x=>x.Id==zoomClip.Id||x.Id==repeatedRight));
   selectedEffect=new("zoom",Guid.NewGuid());TimelineView.Selection=selectedEffect;var staleSnapshot=System.Text.Json.JsonSerializer.Serialize(project);await SplitAtPlayhead();DeleteCurrentSelection();Check(System.Text.Json.JsonSerializer.Serialize(project)==staleSnapshot,"失效效果选择不会回退误切或误删主视频");selectedEffect=null;TimelineView.Selection=null;
   Edit(p=>p.Timeline.Segments=[new(){SourceStart=3,SourceDuration=2,PlaybackRate=2},new(){SourceStart=0,SourceDuration=2,PlaybackRate=1}]);var reorderedFastSegment=project.Timeline.Segments[0];selectedEffect=null;TimelineView.Selection=new("screen",reorderedFastSegment.Id);Playhead.Value=.5;await TrimAtPlayhead(true);
   Check(Math.Abs(project.Timeline.Segments[0].SourceStart-4)<1e-9&&Math.Abs(project.Timeline.Segments[0].SourceDuration-1)<1e-9&&Playhead.Value==0,"Q按重排变速后的输出位置映射素材时间并回到片段起点");
   Edit(p=>p.Timeline.Segments=[new(){SourceStart=3,SourceDuration=2,PlaybackRate=2},new(){SourceStart=0,SourceDuration=2,PlaybackRate=1}]);reorderedFastSegment=project.Timeline.Segments[0];selectedEffect=null;TimelineView.Selection=new("screen",reorderedFastSegment.Id);Playhead.Value=.5;await TrimAtPlayhead(false);
   Check(Math.Abs(project.Timeline.Segments[0].SourceStart-3)<1e-9&&Math.Abs(project.Timeline.Segments[0].SourceDuration-1)<1e-9&&Math.Abs(Playhead.Value-.5)<1e-9&&Math.Abs(project.Timeline.MapOutputToSource(Playhead.Value)-0)<1e-9,"W按重排变速后的输出位置映射素材时间并停在切点");Edit(p=>p.Timeline.Segments=baselineSegments.Select(x=>x.Copy()).ToList());selectedEffect=null;TimelineView.Selection=null;Playhead.Value=.5;
   var motionSource=project.Timeline.Segments[0];var motionDuration=Math.Min(1.5,motionSource.SourceDuration*.7);if(motionDuration<.3)throw new InvalidOperationException("测试素材太短，无法覆盖运动裁剪回落回归。");var motionStart=motionSource.SourceStart+(motionSource.SourceDuration-motionDuration)/2;
   async Task<bool> VerifyMotionReturnTrim(string kind,bool leading,double cutFraction)
   {
    var id=Guid.NewGuid();Edit(p=>{if(kind=="motion")p.ScreenMotions=[new(){Id=id,Start=motionStart,Duration=motionDuration,ReturnDuration=4,Target=new(){Scale=.8}}];else p.CameraMotions=[new(){Id=id,Start=motionStart,Duration=motionDuration,ReturnDuration=4,TargetPosition=new(.7,.6),TargetSize=.4}];});selectedEffect=new(kind,id);TimelineView.Selection=selectedEffect;var cut=motionStart+motionDuration*cutFraction;Playhead.Value=project!.Timeline.MapSourceToOutput(cut)??throw new InvalidOperationException("测试切点不在主时间线上。");await TrimAtPlayhead(leading);var actual=kind=="motion"?project.ScreenMotions.Single(x=>x.Id==id).Duration:project.CameraMotions.Single(x=>x.Id==id).Duration;var returned=kind=="motion"?project.ScreenMotions.Single(x=>x.Id==id).ReturnDuration:project.CameraMotions.Single(x=>x.Id==id).ReturnDuration;return actual<motionDuration-.01&&Math.Abs(returned-actual)<1e-8;
   }
   var oldScreenMotions=project.ScreenMotions.Select(x=>x with{Target=x.Target with{}}).ToList();var oldCameraMotions=project.CameraMotions.Select(x=>x with{TargetPosition=x.TargetPosition with{}}).ToList();var screenTrimRight=await VerifyMotionReturnTrim("motion",false,.65);var screenTrimLeft=await VerifyMotionReturnTrim("motion",true,.35);Check(screenTrimRight&&screenTrimLeft,"屏幕运动Q/W裁剪将较长回落时长限制到裁后时长");var cameraTrimRight=await VerifyMotionReturnTrim("cameraMotion",false,.65);var cameraTrimLeft=await VerifyMotionReturnTrim("cameraMotion",true,.35);Check(cameraTrimRight&&cameraTrimLeft,"摄像头运动Q/W裁剪将较长回落时长限制到裁后时长");Edit(p=>{p.ScreenMotions=oldScreenMotions;p.CameraMotions=oldCameraMotions;});selectedEffect=null;TimelineView.Selection=null;Playhead.Value=.5;
   Edit(p=>p.Timeline.Segments=[new(){SourceStart=0,SourceDuration=2},new(){SourceStart=3,SourceDuration=2}]);var stalePrimaryId=project.Timeline.Segments[0].Id;SelectScreenSegment(stalePrimaryId);Playhead.Value=2.5;await SplitAtPlayhead();var selectedRight=(Clips.SelectedItem as ClipItem)?.Id;Check(project.Timeline.Segments.Count==3&&project.Timeline.Segments[0].Id==stalePrimaryId&&project.Timeline.Segments[0].SourceDuration==2&&project.Timeline.Segments[1].SourceStart==3&&project.Timeline.Segments[1].SourceDuration==.5&&project.Timeline.Segments[2].SourceStart==3.5&&selectedRight==project.Timeline.Segments[2].Id,"S按播放头分割主片段而不沿用旧列表选择，并选中右段");UndoClick(this,new());Check(project.Timeline.Segments.Count==2&&project.Timeline.Segments[1].SourceStart==3&&project.Timeline.Segments[1].SourceDuration==2,"主片段分割可撤销");RedoClick(this,new());Check(project.Timeline.Segments.Count==3&&project.Timeline.Segments[2].SourceStart==3.5,"主片段分割可重做");Edit(p=>p.Timeline.Segments=baselineSegments.Select(x=>x.Copy()).ToList());selectedEffect=null;TimelineView.Selection=null;Playhead.Value=.5;
   var count=project!.Timeline.Segments.Count;Edit(p=>{var s=p.Timeline.Segments[0];p.Timeline.Split(s.Id,s.SourceStart+s.SourceDuration/2);});Check(project.Timeline.Segments.Count==count+1,"分割增加一个片段");UndoClick(this,new());Check(project.Timeline.Segments.Count==count,"撤销恢复片段集合");RedoClick(this,new());Check(project.Timeline.Segments.Count==count+1,"重做恢复分割");
   Edit(p=>{var s=p.Timeline.Segments[0];p.Timeline.Split(s.Id,s.SourceStart+s.SourceDuration/2);var last=p.Timeline.Segments[^1];p.Timeline.Split(last.Id,last.SourceStart+last.SourceDuration/2);});var order=project.Timeline.Segments.Select(s=>s.Id).ToArray();TimelineView.MultiSelection.Clear();TimelineView.MultiSelection.Add(new("screen",order[0]));TimelineView.MultiSelection.Add(new("screen",order[1]));ApplyTimelineChange(new(new("screen",order[0]),"reorder",0,0,order.Length-1));Check(project.Timeline.Segments.Select(s=>s.Id).SequenceEqual(order.Skip(2).Concat(order.Take(2))),"屏幕多选重排保留组内顺序");UndoClick(this,new());Check(project.Timeline.Segments.Select(s=>s.Id).SequenceEqual(order),"多选重排可撤销");UndoClick(this,new());TimelineView.MultiSelection.Clear();
   Edit(p=>p.Camera=new(){MediaPath=p.MediaPath,Duration=p.Media.Duration});auxiliaryDurations[project.MediaPath]=project.Media.Duration;
   Edit(p=>ApplyOneTimelineChange(p,new(new("camera",Guid.Empty),"trim-end",0,1000)));Check(project.Camera!.Duration<=project.Media.Duration,"摄像头拖尾不越过素材末尾");
   ChangeCameraSourceStart(project.Media.Duration-.05);Check(project.Camera.SourceStart+project.Camera.Duration<=project.Media.Duration+.001,"摄像头更改源起点同时约束时长");
   Edit(p=>p.Camera=new(){MediaPath=p.MediaPath,SourceStart=0,TimelineStart=0,Duration=4,SyncSegments=[new(0,0,1),new(1.2,2,2)]});Edit(p=>ApplyOneTimelineChange(p,new(new("camera",Guid.Empty),"move",1,4)));Check(project.Camera!.SyncSegments![1].TimelineStart==3,"分段摄像头移动保留暂停断点");Edit(p=>ApplyOneTimelineChange(p,new(new("camera",Guid.Empty),"trim-start",1.2,3.8)));Check(Math.Abs(project.Camera!.SyncSegments![0].SourceStart-.2)<.001&&Math.Abs(project.Camera.TimelineStart-1.2)<.001,"分段摄像头裁首同步两条时间轴");var cameraTimeline=project.Camera.SyncSegments.Select(x=>x.TimelineStart).ToArray();ChangeCameraSourceStart(1);Check(project.Camera.SyncSegments.Select(x=>x.TimelineStart).SequenceEqual(cameraTimeline)&&Math.Abs(project.Camera.SyncSegments[0].SourceStart-1)<.001,"分段摄像头滑移素材不移动屏幕时钟");
   Edit(p=>p.AudioTracks=new[]{.2,.5,.8}.Select(v=>new AudioTrack{MediaPath=p.MediaPath,Duration=p.Media.Duration,Volume=v}).ToList());TimelineView.MultiSelection.Clear();TimelineView.MultiSelection.Add(new("audio",EditorTimelineControl.AudioId(0)));TimelineView.MultiSelection.Add(new("audio",EditorTimelineControl.AudioId(2)));DeleteSelectionClick(this,new());Check(project.AudioTracks.Count==1&&Math.Abs(project.AudioTracks[0].Volume-.5)<.001,"非连续多选音轨删除保留中间音轨");UndoClick(this,new());Check(project.AudioTracks.Count==3,"多选音轨删除可撤销");
   Edit(p=>p.AudioTracks=[new(){MediaPath=p.MediaPath,SourceStart=0,OffsetSeconds=.2,Duration=1.5,SyncSegments=[new(0,.2,.5,2),new(1,1.2,.5,.5)]}]);
   inspectorTab="audio";selectedScreenSegmentId=null;BuildInspector();
   IEnumerable<System.Windows.DependencyObject> LogicalDescendants(System.Windows.DependencyObject root){foreach(var child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<System.Windows.DependencyObject>()){yield return child;foreach(var descendant in LogicalDescendants(child))yield return descendant;}}
   var offsetBox=LogicalDescendants(InspectorHost).OfType<System.Windows.Controls.TextBox>().Single(x=>System.Windows.Automation.AutomationProperties.GetName(x)=="时间偏移数值");
   offsetBox.Text=.7.ToString(System.Globalization.CultureInfo.CurrentCulture)+" s";offsetBox.RaiseEvent(new System.Windows.Input.KeyboardFocusChangedEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,Environment.TickCount,offsetBox,null){RoutedEvent=System.Windows.Input.Keyboard.LostKeyboardFocusEvent});
   var movedAudio=project.AudioTracks[0];Check(Math.Abs(movedAudio.OffsetSeconds-.7)<1e-9&&movedAudio.SyncSegments is {Count:2} audioParts&&Math.Abs(audioParts[0].TimelineStart-.7)<1e-9&&Math.Abs(audioParts[1].TimelineStart-1.7)<1e-9&&audioParts[0].SourceStart==0&&audioParts[1].SourceStart==1&&audioParts[0].SourceRate==2&&audioParts[1].SourceRate==.5,"声音检查器时间偏移同时移动所有同步段且保留源时钟速率");
   SaveProject();var savedAudio=FrameTrailProjectStore.Load(file).AudioTracks[0];Check(Math.Abs(savedAudio.SyncSegments![1].TimelineStart-1.7)<1e-9&&savedAudio.SyncSegments[1].SourceRate==.5,"声音检查器同步段偏移保存重载保持");
   Edit(p=>p.ZoomClips=[new(){Start=.1,End=.7,Scale=1.8,Focus=new(.3,.4),EnterProgressOffset=.25,ExitProgressOffset=.4,FocusEffect=new(){Target=FocusEffectTarget.Pointer,Blur=.7}},new(){Start=1,End=1.8,Scale=1.2,Focus=new(.6,.7),EnterProgressOffset=.6,ExitProgressOffset=.7,FocusEffect=new(){Target=FocusEffectTarget.Fixed,Blur=.2}}]);selectedEffect=new("zoom",project.ZoomClips[0].Id);CopyEffect();selectedEffect=new("zoom",project.ZoomClips[1].Id);var targetId=selectedEffect.Id;PasteEffect(true);var pasted=project.ZoomClips.First(x=>x.Id==targetId);Check(Math.Abs(pasted.Start-1)<.001&&Math.Abs(pasted.End-1.8)<.001&&Math.Abs(pasted.Scale-1.8)<.001,"粘贴属性保留目标ID起点和时长");Check(pasted.FocusEffect?.Target==FocusEffectTarget.Pointer&&pasted.FocusEffect.Blur==.7,"缩放聚焦属性粘贴保留三类目标及参数");Check(pasted.EnterProgressOffset==0&&pasted.ExitProgressOffset==0,"缩放属性粘贴按编辑语义重置入口和回落锚点");timelineHoverPosition=.2;PasteEffect(false);Check(project.ZoomClips.Count==3&&project.ZoomClips.Select(x=>x.Id).Distinct().Count()==3&&project.ZoomClips[^1].FocusEffect?.Target==FocusEffectTarget.Pointer&&project.ZoomClips[^1].EnterProgressOffset==.25&&project.ZoomClips[^1].ExitProgressOffset==.4,"完整复制创建独立ID并保留缩放锚点与聚焦效果");timelineHoverPosition=null;
   Directory.CreateDirectory(Path.Combine(directory,"media"));var overlayPath="media/regression-overlay.png";var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(System.Windows.Media.Imaging.BitmapSource.Create(1,1,96,96,System.Windows.Media.PixelFormats.Bgra32,null,new byte[]{35,120,210,255},4)));using(var image=File.Create(FrameTrailProjectStore.ResolveAssetPath(file,overlayPath)))encoder.Save(image);var secondOverlay="media/regression-overlay-target.png";File.Copy(FrameTrailProjectStore.ResolveAssetPath(file,overlayPath),FrameTrailProjectStore.ResolveAssetPath(file,secondOverlay));
   var cameraStyle=new CameraTrack{MediaPath=overlayPath,AssetAspectRatio=1.5,SourceStart=.15,Duration=1.4,TimelineStart=.35,Position=new(.72,.74),Size=.31,CornerRadius=.09,BorderWidth=3,BorderColor="#AABBCC",ScaleDuringZoom=.62,ShadowStrength=.42,Opacity=.73,Volume=.8,Muted=false,Mirrored=true,Layout=CameraMotionLayoutKind.Horizontal,Crop=new(.1,.05,.8,.9)};Edit(p=>p.Camera=cameraStyle);selectedEffect=new("camera",Guid.Empty);CopyEffect();Edit(p=>p.Camera=new(){MediaPath=p.MediaPath,AssetAspectRatio=1,SourceStart=.2,Duration=1,TimelineStart=.8,Layout=CameraMotionLayoutKind.Circle,Opacity=.4,ShadowStrength=.1});PasteEffect(true);var cameraAttributes=project.Camera!;Check(cameraAttributes.MediaPath==project.MediaPath&&cameraAttributes.SourceStart==.2&&cameraAttributes.Duration==1&&cameraAttributes.TimelineStart==.8&&cameraAttributes.Layout==CameraMotionLayoutKind.Horizontal&&cameraAttributes.Opacity==.73&&cameraAttributes.ShadowStrength==.42&&cameraAttributes.ScaleDuringZoom==.62&&cameraAttributes.BorderColor=="#AABBCC","摄像头属性粘贴复制新布局/透明度/阴影并保留目标素材及时序");Edit(p=>p.Camera=null);PasteEffect(false);var pastedCamera=project.Camera;Check(pastedCamera!=null&&pastedCamera.MediaPath==overlayPath&&pastedCamera.Layout==CameraMotionLayoutKind.Horizontal&&pastedCamera.Opacity==.73&&pastedCamera.ShadowStrength==.42&&pastedCamera.ScaleDuringZoom==.62&&pastedCamera.BorderWidth==3&&pastedCamera.Mirrored,"完整摄像头粘贴替换单条轨道并保留外观字段");UndoClick(this,new());Check(project.Camera==null,"完整摄像头替换可撤销");
   Edit(p=>{p.ScreenMotions=[new(){Start=.1,Duration=.6,Target=new(){Scale=.6}},new(){Start=1,Duration=.8,Target=new(){Scale=.9}}];p.CameraMotions=[new(){Start=.1,Duration=.6,TargetSize=.2,TargetLayout=CameraMotionLayoutKind.Horizontal,TargetRoundness=.65,TargetOpacity=.75},new(){Start=1,Duration=.8,TargetSize=.4,TargetLayout=CameraMotionLayoutKind.Circle,TargetRoundness=.2,TargetOpacity=.3}];p.BlurClips=[new(){Start=.1,Duration=.6,Strength=.2},new(){Start=1,Duration=.8,Strength=.8}];p.Overlays=[new(){MediaPath=overlayPath,Start=.1,Duration=.6,Opacity=.2,BorderWidth=3,BorderColor="#123456",ShadowOpacity=.32,ShadowRadius=18,ShadowOffsetX=4,ShadowOffsetY=-5,BackdropBlur=12,BackdropBlurIncludesCamera=true,HidesScreen=true,HidesCamera=true,AnimationCurve=ElementMotionCurve.Gentle},new(){MediaPath=secondOverlay,Start=1,Duration=.8,Opacity=.8}];});
   (Guid Id,double Start,double Duration,double Value)[] Rows(string kind)=>kind switch{"motion"=>project.ScreenMotions.Select(x=>(x.Id,x.Start,x.Duration,x.Target.Scale)).ToArray(),"cameraMotion"=>project.CameraMotions.Select(x=>(x.Id,x.Start,x.Duration,x.TargetSize)).ToArray(),"blur"=>project.BlurClips.Select(x=>(x.Id,x.Start,x.Duration,x.Strength)).ToArray(),_=>project.Overlays.Select(x=>(x.Id,x.Start,x.Duration,x.Opacity)).ToArray()};
   foreach(var kind in new[]{"motion","cameraMotion","blur","overlay"}){var before=Rows(kind);selectedEffect=new(kind,before[0].Id);CopyEffect();selectedEffect=new(kind,before[1].Id);PasteEffect(true);var target=Rows(kind).Single(x=>x.Id==before[1].Id);Check(target.Start==before[1].Start&&target.Duration==before[1].Duration&&target.Value==before[0].Value,kind+"属性粘贴保留ID及时间并复制参数");if(kind=="overlay"){var value=project.Overlays.Single(x=>x.Id==target.Id);Check(value.MediaPath==secondOverlay,"贴图属性粘贴保留目标图片");Check(value.BorderWidth==3&&value.BorderColor=="#123456"&&value.ShadowOpacity==.32&&value.ShadowRadius==18&&value.ShadowOffsetX==4&&value.ShadowOffsetY==-5&&value.BackdropBlur==12&&value.BackdropBlurIncludesCamera&&value.HidesScreen&&value.HidesCamera&&value.AnimationCurve==ElementMotionCurve.Gentle,"贴图样式、背景柔化、隐藏标记和曲线随属性粘贴");}if(kind=="cameraMotion"){var value=project.CameraMotions.Single(x=>x.Id==target.Id);Check(value.TargetLayout==CameraMotionLayoutKind.Horizontal&&value.TargetRoundness==.65&&value.TargetOpacity==.75,"摄像头运动布局、圆角和透明度随属性粘贴");}timelineHoverPosition=.2;PasteEffect(false);Check(Rows(kind).Length==3&&Rows(kind).Select(x=>x.Id).Distinct().Count()==3,kind+"完整复制保留独立ID");if(kind=="overlay"){var value=project.Overlays[^1];Check(value.BorderWidth==3&&value.BackdropBlur==12&&value.BackdropBlurIncludesCamera&&value.HidesScreen&&value.HidesCamera&&value.AnimationCurve==ElementMotionCurve.Gentle,"贴图完整复制保留新样式字段");}if(kind=="cameraMotion"){var value=project.CameraMotions[^1];Check(value.TargetLayout==CameraMotionLayoutKind.Horizontal&&value.TargetRoundness==.65&&value.TargetOpacity==.75,"摄像头运动完整复制保留目标形状字段");}}timelineHoverPosition=null;
   var blurOrder=project.BlurClips.OrderBy(x=>x.Start).Take(2).ToArray();var difference=blurOrder[1].Start-blurOrder[0].Start;TimelineView.MultiSelection.Clear();foreach(var blur in blurOrder)TimelineView.MultiSelection.Add(new("blur",blur.Id));ApplyTimelineChange(new(new("blur",blurOrder[0].Id),"move",-2,blurOrder[0].Duration));var moved=project.BlurClips.Where(x=>blurOrder.Any(y=>y.Id==x.Id)).OrderBy(x=>x.Start).ToArray();Check(moved[0].Start==0&&Math.Abs(moved[1].Start-moved[0].Start-difference)<.001,"组移动越界时整体约束且保持相对位置");TimelineView.MultiSelection.Clear();selectedEffect=null;
   Edit(p=>{p.Camera=null;p.AudioTracks.Clear();p.ZoomClips.Clear();p.ScreenMotions.Clear();p.CameraMotions.Clear();p.BlurClips.Clear();p.Overlays.Clear();});TimelineView.RangeStart=.25;TimelineView.RangeEnd=1.25;var range=ExportSnapshot();var output=Path.Combine(directory,"range.mp4");await new ExportEngine().ExportAsync(range,FrameTrailProjectStore.ResolveMediaPath(file,project),output,new(640,360,30,false){ProjectFilePath=file});var media=await MediaTools.ProbeAsync(output);Check(Math.Abs(media.Duration-1)<.08,"界面范围快照导出MP4为1秒");
   SaveProject();await OpenProject(file);Check(project.Timeline.Segments.Count==count+1,"保存重开保留编辑");result=new{Passed=true,Checks=checks,Project=file,RangeExport=output};
  }
  catch(Exception ex){App.Log(ex);result=new{Passed=false,Checks=checks,Error=ex.ToString()};}
  Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);await File.WriteAllTextAsync(report,JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
 }
}
