using System.IO;
using System.Text.Json;
using FrameTrail.Capture;
using FrameTrail.Media;
namespace FrameTrail.App;
public partial class MainWindow
{
 private async Task<string> RecoverReadableRecording(string folder)
 {
  var media=Path.Combine(folder,"media","screen.mp4");var notes=new List<string>();MediaInfo? info=null;Exception? mediaError=null;
  if(File.Exists(media))try{info=await MediaTools.ProbeAsync(media);}catch(Exception ex){mediaError=ex;}
  if(info==null||info.Duration<=0||info.Width==0){var candidates=FrameCaptureService.FindRecoverableRecordings(folder).Where(x=>Path.GetFileName(x.OriginalOutputPath).Equals("screen.mp4",StringComparison.OrdinalIgnoreCase)).OrderByDescending(x=>File.GetLastWriteTimeUtc(x.ManifestPath)).ToArray();if(candidates.Length==0)throw new InvalidDataException("录制中断前未留下可解码的视频或已封装片段。原始素材与日志已保留："+folder,mediaError);media=Path.Combine(folder,"media","screen-recovered-"+Guid.NewGuid().ToString("N")[..8]+".mp4");await FrameCaptureService.RecoverReadableRecordingAsync(candidates[0],media);info=await MediaTools.ProbeAsync(media);notes.Add("已从正常封装的录制片段恢复画面；中断时未封装的末段与原文件保留，未加入恢复工程。");}
  if(info.Duration<=0||info.Width==0)throw new InvalidDataException("素材没有完整可读的视频帧。");
  var recovered=MakeProject(Path.GetRelativePath(folder,media).Replace('\\','/'),info);RecordingResult? timing=null;
  try{var resultPath=Path.Combine(folder,"capture-result.json");if(File.Exists(resultPath))timing=JsonSerializer.Deserialize<RecordingResult>(await File.ReadAllTextAsync(resultPath));}catch(Exception ex){notes.Add("录制时钟记录无法读取："+ex.Message);}
  var handles=new List<CaptureTrackHandle>();try{var handlePath=Path.Combine(folder,"independent-tracks.json");if(File.Exists(handlePath))handles=JsonSerializer.Deserialize<List<CaptureTrackHandle>>(await File.ReadAllTextAsync(handlePath))??[];}catch(Exception ex){notes.Add("独立轨时钟记录无法读取："+ex.Message);}
  var results=new List<CaptureTrackResult>();try{var resultPath=Path.Combine(folder,"independent-tracks-result.json");if(File.Exists(resultPath))results=JsonSerializer.Deserialize<List<CaptureTrackResult>>(await File.ReadAllTextAsync(resultPath))??[];}catch(Exception ex){notes.Add("独立轨暂停记录无法读取："+ex.Message);}
  foreach(var name in new[]{"system.wav","microphone.wav","camera.mp4"})
  {
   var relative="media/"+name;var asset=Path.Combine(folder,"media",name);if(!File.Exists(asset))continue;
   try{var trackInfo=await MediaTools.ProbeAsync(asset);if(trackInfo.Duration<=0)continue;var handle=handles.FirstOrDefault(x=>Path.GetFileName(x.MediaPath).Equals(name,StringComparison.OrdinalIgnoreCase));var trackResult=results.FirstOrDefault(x=>Path.GetFileName(x.Handle.MediaPath).Equals(name,StringComparison.OrdinalIgnoreCase));var offset=timing!=null&&handle!=null?MediaTime(handle.StartStopwatchTicks,timing):0;if(timing==null||handle==null)notes.Add(name+" 缺少完整首帧时钟，已按零偏移恢复，可在声音/摄像头面板校准。");if(name=="camera.mp4"&&trackInfo.Width>0){if(timing!=null&&trackResult!=null)AttachMappedCamera(recovered,timing,trackResult,relative,trackInfo.Duration);else{recovered.Camera=new(){MediaPath=relative,Duration=trackInfo.Duration,TimelineStart=offset};notes.Add("camera.mp4 缺少完整相机暂停记录，已按连续素材恢复；暂停后的局部同步需要校准。");}}else if(trackInfo.HasAudio){if(timing!=null&&trackResult!=null)AttachMappedAudio(recovered,timing,trackResult,relative,trackInfo.Duration);else{recovered.AudioTracks.Add(new(){MediaPath=relative,Duration=trackInfo.Duration,OffsetSeconds=offset});if(timing?.PausedIntervals?.Count>0)notes.Add(name+" 缺少音轨暂停记录，已恢复原始连续音轨；多次暂停后的局部同步需校准。");}}}
   catch(Exception ex){notes.Add(name+" 未加入工程，原文件保留："+ex.Message);}
  }
  var pointerPath=Path.Combine(folder,"pointer.jsonl");if(timing!=null&&File.Exists(pointerPath))foreach(var line in File.ReadLines(pointerPath))try{var input=JsonSerializer.Deserialize<InputEvent>(line);if(input==null||input.Kind is not (InputEventKind.Move or InputEventKind.MouseDown))continue;if(timing.PausedIntervals?.Any(x=>input.StopwatchTicks>=x.PauseStartStopwatchTicks&&input.StopwatchTicks<x.ResumeStopwatchTicks)==true)continue;var time=MediaTime(input.StopwatchTicks,timing);if(time<0||time>info.Duration)continue;var kind=input.Kind==InputEventKind.Move?PointerEventKind.Move:input.MouseButton==FrameTrail.Capture.MouseButton.Right?PointerEventKind.RightClick:PointerEventKind.LeftClick;recovered.PointerEvents.Add(new(time,new(input.NormalizedX,input.NormalizedY),kind));}catch(JsonException){notes.Add("忽略一条未写完的指针日志。");}
  else if(File.Exists(pointerPath))notes.Add("没有首帧时钟，指针原始日志保留，未自动加入工程。");
  var markersPath=Path.Combine(folder,"markers.jsonl");if(timing!=null&&File.Exists(markersPath))foreach(var line in File.ReadLines(markersPath))try{using var json=JsonDocument.Parse(line);if(!json.RootElement.TryGetProperty("Tick",out var tickValue)||!tickValue.TryGetInt64(out var tick)){notes.Add("忽略缺少时间的标记记录。");continue;}var label=json.RootElement.TryGetProperty("Label",out var labelValue)&&labelValue.ValueKind==JsonValueKind.String?labelValue.GetString()??"标记":"标记";recovered.Markers.Add(new(Guid.NewGuid(),Math.Clamp(MediaTime(tick,timing),0,info.Duration),label));}catch(JsonException){notes.Add("忽略一条未写完的标记日志。");}
  recovered.ZoomClips=AutoZoomPlanner.Plan(recovered.PointerEvents);var path=Path.Combine(folder,"recovered-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+".frametrail");FrameTrailProjectStore.Save(path,recovered);await File.WriteAllLinesAsync(path+".recovery.txt",notes.Count>0?notes:["已恢复可读视频、独立轨道与可对齐的指针记录。"]);return path;
 }
}
