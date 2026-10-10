using FrameTrail.Media;
using System.IO;
using System.Windows;
using Microsoft.Win32;
namespace FrameTrail.App;
public partial class MainWindow
{
 private readonly Dictionary<string,double> auxiliaryDurations=new(StringComparer.OrdinalIgnoreCase);
 private async Task<bool> RelocateMissingAuxiliaryAssets(FrameTrailProject p,string file,ProjectAssetRelocationTransaction relocation)
 {
  var references=new List<(string Path,string Label,Action<string> Update)>();
  if(p.Camera is {} camera)references.Add((camera.MediaPath,"摄像头",v=>camera.MediaPath=v));
  foreach(var audio in p.AudioTracks)references.Add((audio.MediaPath,"声音",v=>audio.MediaPath=v));
  foreach(var overlay in p.Overlays)references.Add((overlay.MediaPath,"贴图",v=>overlay.MediaPath=v));
  if(p.Settings.BackgroundStyle is {ImagePath:not null} background)references.Add((background.ImagePath,"背景",v=>background.ImagePath=v));
  if(p.Settings.BackgroundStyle is {VideoPath:not null} videoBackground)references.Add((videoBackground.VideoPath,"视频背景",v=>videoBackground.VideoPath=v));
  var replacements=new Dictionary<string,(string Path,MediaInfo Info)>(StringComparer.OrdinalIgnoreCase);var validated=new Dictionary<string,MediaInfo>(StringComparer.OrdinalIgnoreCase);var changed=false;
  foreach(var item in references)
  {
   var validationKey=item.Label+"\0"+item.Path;
   if(replacements.TryGetValue(validationKey,out var existing)){item.Update(existing.Path);continue;}
   if(validated.ContainsKey(validationKey))continue;
   var existingPath=FrameTrailProjectStore.ResolveAssetPath(file,item.Path);
   var resolved=await AssetCandidateResolver.ResolveAsync(existingPath,
    async candidate=>{var info=await MediaTools.ProbeAsync(candidate);await ValidateReplacement(item,info,candidate,requireCoverage:false);return info;},
    async candidate=>{var info=await MediaTools.ProbeAsync(candidate);await ValidateReplacement(item,info,candidate,requireCoverage:true);return info;},
    ex=>
    {
     App.Log(ex);
     SurfaceDialog.Show(this,$"项目中的“{item.Label}”素材缺失或无法读取，请重新选择。\n\n{ex.Message}","素材验证失败",MessageBoxButton.OK,MessageBoxImage.Warning);
     return Task.CompletedTask;
    },
    ()=>
    {
     var dialog=new OpenFileDialog{Title=$"{item.Label}素材缺失或无法读取：重新定位 {Path.GetFileName(item.Path)}",Filter="媒体文件|*.mp4;*.mov;*.mkv;*.avi;*.wav;*.mp3;*.m4a;*.aac;*.flac;*.png;*.jpg;*.jpeg;*.webp;*.bmp|所有文件|*.*"};
     return Task.FromResult(dialog.ShowDialog(this)==true?dialog.FileName:null);
    });
   if(Path.GetFullPath(resolved.Path).Equals(Path.GetFullPath(existingPath),OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal)){validated[validationKey]=resolved.Info;continue;}
   var relative="media/relocated-"+Guid.NewGuid().ToString("N")+Path.GetExtension(resolved.Path);var destination=FrameTrailProjectStore.ResolveAssetPath(file,relative);relocation.Copy(resolved.Path,destination);item.Update(relative);replacements[validationKey]=(relative,resolved.Info);changed=true;
  }
  return changed;

  async Task ValidateReplacement((string Path,string Label,Action<string> Update) item,MediaInfo info,string candidatePath,bool requireCoverage)
  {
   if(item.Label is "摄像头" or "声音")
   {
    if(info.Duration<=0||item.Label=="摄像头"&&info.Width<1||item.Label=="声音"&&!info.HasAudio)throw new InvalidDataException("所选文件没有需要的媒体轨道。");
    var end=item.Label=="摄像头"?CameraAssetEnd(p.Camera!):p.AudioTracks.Where(a=>string.Equals(a.MediaPath,item.Path,StringComparison.OrdinalIgnoreCase)).Select(AudioAssetEnd).DefaultIfEmpty(0).Max();
    if(requireCoverage&&info.Duration+.05<end)throw new InvalidDataException("重新定位的素材过短，无法匹配原片段。");
    if(item.Label=="摄像头")await MediaTools.DecodeFrameAsync(candidatePath,0,16,16,CancellationToken.None);
    else await MediaTools.RunAsync(MediaTools.Ffmpeg,["-v","error","-xerror","-i",candidatePath,"-map","0:a:0","-t","0.25","-f","null","-"],CancellationToken.None);
   }
   else
   {
    if(info.Width<1||item.Label=="视频背景"&&info.Duration<=0)throw new InvalidDataException("所选文件没有可用的图片或视频画面。");
    await MediaTools.DecodeFrameAsync(candidatePath,0,16,16,CancellationToken.None);
   }
  }
 }
 private async Task<(Dictionary<string,double> Durations,bool Changed)> LoadAuxiliaryDurations(FrameTrailProject p,string file)
 {
  var durations=new Dictionary<string,double>(StringComparer.OrdinalIgnoreCase);var paths=p.AudioTracks.Select(a=>a.MediaPath).Concat(p.Camera is {} camera?[camera.MediaPath]:Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();var changed=false;var warnings=new List<string>();
  foreach(var relative in paths)
  {
   MediaInfo info;
    try{info=await MediaTools.ProbeAsync(FrameTrailProjectStore.ResolveAssetPath(file,relative));}
    catch(OperationCanceledException){throw;}
    catch(Exception ex){App.Log(ex);throw new InvalidDataException($"无法读取辅助素材，项目未打开：{relative}",ex);}
   if(info.Duration<=0)throw new InvalidDataException($"辅助素材没有有效时长：{relative}");
   durations[relative]=info.Duration;
   if(p.Camera is { } measuredCamera&&string.Equals(measuredCamera.MediaPath,relative,StringComparison.OrdinalIgnoreCase))
   {
    if(info.Width>0&&info.Height>0){var aspect=info.Width/(double)info.Height;if(measuredCamera.AssetAspectRatio!=aspect){measuredCamera.AssetAspectRatio=aspect;changed=true;}}
    if(CameraAssetEnd(measuredCamera)>info.Duration+.05){try{CameraTimelineOperations.TrimAssetEnd(measuredCamera,info.Duration,info.Duration);changed=true;warnings.Add("摄像头同步区间超出素材时长，已按实际可用范围裁短。");}catch(InvalidOperationException ex){throw new InvalidDataException($"摄像头素材时长不足以覆盖工程同步范围：{relative}",ex);}}
   }
   foreach(var audio in p.AudioTracks.Where(x=>string.Equals(x.MediaPath,relative,StringComparison.OrdinalIgnoreCase)))
    if(AudioAssetEnd(audio)>info.Duration+.05){try{AudioTimelineOperations.TrimAssetEnd(audio,info.Duration);changed=true;warnings.Add("独立音轨同步区间超出素材时长，已按实际可用范围裁短。");}catch(InvalidOperationException ex){throw new InvalidDataException($"独立音轨素材时长不足以覆盖工程同步范围：{relative}",ex);}}
  }

  if(warnings.Count>0)Status.Text=string.Join(" ",warnings.Distinct());
  return (durations,changed);
 }
 private double AvailableAssetDuration(string path,double fallback)=>auxiliaryDurations.TryGetValue(path,out var duration)?duration:fallback;
 private static double CameraAssetEnd(CameraTrack camera)=>camera.SyncSegments is {Count:>0} parts?parts.Max(x=>x.SourceStart+x.Duration*x.SourceRate):camera.SourceStart+camera.Duration;
 private static double AudioAssetEnd(AudioTrack audio)=>audio.SyncSegments is {Count:>0} parts?parts.Max(x=>x.SourceStart+x.Duration*x.SourceRate):audio.SourceStart+audio.Duration*audio.SourceRate;
 private void ChangeCameraSourceStart(double value)=>ApplyEdit(p=>{if(p.Camera is not {} camera)return;if(camera.SyncSegments is {Count:>0}){CameraTimelineOperations.SlipAssetStart(camera,value,AvailableAssetDuration(camera.MediaPath,CameraAssetEnd(camera)));return;}var available=AvailableAssetDuration(camera.MediaPath,camera.SourceStart+camera.Duration);camera.SourceStart=Math.Clamp(value,0,Math.Max(0,available-.04));camera.Duration=Math.Min(camera.Duration,available-camera.SourceStart);});
}
