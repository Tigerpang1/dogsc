using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace FrameTrail.App;
public partial class MainWindow
{
 private async Task VerifyRealtimePlayback(string report)
 {
  object result;try{if(project==null)throw new InvalidOperationException("必须打开项目。");Playhead.Value=0;await StartRealtimePlayback();await Task.Delay(1800);var time=Playhead.Value;var frames=playbackRenderedFrames;StopPlayback();if(time<.6||frames<10)throw new InvalidOperationException($"播放未推进：time={time},frames={frames}");var paused=Playhead.Value;await Task.Delay(250);if(Playhead.Value!=paused)throw new InvalidOperationException("暂停后播放头仍移动。");await StartRealtimePlayback();await Task.Delay(600);var resumeFrames=playbackRenderedFrames;var resumed=Playhead.Value;StopPlayback();if(resumeFrames<5||resumed<=paused)throw new InvalidOperationException("再次播放没有恢复音画。");Playhead.Value=Math.Min(2,project.Timeline.Duration/2);await RenderPreview(Playhead.Value);TimelineView.RangeStart=.25;TimelineView.RangeEnd=Math.Min(1.25,project.Timeline.Duration);var range=ExportSnapshot();if(Math.Abs(range.Timeline.Duration-(TimelineView.RangeEnd.Value-.25))>.001)throw new InvalidOperationException("范围导出时长错误。");result=new{Passed=true,Frames=frames,AdvancedSeconds=time,PausedSeconds=paused,ResumeFrames=resumeFrames,ResumedSeconds=resumed,RangeSeconds=range.Timeline.Duration};}catch(Exception ex){App.Log(ex);result=new{Passed=false,Error=ex.ToString()};}Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);await File.WriteAllTextAsync(report,System.Text.Json.JsonSerializer.Serialize(result,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
 }
 private async Task CaptureVisualStates(string directory)
 {
  Directory.CreateDirectory(directory);Width=1920;Height=1249;UpdateLayout();await Task.Delay(700);
  foreach(var state in new[]{("frame","layout","editor"),("frame","background","background"),("frame","frame","mockup"),("zoom","motion","motion"),("zoom","zoom","zoom"),("cursor","","cursor"),("camera","","camera"),("audio","","audio"),("opening","","opening")})
  {
   inspectorTab=state.Item1;if(inspectorTab=="frame")frameTab=state.Item2;if(inspectorTab=="zoom")motionTab=state.Item2;
   if(state.Item3=="motion"&&project?.ScreenMotions.FirstOrDefault() is {} motion)selectedEffect=new("motion",motion.Id);else selectedEffect=null;
   BuildInspector();InspectorScroll.ScrollToTop();var at=state.Item3=="motion"?12.8:project?.Settings.Opening is { Enabled:true } opening?opening.Duration+opening.Stagger*2+.2:.26;at=Math.Min(at,project?.Timeline.Duration??0);refreshing=true;Playhead.Value=at;refreshing=false;UpdateTime();TimelineView.Selection=selectedEffect;await RenderPreview(at);await Task.Delay(150);UpdateLayout();SaveSnapshot(Path.Combine(directory,state.Item3+".png"));
  }
  await ShowFloatingRecorder();await Task.Delay(300);if(recorderSetup!=null){CaptureWindowVisual(recorderSetup,Path.Combine(directory,"recorder.png"));recorderSetup.Close();}
  if(project!=null){selectedEffect=project.ScreenMotions.FirstOrDefault() is {} exportMotion?new("motion",exportMotion.Id):null;await RenderPreview(Math.Min(12.8,project.Timeline.Duration));var export=new ExportWindow(project,PreviewImage.Source,ProjectTitle.Text,(_,_,_,_)=>throw new InvalidOperationException("截图模式不执行导出。")){Owner=this};export.Show();await Task.Delay(300);CaptureWindowVisual(export,Path.Combine(directory,"export.png"));export.Close();}
 }
 private static void CaptureWindowVisual(Window window,string path)
 {
  window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(path);encoder.Save(stream);
 }
}
