using System.Diagnostics;
using System.IO;
using System.Text.Json;
using FrameTrail.App;
using FrameTrail.Media;

internal static class TimelineAssetsProbe
{
    internal static async Task Run(string? output)
    {
        var root = Path.GetFullPath(output ?? Path.Combine("artifacts", "validation", "timeline-assets-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")));
        Directory.CreateDirectory(root);
        var video = Path.Combine(root, "red-then-blue.mp4");
        var silent = Path.Combine(root, "no-audio.mp4");
        var checks = new List<string>();
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            checks.Add(description);
        }
        await MediaTools.RunAsync(MediaTools.Ffmpeg,
            ["-v", "error", "-f", "lavfi", "-i", "color=red:s=320x180:r=2:d=4",
             "-f", "lavfi", "-i", "color=blue:s=320x180:r=2:d=4",
             "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000:duration=8",
             "-filter_complex", "[0:v][1:v]concat=n=2:v=1:a=0[v]", "-map", "[v]", "-map", "2:a",
             "-c:v", "libx264", "-preset", "ultrafast", "-g", "4", "-c:a", "aac", "-y", video]);
        var clock = Stopwatch.StartNew();
        var data = await TimelineAssets.BuildAsync(video, Path.Combine(root, "cache"), CancellationToken.None);
        Check(data.Thumbnails.Count == 8 && Math.Abs(data.Duration - 8) < .1, "8秒素材生成8张缩略图，时间范围正确");
        foreach (var frame in data.Thumbnails)
        {
            var bytes = new byte[frame.Image.PixelWidth * frame.Image.PixelHeight * 4];
            frame.Image.CopyPixels(bytes, frame.Image.PixelWidth * 4, 0);
            var center = ((frame.Image.PixelHeight / 2) * frame.Image.PixelWidth + frame.Image.PixelWidth / 2) * 4;
            var red = bytes[center + 2]; var blue = bytes[center];
            Check(frame.SourceTime < 4 ? red > 220 && blue < 25 : blue > 220 && red < 25,
                $"{frame.SourceTime:0.000}秒缩略图与源视频红/蓝区间一致");
            Check(frame.Image.IsFrozen && frame.Image.PixelWidth <= 160 && frame.Image.PixelHeight <= 160,
                $"{frame.SourceTime:0.000}秒缩略图可跨线程读取且像素尺寸受限");
        }
        Check(data.Peaks.Count is > 0 and <= 1000 && data.Peaks.Max() > .05,
            "真实音轨产生有界且非零的波形");
        var firstBuildSeconds = clock.Elapsed.TotalSeconds;
        await MediaTools.RunAsync(MediaTools.Ffmpeg, ["-v", "error", "-i", video, "-an", "-c:v", "copy", "-y", silent]);
        var noAudio = await TimelineAssets.BuildAsync(silent, Path.Combine(root, "cache"), CancellationToken.None);
        Check(noAudio.Thumbnails.Count == 8 && noAudio.Peaks.Count == 0, "无音轨素材保留缩略图且不生成假波形");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var cancelled = false; clock.Restart();
        try { await TimelineAssets.BuildAsync(video, Path.Combine(root, "cache"), cancel.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && clock.Elapsed < TimeSpan.FromSeconds(3), "生成中的取消在3秒内结束");
        var report = new { Passed = true, Checks = checks, FirstBuildSeconds = firstBuildSeconds, CancellationSeconds = clock.Elapsed.TotalSeconds };
        await File.WriteAllTextAsync(Path.Combine(root, "result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Timeline asset tests passed: {checks.Count}; report={Path.Combine(root, "result.json")}");
    }
}
