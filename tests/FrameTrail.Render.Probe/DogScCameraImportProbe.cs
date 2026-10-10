using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using FrameTrail;
using FrameTrail.App;
using FrameTrail.Media;

namespace FrameTrail.Render.Probe;

internal static class DogScCameraImportProbe
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "frametrail-dogsc-camera-import-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "synthetic-camera.dogscproject");
        var destination = Path.Combine(root, "Imported.frametrail");
        Directory.CreateDirectory(Path.Combine(source, "media"));
        try
        {
            var cameraPath = Path.Combine(source, "media", "camera.mp4");
            await MediaTools.RunAsync(MediaTools.Ffmpeg,
                ["-v", "error", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=30:duration=1.2", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-an", "-y", cameraPath]);
            var screenPath = Path.Combine(source, "media", "screen.mp4");
            File.Copy(cameraPath, screenPath);
            var sourceManifest = """
            {
              "version":27,
              "title":"Synthetic camera Original ratio probe",
              "media":{
                "screen":{"relativePath":"media/screen.mp4"},
                "camera":{"relativePath":"media/camera.mp4","sourceStartTime":0,"sourceEndTime":1.2,"sourceTimeScale":1.0}
              },
              "camera":{"shape":"original","position":{"x":0.8,"y":0.7},"size":0.3}
            }
            """;
            var manifestPath = Path.Combine(source, "project.json");
            await File.WriteAllTextAsync(manifestPath, sourceManifest);
            var packageHashBefore = HashTree(source);

            var screenInfo = await MediaTools.ProbeAsync(screenPath);
            var imported = DogScProjectImporter.Import(source, destination,
                new MediaAsset { Width = screenInfo.Width, Height = screenInfo.Height, Duration = screenInfo.Duration });
            Check(imported.Project.Camera?.AssetAspectRatio is null,
                "schema 导入阶段不从 DogSC JSON 虚构宽高字段推导摄像头比例");
            imported = await DogScImportMetadata.ApplyCameraAspectRatioAsync(imported);
            var reopened = FrameTrailProjectStore.Load(imported.ProjectFilePath);
            Check(reopened.Camera?.Layout == CameraMotionLayoutKind.Original
                && Math.Abs(reopened.Camera.AssetAspectRatio!.Value - 16d / 9) < 1e-9,
                "真实探测合成 160x90 摄像头素材后保存重开保留 Original 16:9 比例");
            Check(!imported.Report.Warnings.Any(w => w.Contains("无法读取摄像头素材宽高比", StringComparison.Ordinal)),
                "读取素材比例后导入报告清除暂时性 Original 比例提示");
            Check(HashTree(source) == packageHashBefore,
                "导入及探测没有改写合成 DogSC 包中的 project.json 或媒体字节");

            var lockedDestination = Path.Combine(root, "Locked.frametrail");
            var lockedImport = DogScProjectImporter.Import(source, lockedDestination,
                new MediaAsset { Width = screenInfo.Width, Height = screenInfo.Height, Duration = screenInfo.Duration });
            var saveFailurePropagated = false;
            using (new FileStream(lockedImport.ProjectFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { _ = await DogScImportMetadata.ApplyCameraAspectRatioAsync(lockedImport); }
                catch (IOException ex) when (ex.Message.Contains("无法替换项目文件", StringComparison.Ordinal))
                { saveFailurePropagated = true; }
            }
            Check(saveFailurePropagated, "目标项目被独占锁定时，比例保存错误作为IO异常传播而非伪装成探测警告");
            Check(FrameTrailProjectStore.Load(lockedImport.ProjectFilePath).Camera?.AssetAspectRatio is null
                && lockedImport.Project.Camera?.AssetAspectRatio is null,
                "保存失败后磁盘项目与调用方对象均未留下未持久化的摄像头比例");
            Console.WriteLine("通过：DogSC 摄像头 Original 比例集成验证（合成视频，不代表真实 Mac 工程）。");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static string HashTree(string directory)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).OrderBy(p => Path.GetRelativePath(directory, p), StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(relative));
            hash.AppendData([0]);
            hash.AppendData(SHA256.HashData(File.ReadAllBytes(file)));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("Probe failed: " + label);
        Console.WriteLine("通过：" + label);
    }
}
