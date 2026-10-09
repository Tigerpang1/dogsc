using System.Diagnostics;
using System.Text.Json;
using FrameTrail;
using FrameTrail.Media;

var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
var parsed = ParseArguments(args);
var repoRoot = FindRepositoryRoot();
if (parsed.SyncOnly) return await RunSyncFixtureAsync(repoRoot, parsed.OutputPath);
if (parsed.FfmpegProbeOnly)
{
    try
    {
        Console.WriteLine($"FFmpeg={MediaTools.Ffmpeg}");
        Console.WriteLine($"FFprobe={MediaTools.Ffprobe}");
        var version = await MediaTools.RunAsync(MediaTools.Ffmpeg, ["-version"]);
        Console.WriteLine(version.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).FirstOrDefault());
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
}
var mode = parsed.RealMediaPath is null ? "synthetic-testsrc2" : "real-recording";
var outputRoot = parsed.OutputPath is null
    ? Path.Combine(repoRoot, "artifacts", "validation", $"{mode}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}")
    : Path.GetFullPath(parsed.OutputPath);
Directory.CreateDirectory(outputRoot);

var result = new ValidationResult
{
    InputKind = mode,
    CreatedUtc = DateTimeOffset.UtcNow,
    OutputDirectory = outputRoot,
    FfmpegPath = MediaTools.Ffmpeg,
    FfprobePath = MediaTools.Ffprobe
};
var resultPath = Path.Combine(outputRoot, "result.json");

