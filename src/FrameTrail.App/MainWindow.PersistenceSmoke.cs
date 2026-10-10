using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using FrameTrail.Media;

namespace FrameTrail.App;

public partial class MainWindow
{
    private async Task VerifyPersistenceFailureRecovery(string report)
    {
        var checks = new List<string>();
        var output = Path.GetFullPath(report);
        EnsureArtifactsRoot(Path.GetDirectoryName(output)!);
        var directory = Path.Combine(Path.GetDirectoryName(output)!, "persistence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        void Check(bool passed, string name)
        {
            if (!passed) throw new InvalidOperationException(name);
            checks.Add(name);
        }
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        bool FailsWhileLocked(string path, Action action)
        {
            using var fileLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try { action(); return false; }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }
        try
        {
            if (project is null || projectFile is null) throw new InvalidOperationException("请先打开测试项目。");
            var sourceMedia = FrameTrailProjectStore.ResolveMediaPath(projectFile, project);
            var mediaHash = Hash(sourceMedia);
            var media = Path.Combine(directory, "source" + Path.GetExtension(sourceMedia));
            File.Copy(sourceMedia, media);
            var copy = MakeProject(Path.GetFileName(media), await MediaTools.ProbeAsync(media));
            copy.Settings.Padding = .055;
            var file = Path.Combine(directory, "project.frametrail");
            FrameTrailProjectStore.Save(file, copy);
            await OpenProject(file);
            var initialHash = Hash(file);
            Check(FailsWhileLocked(file, () => Edit(p => p.Settings.Padding = .09)), "文件占用时自动保存报告失败");
            Check(project!.Settings.Padding == .055 && Hash(file) == initialHash && !history!.CanUndo && !history.CanRedo,
                "自动保存失败保持内存、磁盘和历史一致");
            Check(!Directory.EnumerateFiles(directory, "*.tmp-*").Any(), "失败自动保存清理临时文件");
            Edit(p => p.Settings.Padding = .09);
            Check(FrameTrailProjectStore.Load(file).Settings.Padding == .09 && history!.CanUndo, "解除占用后编辑自动保存成功");
            var editedHash = Hash(file);
            Check(FailsWhileLocked(file, () => ApplyHistoryChange(false)), "文件占用时撤销保存报告失败");
            Check(project!.Settings.Padding == .09 && Hash(file) == editedHash && history!.CanUndo && !history.CanRedo,
                "撤销保存失败保持内存、磁盘和历史一致");
            ApplyHistoryChange(false);
            Check(project!.Settings.Padding == .055 && FrameTrailProjectStore.Load(file).Settings.Padding == .055 && history!.CanRedo,
                "解除占用后撤销重试成功");
            var undoneHash = Hash(file);
            Check(FailsWhileLocked(file, () => ApplyHistoryChange(true)), "文件占用时重做保存报告失败");
            Check(project!.Settings.Padding == .055 && Hash(file) == undoneHash && !history!.CanUndo && history.CanRedo,
                "重做保存失败保持内存、磁盘和历史一致");
            ApplyHistoryChange(true);
            Check(FrameTrailProjectStore.Load(file).Settings.Padding == .09 && history!.CanUndo && !history.CanRedo,
                "解除占用后重做重试成功");
            await OpenProject(file);
            Check(project!.ProjectId == copy.ProjectId && project.Settings.Padding == .09 && Hash(media) == mediaHash && Hash(sourceMedia) == mediaHash,
                "保存重开保留工程身份、修改和原素材字节");
            Check(!Directory.EnumerateFiles(directory, "*.tmp-*").Any(), "所有失败与重试完成后无临时工程残留");
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { Passed = true, Checks = checks, Project = file }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error)
        {
            Environment.ExitCode = 1;
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { Passed = false, Checks = checks, Error = error.ToString(), Directory = directory }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
