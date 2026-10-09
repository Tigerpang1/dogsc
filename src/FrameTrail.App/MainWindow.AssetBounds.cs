using FrameTrail.Media;
using System.IO;
using Microsoft.Win32;
namespace FrameTrail.App;
public partial class MainWindow
{
 private readonly Dictionary<string,double> auxiliaryDurations=new(StringComparer.OrdinalIgnoreCase);
 private async Task RelocateMissingAuxiliaryAssets(FrameTrailProject p,string file)
 {
  var references=new List<(string Path,string Label,Action<string> Update)>();
  if(p.Camera is {} camera)references.Add((camera.MediaPath,"摄像头",v=>camera.MediaPath=v));
  foreach(var audio in p.AudioTracks)references.Add((audio.MediaPath,"声音",v=>audio.MediaPath=v));
  foreach(var overlay in p.Overlays)references.Add((overlay.MediaPath,"贴图",v=>overlay.MediaPath=v));
  if(p.Settings.BackgroundStyle is {ImagePath:not null} background)references.Add((background.ImagePath,"背景",v=>background.ImagePath=v));
  if(p.Settings.BackgroundStyle is {VideoPath:not null} videoBackground)references.Add((videoBackground.VideoPath,"视频背景",v=>videoBackground.VideoPath=v));
  var replacements=new Dictionary<string,(string Path,MediaInfo Info)>(StringComparer.OrdinalIgnoreCase);var changed=false;
  foreach(var item in references)
  {
   if(File.Exists(FrameTrailProjectStore.ResolveAssetPath(file,item.Path)))continue;
   if(replacements.TryGetValue(item.Path,out var existing)){await ValidateReplacement(item,existing.Info,FrameTrailProjectStore.ResolveAssetPath(file,existing.Path));item.Update(existing.Path);continue;}
   var dialog=new OpenFileDialog{Title=$"{item.Label}素材缺失：重新定位 {Path.GetFileName(item.Path)}",Filter="媒体文件|*.mp4;*.mov;*.mkv;*.avi;*.wav;*.mp3;*.m4a;*.aac;*.flac;*.png;*.jpg;*.jpeg;*.webp;*.bmp|所有文件|*.*"};
   if(dialog.ShowDialog(this)!=true)throw new FileNotFoundException("素材未重新定位，项目尚未打开。",item.Path);
   var info=await MediaTools.ProbeAsync(dialog.FileName);
   await ValidateReplacement(item,info,dialog.FileName);
   var relative="media/relocated-"+Guid.NewGuid().ToString("N")+Path.GetExtension(dialog.FileName);var destination=FrameTrailProjectStore.ResolveAssetPath(file,relative);Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.Copy(dialog.FileName,destination);item.Update(relative);replacements[item.Path]=(relative,info);changed=true;
  }
  if(changed)FrameTrailProjectStore.Save(file,p);

  async Task ValidateReplacement((string Path,string Label,Action<string> Update) item,MediaInfo info,string candidatePath)
  {
   if(item.Label is "摄像头" or "声音")
   {
    if(info.Duration<=0||item.Label=="摄像头"&&info.Width<1||item.Label=="声音"&&!info.HasAudio)throw new InvalidDataException("所选文件没有需要的媒体轨道。");
    var end=item.Label=="摄像头"?CameraAssetEnd(p.Camera!):p.AudioTracks.Where(a=>string.Equals(a.MediaPath,item.Path,StringComparison.OrdinalIgnoreCase)).Select(AudioAssetEnd).DefaultIfEmpty(0).Max();
    if(info.Duration+.05<end)throw new InvalidDataException("重新定位的素材过短，无法匹配原片段。");
   }
   else
   {
    if(info.Width<1||item.Label=="视频背景"&&info.Duration<=0)throw new InvalidDataException("所选文件没有可用的图片或视频画面。");
    await MediaTools.DecodeFrameAsync(candidatePath,0,16,16,CancellationToken.None);
   }
  }
 }
 private async Task LoadAuxiliaryDurations(FrameTrailProject p,string file)
 {
  auxiliaryDurations.Clear();var paths=p.AudioTracks.Select(a=>a.MediaPath).Concat(p.Camera is {} camera?[camera.MediaPath]:Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();var changed=false;var warnings=new List<string>();
  foreach(var relative in paths)
  {
   MediaInfo info;
   try{info=await MediaTools.ProbeAsync(FrameTrailProjectStore.ResolveAssetPath(file,relative));}
   catch(Exception ex){App.Log(ex);continue;}
   if(info.Duration<=0)throw new InvalidDataException($"辅助素材没有有效时长：{relative}");
   auxiliaryDurations[relative]=info.Duration;
   if(p.Camera is { } measuredCamera&&string.Equals(measuredCamera.MediaPath,relative,StringComparison.OrdinalIgnoreCase))
   {
    if(info.Width>0&&info.Height>0){var aspect=info.Width/(double)info.Height;if(measuredCamera.AssetAspectRatio!=aspect){measuredCamera.AssetAspectRatio=aspect;changed=true;}}
    if(CameraAssetEnd(measuredCamera)>info.Duration+.05){try{CameraTimelineOperations.TrimAssetEnd(measuredCamera,info.Duration,info.Duration);changed=true;warnings.Add("摄像头同步区间超出素材时长，已按实际可用范围裁短。");}catch(InvalidOperationException ex){throw new InvalidDataException($"摄像头素材时长不足以覆盖工程同步范围：{relative}",ex);}}
   }
   foreach(var audio in p.AudioTracks.Where(x=>string.Equals(x.MediaPath,relative,StringComparison.OrdinalIgnoreCase)))
    if(AudioAssetEnd(audio)>info.Duration+.05){try{AudioTimelineOperations.TrimAssetEnd(audio,info.Duration);changed=true;warnings.Add("独立音轨同步区间超出素材时长，已按实际可用范围裁短。");}catch(InvalidOperationException ex){throw new InvalidDataException($"独立音轨素材时长不足以覆盖工程同步范围：{relative}",ex);}}
  }
  if(changed)FrameTrailProjectStore.Save(file,p);
  if(warnings.Count>0)Status.Text=string.Join(" ",warnings.Distinct());
 }
 private double AvailableAssetDuration(string path,double fallback)=>auxiliaryDurations.TryGetValue(path,out var duration)?duration:fallback;
 private static double CameraAssetEnd(CameraTrack camera)=>camera.SyncSegments is {Count:>0} parts?parts.Max(x=>x.SourceStart+x.Duration*x.SourceRate):camera.SourceStart+camera.Duration;
 private static double AudioAssetEnd(AudioTrack audio)=>audio.SyncSegments is {Count:>0} parts?parts.Max(x=>x.SourceStart+x.Duration*x.SourceRate):audio.SourceStart+audio.Duration*audio.SourceRate;
 private void ChangeCameraSourceStart(double value)=>ApplyEdit(p=>{if(p.Camera is not {} camera)return;if(camera.SyncSegments is {Count:>0}){CameraTimelineOperations.SlipAssetStart(camera,value,AvailableAssetDuration(camera.MediaPath,CameraAssetEnd(camera)));return;}var available=AvailableAssetDuration(camera.MediaPath,camera.SourceStart+camera.Duration);camera.SourceStart=Math.Clamp(value,0,Math.Max(0,available-.04));camera.Duration=Math.Min(camera.Duration,available-camera.SourceStart);});
}
