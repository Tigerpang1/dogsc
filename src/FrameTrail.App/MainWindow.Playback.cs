using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FrameTrail.Media;
namespace FrameTrail.App;
public partial class MainWindow
{
 private CancellationTokenSource? playbackCancellation;
 private int playbackRenderedFrames;
 private bool playbackStarting;
 private async Task StartRealtimePlayback()
 {
  if(playbackStarting)return;playbackStarting=true;StopPlayback();playbackCancellation=new();
  try{await StartRealtimePlaybackCore(playbackCancellation.Token);}catch{StopPlayback();throw;}finally{playbackStarting=false;}
 }
 private async Task StartRealtimePlaybackCore(CancellationToken token)
 {
  if(project==null||projectFile==null)return;seekCancellation?.Cancel();var snapshot=project;var file=projectFile;var source=FrameTrailProjectStore.ResolveMediaPath(file,snapshot);
  var info=await MediaTools.ProbeAsync(source,token);
  var hasAudio=info.HasAudio&&MediaTools.HasAudibleMainAudio(snapshot.Timeline,snapshot.Settings.MainAudioVolume,snapshot.Settings.MainAudioMuted)
   ||MediaTools.HasAudibleAudioTracks(snapshot.Timeline,snapshot.AudioTracks,snapshot.Settings.MainAudioVolume,snapshot.Settings.MainAudioMuted);
  if(snapshot.Camera is {Muted:false,Volume:>0} camera)
  {
   var cameraInfo=await MediaTools.ProbeAsync(FrameTrailProjectStore.ResolveAssetPath(file,camera.MediaPath),token);
   if(cameraInfo.HasAudio)
   {
    IReadOnlyList<CameraSyncSegment> mappings=camera.SyncSegments is {Count:>0} sync?sync:[new CameraSyncSegment(camera.SourceStart,camera.TimelineStart,camera.Duration)];
    var track=new AudioTrack{MediaPath=camera.MediaPath,SourceStart=camera.SourceStart,Duration=camera.Duration,OffsetSeconds=camera.TimelineStart,Volume=camera.Volume,SourceRate=mappings[0].SourceRate,SyncSegments=mappings.ToList(),Source=AudioTrackSource.Camera};
    hasAudio|=MediaTools.HasAudibleAudioTrack(snapshot.Timeline,track);
   }
  }
  if(hasAudio&&(proxyPath==null||!File.Exists(proxyPath)))
  {
   var dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FrameTrail","cache");Directory.CreateDirectory(dir);var candidate=Path.Combine(dir,Guid.NewGuid()+".m4a");
   await RunExport(candidate,new(960,540,30,true,true),true,token);token.ThrowIfCancellationRequested();if(!ReferenceEquals(snapshot,project)){if(File.Exists(candidate))File.Delete(candidate);return;}proxyPath=candidate;
  }
  token.ThrowIfCancellationRequested();var start=Playhead.Value>=Playhead.Maximum-.03?0:Playhead.Value;
  var width=previewResolution;var frameRate=previewFrameRate;var height=Math.Max(2,(int)(width*(double)snapshot.Settings.CanvasHeight/snapshot.Settings.CanvasWidth)/2*2);
  var session=await PreviewPlaybackSession.CreateAsync(snapshot,source,file,width,height,frameRate,token);var pixels=new byte[width*height*4];
  try{await session.ReadFrameAsync(start,pixels,token);}catch{await session.DisposeAsync();throw;}
  playbackRenderedFrames=0;playing=true;PreviewImage.Visibility=Visibility.Visible;Player.Visibility=Visibility.Collapsed;PlayButton.Content=new EditorIcon{Kind="pause",Width=18,Height=18};
  if(hasAudio)
  {
   Player.Close();Player.Source=null;var ready=new TaskCompletionSource<bool>();RoutedEventHandler? opened=null;opened=(_,_)=>{Player.MediaOpened-=opened;Player.Position=TimeSpan.FromSeconds(start);Player.Play();ready.TrySetResult(true);};Player.MediaOpened+=opened;
   Player.Source=new Uri(proxyPath!);Player.Play();try{await ready.Task.WaitAsync(TimeSpan.FromSeconds(5),token);}catch{StopPlayback();await session.DisposeAsync();throw;}finally{Player.MediaOpened-=opened;}
  }
  var clock=Stopwatch.StartNew();
  _=Task.Run(async()=>
  {
   try
   {
    await using var ownedSession=session;var lastFrame=-1;var audioCorrection=0d;var nextAudioCheck=1d;
    while(!token.IsCancellationRequested)
    {
     var time=start+clock.Elapsed.TotalSeconds+audioCorrection;if(hasAudio&&clock.Elapsed.TotalSeconds>=nextAudioCheck){var audioTime=await Dispatcher.InvokeAsync(()=>Player.Position.TotalSeconds);if(Math.Abs(audioTime-time)>.08)audioCorrection+=audioTime-time;nextAudioCheck=clock.Elapsed.TotalSeconds+1;time=start+clock.Elapsed.TotalSeconds+audioCorrection;}
     if(time>=snapshot.Timeline.Duration-.015)break;
     var frameIndex=(int)Math.Floor(time*frameRate);if(frameIndex==lastFrame){await Task.Delay(3,token);continue;}lastFrame=frameIndex;time=frameIndex/(double)frameRate;
     await session.ReadFrameAsync(time,pixels,token);token.ThrowIfCancellationRequested();var frame=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,pixels,width*4);frame.Freeze();
     await Dispatcher.InvokeAsync(()=>{if(token.IsCancellationRequested)return;playbackRenderedFrames++;PreviewImage.Source=frame;refreshing=true;Playhead.Value=time;refreshing=false;UpdateTime();});
    }
    if(!token.IsCancellationRequested)await Dispatcher.InvokeAsync(()=>{if(token.IsCancellationRequested)return;StopPlayback();Playhead.Value=Playhead.Maximum;});
   }
   catch(OperationCanceledException){}
   catch(Exception ex){App.Log(ex);await Dispatcher.InvokeAsync(()=>{if(!token.IsCancellationRequested){StopPlayback();Status.Text="预览播放未完成："+ex.Message;}});}
  });
 }
}
