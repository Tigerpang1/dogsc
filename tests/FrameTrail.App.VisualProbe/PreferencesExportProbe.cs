using System.Reflection;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using FrameTrail;
using FrameTrail.App;
using FrameTrail.Media;

namespace FrameTrail.App.VisualProbe;

internal static class PreferencesExportProbe
{
    public static void Verify(MainWindow window, string output, Action<bool, string, object?> check)
    {
        var preferencesFile = (string)(typeof(MainWindow).GetProperty("PreferencesFile", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null) ?? throw new InvalidOperationException("无法定位偏好文件路径。"));
        var preferencesExisted = File.Exists(preferencesFile);
        var preferencesBefore = preferencesExisted ? SHA256.HashData(File.ReadAllBytes(preferencesFile)) : null;
        check(window.IsVisible == false && new WindowInteropHelper(window).Handle == IntPtr.Zero,
            "创建偏好测试窗口后仍未显示且未创建 HWND", null);

        var project = new FrameTrailProject
        {
            MediaPath = "generated-testsrc2.mp4",
            Media = new() { Width = 320, Height = 180, Duration = 1, FrameRate = 30 },
            Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 1 }] },
            Settings = new() { CanvasWidth = 320, CanvasHeight = 180, Padding = 0, CornerRadius = 0, Shadow = 0 }
        };
        var projectBefore = JsonSerializer.Serialize(project);
        var source = Path.Combine(output, project.MediaPath);
        MediaTools.RunAsync(MediaTools.Ffmpeg,
        ["-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30:duration=1", "-frames:v", "30", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-y", source]).GetAwaiter().GetResult();

        var apply = typeof(MainWindow).GetMethod("ApplyUserPreferences", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MainWindow).FullName, "ApplyUserPreferences");
        var preferenceType = typeof(MainWindow).GetNestedType("UserPreferences", BindingFlags.NonPublic)
            ?? throw new MissingMemberException(typeof(MainWindow).FullName, "UserPreferences");
        var lowLight = CreatePreferences(preferenceType, 960, false);
        apply.Invoke(window, [lowLight]);
        var lowSurface = GetSurface(window);
        var lowResolution = GetField<int>(window, "previewResolution");
        var lowFrameRate = GetField<int>(window, "previewFrameRate");
        check(lowResolution == 960 && lowFrameRate == 30 && GetField<bool>(window, "darkTheme") == false,
            "真实偏好应用路径设置浅色与低预览分辨率", new { lowResolution, lowFrameRate, Dark = GetField<bool>(window, "darkTheme") });

        var lowVideo = Path.Combine(output, "light-low.mp4");
        var lowResult = new ExportEngine().ExportAsync(project, source, lowVideo, new(320, 180, 30, false))
            .GetAwaiter().GetResult();
        var lowFrames = GetFrameHashes(lowVideo);
        File.WriteAllText(Path.Combine(output, "light-low.framemd5"), lowFrames.Text);

        var highDark = CreatePreferences(preferenceType, 1920, true);
        apply.Invoke(window, [highDark]);
        var highSurface = GetSurface(window);
        var highResolution = GetField<int>(window, "previewResolution");
        var highFrameRate = GetField<int>(window, "previewFrameRate");
        check(highResolution == 1920 && highFrameRate == 60 && GetField<bool>(window, "darkTheme"),
            "真实偏好应用路径设置深色与高预览分辨率", new { highResolution, highFrameRate, Dark = GetField<bool>(window, "darkTheme") });
        check(lowSurface != highSurface, "Light/Dark 偏好确实切换 App 的 Surface 资源", new { Light = lowSurface, Dark = highSurface });

        var highVideo = Path.Combine(output, "dark-high.mp4");
        var highResult = new ExportEngine().ExportAsync(project, source, highVideo, new(320, 180, 30, false))
            .GetAwaiter().GetResult();
        var highFrames = GetFrameHashes(highVideo);
        File.WriteAllText(Path.Combine(output, "dark-high.framemd5"), highFrames.Text);
        check(lowFrames.Hashes.Count == 30 && highFrames.Hashes.Count == 30,
            "两种偏好下均完成 30 帧实际 MP4 导出并解码", new { Low = lowFrames.Hashes.Count, High = highFrames.Hashes.Count });
        check(lowFrames.Hashes.SequenceEqual(highFrames.Hashes),
            "Light/Low 与 Dark/High 导出的每帧解码像素哈希一致",
            new { LowExport = lowResult.Encoder, HighExport = highResult.Encoder, FrameCount = lowFrames.Hashes.Count, Hashes = lowFrames.Hashes });
        check(JsonSerializer.Serialize(project) == projectBefore,
            "偏好应用与两次导出均未改写工程设置", null);
        check(File.Exists(preferencesFile) == preferencesExisted &&
              (!preferencesExisted || CryptographicOperations.FixedTimeEquals(preferencesBefore!, SHA256.HashData(File.ReadAllBytes(preferencesFile)))),
            "偏好应用与导出未写用户 settings.json", new { Exists = preferencesExisted, Path = preferencesFile });
        check(window.IsVisible == false && new WindowInteropHelper(window).Handle == IntPtr.Zero,
            "探针完成后窗口仍未显示且没有 HWND", null);
    }

    private static object CreatePreferences(Type type, int previewResolution, bool darkTheme)
    {
        var settings = Activator.CreateInstance(type)!;
        type.GetProperty("PreviewResolution")!.SetValue(settings, previewResolution);
        type.GetProperty("PreviewFrameRate")!.SetValue(settings, darkTheme ? 60 : 30);
        type.GetProperty("DarkTheme")!.SetValue(settings, darkTheme);
        return settings;
    }

    private static Color GetSurface(MainWindow window) => ((SolidColorBrush)window.FindResource("Surface")).Color;

    private static T GetField<T>(MainWindow window, string name) =>
        (T)(typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)
            ?? throw new InvalidOperationException($"字段 {name} 没有值。"));

    private static (string Text, List<string> Hashes) GetFrameHashes(string video)
    {
        var text = MediaTools.RunAsync(MediaTools.Ffmpeg,
            ["-v", "error", "-i", video, "-map", "0:v:0", "-f", "framemd5", "-"]).GetAwaiter().GetResult();
        var hashes = text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith('#'))
            .Select(line => line.Split(',').Last().Trim())
            .ToList();
        if (hashes.Count == 0 || hashes.Any(hash => hash.Length != 32 || !hash.All(Uri.IsHexDigit)))
            throw new InvalidDataException("framemd5 未返回有效的视频帧哈希。");
        return (text, hashes);
    }
}
