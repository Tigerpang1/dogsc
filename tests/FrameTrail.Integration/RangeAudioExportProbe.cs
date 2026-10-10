using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using FrameTrail;
using FrameTrail.Media;

internal static class RangeAudioExportProbe
{
    private const double RangeStart = .25;
    private const double RangeEnd = 7;

    private sealed record AudioCheck(double OutputTime,
        double System440Hz, double System660Hz, double Microphone1100Hz, double Microphone1320Hz);

    internal static async Task<int> RunAsync(string? requestedOutput)
    {
        var root = requestedOutput is null
            ? Path.GetFullPath(Path.Combine("artifacts", "validation", $"range-audio-export-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}"))
            : Path.GetFullPath(requestedOutput);
        Directory.CreateDirectory(root);
        var reportPath = Path.Combine(root, "result.json");
        var report = new Dictionary<string, object?>
        {
            ["Passed"] = false,
            ["GeneratedFixtureOnly"] = true,
            ["RangeStart"] = RangeStart,
            ["RangeEnd"] = RangeEnd,
            ["Ffmpeg"] = MediaTools.Ffmpeg,
            ["Ffprobe"] = MediaTools.Ffprobe
        };

        try
        {
            var sourcePath = Path.Combine(root, "screen.mp4");
            var systemPath = Path.Combine(root, "system.wav");
            var microphonePath = Path.Combine(root, "microphone.wav");
            var projectPath = Path.Combine(root, "project.frametrail");
            var rangedProjectPath = Path.Combine(root, "range-project.frametrail");
            var videoPath = Path.Combine(root, "range.mp4");
            var audioPath = Path.Combine(root, "range.m4a");

            await CreateScreenSourceAsync(sourcePath);
            await CreateToneSourceAsync(systemPath, [440, 660, 880]);
            await CreateToneSourceAsync(microphonePath, [1100, 1320, 1760]);

            var sourceInfo = await MediaTools.ProbeAsync(sourcePath);
            var systemInfo = await MediaTools.ProbeAsync(systemPath);
            var microphoneInfo = await MediaTools.ProbeAsync(microphonePath);
            if (sourceInfo.Width != 320 || sourceInfo.Height != 180 || sourceInfo.FrameRate != 30 || sourceInfo.Duration < 7.9
                || systemInfo.Duration < 11.9 || microphoneInfo.Duration < 11.9)
                throw new InvalidDataException("合成范围导出素材元数据不符合冻结前提。 ");

            var sourceProject = new FrameTrailProject
            {
                DisplayName = "范围导出音频映射回归",
                MediaPath = "screen.mp4",
                Media = new() { Width = sourceInfo.Width, Height = sourceInfo.Height, Duration = sourceInfo.Duration, FrameRate = sourceInfo.FrameRate },
                Timeline = new()
                {
                    Segments =
                    [
                        new() { SourceStart = 4, SourceDuration = 2, PlaybackRate = 2, AudioOverrides = new() { SystemMuted = true } },
                        new() { SourceStart = 0, SourceDuration = 2, PlaybackRate = .5, AudioOverrides = new() { MicrophoneMuted = true } },
                        new() { SourceStart = 3, SourceDuration = 2, PlaybackRate = 1 }
                    ]
                },
                Settings = new() { CanvasWidth = 320, CanvasHeight = 180, Background = "#000000", Padding = 0, CornerRadius = 0, Shadow = 0, CursorVisible = false, AutoZoom = false },
                AudioTracks =
                [
                    new AudioTrack
                    {
                        MediaPath = "system.wav", Duration = 6, Volume = 1, Source = AudioTrackSource.System,
                        SyncSegments = [new(0, 0, 2, 1), new(4, 2, 4, .5)]
                    },
                    new AudioTrack
                    {
                        MediaPath = "microphone.wav", Duration = 6, Volume = 1, Source = AudioTrackSource.Microphone,
                        SyncSegments = [new(6, 0, 2, 1), new(0, 2, 4, 2)]
                    }
                ]
            };

            // Apply the same output-time range intersection used by MainWindow.ExportSnapshot.
            // Freeze the expected clipped source coordinates before rendering.
            var rangedSegments = SliceOutputRange(sourceProject.Timeline, RangeStart, RangeEnd);
            var expectedSegments = new[]
            {
                new { SourceStart = 4.5, SourceEnd = 6d, Rate = 2d, MutedSystem = true, MutedMicrophone = false },
                new { SourceStart = 0d, SourceEnd = 2d, Rate = .5, MutedSystem = false, MutedMicrophone = true },
                new { SourceStart = 3d, SourceEnd = 5d, Rate = 1d, MutedSystem = false, MutedMicrophone = false }
            };
            if (rangedSegments.Count != expectedSegments.Length || rangedSegments.Where((segment, index) =>
                    Math.Abs(segment.SourceStart - expectedSegments[index].SourceStart) > 1e-8
                    || Math.Abs(segment.SourceEnd - expectedSegments[index].SourceEnd) > 1e-8
                    || Math.Abs(segment.PlaybackRate - expectedSegments[index].Rate) > 1e-8
                    || (segment.AudioOverrides?.SystemMuted ?? false) != expectedSegments[index].MutedSystem
                    || (segment.AudioOverrides?.MicrophoneMuted ?? false) != expectedSegments[index].MutedMicrophone).Any())
                throw new InvalidDataException("范围裁切快照与预先冻结的源区间/静音预期不同。 ");

            FrameTrailProjectStore.Save(projectPath, sourceProject);
            var loadedProject = FrameTrailProjectStore.Load(projectPath);
            var rangedProject = new FrameTrailProject
            {
                ProjectId = loadedProject.ProjectId,
                DisplayName = loadedProject.DisplayName,
                MediaPath = loadedProject.MediaPath,
                Media = loadedProject.Media,
                Timeline = new() { Segments = rangedSegments },
                PointerEvents = loadedProject.PointerEvents,
                Markers = loadedProject.Markers,
                MouseClock = loadedProject.MouseClock,
                ZoomClips = loadedProject.ZoomClips,
                ScreenMotions = loadedProject.ScreenMotions,
                Overlays = loadedProject.Overlays,
                BlurClips = loadedProject.BlurClips,
                Camera = loadedProject.Camera,
                CameraMotions = loadedProject.CameraMotions,
                AudioTracks = loadedProject.AudioTracks,
                Settings = loadedProject.Settings
            };
            FrameTrailProjectStore.Save(rangedProjectPath, rangedProject);
            rangedProject = FrameTrailProjectStore.Load(rangedProjectPath);
            var resolvedScreen = FrameTrailProjectStore.ResolveMediaPath(rangedProjectPath, rangedProject);
            if (rangedProject.AudioTracks.Count != 2 || rangedProject.Timeline.Segments.Count != 3
                || Math.Abs(rangedProject.Timeline.Duration - 6.75) > 1e-8)
                throw new InvalidDataException("保存重开后的范围工程时长或音轨数量错误。 ");

            var engine = new ExportEngine();
            var videoExport = await engine.ExportAsync(rangedProject, resolvedScreen, videoPath,
                new ExportOptions(320, 180, 30, HardwarePreferred: false, ProjectFilePath: rangedProjectPath));
            var videoInfo = await MediaTools.ProbeAsync(videoPath);
            if (videoExport.FrameCount != 203 || videoInfo.Width != 320 || videoInfo.Height != 180 || !videoInfo.HasAudio
                || Math.Abs(videoInfo.Duration - 6.75) > .12 || Math.Abs(videoInfo.FrameRate - 30) > .01)
                throw new InvalidDataException($"MP4范围输出核验失败：{videoInfo.Width}x{videoInfo.Height}@{videoInfo.FrameRate}, {videoExport.FrameCount} frames, {videoInfo.Duration:F3}s, audio={videoInfo.HasAudio}");

            var audioExport = await engine.ExportAsync(rangedProject, resolvedScreen, audioPath,
                new ExportOptions(320, 180, 30, HardwarePreferred: false, AudioOnly: true, ProjectFilePath: rangedProjectPath));
            var audioInfo = await MediaTools.ProbeAsync(audioPath);
            if (!audioInfo.HasAudio || audioInfo.Width != 0 || Math.Abs(audioInfo.Duration - 6.75) > .12)
                throw new InvalidDataException($"M4A范围输出核验失败：{audioInfo.Duration:F3}s, audio={audioInfo.HasAudio}");

            var fractionalFrameChecks = await VerifyFractionalFrameCountsAsync(root, sourcePath, sourceInfo, systemPath, systemInfo);

            var videoChecks = new[]
            {
                (Time: .4, Expected: "blue"), (Time: 1.1, Expected: "red"),
                (Time: 5.3, Expected: "green"), (Time: 5.9, Expected: "blue")
            };
            var videoResults = new List<object>();
            foreach (var item in videoChecks)
            {
                var pixels = await MediaTools.DecodeFrameAsync(videoPath, item.Time, 320, 180, CancellationToken.None);
                var color = ClassifyColor(pixels, 320, 160, 90);
                if (color != item.Expected) throw new InvalidDataException($"范围切口画面在{item.Time:F2}s应为{item.Expected}，实际为{color}。");
                videoResults.Add(new { item.Time, item.Expected, Actual = color, CenterBgra = Pixel(pixels, 320, 160, 90) });
            }

            var decodedPcm = Path.Combine(root, "range-decoded.f32le");
            await MediaTools.RunAsync(MediaTools.Ffmpeg,
                ["-v", "error", "-i", audioPath, "-vn", "-ac", "1", "-ar", "48000", "-f", "f32le", "-y", decodedPcm]);
            var pcmBytes = await File.ReadAllBytesAsync(decodedPcm);
            if (pcmBytes.Length % 4 != 0) throw new InvalidDataException("M4A解码PCM字节数未对齐float32样本。");
            var pcm = new float[pcmBytes.Length / 4];
            for (var i = 0; i < pcm.Length; i++) pcm[i] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(pcmBytes.AsSpan(i * 4, 4)));
            var checks = new[]
            {
                Measure(pcm, .3),
                Measure(pcm, 1.1),
                Measure(pcm, 5.3),
                Measure(pcm, 5.9)
            };
            if (checks[0].Microphone1320Hz < .08 || checks[0].System440Hz > .015 || checks[0].System660Hz > .015
                || checks[1].System440Hz < .08 || checks[1].Microphone1100Hz > .015 || checks[1].Microphone1320Hz > .015
                || checks[2].System660Hz < .08 || checks[2].Microphone1100Hz < .08
                || checks[3].System660Hz < .08 || checks[3].Microphone1320Hz < .08)
                throw new InvalidDataException("M4A分段频率/静音断言未达到冻结门槛：" + JsonSerializer.Serialize(checks));

            report["Passed"] = true;
            report["ProjectPath"] = projectPath;
            report["RangeProjectPath"] = rangedProjectPath;
            report["SourceMedia"] = new { Screen = sourceInfo, SystemAudio = systemInfo, MicrophoneAudio = microphoneInfo };
            report["ExpectedClippedSegments"] = expectedSegments;
            report["ExportTimelineSeconds"] = rangedProject.Timeline.Duration;
            report["VideoExport"] = new { videoExport.FrameCount, videoExport.Encoder, videoExport.Renderer, Probe = videoInfo, FrameChecks = videoResults };
            report["AudioExport"] = new { audioExport.FrameCount, audioExport.Encoder, Probe = audioInfo, PcmSamples = pcm.Length, Checks = checks };
            report["FractionalFrameExports"] = fractionalFrameChecks;
            report["FrozenThresholds"] = new { ExpectedToneAmplitudeMinimum = .08, MutedBandAmplitudeMaximum = .015, FrequencyWindowSeconds = .18, PcmSampleRate = 48000 };
        }
        catch (Exception ex)
        {
            report["Error"] = ex.ToString();
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.Error.WriteLine($"范围导出媒体回归失败；报告：{reportPath}");
            Console.Error.WriteLine(ex);
            return 1;
        }

        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"范围MP4/M4A及音轨切口核验通过；{reportPath}");
        return 0;
    }

    private static List<TimelineSegment> SliceOutputRange(ProjectTimeline timeline, double start, double end)
    {
        var result = new List<TimelineSegment>();
        var elapsed = 0d;
        foreach (var segment in timeline.Segments)
        {
            var a = Math.Max(start, elapsed);
            var b = Math.Min(end, elapsed + segment.OutputDuration);
            if (b > a)
                result.Add(segment with
                {
                    SourceStart = segment.SourceStart + (a - elapsed) * segment.PlaybackRate,
                    SourceDuration = (b - a) * segment.PlaybackRate
                });
            elapsed += segment.OutputDuration;
        }
        if (result.Count == 0) throw new InvalidOperationException("导出范围没有覆盖时间线片段。");
        return result;
    }

    private static async Task<object[]> VerifyFractionalFrameCountsAsync(string root, string sourcePath, MediaInfo sourceInfo,
        string finiteAudioPath, MediaInfo finiteAudioInfo)
    {
        var results = new List<object>();
        var source60Path = Path.Combine(root, "fractional-source-60fps.mp4");
        await CreateScreenSourceAsync(source60Path, 60);
        var source60Info = await MediaTools.ProbeAsync(source60Path);
        foreach (var (name, duration, fps) in new[]
                 {
                     ("fractional-30fps", 12.6759265, 30),
                     ("fractional-60fps", 1.017, 60)
                 })
        {
            var testSourcePath = fps == 60 ? source60Path : sourcePath;
            var testSourceInfo = fps == 60 ? source60Info : sourceInfo;
            var expectedFrames = checked((int)Math.Ceiling(duration * fps - 1e-7));
            var lastExpectedSample = (expectedFrames - 1) / (double)fps;
            var precedingSample = (expectedFrames - 2) / (double)fps;
            // Put a visible source-color cut between the last two expected samples. If the
            // muxer drops the final CFR sample, the output's last frame remains red.
            var finalSegmentStart = (lastExpectedSample + precedingSample) / 2;
            var segments = new List<TimelineSegment>();
            var redRemaining = finalSegmentStart;
            while (redRemaining > 1e-8)
            {
                var length = Math.Min(2, redRemaining);
                segments.Add(new() { SourceStart = 0, SourceDuration = length });
                redRemaining -= length;
            }
            segments.Add(new() { SourceStart = 2, SourceDuration = duration - finalSegmentStart });

            var projectPath = Path.Combine(root, name + ".frametrail");
            var outputPath = Path.Combine(root, name + ".mp4");
            var project = new FrameTrailProject
            {
                DisplayName = name,
                MediaPath = Path.GetFileName(testSourcePath),
                Media = new() { Width = testSourceInfo.Width, Height = testSourceInfo.Height, Duration = testSourceInfo.Duration, FrameRate = testSourceInfo.FrameRate },
                Timeline = new() { Segments = segments },
                AudioTracks = fps == 30
                    ? [new AudioTrack
                    {
                        MediaPath = Path.GetFileName(finiteAudioPath), Duration = finiteAudioInfo.Duration,
                        Volume = 1, Source = AudioTrackSource.System,
                        SyncSegments = [new(0, 0, finiteAudioInfo.Duration, 1)]
                    }]
                    : [],
                Settings = new() { CanvasWidth = 320, CanvasHeight = 180, Background = "#000000", Padding = 0, CornerRadius = 0, Shadow = 0, CursorVisible = false, AutoZoom = false }
            };
            FrameTrailProjectStore.Save(projectPath, project);
            project = FrameTrailProjectStore.Load(projectPath);
            var resolvedSource = FrameTrailProjectStore.ResolveMediaPath(projectPath, project);
            var export = await new ExportEngine().ExportAsync(project, resolvedSource, outputPath,
                new ExportOptions(320, 180, fps, HardwarePreferred: false, ProjectFilePath: projectPath));
            var probeJson = await MediaTools.RunAsync(MediaTools.Ffprobe,
                ["-v", "error", "-count_frames", "-select_streams", "v:0", "-show_entries", "stream=nb_read_frames,duration,avg_frame_rate", "-of", "json", outputPath]);
            using var probe = JsonDocument.Parse(probeJson);
            var video = probe.RootElement.GetProperty("streams").EnumerateArray().Single();
            var decodedFrames = int.Parse(video.GetProperty("nb_read_frames").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            var videoInfo = await MediaTools.ProbeAsync(outputPath);
            double? outputAudioDuration = null;
            if (fps == 30)
            {
                using var audioProbe = JsonDocument.Parse(await MediaTools.RunAsync(MediaTools.Ffprobe,
                    ["-v", "error", "-select_streams", "a:0", "-show_entries", "stream=duration", "-of", "json", outputPath]));
                var audioStream = audioProbe.RootElement.GetProperty("streams").EnumerateArray().Single();
                outputAudioDuration = double.Parse(audioStream.GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                if (!videoInfo.HasAudio || Math.Abs(outputAudioDuration.Value - duration) > .08
                    || outputAudioDuration.Value > videoInfo.Duration
                    || videoInfo.Duration < duration || videoInfo.Duration > duration + 1 / (double)fps + .002)
                    throw new InvalidDataException($"{name}有限音轨/容器时长错误：输入音轨={finiteAudioInfo.Duration:R}s，输出音轨={outputAudioDuration:R}s，timeline={duration:R}s，容器={videoInfo.Duration:R}s。");
            }
            var hashes = (await MediaTools.RunAsync(MediaTools.Ffmpeg,
                    ["-v", "error", "-i", outputPath, "-map", "0:v:0", "-an", "-f", "framemd5", "-"]))
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Where(line => !line.StartsWith('#')).ToArray();
            if (export.FrameCount != expectedFrames || decodedFrames != expectedFrames || hashes.Length != expectedFrames)
                throw new InvalidDataException($"{name}帧数错误：预期 {expectedFrames}，ExportResult={export.FrameCount}，ffprobe={decodedFrames}，framemd5={hashes.Length}。");
            var previousPixels = await MediaTools.DecodeFrameAsync(outputPath, precedingSample, 320, 180, CancellationToken.None);
            var lastPixels = await MediaTools.DecodeFrameAsync(outputPath, lastExpectedSample, 320, 180, CancellationToken.None);
            var precedingColor = ClassifyColor(previousPixels, 320, 160, 90);
            var lastColor = ClassifyColor(lastPixels, 320, 160, 90);
            if (precedingColor != "red" || lastColor != "green")
                throw new InvalidDataException($"{name}尾帧内容错误：前一采样点应为red/实际{precedingColor}，最后预期采样点应为green/实际{lastColor}。");
            var expectedDuration = expectedFrames / (double)fps;
            if (Math.Abs(videoInfo.Duration - expectedDuration) > .002 || videoInfo.Duration + 1e-8 < duration)
                throw new InvalidDataException($"{name}封装时长错误：timeline={duration:R}s，MP4={videoInfo.Duration:R}s，帧对齐预期={expectedDuration:R}s。");
            results.Add(new
            {
                Name = name,
                TimelineDurationSeconds = duration,
                FrameRate = fps,
                ExpectedCfrFrames = expectedFrames,
                ExportResultFrames = export.FrameCount,
                FfprobeDecodedFrames = decodedFrames,
                FrameMd5Count = hashes.Length,
                PreviousSampleTime = precedingSample,
                PreviousSampleColor = precedingColor,
                LastExpectedSampleTime = lastExpectedSample,
                LastExpectedSampleColor = lastColor,
                TimelineDurationRoundedToCfrFrames = videoInfo.Duration,
                ExpectedCfrDuration = expectedDuration,
                FiniteAudioInputDuration = fps == 30 ? finiteAudioInfo.Duration : (double?)null,
                OutputAudioDuration = outputAudioDuration,
                HasAudio = videoInfo.HasAudio,
                Encoder = export.Encoder,
                Renderer = export.Renderer
            });
        }
        return results.ToArray();
    }

    private static async Task CreateScreenSourceAsync(string path, int fps = 30)
    {
        var inputs = new List<string>();
        foreach (var color in new[] { "red", "green", "blue", "yellow" })
        {
            inputs.Add("-f"); inputs.Add("lavfi"); inputs.Add("-i"); inputs.Add($"color=c={color}:s=320x180:r={fps}:d=2");
        }
        await MediaTools.RunAsync(MediaTools.Ffmpeg, ["-v", "error", .. inputs,
            "-filter_complex", "[0:v][1:v][2:v][3:v]concat=n=4:v=1:a=0,format=yuv420p[v]", "-map", "[v]", "-an", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "8", "-pix_fmt", "yuv420p", "-y", path]);
    }

    private static async Task CreateToneSourceAsync(string path, int[] frequencies)
    {
        if (frequencies.Length != 3) throw new ArgumentException("测试音频需三段频率。", nameof(frequencies));
        var args = new List<string> { "-v", "error" };
        foreach (var frequency in frequencies)
        {
            args.AddRange(["-f", "lavfi", "-i", $"sine=frequency={frequency}:sample_rate=48000:duration=4"]);
        }
        args.AddRange(["-filter_complex", "[0:a]volume=2[a0];[1:a]volume=2[a1];[2:a]volume=2[a2];[a0][a1][a2]concat=n=3:v=0:a=1[a]", "-map", "[a]", "-c:a", "pcm_s16le", "-y", path]);
        await MediaTools.RunAsync(MediaTools.Ffmpeg, args);
    }

    private static AudioCheck Measure(float[] pcm, double time)
    {
        var amplitude = new Dictionary<int, double>();
        foreach (var frequency in new[] { 440, 660, 1100, 1320 }) amplitude[frequency] = ToneAmplitude(pcm, time, frequency);
        return new(time, amplitude[440], amplitude[660], amplitude[1100], amplitude[1320]);
    }

    private static double ToneAmplitude(float[] pcm, double time, int frequency)
    {
        const int sampleRate = 48000;
        const int windowSize = 8640;
        var start = checked((int)Math.Round(time * sampleRate));
        if (start < 0 || start + windowSize > pcm.Length) throw new InvalidDataException($"音频分析窗超出解码PCM范围：t={time:F3}s, samples={pcm.Length}。");
        double real = 0, imaginary = 0;
        for (var i = 0; i < windowSize; i++)
        {
            var phase = 2 * Math.PI * frequency * i / sampleRate;
            var value = pcm[start + i];
            real += value * Math.Cos(phase);
            imaginary += value * Math.Sin(phase);
        }
        return 2 * Math.Sqrt(real * real + imaginary * imaginary) / windowSize;
    }

    private static string ClassifyColor(byte[] bgra, int width, int x, int y)
    {
        var pixel = Pixel(bgra, width, x, y);
        var b = pixel[0]; var g = pixel[1]; var r = pixel[2];
        if (r > 150 && g < 100 && b < 100) return "red";
        // FFmpeg's named `green` fixture color is #008000, not full-intensity lime.
        if (g > 90 && r < 80 && b < 80) return "green";
        if (b > 150 && r < 100 && g < 100) return "blue";
        if (r > 150 && g > 130 && b < 100) return "yellow";
        return $"unknown(B={b},G={g},R={r})";
    }

    private static int[] Pixel(byte[] bgra, int width, int x, int y)
    {
        var offset = checked((y * width + x) * 4);
        return [bgra[offset], bgra[offset + 1], bgra[offset + 2], bgra[offset + 3]];
    }
}
