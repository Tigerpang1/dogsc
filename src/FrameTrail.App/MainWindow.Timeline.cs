using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Controls;
namespace FrameTrail.App;
public partial class MainWindow
{
 private string? effectClipboardProjectFile;
 private void ApplyTimelineChange(TimelineChange c)=>ApplyEdit(p=>
 {
  var many=TimelineView.MultiSelection.ToArray();
  if(c.Operation=="reorder"&&c.Selection.Kind=="screen"){
   var selectedIds=many.Where(s=>s.Kind=="screen").Select(s=>s.Id).ToHashSet();
   if(selectedIds.Count>1&&selectedIds.Contains(c.Selection.Id)){var original=p.Timeline.Segments;var group=original.Where(s=>selectedIds.Contains(s.Id)).ToList();var first=original.FindIndex(s=>selectedIds.Contains(s.Id));var anchor=original.FindIndex(s=>s.Id==c.Selection.Id);var remaining=original.Where(s=>!selectedIds.Contains(s.Id)).ToList();var index=Math.Clamp(first+c.NewIndex-anchor,0,remaining.Count);remaining.InsertRange(index,group);p.Timeline.Segments=remaining;return;}
  }
  if(c.Operation=="move"&&many.Length>1&&many.Contains(c.Selection)&&Span(p,c.Selection) is {} span){var group=many.Select(s=>(Selection:s,Span:Span(p,s))).Where(x=>x.Span.HasValue).Select(x=>(x.Selection,Span:x.Span!.Value)).ToArray();var lower=-group.Min(x=>x.Span.Start);var upper=group.Min(x=>p.Media.Duration-x.Span.Start-x.Span.Duration);var delta=Math.Clamp(c.SourceStart-span.Start,lower,Math.Max(lower,upper));foreach(var item in group)ApplyOneTimelineChange(p,new(item.Selection,"move",item.Span.Start+delta,item.Span.Duration));return;}
  ApplyOneTimelineChange(p,c);
 });
 private static (double Start,double Duration)? Span(FrameTrailProject p,TimelineSelection s)=>s.Kind switch
 {"camera"=>p.Camera is {} camera?(camera.TimelineStart,camera.Duration):null,"audio"=>Enumerable.Range(0,p.AudioTracks.Count).Where(i=>EditorTimelineControl.AudioId(i)==s.Id).Select(i=>((double,double)?)(p.AudioTracks[i].OffsetSeconds,p.AudioTracks[i].Duration)).FirstOrDefault(),"zoom"=>p.ZoomClips.Where(x=>x.Id==s.Id).Select(x=>((double,double)?)(x.Start,x.End-x.Start)).FirstOrDefault(),"motion"=>p.ScreenMotions.Where(x=>x.Id==s.Id).Select(x=>((double,double)?)(x.Start,x.Duration)).FirstOrDefault(),"cameraMotion"=>p.CameraMotions.Where(x=>x.Id==s.Id).Select(x=>((double,double)?)(x.Start,x.Duration)).FirstOrDefault(),"overlay"=>p.Overlays.Where(x=>x.Id==s.Id).Select(x=>((double,double)?)(x.Start,x.Duration)).FirstOrDefault(),"blur"=>p.BlurClips.Where(x=>x.Id==s.Id).Select(x=>((double,double)?)(x.Start,x.Duration)).FirstOrDefault(),_=>null};
 private void ApplyOneTimelineChange(FrameTrailProject p,TimelineChange c)
 {
  if(c.Selection.Kind=="screen"){var s=p.Timeline.Segments.First(x=>x.Id==c.Selection.Id);if(c.Operation=="reorder")p.Timeline.Reorder(s.Id,c.NewIndex);else{s.SourceStart=c.SourceStart;s.SourceDuration=c.SourceDuration;}}
  else if(c.Selection.Kind=="zoom"){var z=p.ZoomClips.First(x=>x.Id==c.Selection.Id);var startChanged=Math.Abs(z.Start-c.SourceStart)>1e-6;var endChanged=Math.Abs(z.End-(c.SourceStart+c.SourceDuration))>1e-6;z.Start=c.SourceStart;z.End=c.SourceStart+c.SourceDuration;if(c.Operation=="move"&&startChanged){z.EnterProgressOffset=0;z.ExitProgressOffset=0;}else{if(c.Operation=="trim-start"&&startChanged)z.EnterProgressOffset=0;if(c.Operation=="trim-end"&&endChanged)z.ExitProgressOffset=0;}}
  else if(c.Selection.Kind=="motion"){var z=p.ScreenMotions.First(x=>x.Id==c.Selection.Id);z.Start=c.SourceStart;z.Duration=c.SourceDuration;}
  else if(c.Selection.Kind=="cameraMotion"){var z=p.CameraMotions.First(x=>x.Id==c.Selection.Id);z.Start=c.SourceStart;z.Duration=c.SourceDuration;}
  else if(c.Selection.Kind=="overlay"){var z=p.Overlays.First(x=>x.Id==c.Selection.Id);z.Start=c.SourceStart;z.Duration=c.SourceDuration;}
  else if(c.Selection.Kind=="blur"){var z=p.BlurClips.First(x=>x.Id==c.Selection.Id);z.Start=c.SourceStart;z.Duration=c.SourceDuration;}
  else if(c.Selection.Kind=="camera"&&p.Camera!=null){var a=p.Camera;if(a.SyncSegments is {Count:>0}){if(c.Operation=="trim-start")CameraTimelineOperations.TrimTimelineStart(a,Math.Clamp(c.SourceStart,a.TimelineStart,a.TimelineStart+a.Duration-.04));else if(c.Operation=="trim-end")CameraTimelineOperations.TrimTimelineEnd(a,a.TimelineStart+Math.Clamp(c.SourceDuration,.04,a.Duration));else CameraTimelineOperations.Move(a,c.SourceStart-a.TimelineStart);return;}var available=AvailableAssetDuration(a.MediaPath,a.SourceStart+a.Duration);if(c.Operation=="trim-start"){var delta=Math.Clamp(c.SourceStart-a.TimelineStart,-a.SourceStart,a.Duration-.04);a.SourceStart+=delta;a.TimelineStart+=delta;a.Duration-=delta;}else{a.TimelineStart=c.SourceStart;a.Duration=Math.Min(c.SourceDuration,available-a.SourceStart);}}
  else if(c.Selection.Kind=="audio"){var index=Enumerable.Range(0,p.AudioTracks.Count).FirstOrDefault(i=>EditorTimelineControl.AudioId(i)==c.Selection.Id,-1);if(index>=0){var a=p.AudioTracks[index];var available=AvailableAssetDuration(a.MediaPath,a.SourceStart+a.Duration*a.SourceRate);if(a.SyncSegments is {Count:>0}){if(c.Operation=="trim-start")AudioTimelineOperations.TrimTimelineStart(a,Math.Clamp(c.SourceStart,a.OffsetSeconds,a.OffsetSeconds+a.Duration-.04));else if(c.Operation=="trim-end")AudioTimelineOperations.TrimTimelineEnd(a,Math.Clamp(c.SourceStart+c.SourceDuration,a.OffsetSeconds+.04,a.OffsetSeconds+a.Duration));else AudioTimelineOperations.Move(a,Math.Max(c.SourceStart-a.OffsetSeconds,-a.OffsetSeconds));}else if(c.Operation=="trim-start"){var delta=Math.Clamp(c.SourceStart-a.OffsetSeconds,-a.SourceStart/a.SourceRate,a.Duration-.04);a.SourceStart+=delta*a.SourceRate;a.OffsetSeconds+=delta;a.Duration-=delta;}else if(c.Operation=="trim-end"){a.Duration=Math.Min(a.Duration,Math.Max(.04,c.SourceStart+c.SourceDuration-a.OffsetSeconds));AudioTimelineOperations.TrimAssetEnd(a,available);}else{a.OffsetSeconds=c.SourceStart;a.Duration=Math.Min(c.SourceDuration,Math.Max(0,(available-a.SourceStart)/a.SourceRate));}}}
 }
 private void DeleteSelectionClick(object sender,RoutedEventArgs e)=>ApplyEdit(p=>
 {
  if(TimelineView.MultiSelection.Count>1){var selections=TimelineView.MultiSelection.ToArray();var screens=selections.Where(s=>s.Kind=="screen").Select(s=>s.Id).ToHashSet();if(p.Timeline.Segments.All(s=>screens.Contains(s.Id)))throw new InvalidOperationException("至少保留一个屏幕片段。");var audioIds=selections.Where(s=>s.Kind=="audio").Select(s=>s.Id).ToHashSet();var audioIndexes=Enumerable.Range(0,p.AudioTracks.Count).Where(i=>audioIds.Contains(EditorTimelineControl.AudioId(i))).OrderByDescending(i=>i).ToArray();foreach(var selection in selections.Where(s=>s.Kind!="audio"))DeleteSelected(p,selection);foreach(var index in audioIndexes)p.AudioTracks.RemoveAt(index);TimelineView.MultiSelection.Clear();selectedEffect=null;return;}
  var s=selectedEffect;
  if(s==null){if(p.Timeline.Segments.Count<=1)throw new InvalidOperationException("至少保留一个屏幕片段；可重新导入或建立新项目。");var id=Selected(p).Id;p.Timeline.Segments.RemoveAll(x=>x.Id==id);}
  else if(s.Kind=="zoom")p.ZoomClips.RemoveAll(x=>x.Id==s.Id);
  else if(s.Kind=="motion")p.ScreenMotions.RemoveAll(x=>x.Id==s.Id);
  else if(s.Kind=="cameraMotion")p.CameraMotions.RemoveAll(x=>x.Id==s.Id);
  else if(s.Kind=="overlay")p.Overlays.RemoveAll(x=>x.Id==s.Id);
  else if(s.Kind=="blur")p.BlurClips.RemoveAll(x=>x.Id==s.Id);
  else if(s.Kind=="camera")p.Camera=null;
  else if(s.Kind=="audio"){var i=Enumerable.Range(0,p.AudioTracks.Count).FirstOrDefault(i=>EditorTimelineControl.AudioId(i)==s.Id,-1);if(i>=0)p.AudioTracks.RemoveAt(i);}
  selectedEffect=null;
 });
 private static void DeleteSelected(FrameTrailProject p,TimelineSelection s)
 {
  switch(s.Kind){case "screen":p.Timeline.Segments.RemoveAll(x=>x.Id==s.Id);break;case "zoom":p.ZoomClips.RemoveAll(x=>x.Id==s.Id);break;case "motion":p.ScreenMotions.RemoveAll(x=>x.Id==s.Id);break;case "cameraMotion":p.CameraMotions.RemoveAll(x=>x.Id==s.Id);break;case "overlay":p.Overlays.RemoveAll(x=>x.Id==s.Id);break;case "blur":p.BlurClips.RemoveAll(x=>x.Id==s.Id);break;case "camera":p.Camera=null;break;case "audio":var i=Enumerable.Range(0,p.AudioTracks.Count).FirstOrDefault(i=>EditorTimelineControl.AudioId(i)==s.Id,-1);if(i>=0)p.AudioTracks.RemoveAt(i);break;}
 }
 private void TimelineContextMenu()
 {
  var menu=new ContextMenu();void Item(string label,RoutedEventHandler action){var m=new MenuItem{Header=label};m.Click+=action;menu.Items.Add(m);}
  Item("分割屏幕片段  S",SplitClick);Item("删除所选  Delete",DeleteSelectionClick);
  Item("设置导出起点  I",(_,_)=>SetExportRange(true));Item("设置导出终点  O",(_,_)=>SetExportRange(false));Item("清除导出范围",(_,_)=>{TimelineView.RangeStart=null;TimelineView.RangeEnd=null;TimelineView.InvalidateVisual();});
  if(selectedEffect!=null){Item("复制效果  Ctrl+C",(_,_)=>CopyEffect());Item(selectedEffect.Kind=="camera"?"粘贴效果并替换摄像头轨  Ctrl+V":"粘贴效果  Ctrl+V",(_,_)=>PasteEffect(false));Item("粘贴属性  Ctrl+Shift+V",(_,_)=>PasteEffect(true));}
  else{Item("裁去播放头左侧",TrimLeftClick);Item("裁去播放头右侧",TrimRightClick);foreach(var rate in new[]{.25,.5,.75,1d,1.5,2,4,8})Item($"速度 {rate}×",(_,_)=>ApplyEdit(p=>p.Timeline.SetPlaybackRate(Selected(p).Id,rate)));Item("向左重排",MoveLeftClick);Item("向右重排",MoveRightClick);}
  menu.PlacementTarget=TimelineView;menu.IsOpen=true;
 }
 private void CopyEffect()
 {
  if(project==null||selectedEffect==null)return;object? value=selectedEffect.Kind switch{"zoom"=>project.ZoomClips.FirstOrDefault(x=>x.Id==selectedEffect.Id),"motion"=>project.ScreenMotions.FirstOrDefault(x=>x.Id==selectedEffect.Id),"cameraMotion"=>project.CameraMotions.FirstOrDefault(x=>x.Id==selectedEffect.Id),"camera"=>project.Camera,"overlay"=>project.Overlays.FirstOrDefault(x=>x.Id==selectedEffect.Id),"blur"=>project.BlurClips.FirstOrDefault(x=>x.Id==selectedEffect.Id),_=>null};if(value==null)return;
  effectClipboardKind=selectedEffect.Kind;effectClipboardProjectFile=projectFile;effectClipboardJson=JsonSerializer.Serialize(value,value.GetType());Status.Text="已复制完整效果片段。";
 }
 private void PasteEffect(bool attributes)
 {
  if(effectClipboardJson==null||effectClipboardKind==null||project==null)return;
  var t=project.Timeline.MapOutputToSource(Math.Clamp(timelineHoverPosition??Playhead.Value,0,project.Timeline.Duration));t=Math.Min(t,Math.Max(0,project.Media.Duration-.05));
  ApplyEdit(p=>{
   if(attributes&&selectedEffect?.Kind!=effectClipboardKind)throw new InvalidOperationException("请先选择同类效果片段。");
   if(effectClipboardKind=="zoom"){var value=JsonSerializer.Deserialize<ZoomClip>(effectClipboardJson)!;if(value.Automatic)value.Focus=ToCropLocalFocus(p,value.Focus);var target=attributes?p.ZoomClips.First(x=>x.Id==selectedEffect!.Id):null;var duration=value.End-value.Start;value=value with{Id=target?.Id??Guid.NewGuid(),Start=target?.Start??t,End=target?.End??Math.Min(p.Media.Duration,t+duration),EnterProgressOffset=attributes?0:value.EnterProgressOffset,ExitProgressOffset=attributes?0:value.ExitProgressOffset,Automatic=false};if(target!=null)p.ZoomClips.Remove(target);p.ZoomClips.Add(value);selectedEffect=new("zoom",value.Id);}
   if(effectClipboardKind=="motion"){var value=JsonSerializer.Deserialize<ScreenMotionClip>(effectClipboardJson)!;var target=attributes?p.ScreenMotions.First(x=>x.Id==selectedEffect!.Id):null;value=value with{Id=target?.Id??Guid.NewGuid(),Start=target?.Start??t,Duration=target?.Duration??Math.Min(value.Duration,p.Media.Duration-t)};if(target!=null)p.ScreenMotions.Remove(target);p.ScreenMotions.Add(value);selectedEffect=new("motion",value.Id);}
   if(effectClipboardKind=="cameraMotion"){var value=JsonSerializer.Deserialize<CameraMotionClip>(effectClipboardJson)!;var target=attributes?p.CameraMotions.First(x=>x.Id==selectedEffect!.Id):null;value=value with{Id=target?.Id??Guid.NewGuid(),Start=target?.Start??t,Duration=target?.Duration??Math.Min(value.Duration,p.Media.Duration-t)};if(target!=null)p.CameraMotions.Remove(target);p.CameraMotions.Add(value);selectedEffect=new("cameraMotion",value.Id);}
   if(effectClipboardKind=="camera"){var value=JsonSerializer.Deserialize<CameraTrack>(effectClipboardJson)!;var target=attributes?(p.Camera??throw new InvalidOperationException("请先选择摄像头轨道。")):null;if(target!=null)value=value with{MediaPath=target.MediaPath,AssetAspectRatio=target.AssetAspectRatio,SourceStart=target.SourceStart,Duration=target.Duration,TimelineStart=target.TimelineStart,SyncSegments=target.SyncSegments?.Select(x=>x with{}).ToList()};else if(effectClipboardProjectFile!=null&&!string.Equals(System.IO.Path.GetFullPath(effectClipboardProjectFile),System.IO.Path.GetFullPath(projectFile!),StringComparison.OrdinalIgnoreCase)){var source=FrameTrailProjectStore.ResolveAssetPath(effectClipboardProjectFile,value.MediaPath);if(!System.IO.File.Exists(source))throw new FileNotFoundException("复制的摄像头素材不存在。",source);var asset="media/pasted-camera-"+Guid.NewGuid().ToString("N")+System.IO.Path.GetExtension(source);var destination=FrameTrailProjectStore.ResolveAssetPath(projectFile!,asset);System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);System.IO.File.Copy(source,destination);value=value with{MediaPath=asset};}p.Camera=value;selectedEffect=new("camera",Guid.Empty);}
   if(effectClipboardKind=="overlay"){var value=JsonSerializer.Deserialize<OverlayClip>(effectClipboardJson)!;var target=attributes?p.Overlays.First(x=>x.Id==selectedEffect!.Id):null;var asset=target?.MediaPath??value.MediaPath;if(target==null&&effectClipboardProjectFile!=null&&projectFile!=effectClipboardProjectFile){var source=FrameTrailProjectStore.ResolveAssetPath(effectClipboardProjectFile,value.MediaPath);asset="media/pasted-"+Guid.NewGuid().ToString("N")+System.IO.Path.GetExtension(source);var destination=FrameTrailProjectStore.ResolveAssetPath(projectFile!,asset);System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);System.IO.File.Copy(source,destination);}value=value with{MediaPath=asset,Id=target?.Id??Guid.NewGuid(),Start=target?.Start??t,Duration=target?.Duration??Math.Min(value.Duration,p.Media.Duration-t)};if(target!=null)p.Overlays.Remove(target);p.Overlays.Add(value);selectedEffect=new("overlay",value.Id);}
   if(effectClipboardKind=="blur"){var value=JsonSerializer.Deserialize<BlurClip>(effectClipboardJson)!;var target=attributes?p.BlurClips.First(x=>x.Id==selectedEffect!.Id):null;value=value with{Id=target?.Id??Guid.NewGuid(),Start=target?.Start??t,Duration=target?.Duration??Math.Min(value.Duration,p.Media.Duration-t)};if(target!=null)p.BlurClips.Remove(target);p.BlurClips.Add(value);selectedEffect=new("blur",value.Id);}
  });
 }
}
