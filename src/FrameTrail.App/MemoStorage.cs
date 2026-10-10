using System.IO;
using System.Text;
using System.Text.Json;

namespace FrameTrail.App;

internal static class MemoStorage
{
    internal static string SettingsPath(string textPath) => textPath + ".settings.json";

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
