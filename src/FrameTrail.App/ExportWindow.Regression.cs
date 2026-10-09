using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FrameTrail.Media;

namespace FrameTrail.App;

public sealed partial class ExportWindow
{
    // Diagnostic entry point: exercises the actual controls and media engine in an isolated output folder.
    internal async Task<IReadOnlyList<string>> VerifyPanelAsync(string outputFolder)
    {
        var checks = new List<string>();
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            checks.Add(description);
        }
        async Task WaitUntil(Func<bool> condition)
        {
            var timer = Stopwatch.StartNew();
            while (!condition())
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(90)) throw new TimeoutException("导出面板验证超时。");
                await Task.Delay(15);
            }
        }
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        outputDirectory = outputFolder;
        fileName.Text = audioOnly ? "panel-audio" : "panel-video";
        RefreshPreview();
        if (audioOnly)
        {
            Check(extensionBadge.Text == ".m4a" && previewFormat.Text == "M4A 音频" && resolutionChoices.All(b => !b.IsEnabled) && frameRateChoices.All(b => !b.IsEnabled),
                "音频专用入口直接显示M4A模式并禁用视频分辨率和帧率");
            Click(exportButton);
            await WaitUntil(() => !running);
            Check(completedPath is not null && File.Exists(completedPath), "音频面板实际生成文件");
            var info = await MediaTools.ProbeAsync(completedPath!);
            Check(info.HasAudio && info.Width == 0 && Math.Abs(info.Duration - project.Timeline.Duration) < .12,
                "M4A只有音频且时长与剪辑一致");
            Check(openFileButton.IsVisible && openFolderButton.IsVisible && !exportButton.IsVisible,
                "成功后展示打开文件与文件夹操作");
            return checks;
        }

        var blockedDirectory = Path.Combine(outputFolder, "blocked-output");
        await File.WriteAllTextAsync(blockedDirectory, "preserve-existing-file");
        outputDirectory = blockedDirectory;
        RefreshPreview();
        Click(exportButton);
        await WaitUntil(() => !running);
        Check(completedPath is null && stageText.Text == "导出失败，可修改设置后重试" && exportButton.IsEnabled,
            "输出目录不可用时显示失败并恢复重试操作");
        Check(await File.ReadAllTextAsync(blockedDirectory) == "preserve-existing-file", "失败不会改写已有目标文件");
        outputDirectory = outputFolder;
        RefreshPreview();
        Click(resolutionChoices[0]);
        Click(exportButton);
        await WaitUntil(() => !running || progressBar.Value > .05);
        Check(running && progressBar.Value > .05 && stageText.Text.Contains("编码"), "真实视频合成进度到达后可以取消");
        Check(!videoModeButton.IsEnabled && !audioModeButton.IsEnabled && !browseButton.IsEnabled && !fileName.IsEnabled,
            "导出进行中锁定内容类型和输出位置，摘要与当前任务一致");
        Click(cancelButton);
        await WaitUntil(() => !running);
        Check(completedPath is null && stageText.Text == "已取消导出" && exportButton.IsEnabled && cancelButton.IsEnabled,
            "取消完成后恢复导出按钮且不标记成功");
        Check(!File.Exists(Path.Combine(outputFolder, "panel-video.mp4")) && !Directory.EnumerateFiles(outputFolder, ".frametrail-*").Any(),
            "取消后没有成片或导出临时文件残留");
        Click(exportButton);
        await WaitUntil(() => !running);
        Check(completedPath is not null && File.Exists(completedPath), "取消后在同一面板重新导出成功");
        var video = await MediaTools.ProbeAsync(completedPath!);
        Check(video.Width > 0 && Math.Abs(video.Duration - project.Timeline.Duration) < .12,
            "重试成片可解码且时长正确");
        return checks;
    }
}

public partial class MainWindow
{
    private async Task VerifyExportPanel(string reportPath)
    {
        var checks = new List<string>();
        var output = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, "export-panel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        object result;
        try
        {
            if (project is null || projectFile is null) throw new InvalidOperationException("必须打开含音轨的项目。");
            StopPlayback();
            foreach (var audio in new[] { true, false })
            {
                var snapshot = Clone(project);
                var file = projectFile;
                var source = FrameTrailProjectStore.ResolveMediaPath(file, snapshot);
                var window = new ExportWindow(snapshot, PreviewImage.Source, "导出面板回归", (path, options, progress, token) =>
                    Task.Run(() => new ExportEngine().ExportAsync(snapshot, source, path, options with { ProjectFilePath = file }, progress, token)), initialAudioOnly: audio) { Owner = this };
                try { window.Show(); checks.AddRange(await window.VerifyPanelAsync(output)); }
                finally { window.Close(); }
            }
            result = new { Passed = true, Checks = checks, Output = output };
        }
        catch (Exception ex) { App.Log(ex); result = new { Passed = false, Checks = checks, Output = output, Error = ex.ToString() }; }
        await File.WriteAllTextAsync(reportPath, System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
}
