using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FrameTrail.Capture;

internal static class CaptureVideoQualityProbe
{
    private sealed record MediaInfo(int Width, int Height, double FramesPerSecond, double DurationSeconds, long Bytes, long? AverageBitrate);
    private sealed record KeyframeInfo(IReadOnlyList<double> TimesSeconds, IReadOnlyList<double> GapsSeconds, double MaximumGapSeconds);
    private const int Width = 3840;
    private const int Height = 2160;
    private const int FrameRate = 60;
    private const int DurationSeconds = 2;

    internal static async Task RunAsync(string outputDirectory)
    {
        ValidatePolicy();
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException($"Refusing to reuse video quality probe output: {root}");
        Directory.CreateDirectory(root);
        var ffmpeg = LocateTool("ffmpeg.exe");
        var ffprobe = LocateTool("ffprobe.exe");

        var fixture = Path.Combine(root, "fixture-4k60-lossless.mkv");
        var oldOutput = Path.Combine(root, "baseline-g250.mp4");
        var newOutput = Path.Combine(root, "quality-policy.mp4");
        var options = new VideoOptions(FrameRate: FrameRate);
        var filter = "testsrc2=size=3840x2160:rate=60:duration=2,drawgrid=width=12:height=12:thickness=1:color=white@0.45,drawtext=fontfile='C\\:/Windows/Fonts/arial.ttf':text='FrameTrail 4K 0123456789 fine text':fontsize=96:fontcolor=white:borderw=3:bordercolor=black:x=120:y=120";
        await RunTool(ffmpeg, ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", filter,
            "-frames:v", (FrameRate * DurationSeconds).ToString(CultureInfo.InvariantCulture), "-an", "-c:v", "ffv1", "-level", "3", fixture]);

        var oldArguments = new List<string> { "-hide_banner", "-loglevel", "error", "-i", fixture,
            "-frames:v", (FrameRate * DurationSeconds).ToString(CultureInfo.InvariantCulture), "-an" };
        oldArguments.AddRange(CaptureVideoQuality.EncoderArguments("h264_nvenc", options, Width, Height));
        var gopIndex = oldArguments.IndexOf("-g");
        if (gopIndex < 0) throw new InvalidOperationException("The encoder policy did not include a GOP limit.");
        oldArguments[gopIndex + 1] = "250";
        oldArguments.AddRange(["-pix_fmt", "yuv420p", "-y", oldOutput]);
        var newArguments = new List<string> { "-hide_banner", "-loglevel", "error", "-i", fixture,
            "-frames:v", (FrameRate * DurationSeconds).ToString(CultureInfo.InvariantCulture), "-an" };
        newArguments.AddRange(CaptureVideoQuality.EncoderArguments("h264_nvenc", options, Width, Height));
        newArguments.AddRange(["-pix_fmt", "yuv420p", "-y", newOutput]);

        var oldElapsed = await RunTimed(ffmpeg, oldArguments);
        var newElapsed = await RunTimed(ffmpeg, newArguments);
        var sourceInfo = await ProbeFile(ffprobe, fixture);
        var oldInfo = await ProbeFile(ffprobe, oldOutput);
        var newInfo = await ProbeFile(ffprobe, newOutput);
        var oldSsim = await MeasureSsim(ffmpeg, fixture, oldOutput, Path.Combine(root, "old-ssim.txt"));
        var newSsim = await MeasureSsim(ffmpeg, fixture, newOutput, Path.Combine(root, "new-ssim.txt"));
        var oldKeyframes = await ProbeKeyframes(ffprobe, oldOutput, oldInfo.DurationSeconds);
        var newKeyframes = await ProbeKeyframes(ffprobe, newOutput, newInfo.DurationSeconds);
        CheckFrameAccurateKeyframeGaps(newKeyframes, FrameRate, CaptureVideoQuality.KeyframeIntervalFrames(FrameRate), "60 fps policy");
        Check(newKeyframes.MaximumGapSeconds <= CaptureVideoQuality.TargetKeyframeIntervalSeconds + 1d / FrameRate,
            $"The policy encode keyframe gap was {newKeyframes.MaximumGapSeconds:0.000}s, above the half-second target.");
        Check(oldKeyframes.MaximumGapSeconds > CaptureVideoQuality.TargetKeyframeIntervalSeconds + 1d / FrameRate,
            "The baseline encode should demonstrate the longer default GOP interval.");
        var shortGopSamples = new[]
        {
            await EncodeShortGopSample(ffmpeg, ffprobe, root, "h264_nvenc", 30),
            await EncodeShortGopSample(ffmpeg, ffprobe, root, "libx264", 30)
        };