try
{
    var originalMedia = parsed.RealMediaPath is null
        ? await CreateSyntheticMediaAsync(outputRoot)
        : Path.GetFullPath(parsed.RealMediaPath);
    if (!File.Exists(originalMedia)) throw new FileNotFoundException("输入媒体不存在。", originalMedia);

    result.InputMediaPath = originalMedia;
    var copiedMediaName = parsed.RealMediaPath is null ? "synthetic-source.mp4" : "real-source.mp4";
    var projectMedia = Path.Combine(outputRoot, copiedMediaName);
    if (!Path.GetFullPath(originalMedia).Equals(Path.GetFullPath(projectMedia), StringComparison.OrdinalIgnoreCase))
        File.Copy(originalMedia, projectMedia, true);
    result.CopiedProjectMediaPath = projectMedia;

    var sourceInfo = await MediaTools.ProbeAsync(projectMedia);
    if (sourceInfo.Width <= 0 || sourceInfo.Height <= 0 || sourceInfo.Duration < 1)
        throw new InvalidDataException($"输入媒体元数据不完整：{sourceInfo}");
    result.SourceProbe = ToProbe(sourceInfo);
    var outputWidth = 320;
    var outputHeight = Math.Max(120, (int)Math.Round(outputWidth * (double)sourceInfo.Height / sourceInfo.Width / 2) * 2);
    outputHeight = Math.Min(outputHeight, 720);

    var project = new FrameTrailProject
    {
        MediaPath = copiedMediaName,
        Media = new() { Width = sourceInfo.Width, Height = sourceInfo.Height, Duration = sourceInfo.Duration },
        Timeline = new() { Segments = [new() { SourceDuration = sourceInfo.Duration }] },
        Settings = new()
        {
            CanvasWidth = outputWidth,
            CanvasHeight = outputHeight,
            Background = "#23313D",
            Padding = .06,
            CornerRadius = .025,
            Shadow = .2,
            CursorVisible = true,
            AutoZoom = true
        }
    };

    string? rawInputEventsPath = null;
    if (parsed.RealMediaPath is null)
    {
        // This synthetic interaction track is deliberately separate from the generated media.
        project.PointerEvents =
        [
            new(Math.Min(1.0, sourceInfo.Duration * .2), new(.24, .31), PointerEventKind.LeftClick),
            new(Math.Min(1.2, sourceInfo.Duration * .24), new(.27, .34), PointerEventKind.Move),
            new(Math.Min(4.0, sourceInfo.Duration * .72), new(.77, .62), PointerEventKind.RightClick)
        ];
        project.ZoomClips = AutoZoomPlanner.Plan(project.PointerEvents);
        result.PointerOrigin = "synthetic fixed clicks, created for this integration fixture";
    }
    else
    {
        rawInputEventsPath = parsed.PointerEventsPath is null
            ? FindPointerSidecar(parsed.RealMediaPath)
            : Path.GetFullPath(parsed.PointerEventsPath);
        if (rawInputEventsPath is not null && File.Exists(rawInputEventsPath))
        {
            var raw = File.ReadAllText(rawInputEventsPath);
            var copiedSidecar = Path.Combine(outputRoot, "real-source.input.jsonl");
            File.WriteAllText(copiedSidecar, raw);
            project.PointerEvents = ParseInputJsonLines(raw, sourceInfo.Duration);
            project.ZoomClips = AutoZoomPlanner.Plan(project.PointerEvents);
            result.PointerEventsPath = copiedSidecar;
            result.PointerOrigin = "adjacent capture JSONL; relative to the first StopwatchTicks record because the sidecar has no recording-start tick";
        }
        else
        {
            project.PointerEvents = [];
            project.ZoomClips = [];
            result.PointerOrigin = "no pointer sidecar supplied or found";
        }
    }
    result.PointerEventCount = project.PointerEvents.Count;
    result.AutomaticZoomClipCount = project.ZoomClips.Count(x => x.Automatic);

    var midpoint = sourceInfo.Duration / 2;
    var originalSegmentId = project.Timeline.Segments.Single().Id;
    var (left, right) = project.Timeline.Split(originalSegmentId, midpoint);
    var leftStart = Math.Min(sourceInfo.Duration * .1, midpoint - .01);
    project.Timeline.Trim(left.Id, leftStart, midpoint);
    project.Timeline.SetPlaybackRate(right.Id, 2);
    project.Timeline.Reorder(right.Id, 0);
    project.Timeline.Validate();
    result.TimelineOperations = ["split", "trim", "set playback rate 2x", "reorder"];
    result.TimelineSegmentCount = project.Timeline.Segments.Count;
    result.OutputTimelineDuration = project.Timeline.Duration;

    var projectFile = Path.Combine(outputRoot, "project.frametrail");
    FrameTrailProjectStore.Save(projectFile, project);
    var loaded = FrameTrailProjectStore.Load(projectFile);
    var resolvedMedia = FrameTrailProjectStore.ResolveMediaPath(projectFile, loaded);
    if (!File.Exists(resolvedMedia)) throw new FileNotFoundException("重新载入项目后找不到相對素材。", resolvedMedia);
    if (loaded.PointerEvents.Count != project.PointerEvents.Count || loaded.ZoomClips.Count != project.ZoomClips.Count)
        throw new InvalidDataException("项目重新载入后指针或自动缩放轨道数量发生变化。");
    result.ProjectPath = projectFile;
    result.ProjectReloadSucceeded = true;
    result.ResolvedProjectMediaPath = resolvedMedia;

    var engine = new ExportEngine();
    var exportOptions = new ExportOptions(outputWidth, outputHeight, 30, HardwarePreferred: false);
    var cancelledPath = Path.Combine(outputRoot, "cancelled-export.mp4");
    CancelOnFirstRenderedFrameProgress cancellationProgress;
    using (var cancellation = new CancellationTokenSource())
    {
        cancellationProgress = new CancelOnFirstRenderedFrameProgress(cancellation);
        try
        {
            await engine.ExportAsync(loaded, resolvedMedia, cancelledPath, exportOptions, cancellationProgress, cancellation.Token);
            throw new InvalidOperationException("取消验收失败：导出在取消请求前已完成。");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            result.CancellationObserved = true;
        }
    }
    var temporaryFiles = Directory.EnumerateFiles(outputRoot)
        .Select(Path.GetFileName)
        .Where(name => name is not null && name.StartsWith(".frametrail-", StringComparison.OrdinalIgnoreCase))
        .ToArray();
    if (File.Exists(cancelledPath) || temporaryFiles.Length != 0)
        throw new IOException("取消后仍留有输出文件或导出临时文件：" + string.Join(", ", temporaryFiles));
    result.CancellationCleanupSucceeded = true;
    result.CancellationProgressStages = cancellationProgress.Stages;

    var mp4Path = Path.Combine(outputRoot, "export.mp4");
    var videoResult = await engine.ExportAsync(loaded, resolvedMedia, mp4Path, exportOptions);
    var videoProbe = await MediaTools.ProbeAsync(mp4Path);
    if (!videoProbe.HasAudio || videoProbe.Width <= 0 || videoProbe.Height <= 0 ||
        Math.Abs(videoProbe.Duration - loaded.Timeline.Duration) > .12)
        throw new InvalidDataException($"MP4 媒体核验失败：{videoProbe}");
    result.VideoExport = new ExportCheck(videoResult.Path, videoResult.Encoder, videoResult.Renderer,
        videoResult.FrameCount, ToProbe(videoProbe));

    var m4aPath = Path.Combine(outputRoot, "export.m4a");
    var audioResult = await engine.ExportAsync(loaded, resolvedMedia, m4aPath,
        exportOptions with { AudioOnly = true });
    var audioProbe = await MediaTools.ProbeAsync(m4aPath);
    if (!audioProbe.HasAudio || audioProbe.Width != 0 ||
        Math.Abs(audioProbe.Duration - loaded.Timeline.Duration) > .12)
        throw new InvalidDataException($"M4A 媒体核验失败：{audioProbe}");
    result.AudioExport = new ExportCheck(audioResult.Path, audioResult.Encoder, audioResult.Renderer,
        audioResult.FrameCount, ToProbe(audioProbe));

    result.SceneComparisons = await ComparePreviewAndExportAsync(loaded, resolvedMedia, projectFile, mp4Path, outputWidth, outputHeight);
    // At 320x180 the chroma-subsampled H.264 export can vary by about one dB on small cursor/text edges.
    // Keep a strict pixel-error bound alongside the compression-aware PSNR floor.
    if (result.SceneComparisons.Count < 3 || result.SceneComparisons.Any(x => x.PsnrDb < 23.5 || x.MeanAbsoluteError > 8))
        throw new InvalidDataException("预览与导出帧构图比较未达到压缩容差。" );

    var performanceProject = new FrameTrailProject
    {
        MediaPath = copiedMediaName,
        Media = project.Media,
        Timeline = new() { Segments = [new() { SourceDuration = Math.Min(6, sourceInfo.Duration) }] },
        Settings = new() { CanvasWidth = 1920, CanvasHeight = 1080, Background = "#000000", Padding = 0, CornerRadius = 0, Shadow = 0, CursorVisible = false, AutoZoom = false }
    };
    var performancePath = Path.Combine(outputRoot, "performance-1080p60.mp4");
    var performanceWatch = Stopwatch.StartNew();
    var performanceExport = await engine.ExportAsync(performanceProject, resolvedMedia, performancePath,
        new ExportOptions(1920, 1080, 60, HardwarePreferred: true));
    performanceWatch.Stop();
    var performanceProbe = await MediaTools.ProbeAsync(performancePath);
    result.Performance1080p60 = new PerformanceCheck(performanceWatch.Elapsed.TotalSeconds, performanceExport.Encoder,
        performanceExport.Renderer, performanceExport.FrameCount, ToProbe(performanceProbe));
    if (Math.Abs(performanceProbe.Duration - performanceProject.Timeline.Duration) > .12 || performanceExport.FrameCount != (int)Math.Ceiling(performanceProject.Timeline.Duration * 60))
        throw new InvalidDataException("1080p60 性能导出帧数或时长不符。" );

    var pngPath = Path.Combine(outputRoot, "export-frame-0001.png");
    await MediaTools.RunAsync(MediaTools.Ffmpeg,
        ["-v", "error", "-ss", "0.1", "-i", mp4Path, "-frames:v", "1", "-f", "image2", "-y", pngPath]);
    var pngHeader = new byte[8];
    await using (var png = File.OpenRead(pngPath)) await png.ReadExactlyAsync(pngHeader);
    if (!pngHeader.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) || new FileInfo(pngPath).Length < 128)
        throw new InvalidDataException("导出帧 PNG 未通过文件头核验。");
    result.PngFramePath = pngPath;
    result.Success = true;
}
catch (Exception ex)
{
    result.Success = false;
    result.Error = ex.ToString();
    await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result, jsonOptions));
    Console.Error.WriteLine(result.Error);
    Console.Error.WriteLine($"Validation JSON: {resultPath}");
    return 1;
}

