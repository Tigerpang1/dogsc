using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace FrameTrail.Media;

public sealed record MediaInfo(int Width, int Height, double Duration, double FrameRate, bool HasAudio);
public static class MediaTools
{
    public static string Ffmpeg => Locate("ffmpeg.exe");
    public static string Ffprobe => Locate("ffprobe.exe");
    public static string Number(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);
    private static string Locate(string name)
    {
        var custom = Environment.GetEnvironmentVariable("FRAMETRAIL_FFMPEG_DIR");
        var roots = new List<string> { custom ?? "", Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg"), AppContext.BaseDirectory };
        roots.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(winget))
            foreach (var d in Directory.GetDirectories(winget, "Gyan.FFmpeg_*")) roots.AddRange(Directory.GetDirectories(d).Select(x => Path.Combine(x, "bin")));
        foreach (var root in roots.Where(x => !string.IsNullOrWhiteSpace(x))) { var candidate = Path.Combine(root, name); if (File.Exists(candidate)) return candidate; }
        throw new FileNotFoundException($"缺少 {name}。请将 FFmpeg 放到应用 tools/ffmpeg 目录，或设置 FRAMETRAIL_FFMPEG_DIR。");
    }
    public static Process Start(string executable, IEnumerable<string> arguments, bool input = false, bool output = false)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = input, RedirectStandardOutput = output, RedirectStandardError = true };
        foreach (var a in arguments) info.ArgumentList.Add(a);
        return Process.Start(info) ?? throw new IOException($"无法启动 {executable}");
    }
    public static void Kill(Process p) { try { if (!p.HasExited) p.Kill(true); } catch (InvalidOperationException) { } }
    public static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellation = default)
    {
        using var p = Start(executable, arguments, output: true);
        using var registration = cancellation.Register(() => Kill(p));
        var error = p.StandardError.ReadToEndAsync(); var output = p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync(cancellation); cancellation.ThrowIfCancellationRequested();
        var stderr = await error; var stdout = await output;
        if (p.ExitCode != 0) throw new IOException($"媒体处理失败 ({p.ExitCode})：{Tail(stderr)}");
        return stdout;
    }
    public static string Tail(string s) => s.Length <= 6000 ? s : s[^6000..];
    public static async Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellation = default)
    {
        var json = await RunAsync(Ffprobe, ["-v", "error", "-show_streams", "-show_format", "-of", "json", path], cancellation);
        using var doc = JsonDocument.Parse(json);
        var streams = doc.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(x => x.GetProperty("codec_type").GetString() == "video");
        var duration = doc.RootElement.GetProperty("format").TryGetProperty("duration", out var dur) ? double.Parse(dur.GetString()!, CultureInfo.InvariantCulture) : 0;
        var rate = video.ValueKind != JsonValueKind.Undefined && video.TryGetProperty("avg_frame_rate", out var fr) ? fr.GetString()!.Split('/') : ["0", "1"];
        var denominator = double.Parse(rate[1], CultureInfo.InvariantCulture);
        return new(video.ValueKind == JsonValueKind.Undefined ? 0 : video.GetProperty("width").GetInt32(), video.ValueKind == JsonValueKind.Undefined ? 0 : video.GetProperty("height").GetInt32(), duration, denominator == 0 ? 0 : double.Parse(rate[0], CultureInfo.InvariantCulture) / denominator, streams.Any(x => x.GetProperty("codec_type").GetString() == "audio"));
    }
    public static string VideoFilter(ProjectTimeline timeline, int width, int height, int fps)
    {
        timeline.Validate();
        var filters = new List<string>(); var n = timeline.Segments.Count;
        if (n > 1) filters.Add($"[0:v]split={n}" + string.Concat(Enumerable.Range(0, n).Select(i => $"[s{i}]")));
        for (var i = 0; i < n; i++)
        {
            var s = timeline.Segments[i];
            filters.Add($"[{(n == 1 ? "0:v" : $"s{i}")}]trim=start={Number(s.SourceStart)}:end={Number(s.SourceEnd)},setpts=(PTS-STARTPTS)/{Number(s.PlaybackRate)}[v{i}]");
        }
        filters.Add(string.Concat(Enumerable.Range(0,n).Select(i => $"[v{i}]")) + (n > 1 ? $"concat=n={n}:v=1:a=0," : "") + $"fps={fps},scale={width}:{height}:flags=bilinear,format=bgra[out]");
        return string.Join(';', filters);
    }
    public static string AudioFilter(ProjectTimeline timeline)
    {
        timeline.Validate(); var filters = new List<string>(); var n = timeline.Segments.Count;
        if (n > 1) filters.Add($"[0:a]asplit={n}" + string.Concat(Enumerable.Range(0,n).Select(i => $"[s{i}]")));
        for (var i=0;i<n;i++)
        {
            var s = timeline.Segments[i]; var rate = s.PlaybackRate; var tempo = new List<string>();
            while (rate > 2) { tempo.Add("atempo=2"); rate /= 2; }
            while (rate < .5) { tempo.Add("atempo=0.5"); rate *= 2; }
            tempo.Add("atempo=" + Number(rate));
            filters.Add($"[{(n == 1 ? "0:a" : $"s{i}")}]atrim=start={Number(s.SourceStart)}:end={Number(s.SourceEnd)},asetpts=PTS-STARTPTS,{string.Join(',',tempo)},apad,atrim=duration={Number(s.OutputDuration)}[a{i}]");
        }
        filters.Add(string.Concat(Enumerable.Range(0,n).Select(i => $"[a{i}]")) + (n > 1 ? $"concat=n={n}:v=0:a=1" : "anull") + "[out]");
        return string.Join(';', filters);
    }
    public static async Task<byte[]> DecodeFrameAsync(string path, double sourceTime, int width, int height, CancellationToken cancellation)
    {
        using var p = Start(Ffmpeg, ["-v", "error", "-ss", Number(sourceTime), "-i", path, "-an", "-frames:v", "1", "-vf", $"scale={width}:{height}", "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1"], output: true);
        using var reg = cancellation.Register(() => Kill(p)); var errors = p.StandardError.ReadToEndAsync();
        var buffer = new byte[checked(width * height * 4)];
        try { await p.StandardOutput.BaseStream.ReadExactlyAsync(buffer, cancellation); await p.WaitForExitAsync(cancellation); if (p.ExitCode != 0) throw new IOException(await errors); }
        finally { Kill(p); }
        return buffer;
    }
}
