using System.IO;
using System.Text;
using System.Text.Json;

namespace FrameTrail.App;

internal static class MemoStorage
{
    internal static string SettingsPath(string textPath) => textPath + ".settings.json";

    // The first six values are the legacy layout: font, background opacity, width, height, left, top.
    // New readers accept both that layout and the appended mode/speed values.
    internal static double[] CreateSettings(double fontSize, double backgroundOpacity, double width, double height,
        double left, double top, bool isEditing, double scrollSpeed) =>
        [FiniteOr(fontSize, 18), FiniteOr(backgroundOpacity, .92), FiniteOr(width, 410), FiniteOr(height, 400),
            FiniteOr(left, 0), FiniteOr(top, 0), isEditing ? 1 : 0, FiniteOr(scrollSpeed, 45)];

    private static double FiniteOr(double value, double fallback) => double.IsFinite(value) ? value : fallback;

    internal static (bool IsEditing, double ScrollSpeed) ReadTeleprompterSettings(double[]? values, bool defaultEditing, double defaultSpeed)
    {
        if (values is not { Length: >= 8 }) return (defaultEditing, defaultSpeed);
        var editing = double.IsFinite(values[6]) ? values[6] >= .5 : defaultEditing;
        var speed = double.IsFinite(values[7]) ? Math.Clamp(values[7], 8, 180) : defaultSpeed;
        return (editing, speed);
    }

    internal static void SaveText(string textPath, string text) => WriteAtomically(textPath, text);

    internal static void SaveSnapshot(string textPath, string text, double[] preferences)
    {
        WriteAtomically(textPath, text);
        WriteAtomically(SettingsPath(textPath), JsonSerializer.Serialize(preferences));
    }

    private static void WriteAtomically(string targetPath, string contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(contents);
        var fullTargetPath = Path.GetFullPath(targetPath);
        var directory = Path.GetDirectoryName(fullTargetPath)
            ?? throw new IOException("备忘录文件路径没有父目录。");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory,
            $".{Path.GetFileName(fullTargetPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(fullTargetPath)) File.Replace(temporaryPath, fullTargetPath, destinationBackupFileName: null);
            else File.Move(temporaryPath, fullTargetPath);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