await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result, jsonOptions));
Console.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
Console.WriteLine($"Validation JSON: {resultPath}");
return 0;

static async Task<string> CreateSyntheticMediaAsync(string outputRoot)
{
    var path = Path.Combine(outputRoot, "synthetic-source.mp4");
    await MediaTools.RunAsync(MediaTools.Ffmpeg,
    [
        "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30",
        "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000",
        "-t", "6", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
        "-c:a", "aac", "-b:a", "128k", "-shortest", "-movflags", "+faststart", "-y", path
    ]);
    return path;
}

static async Task<List<SceneComparison>> ComparePreviewAndExportAsync(FrameTrailProject project, string sourcePath, string projectFilePath, string exportPath, int width, int height)
{
    var evaluator = new PreparedFrameSceneEvaluator(project);
    var samples = new[] { 16d / 30, 22d / 30, 67d / 30, 76d / 30 };
    var output = new List<SceneComparison>();
    await using var preview = await PreviewPlaybackSession.CreateAsync(project, sourcePath, projectFilePath, width, height, 30);
    var expected = new byte[checked(width * height * 4)];
    foreach (var time in samples.Where(t => t < project.Timeline.Duration))
    {
        var scene = evaluator.Evaluate(time);
        var previewFrame = await preview.ReadFrameAsync(time, expected);
        var actual = await DecodeOutputFrameAsync(exportPath, time, width, height);
        double squared = 0, absolute = 0; long count = 0;
        for (var i = 0; i < expected.Length; i += 4)
        for (var c = 0; c < 3; c++) { var delta = expected[i + c] - actual[i + c]; squared += delta * delta; absolute += Math.Abs(delta); count++; }
        var mse = squared / count;
        if (Math.Abs(previewFrame.SourceTime - scene.SourceTime) > 1e-6) throw new InvalidDataException("预览与场景求值的源时间不同步。");
        output.Add(new(time, previewFrame.SourceTime, scene.Zoom, mse == 0 ? 99 : 10 * Math.Log10(255d * 255 / mse), absolute / count));
    }
    return output;
}