        var report = new
        {
            probe = "synthetic-ffmpeg-encode-comparison",
            source = sourceInfo,
            old = new { encoder = "h264_nvenc", arguments = oldArguments, elapsedSeconds = oldElapsed.TotalSeconds, ssim = oldSsim, media = oldInfo, keyframes = oldKeyframes },
            qualityPolicy = new { encoder = "h264_nvenc", arguments = CaptureVideoQuality.EncoderArguments("h264_nvenc", options, Width, Height), elapsedSeconds = newElapsed.TotalSeconds, ssim = newSsim, media = newInfo, keyframes = newKeyframes },
            shortGopSamples,
            policyChecks = new { baseline1080p60 = CaptureVideoQuality.ResolveBitrate(1920, 1080, 60), target4k60 = CaptureVideoQuality.ResolveBitrate(3840, 2160, 60), explicitBitrate = CaptureVideoQuality.ResolveBitrate(3840, 2160, 60, 9_000_000) },
            limitations = "Synthetic FFmpeg source only; this does not validate live desktop capture, GPU capture throughput, or perceived quality in the application."
        };
        var reportPath = Path.Combine(root, "result.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Synthetic 4K60 comparison completed. Old SSIM={oldSsim:0.000000}, new SSIM={newSsim:0.000000}; output dimensions {newInfo.Width}x{newInfo.Height}; report: {reportPath}");
    }

    private static void ValidatePolicy()
    {
        Check(CaptureVideoQuality.ResolveBitrate(1920, 1080, 60) == 16_000_000, "1080p60 should default to 16 Mbps.");
        Check(CaptureVideoQuality.ResolveBitrate(3840, 2160, 60) == 64_000_000, "4K60 should default to 64 Mbps.");
        Check(CaptureVideoQuality.ResolveBitrate(1920, 1080, 30) == 8_000_000, "1080p30 should default to 8 Mbps.");
        Check(CaptureVideoQuality.ResolveBitrate(3840, 2160, 60, 9_000_000) == 9_000_000, "An explicit bitrate must win.");
        Check(CaptureVideoQuality.KeyframeIntervalFrames(60) == 30, "60 fps should use a 30-frame maximum GOP.");
        Check(CaptureVideoQuality.KeyframeIntervalFrames(30) == 15, "30 fps should use a 15-frame maximum GOP.");
        Check(CaptureVideoQuality.KeyframeIntervalFrames(1) == 1, "Low frame rates must keep a valid one-frame GOP.");
        foreach (var encoder in new[] { "h264_nvenc", "h264_amf", "h264_qsv", "h264_mf", "libx264" })
        {
            var encoderArgs = CaptureVideoQuality.EncoderArguments(encoder, new VideoOptions(FrameRate: 60), Width, Height);
            Check(OptionValue(encoderArgs, "-g") == "30", $"{encoder} must receive the 30-frame maximum GOP at 60 fps.");
        }
        var args = CaptureVideoQuality.EncoderArguments("h264_nvenc", new VideoOptions(FrameRate: 60), Width, Height).ToList();
        Check(OptionValue(args, "-rc") == "vbr", "NVENC must use VBR.");
        Check(OptionValue(args, "-cq") == "19", "NVENC must set constant quality 19.");
        Check(OptionValue(args, "-b:v") == "64000000", "NVENC target bitrate must be resolution-scaled.");
        Check(OptionValue(args, "-maxrate") == "64000000", "NVENC maximum rate must preserve the bitrate budget.");
        var explicitArgs = CaptureVideoQuality.EncoderArguments("h264_nvenc", new VideoOptions(FrameRate: FrameRate, Bitrate: 9_000_000), Width, Height);
        Check(OptionValue(explicitArgs, "-b:v") == "9000000" && OptionValue(explicitArgs, "-maxrate") == "9000000", "An explicit bitrate must set both the NVENC target and maximum rate.");
    }

    private static async Task<KeyframeInfo> ProbeKeyframes(string ffprobe, string path, double durationSeconds)
    {
        var output = await RunTool(ffprobe, ["-v", "error", "-select_streams", "v:0", "-show_frames", "-show_entries", "frame=key_frame,best_effort_timestamp_time", "-of", "json", path], captureOutput: true);
        using var document = JsonDocument.Parse(output.StandardOutput);
        var times = document.RootElement.GetProperty("frames").EnumerateArray()
            .Where(frame => frame.GetProperty("key_frame").GetInt32() == 1)
            .Select(frame => double.Parse(frame.GetProperty("best_effort_timestamp_time").GetString()!, CultureInfo.InvariantCulture))
            .ToList();
        if (times.Count == 0) throw new InvalidDataException($"No keyframes found in {path}.");
        var gaps = times.Zip(times.Skip(1), (start, end) => end - start).ToList();
        gaps.Add(durationSeconds - times[^1]);
        return new KeyframeInfo(times, gaps, gaps.Max());
    }

    private static async Task<object> EncodeShortGopSample(string ffmpeg, string ffprobe, string root, string encoder, int frameRate)
    {
        const int width = 640, height = 360, durationSeconds = 1;
        var path = Path.Combine(root, $"{encoder}-{frameRate}fps-gop-sample.mp4");
        var options = new VideoOptions(FrameRate: frameRate);
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
            FormattableString.Invariant($"testsrc2=size={width}x{height}:rate={frameRate}:duration={durationSeconds}"),
            "-frames:v", (frameRate * durationSeconds).ToString(CultureInfo.InvariantCulture), "-an"
        };
        arguments.AddRange(CaptureVideoQuality.EncoderArguments(encoder, options, width, height));
        arguments.AddRange(["-pix_fmt", "yuv420p", "-y", path]);
        await RunTool(ffmpeg, arguments);
        var media = await ProbeFile(ffprobe, path);
        var keyframes = await ProbeKeyframes(ffprobe, path, media.DurationSeconds);
        var expectedFrames = CaptureVideoQuality.KeyframeIntervalFrames(frameRate);
        CheckFrameAccurateKeyframeGaps(keyframes, frameRate, expectedFrames, $"{encoder} at {frameRate} fps");
        return new { encoder, frameRate, expectedGopFrames = expectedFrames, arguments = CaptureVideoQuality.EncoderArguments(encoder, options, width, height), media, keyframes };
    }

    private static void CheckFrameAccurateKeyframeGaps(KeyframeInfo keyframes, int frameRate, int expectedFrames, string label)
    {
        Check(keyframes.TimesSeconds.Count >= 2, $"{label} must contain at least two keyframes to verify GOP spacing.");
        var frameGaps = keyframes.GapsSeconds.Select(gap => gap * frameRate).ToArray();
        Check(frameGaps.All(gap => gap <= expectedFrames + 0.01), $"{label} exceeded its {expectedFrames}-frame GOP limit.");
        Check(frameGaps.Any(gap => Math.Abs(gap - expectedFrames) <= 0.01), $"{label} did not produce a keyframe interval of exactly {expectedFrames} frames.");
    }

    private static string? OptionValue(IReadOnlyList<string> args, string option)
    {
        for (var index = 0; index + 1 < args.Count; index++)
            if (args[index] == option) return args[index + 1];
        return null;
    }

    private static async Task<double> MeasureSsim(string ffmpeg, string reference, string encoded, string statsPath)
    {
        var filterStatsPath = Path.GetRelativePath(Environment.CurrentDirectory, statsPath).Replace('\\', '/');
        var result = await RunTool(ffmpeg, ["-hide_banner", "-i", reference, "-i", encoded, "-lavfi", $"[0:v][1:v]ssim=shortest=1:stats_file={filterStatsPath}", "-f", "null", "NUL"], captureOutput: true);
        var match = Regex.Match(result.StandardError, @"All:([0-9.]+)");
        if (!match.Success) throw new InvalidDataException($"Could not parse SSIM result: {result.StandardError}");
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static async Task<TimeSpan> RunTimed(string executable, IReadOnlyList<string> arguments)
    {
        var timer = Stopwatch.StartNew();
        await RunTool(executable, arguments);
        timer.Stop();
        return timer.Elapsed;
    }

    private static async Task<MediaInfo> ProbeFile(string ffprobe, string path)
    {
        var output = await RunTool(ffprobe, ["-v", "error", "-show_streams", "-show_format", "-of", "json", path], captureOutput: true);
        using var document = JsonDocument.Parse(output.StandardOutput);
        var video = document.RootElement.GetProperty("streams").EnumerateArray().First(s => s.GetProperty("codec_type").GetString() == "video");
        var format = document.RootElement.GetProperty("format");
        var rate = video.GetProperty("avg_frame_rate").GetString() ?? "0/1";
        var parts = rate.Split('/');
        var fps = double.Parse(parts[0], CultureInfo.InvariantCulture) / double.Parse(parts[1], CultureInfo.InvariantCulture);
        var duration = double.Parse(format.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        var bytes = long.Parse(format.GetProperty("size").GetString()!, CultureInfo.InvariantCulture);
        long? bitrate = format.TryGetProperty("bit_rate", out var bitRateElement) && long.TryParse(bitRateElement.GetString(), out var parsed) ? parsed : null;
        return new MediaInfo(video.GetProperty("width").GetInt32(), video.GetProperty("height").GetInt32(), fps, duration, bytes, bitrate);
    }

    private static string LocateTool(string fileName)
    {
        var candidates = new List<string>();
        var custom = Environment.GetEnvironmentVariable("FRAMETRAIL_FFMPEG_DIR");
        if (!string.IsNullOrWhiteSpace(custom)) candidates.Add(Path.Combine(custom, fileName));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", fileName));
        candidates.Add(Path.Combine(Environment.CurrentDirectory, "artifacts", "release", "FrameTrail-0.2.11-win-x64", "app", "tools", "ffmpeg", fileName));
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(directory, fileName));
        return candidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException($"Could not locate {fileName}.");
    }

    private static async Task<(string StandardOutput, string StandardError)> RunTool(string executable, IReadOnlyList<string> arguments, bool captureOutput = false)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {executable}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var standardOutput = await stdout;
        var standardError = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(executable)} exited {process.ExitCode}: {standardError}");
        return (captureOutput ? standardOutput : string.Empty, standardError);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
