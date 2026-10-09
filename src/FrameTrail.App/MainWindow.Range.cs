using System.Windows;
using System.Windows.Controls;
namespace FrameTrail.App;
public partial class MainWindow
{
 private void SetExportRange(bool start)
 {
  if(project==null)return;var time=timelineHoverPosition??Playhead.Value;if(start){TimelineView.RangeStart=time;if(TimelineView.RangeEnd<=time)TimelineView.RangeEnd=project.Timeline.Duration;}else{TimelineView.RangeEnd=time;if(TimelineView.RangeStart>=time)TimelineView.RangeStart=0;}
  TimelineView.RangeStart??=0;TimelineView.RangeEnd??=project.Timeline.Duration;TimelineView.InvalidateVisual();Status.Text=$"导出范围 {TimelineView.RangeStart:0.00}s–{TimelineView.RangeEnd:0.00}s，右键可清除。";
 }
 private FrameTrailProject ExportSnapshot()
 {
  var snapshot=Clone(project!);if(TimelineView.RangeStart is not {} start||TimelineView.RangeEnd is not {} end||end<=start)return snapshot;
  var segments=new List<TimelineSegment>();var elapsed=0d;foreach(var s in snapshot.Timeline.Segments){var a=Math.Max(start,elapsed);var b=Math.Min(end,elapsed+s.OutputDuration);if(b>a)segments.Add(s with{SourceStart=s.SourceStart+(a-elapsed)*s.PlaybackRate,SourceDuration=(b-a)*s.PlaybackRate});elapsed+=s.OutputDuration;}if(segments.Count==0)throw new InvalidOperationException("导出范围内没有视频片段。");snapshot.Timeline.Segments=segments;return snapshot;
 }
 private void TimelineTracksMenu()
 {
  var menu=new ContextMenu();foreach(var entry in new[]{("zoom","缩放"),("motion","屏幕 3D"),("camera","摄像头"),("cameraMotion","摄像运动"),("overlay","贴图"),("blur","柔化"),("audio","声音")}){var item=new MenuItem{Header=entry.Item2,IsCheckable=true,IsChecked=!TimelineView.HiddenTracks.Contains(entry.Item1)};item.Click+=(_,_)=>{if(item.IsChecked)TimelineView.HiddenTracks.Remove(entry.Item1);else TimelineView.HiddenTracks.Add(entry.Item1);TimelineView.Refresh();};menu.Items.Add(item);}menu.PlacementTarget=TimelineView;menu.IsOpen=true;
 }
}