static async Task<byte[]> DecodeOutputFrameAsync(string path, double time, int width, int height)
{
    var frame = new byte[checked(width * height * 4)];
    using var process = MediaTools.Start(MediaTools.Ffmpeg,
        ["-v", "error", "-ss", MediaTools.Number(time), "-i", path, "-frames:v", "1", "-vf", $"scale={width}:{height}:flags=bilinear,format=bgra", "-f", "rawvideo", "pipe:1"], output: true);
    var errors = process.StandardError.ReadToEndAsync();
    await process.StandardOutput.BaseStream.ReadExactlyAsync(frame);
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new IOException("导出帧解码失败：" + await errors);
    return frame;
}

static async Task<int> RunSyncFixtureAsync(string repoRoot, string? requestedOutput)
{
    var output = requestedOutput is null
        ? Path.Combine(repoRoot, "artifacts", "validation", $"sync-1080p60-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}")
        : Path.GetFullPath(requestedOutput);
    Directory.CreateDirectory(output);
    var resultPath = Path.Combine(output, "sync-result.json");
    var result = new SyncValidationResult { OutputDirectory = output, FfmpegPath = MediaTools.Ffmpeg };
    try
    {
        var source = Path.Combine(output, "sync-source.mkv");
        var video = "color=c=black:s=320x180:r=60:d=6,drawbox=x=0:y=0:w=iw:h=ih:color=white:t=fill:enable='between(t,1,1.1)+between(t,3,3.1)+between(t,5,5.1)'";
        var audio = "aevalsrc=exprs='0.2*sin(2*PI*1000*t)*gt(between(t\\,1\\,1.1)+between(t\\,3\\,3.1)+between(t\\,5\\,5.1)\\,0)':s=48000:d=6";
        await MediaTools.RunAsync(MediaTools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i", video, "-f", "lavfi", "-i", audio, "-c:v", "ffv1", "-c:a", "pcm_s16le", "-shortest", "-y", source]);
        var project = new FrameTrailProject
        {
            MediaPath = Path.GetFileName(source), Media = new() { Width = 320, Height = 180, Duration = 6 },
            Timeline = new() { Segments = [
                new() { SourceStart = 2, SourceDuration = 2, PlaybackRate = 2 },
                new() { SourceStart = 0, SourceDuration = 2, PlaybackRate = 1 },
                new() { SourceStart = 4, SourceDuration = 2, PlaybackRate = 1 }
            ] },
            Settings = new() { CanvasWidth = 1920, CanvasHeight = 1080, Background = "#000000", Padding = 0, CornerRadius = 0, Shadow = 0, CursorVisible = false, AutoZoom = false }
        };
        var engine = new ExportEngine();
        var export = Path.Combine(output, "sync-export.mp4");
        var watch = Stopwatch.StartNew();
        var exportResult = await engine.ExportAsync(project, source, export, new ExportOptions(1920, 1080, 60, true));
        watch.Stop();
        result.ExportElapsedSeconds = watch.Elapsed.TotalSeconds;
        result.Encoder = exportResult.Encoder; result.Renderer = exportResult.Renderer; result.FrameCount = exportResult.FrameCount;
        var videoProcess = MediaTools.Start(MediaTools.Ffmpeg, ["-v", "error", "-i", export, "-vf", "scale=1:1:flags=area,format=gray", "-f", "rawvideo", "pipe:1"], output: true);
        var videoErrors = videoProcess.StandardError.ReadToEndAsync();
        using (var videoBytes = new MemoryStream()) { await videoProcess.StandardOutput.BaseStream.CopyToAsync(videoBytes); await videoProcess.WaitForExitAsync(); if(videoProcess.ExitCode != 0) throw new IOException(await videoErrors); result.VideoFlashTimes = DetectPulseStarts(videoBytes.ToArray().Select(x => (double)x).ToArray(), 30, 60); }
        var audioProcess = MediaTools.Start(MediaTools.Ffmpeg, ["-v", "error", "-i", export, "-vn", "-ac", "1", "-ar", "48000", "-f", "f32le", "pipe:1"], output: true);
        var audioErrors = audioProcess.StandardError.ReadToEndAsync(); byte[] audioBytes;
        using (var audioData = new MemoryStream()) { await audioProcess.StandardOutput.BaseStream.CopyToAsync(audioData); await audioProcess.WaitForExitAsync(); if(audioProcess.ExitCode != 0) throw new IOException(await audioErrors); audioBytes = audioData.ToArray(); }
        var samples = new float[audioBytes.Length / 4]; Buffer.BlockCopy(audioBytes, 0, samples, 0, audioBytes.Length);
        const int window = 480;
        var rms = Enumerable.Range(0, samples.Length / window).Select(i => Math.Sqrt(Enumerable.Range(i * window, window).Select(n => (double)samples[n] * samples[n]).Average())).ToArray();
        result.AudioPulseTimes = DetectPulseStarts(rms, .02, 100);
        if (result.VideoFlashTimes.Count != 3 || result.AudioPulseTimes.Count != 3) throw new InvalidDataException($"脉冲数量错误：video={result.VideoFlashTimes.Count}, audio={result.AudioPulseTimes.Count}");
        result.AVOffsetMilliseconds = result.VideoFlashTimes.Zip(result.AudioPulseTimes, (v,a) => (v-a)*1000).ToList();
        result.MaxAbsoluteOffsetMilliseconds = result.AVOffsetMilliseconds.Max(Math.Abs);
        if (result.MaxAbsoluteOffsetMilliseconds > 50) throw new InvalidDataException($"音画最大偏差 {result.MaxAbsoluteOffsetMilliseconds:0.0}ms 超过 50ms。");
        result.Success = true;
    }
    catch (Exception ex) { result.Error = ex.ToString(); }
    await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(await File.ReadAllTextAsync(resultPath));
    return result.Success ? 0 : 1;
}

static List<double> DetectPulseStarts(double[] values, double threshold, double rate)
{
    var starts = new List<double>(); var active = false; var gap = 0;
    for (var i = 0; i < values.Length; i++)
    {
        var now = values[i] >= threshold;
        if (now && !active) starts.Add(i / rate);
        if (now) { active = true; gap = 0; } else if (++gap >= 2) active = false;
    }
    return starts;
}

static List<PointerEvent> ParseInputJsonLines(string jsonLines, double duration)
{
    var parsed = new List<(long Ticks, PointerEventKind Kind, NormalizedPoint Location)>();
    foreach (var line in jsonLines.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var ticks = GetLong(root, "StopwatchTicks");
        var x = GetDouble(root, "NormalizedX");
        var y = GetDouble(root, "NormalizedY");
        var kind = GetEnumValue(root, "Kind");
        var mouseButton = GetEnumValue(root, "MouseButton");
        if (kind is not (0 or 1) || !double.IsFinite(x + y)) continue;
        var pointerKind = kind == 1 ? mouseButton == 2 ? PointerEventKind.RightClick : PointerEventKind.LeftClick : PointerEventKind.Move;
        parsed.Add((ticks, pointerKind, new NormalizedPoint(x, y).Clamp()));
    }
    if (parsed.Count == 0) return [];
    var origin = parsed.Min(x => x.Ticks);
    return parsed.Select(x => new PointerEvent((x.Ticks - origin) / (double)Stopwatch.Frequency, x.Location, x.Kind))
        .Where(x => x.Time <= duration)
        .OrderBy(x => x.Time)
        .ToList();
}

static string? FindPointerSidecar(string mediaPath)
{
    var fullPath = Path.GetFullPath(mediaPath);
    var directory = Path.GetDirectoryName(fullPath)!;
    var stem = Path.GetFileNameWithoutExtension(fullPath);
    var candidates = new List<string>
    {
        Path.ChangeExtension(fullPath, ".input.jsonl"),
        Path.Combine(directory, stem + ".input.jsonl")
    };
    if (stem.EndsWith("-loopback", StringComparison.OrdinalIgnoreCase))
        candidates.Add(Path.Combine(directory, stem[..^"-loopback".Length] + ".input.jsonl"));
    return candidates.FirstOrDefault(File.Exists);
}

static long GetLong(JsonElement root, string property) => GetProperty(root, property).GetInt64();
static double GetDouble(JsonElement root, string property) => GetProperty(root, property).GetDouble();
static JsonElement GetProperty(JsonElement root, string property)
{
    foreach (var candidate in root.EnumerateObject())
        if (candidate.Name.Equals(property, StringComparison.OrdinalIgnoreCase)) return candidate.Value;
    throw new InvalidDataException($"指针 JSONL 缺少字段 {property}。");
}
static int GetEnumValue(JsonElement root, string property)
{
    var value = GetProperty(root, property);
    if (value.ValueKind == JsonValueKind.Number) return value.GetInt32();
    var text = value.GetString();
    return property == "Kind" ? text switch { "Move" => 0, "MouseDown" => 1, _ => -1 }
        : text switch { "None" => 0, "Left" => 1, "Right" => 2, _ => -1 };
}

static (string? RealMediaPath, string? PointerEventsPath, string? OutputPath, bool SyncOnly, bool FfmpegProbeOnly) ParseArguments(string[] values)
{
    string? media = null, pointers = null, output = null; var sync = false; var ffmpegProbe = false;
    for (var i = 0; i < values.Length; i++)
    {
        switch (values[i])
        {
            case "--sync": sync = true; break;
            case "--ffmpeg-probe": ffmpegProbe = true; break;
            case "--real" when i + 1 < values.Length:
                media = Path.GetFullPath(values[++i]);
                break;
            case "--pointer-jsonl" when i + 1 < values.Length:
                pointers = Path.GetFullPath(values[++i]);
                break;
            case "--out" when i + 1 < values.Length:
                output = Path.GetFullPath(values[++i]);
                break;
            default:
                throw new ArgumentException("参数用法：FrameTrail.Integration [--real PATH] [--pointer-jsonl PATH] [--out PATH]");
        }
    }
    if (pointers is not null && media is null) throw new ArgumentException("--pointer-jsonl 只能与 --real 一起使用。");
    return (media, pointers, output, sync, ffmpegProbe);
}

static string FindRepositoryRoot()
{
    var candidates = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
    foreach (var candidate in candidates)
    {
        var current = new DirectoryInfo(candidate);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "src", "FrameTrail.Core"))) return current.FullName;
            current = current.Parent;
        }
    }
    throw new DirectoryNotFoundException("无法从当前目录或程序集目录定位仓库根目录。请设置工作目录为仓库根目录。 ");
}

