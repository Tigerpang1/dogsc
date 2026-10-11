using System.IO;
using System.Text.Json;
using FrameTrail;
using FrameTrail.Media;

namespace FrameTrail.App;

/// <summary>Enriches an imported project with metadata that DogSC does not store in its manifest.</summary>
public static class DogScImportMetadata
{
    public static async Task<DogScProjectImportResult> ApplyCameraAspectRatioAsync(
        DogScProjectImportResult imported,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imported);
        var camera = imported.Project.Camera;
        if (camera is null || string.IsNullOrWhiteSpace(camera.MediaPath)) return imported;

        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(imported.ProjectFilePath))
            ?? throw new InvalidDataException("导入项目路径无效。");
        if (!Directory.Exists(projectDirectory) || (File.GetAttributes(projectDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("导入项目目录不存在或是重解析点，已停止读取摄像头素材。");
        var cameraPath = Path.GetFullPath(Path.Combine(projectDirectory,
            camera.MediaPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
        var prefix = Path.TrimEndingDirectorySeparator(projectDirectory) + Path.DirectorySeparatorChar;
        if (!cameraPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("导入后的摄像头素材路径越出项目目录。");
        for (var walk = cameraPath; walk.StartsWith(prefix, StringComparison.OrdinalIgnoreCase); walk = Path.GetDirectoryName(walk)!)
        {
            if (!File.Exists(walk) && !Directory.Exists(walk)) break;
            if ((File.GetAttributes(walk) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("导入后的摄像头素材路径经过链接或重解析点，已停止读取。");
        }

        if (!File.Exists(cameraPath))
            return await SaveWarningAsync(imported, projectDirectory, "导入后的摄像头素材文件缺失，Original 布局将使用默认画幅。").ConfigureAwait(false);

        MediaInfo info;
        try
        {
            info = await MediaTools.ProbeAsync(cameraPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.ComponentModel.Win32Exception
            or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return await SaveWarningAsync(imported, projectDirectory,
                "无法读取导入后摄像头素材的真实尺寸，Original 布局将使用默认画幅。").ConfigureAwait(false);
        }

        var ratio = info.Height > 0 ? (double)info.Width / info.Height : double.NaN;
        if (info.Width <= 0 || !double.IsFinite(ratio) || ratio is <= 0 or > 20)
            return await SaveWarningAsync(imported, projectDirectory,
                "导入后的摄像头视频没有有效尺寸，Original 布局将使用默认画幅。").ConfigureAwait(false);

        // IO failures here are project-save failures and must reach the caller instead of becoming a media warning.
        var updatedCamera = camera with { AssetAspectRatio = ratio };
        imported.Project.Camera = updatedCamera;
        try { FrameTrailProjectStore.Save(imported.ProjectFilePath, imported.Project); }
        catch { imported.Project.Camera = camera; throw; }
        var remainingWarnings = imported.Report.Warnings
            .Where(value => !string.Equals(value, DogScProjectImporter.MissingCameraAspectWarning, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (remainingWarnings.Length == imported.Report.Warnings.Count) return imported;
        var cleanedReport = imported.Report with { Warnings = remainingWarnings };
        File.WriteAllText(Path.Combine(projectDirectory, "dogsc-import-report.json"),
            JsonSerializer.Serialize(cleanedReport, new JsonSerializerOptions { WriteIndented = true }));
        return imported with { Report = cleanedReport };
    }

    private static Task<DogScProjectImportResult> SaveWarningAsync(DogScProjectImportResult imported, string projectDirectory, string warning)
    {
        var warnings = imported.Report.Warnings
            .Where(value => !string.Equals(value, DogScProjectImporter.MissingCameraAspectWarning, StringComparison.Ordinal))
            .ToList();
        warnings.Add(warning);
        var report = imported.Report with { Warnings = warnings.Distinct(StringComparer.Ordinal).ToArray() };
        var reportPath = Path.Combine(projectDirectory, "dogsc-import-report.json");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return Task.FromResult(imported with { Report = report });
    }
}
