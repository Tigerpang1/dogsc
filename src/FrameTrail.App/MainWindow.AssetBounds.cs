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
  var replacements=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);var changed=false;
  foreach(var item in references)
  {
   if(File.Exists(FrameTrailProjectStore.ResolveAssetPath(file,item.Path)))continue;
   if(replacements.TryGetValue(item.Path,out var existing)){item.Update(existing);continue;}
   var dialog=new OpenFileDialog{Title=$"{item.Label}素材缺失：重新定位 {Path.GetFileName(item.Path)}",Filter="媒体文件|*.mp4;*.mov;*.mkv;*.avi;*.wav;*.mp3;*.m4a;*.aac;*.flac;*.png;*.jpg;*.jpeg;*.webp;*.bmp|所有文件|*.*"};
   if(dialog.ShowDialog(this)!=true)throw new FileNotFoundException("素材未重新定位，项目尚未打开。",item.Path);
   var info=await MediaTools.ProbeAsync(dialog.FileName);
   if(item.Label is "摄像头" or "声音"){if(info.Duration<=0||item.Label=="摄像头"&&info.Width<1||item.Label=="声音"&&!info.HasAudio)throw new InvalidDataException("所选文件没有需要的媒体轨道。");var end=item.Label=="摄像头"?CameraAssetEnd(p.Camera!):p.AudioTracks.Where(a=>a.MediaPath==item.Path).Max(a=>a.SourceStart+a.Duration);if(info.Duration+.05<end)throw new InvalidDataException("重新定位的素材过短，无法匹配原片段。");}
   else{if(info.Width<1||item.Label=="视频背景"&&info.Duration<=0)throw new InvalidDataException("所选文件没有可用的图片或视频画面。");await MediaTools.DecodeFrameAsync(dialog.FileName,0,16,16,CancellationToken.None);}
   var relative="media/relocated-"+Guid.NewGuid().ToString("N")+Path.GetExtension(dialog.FileName);var destination=FrameTrailProjectStore.ResolveAssetPath(file,relative);Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.Copy(dialog.FileName,destination);item.Update(relative);replacements[item.Path]=relative;changed=true;
  }
  if(changed)FrameTrailProjectStore.Save(file,p);
 }
 private async Task LoadAuxiliaryDurations(FrameTrailProject p,string file)
 {
  auxiliaryDurations.Clear();var paths=p.AudioTracks.Select(a=>a.MediaPath).Concat(p.Camera is {} camera?[camera.MediaPath]:Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
  foreach(var relative in paths)try{var info=await MediaTools.ProbeAsync(FrameTrailProjectStore.ResolveAssetPath(file,relative));auxiliaryDurations[relative]=info.Duration;}catch(Exception ex){App.Log(ex);}
 }
 private double AvailableAssetDuration(string path,double fallback)=>auxiliaryDurations.TryGetValue(path,out var duration)?duration:fallback;
 private static double CameraAssetEnd(CameraTrack camera)=>camera.SyncSegments is {Count:>0} parts?parts.Max(x=>x.SourceStart+x.Duration):camera.SourceStart+camera.Duration;
 private void ChangeCameraSourceStart(double value)=>ApplyEdit(p=>{if(p.Camera is not {} camera)return;if(camera.SyncSegments is {Count:>0}){CameraTimelineOperations.SlipAssetStart(camera,value,AvailableAssetDuration(camera.MediaPath,CameraAssetEnd(camera)));return;}var available=AvailableAssetDuration(camera.MediaPath,camera.SourceStart+camera.Duration);camera.SourceStart=Math.Clamp(value,0,Math.Max(0,available-.04));camera.Duration=Math.Min(camera.Duration,available-camera.SourceStart);});
}