static ProbeSummary ToProbe(MediaInfo info) => new(info.Width, info.Height, info.Duration, info.FrameRate, info.HasAudio);

sealed class CancelOnFirstRenderedFrameProgress(CancellationTokenSource source) : IProgress<ExportProgress>
{
    public List<string> Stages { get; } = [];
    public void Report(ExportProgress value)
    {
        Stages.Add($"{value.Stage} ({value.Fraction:P0})");
        if (value.Stage.StartsWith("合成与编码", StringComparison.Ordinal)) source.Cancel();
    }
}

sealed class ValidationResult
{
    public bool Success { get; set; }
    public string InputKind { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
    public string OutputDirectory { get; set; } = "";
    public string FfmpegPath { get; set; } = "";
    public string FfprobePath { get; set; } = "";
    public string? InputMediaPath { get; set; }
    public string? CopiedProjectMediaPath { get; set; }
    public string? PointerEventsPath { get; set; }
    public string? PointerOrigin { get; set; }
    public int PointerEventCount { get; set; }
    public int AutomaticZoomClipCount { get; set; }
    public ProbeSummary? SourceProbe { get; set; }
    public List<string> TimelineOperations { get; set; } = [];
    public int TimelineSegmentCount { get; set; }
    public double OutputTimelineDuration { get; set; }
    public string? ProjectPath { get; set; }
    public bool ProjectReloadSucceeded { get; set; }
    public string? ResolvedProjectMediaPath { get; set; }
    public bool CancellationObserved { get; set; }
    public bool CancellationCleanupSucceeded { get; set; }
    public List<string> CancellationProgressStages { get; set; } = [];
    public ExportCheck? VideoExport { get; set; }
    public ExportCheck? AudioExport { get; set; }
    public List<SceneComparison> SceneComparisons { get; set; } = [];
    public PerformanceCheck? Performance1080p60 { get; set; }
    public string? PngFramePath { get; set; }
    public string? Error { get; set; }
}

sealed record ProbeSummary(int Width, int Height, double DurationSeconds, double FrameRate, bool HasAudio);
sealed record ExportCheck(string Path, string Encoder, string Renderer, int FrameCount, ProbeSummary Probe);
sealed record SceneComparison(double OutputTime, double SourceTime, double Zoom, double PsnrDb, double MeanAbsoluteError);
sealed record PerformanceCheck(double ElapsedSeconds, string Encoder, string Renderer, int FrameCount, ProbeSummary Probe);
sealed class SyncValidationResult
{
    public bool Success { get; set; }
    public string OutputDirectory { get; set; } = "";
    public string FfmpegPath { get; set; } = "";
    public double ExportElapsedSeconds { get; set; }
    public string Encoder { get; set; } = "";
    public string Renderer { get; set; } = "";
    public int FrameCount { get; set; }
    public List<double> VideoFlashTimes { get; set; } = [];
    public List<double> AudioPulseTimes { get; set; } = [];
    public List<double> AVOffsetMilliseconds { get; set; } = [];
    public double MaxAbsoluteOffsetMilliseconds { get; set; }
    public string? Error { get; set; }
}
