using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using FrameTrail;
using FrameTrail.Capture;
using FrameTrail.Media;

if (args.Length > 0 && string.Equals(args[0], "--captured-project", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length != 3)
        throw new ArgumentException("Usage: FrameTrail.AudioOverrides.Probe --captured-project <capture.sync-playback.json> <outdir>");
    await CapturedProjectProbe.RunAsync(args[1], args[2]);
    return;
}
if (args.Length > 0 && string.Equals(args[0], "--source-rate-probe", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length != 2)
        throw new ArgumentException("Usage: FrameTrail.AudioOverrides.Probe --source-rate-probe <fresh-outdir>");
    await AudioSourceRateProbe.RunAsync(args[1]);
    return;
}

var outputDirectory = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Environment.CurrentDirectory, "artifacts", "validation", "audio-overrides");
Directory.CreateDirectory(outputDirectory);
var systemPath = Path.Combine(outputDirectory, "system-440.wav");
var microphonePath = Path.Combine(outputDirectory, "microphone-880.wav");
var mixPath = Path.Combine(outputDirectory, "overrides-mix.wav");
var mappedMicrophonePath = Path.Combine(outputDirectory, "mapped-microphone.wav");
var mappedMixPath = Path.Combine(outputDirectory, "sync-segments-mix.wav");
var indexMixPath = Path.Combine(outputDirectory, "muted-input-index-mix.wav");
var exportProjectDirectory = Path.Combine(outputDirectory, "export-project");
var exportMediaDirectory = Path.Combine(exportProjectDirectory, "media");
Directory.CreateDirectory(exportMediaDirectory);
var exportScreenPath = Path.Combine(exportProjectDirectory, "screen-with-system-audio.mp4");
var exportSystemPath = Path.Combine(exportMediaDirectory, "system-440.wav");
var exportMicrophonePath = Path.Combine(exportMediaDirectory, "microphone-880.wav");
var exportProjectPath = Path.Combine(exportProjectDirectory, "project.frametrail");
var exportMixPath = Path.Combine(outputDirectory, "export-engine-overrides.m4a");
var attachMappedPath = Path.Combine(outputDirectory, "attach-mapped-source.wav");
var attachMappedMixPath = Path.Combine(outputDirectory, "attach-mapped-render.wav");
var attachProjectPath = Path.Combine(outputDirectory, "attach-project", "project.frametrail");
await RunFfmpeg(["-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=6", "-c:a", "pcm_f32le", "-y", systemPath]);
await RunFfmpeg(["-v", "error", "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=6", "-c:a", "pcm_f32le", "-y", microphonePath]);
await RunFfmpeg(["-v", "error", "-filter_complex", "sine=frequency=660:sample_rate=48000:duration=2[a];sine=frequency=990:sample_rate=48000:duration=1[b];sine=frequency=1320:sample_rate=48000:duration=2[c];[a][b][c]concat=n=3:v=0:a=1[out]", "-map", "[out]", "-c:a", "pcm_f32le", "-y", mappedMicrophonePath]);
await RunFfmpeg(["-v", "error", "-filter_complex", "sine=frequency=440:sample_rate=48000:duration=4[a];sine=frequency=880:sample_rate=48000:duration=2[b];sine=frequency=660:sample_rate=48000:duration=4[c];[a][b][c]concat=n=3:v=0:a=1[out]", "-map", "[out]", "-c:a", "pcm_f32le", "-y", attachMappedPath]);

var attachProject = new FrameTrailProject
{
    MediaPath = "screen.mp4",
    Media = new() { Width = 320, Height = 180, Duration = 8, FrameRate = 60 },
    Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 8 }] }
};
var appAssembly = Assembly.Load("FrameTrail");
var attachMethod = appAssembly.GetType("FrameTrail.App.MainWindow")?.GetMethod("AttachMappedAudio", BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("无法定位 App 的 AttachMappedAudio 实际入口。");
var screenResult = new RecordingResult("screen.mp4", 0, 10_000, 1_000, EncoderMode.Software, "probe", 320, 180, 60,
    null, [], [new CapturePauseInterval(4_000, 6_000)], FirstFrameObservedStopwatchTicks: 0);
var captureHandle = new CaptureTrackHandle(Guid.NewGuid(), CaptureTrackKind.SystemAudio, attachMappedPath, 0, 1_000);
var captureTrackResult = new CaptureTrackResult(captureHandle, 10_000, 0, 0, 0, EncoderMode.Unknown, [], [],
    AudioSampleRate: 48_000, AudioFramesWritten: 480_000,
    AudioClockSegments: [new CaptureAudioClockSegment(0, 10_000, 0, 480_000, 48_000, 1)], AudioSourceRate: 1);
attachMethod.Invoke(null, [attachProject, screenResult, captureTrackResult, "media/captured.wav", 10d, AudioTrackSource.System]);
Check(attachProject.AudioTracks.Count == 1 && attachProject.AudioTracks[0].SyncSegments is { Count: 2 } attachedSegments &&
      attachedSegments[0] == new CameraSyncSegment(0, 0, 4, 1) && attachedSegments[1] == new CameraSyncSegment(6, 4, 4, 1),
    "App AttachMappedAudio clips samples captured during a screen pause and creates two real SyncSegments");
FrameTrailProjectStore.Save(attachProjectPath, attachProject);
var attachRoundTrip = FrameTrailProjectStore.Load(attachProjectPath);
Check(attachRoundTrip.AudioTracks.Single().SyncSegments?.SequenceEqual(attachProject.AudioTracks.Single().SyncSegments!) == true,
    "App-created SyncSegments survive project save and reopen");
var attachedTrackScript = Path.Combine(outputDirectory, "attach-mapped-filter.txt");
var attachedAudioTrack = attachRoundTrip.AudioTracks.Single() with { MediaPath = attachMappedPath };
await File.WriteAllTextAsync(attachedTrackScript, MediaTools.AudioTrackFilter(attachRoundTrip.Timeline, [attachedAudioTrack]));
await RunFfmpeg(["-v", "error", "-i", attachMappedPath, "-filter_complex_script", attachedTrackScript, "-map", "[out]", "-c:a", "pcm_f32le", "-y", attachMappedMixPath]);
var attachSourcePcm = await DecodeFloatStereo(attachMappedPath);
var attachPcm = await DecodeFloatStereo(attachMappedMixPath);
var beforeScreenPause = Analyze(attachPcm, 1, 440, 880);
var afterScreenPause = Analyze(attachPcm, 5, 660, 880);
var attachSource440 = Analyze(attachSourcePcm, 1, 440, 880).System;
var attachSource880 = Analyze(attachSourcePcm, 5, 660, 880).Microphone;
var attachSource660 = Analyze(attachSourcePcm, 7, 660, 880).System;
Check(attachSource440 > 0 && attachSource660 > 0 && attachSource880 > 0 &&
      beforeScreenPause.System / attachSource440 > .9 && beforeScreenPause.Microphone / attachSource880 < .02 &&
      afterScreenPause.System / attachSource660 > .9 && afterScreenPause.Microphone / attachSource880 < .02,
    $"App-mapped/rendered audio preserves 440/660Hz relative to same-decode source baselines and excludes 880Hz pause material (source={attachSource440:0.0000}/{attachSource660:0.0000}/{attachSource880:0.0000}, rendered={beforeScreenPause.System:0.0000}/{beforeScreenPause.Microphone:0.0000}/{afterScreenPause.System:0.0000}/{afterScreenPause.Microphone:0.0000})");

var badRateResult = captureTrackResult with { AudioClockSegments = [new CaptureAudioClockSegment(0, 10_000, 0, 480_000, 44_100, 1)] };
try
{
    attachMethod.Invoke(null, [new FrameTrailProject { Media = new() { Duration = 8 } }, screenResult, badRateResult,
        "media/bad.wav", 10d, AudioTrackSource.System]);
    throw new InvalidOperationException("App accepted a per-segment sample rate that disagrees with the WAV sample rate.");
}
catch (TargetInvocationException exception) when (exception.InnerException is InvalidDataException) { }

var badClockResult = captureTrackResult with { Handle = captureHandle with { StopwatchFrequency = 999 } };
try
{
    attachMethod.Invoke(null, [new FrameTrailProject { Media = new() { Duration = 8 } }, screenResult, badClockResult,
        "media/bad-clock.wav", 10d, AudioTrackSource.System]);
    throw new InvalidOperationException("App accepted audio and screen clocks with different Stopwatch frequencies.");
}
catch (TargetInvocationException exception) when (exception.InnerException is InvalidDataException) { }

var oldMetadataProject = new FrameTrailProject { Media = new() { Duration = 8 } };
var oldMetadataResult = captureTrackResult with { AudioSampleRate = null, AudioClockSegments = null, AudioFramesWritten = null };
attachMethod.Invoke(null, [oldMetadataProject, screenResult, oldMetadataResult, "media/legacy.wav", 10d, AudioTrackSource.System]);
Check(oldMetadataProject.AudioTracks.Count == 2 && oldMetadataProject.AudioTracks[1].SourceStart == 6 &&
      oldMetadataProject.AudioTracks[1].OffsetSeconds == 4,
    "App AttachMappedAudio keeps the legacy continuous mapper for old capture metadata");

var first = new TimelineSegment { SourceStart = 0, SourceDuration = 2, AudioOverrides = new() { SystemVolume = .5, MicrophoneVolume = .5 } };
var second = new TimelineSegment { SourceStart = 2, SourceDuration = 2, AudioOverrides = new() { SystemMuted = true, MicrophoneVolume = 1 } };
var third = new TimelineSegment { SourceStart = 4, SourceDuration = 2, AudioOverrides = new() { SystemVolume = 1, MicrophoneMuted = true } };
var timeline = new ProjectTimeline { Segments = [first, second, third] };
var tracks = new AudioTrack[]
{
    new() { MediaPath = systemPath, SourceStart = 0, Duration = 6, OffsetSeconds = 0, Volume = 1, Source = AudioTrackSource.System },
    new() { MediaPath = microphonePath, SourceStart = 0, Duration = 6, OffsetSeconds = 0, Volume = 1, Source = AudioTrackSource.Microphone }
};
var filterScript = MediaTools.AudioTrackFilter(timeline, tracks);
var scriptPath = Path.Combine(outputDirectory, "audio-overrides-filter.txt");
await File.WriteAllTextAsync(scriptPath, filterScript);
await RunFfmpeg(["-v", "error", "-i", systemPath, "-i", microphonePath, "-filter_complex_script", scriptPath, "-map", "[out]", "-c:a", "pcm_f32le", "-y", mixPath]);

var decoded = await DecodeFloatStereo(mixPath);
var systemBaseline = Analyze(await DecodeFloatStereo(systemPath), 1, 440, 880).System;
var microphoneBaseline = Analyze(await DecodeFloatStereo(microphonePath), 1, 440, 880).Microphone;
var observations = new[]
{
    Analyze(decoded, 0.5, 440, 880),
    Analyze(decoded, 2.5, 440, 880),
    Analyze(decoded, 4.5, 440, 880)
};
var firstSystemRatio = observations[0].System / systemBaseline;
var firstMicrophoneRatio = observations[0].Microphone / microphoneBaseline;
Check(Math.Abs(firstSystemRatio - .5) < .05 && Math.Abs(firstMicrophoneRatio - .5) < .05,
    $"片段一两路音频均按50%音量输出（原始={systemBaseline:0.0000}/{microphoneBaseline:0.0000}，混音={observations[0].System:0.0000}/{observations[0].Microphone:0.0000}）");
Check(observations[1].System < systemBaseline * .02 && observations[1].Microphone > microphoneBaseline * .95, "片段二静音系统声音且麦克风保持输出");
Check(observations[2].System > systemBaseline * .95 && observations[2].Microphone < microphoneBaseline * .02, "片段三保留系统声音且静音麦克风");

var mappedTimeline = new ProjectTimeline { Segments =
[
    new() { SourceStart = 0, SourceDuration = 2 },
    new() { SourceStart = 2, SourceDuration = 2 },
    new() { SourceStart = 4, SourceDuration = 2 }
] };
var mappedTracks = new AudioTrack[]
{
    new() { MediaPath = systemPath, SourceStart = 0, Duration = 6, OffsetSeconds = 0, Source = AudioTrackSource.System },
    new()
    {
        MediaPath = mappedMicrophonePath, SourceStart = 0, Duration = 6, OffsetSeconds = 0,
        Source = AudioTrackSource.Microphone, SourceRate = 1,
        SyncSegments =
        [
            new(0, 0, 2, 1),
            new(2, 2, 2, .5),
            new(3, 4, 2, 1)
        ]
    }
};
var mappedScript = Path.Combine(outputDirectory, "sync-segments-filter.txt");
await File.WriteAllTextAsync(mappedScript, MediaTools.AudioTrackFilter(mappedTimeline, mappedTracks));
await RunFfmpeg(["-v", "error", "-i", systemPath, "-i", mappedMicrophonePath, "-filter_complex_script", mappedScript, "-map", "[out]", "-c:a", "pcm_f32le", "-y", mappedMixPath]);
var mappedPcm = await DecodeFloatStereo(mappedMixPath);
var mappedObservations = new[]
{
    Analyze(mappedPcm, 1, 440, 660),
    Analyze(mappedPcm, 3, 440, 990),
    Analyze(mappedPcm, 5, 440, 1320)
};
Check(mappedObservations.All(x => x.System > systemBaseline * .95 && x.Microphone > microphoneBaseline * .95),
    $"多段同步映射按各区间素材起点与SourceRate输出预期频率（{JsonSerializer.Serialize(mappedObservations.Select(x => new { x.System, x.Microphone }))}）");

var indexTracks = new AudioTrack[]
{
    new() { MediaPath = systemPath, SourceStart = 0, Duration = 6, OffsetSeconds = 0, Volume = 1, Muted = true, Source = AudioTrackSource.Other },
    new() { MediaPath = microphonePath, SourceStart = 0, Duration = 6, OffsetSeconds = 0, Volume = 1, Source = AudioTrackSource.Microphone }
};
var indexScript = Path.Combine(outputDirectory, "muted-input-index-filter.txt");
await File.WriteAllTextAsync(indexScript, MediaTools.AudioTrackFilter(mappedTimeline, indexTracks));
await RunFfmpeg(["-v", "error", "-i", systemPath, "-i", microphonePath, "-filter_complex_script", indexScript, "-map", "[out]", "-c:a", "pcm_f32le", "-y", indexMixPath]);
var indexObservation = Analyze(await DecodeFloatStereo(indexMixPath), 1, 440, 880);
Check(indexObservation.System < microphoneBaseline * .02 && indexObservation.Microphone > microphoneBaseline * .95,
    "静音普通轨排在前面时，后续麦克风音轨仍从正确FFmpeg输入解码");

await RunFfmpeg(["-v", "error", "-f", "lavfi", "-i", "color=c=black:s=320x240:r=30:d=6", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=6", "-map", "0:v", "-map", "1:a", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", "-y", exportScreenPath]);
File.Copy(systemPath, exportSystemPath, true);
File.Copy(microphonePath, exportMicrophonePath, true);
var exportTimeline = new ProjectTimeline { Segments = [
    new() { SourceStart = 0, SourceDuration = 2, AudioOverrides = new() { SystemVolume = .5, MicrophoneVolume = .5 } },
    new() { SourceStart = 2, SourceDuration = 2, AudioOverrides = new() { SystemMuted = true, MicrophoneVolume = 1 } },
    new() { SourceStart = 4, SourceDuration = 2, AudioOverrides = new() { SystemVolume = 1, MicrophoneMuted = true } }
] };
var exportProject = new FrameTrailProject
{
    MediaPath = "screen-with-system-audio.mp4",
    Media = new() { Width = 320, Height = 240, Duration = 6, FrameRate = 30 },
    Timeline = exportTimeline,
    AudioTracks =
    [
        new() { MediaPath = "media/system-440.wav", SourceStart = 0, Duration = 6, OffsetSeconds = 0, Source = AudioTrackSource.System },
        new() { MediaPath = "media/microphone-880.wav", SourceStart = 0, Duration = 6, OffsetSeconds = 0, Source = AudioTrackSource.Microphone }
    ]
};
await File.WriteAllTextAsync(exportProjectPath, "probe placeholder");
var exportResult = await new ExportEngine().ExportAsync(exportProject, exportScreenPath, exportMixPath,
    new ExportOptions(AudioOnly: true, ProjectFilePath: exportProjectPath));
var exportedPcm = await DecodeFloatStereo(exportResult.Path);
var exportedObservations = new[]
{
    Analyze(exportedPcm, .5, 440, 880),
    Analyze(exportedPcm, 2.5, 440, 880),
    Analyze(exportedPcm, 4.5, 440, 880)
};
Check(exportedObservations[0].System > .07 && exportedObservations[0].Microphone > .04, "ExportEngine M4A 第一段同时含源440Hz系统声与50%麦克风");
Check(exportedObservations[1].System < .01 && exportedObservations[1].Microphone > .07, "ExportEngine M4A 第二段按覆写静音系统声且保留麦克风");
Check(exportedObservations[2].System > .07 && exportedObservations[2].Microphone < .01, "ExportEngine M4A 第三段保留系统声且静音麦克风");

var splitTimeline = new ProjectTimeline { Segments = [new TimelineSegment { SourceStart = 0, SourceDuration = 4, AudioOverrides = new() { SystemVolume = .25, MicrophoneMuted = true } }] };
var split = splitTimeline.Split(splitTimeline.Segments[0].Id, 1.5);
Check(split.Left.AudioOverrides == split.Right.AudioOverrides && !ReferenceEquals(split.Left.AudioOverrides, split.Right.AudioOverrides), "拆分片段继承独立副本的音量覆写");
split.Left.AudioOverrides!.SystemVolume = .75;
Check(split.Right.AudioOverrides!.SystemVolume == .25, "拆分后修改一侧不会串改另一侧覆写");

var summary = new
{
    Passed = true,
    Filter = "per-segment-source-audio-overrides",
    Output = mixPath,
    DurationSeconds = (await MediaTools.ProbeAsync(mixPath)).Duration,
    FrequenciesHz = new[] { 440, 880 },
    SourceAmplitude = new { System = systemBaseline, Microphone = microphoneBaseline },
    FirstSegmentOutputRatio = new { System = firstSystemRatio, Microphone = firstMicrophoneRatio },
    Observations = observations.Select(x => new { x.System, x.Microphone }),
    SyncSegmentOutput = mappedMixPath,
    SyncSegmentDurationSeconds = (await MediaTools.ProbeAsync(mappedMixPath)).Duration,
    SyncSegmentExpectedMicrophoneHz = new[] { 660, 990, 1320 },
    SyncSegmentObservations = mappedObservations.Select(x => new { x.System, x.Microphone }),
    MutedPrecedingTrackOutput = indexMixPath,
    MutedPrecedingTrackObservation = new { indexObservation.System, indexObservation.Microphone },
    ExportEngineM4a = exportResult.Path,
    ExportEngineM4aDurationSeconds = (await MediaTools.ProbeAsync(exportResult.Path)).Duration,
    ExportEngineM4aObservations = exportedObservations.Select(x => new { x.System, x.Microphone }),
    AttachMappedAudioOutput = attachMappedMixPath,
    AttachMappedAudioSegments = attachRoundTrip.AudioTracks.Single().SyncSegments,
    AttachMappedAudioSamples = new
    {
        Source440 = attachSource440, Source660 = attachSource660, PauseSource880 = attachSource880,
        BeforeScreenPause = new { Tone440Hz = beforeScreenPause.System, ExcludedTone880Hz = beforeScreenPause.Microphone },
        AfterScreenPause = new { Tone660Hz = afterScreenPause.System, ExcludedTone880Hz = afterScreenPause.Microphone },
        RelativeRatios = new
        {
            Tone440Hz = beforeScreenPause.System / attachSource440,
            Tone660Hz = afterScreenPause.System / attachSource660,
            ExcludedTone880HzBeforePause = beforeScreenPause.Microphone / attachSource880,
            ExcludedTone880HzAfterPause = afterScreenPause.Microphone / attachSource880
        }
    },
    Assertions = new[] { "片段一两路音频均按50%音量输出", "片段二静音系统声音且麦克风保持输出", "片段三保留系统声音且静音麦克风", "多段SyncSegments按SourceStart与SourceRate输出对应素材时钟", "App AttachMappedAudio与屏幕有效区间逐段求交并保存/重开", "Render处理App生成的含source gap SyncSegments", "拒绝Capture segment采样率与WAV不一致", "旧CaptureTrackResult元数据回退到原mapper", "静音普通轨排在前面时FFmpeg输入索引仍与音轨一致", "ExportEngine M4A 按三段规则混合嵌入式系统声与独立麦克风", "拆分片段继承独立副本的音量覆写", "拆分后修改一侧不会串改另一侧覆写" }
};
var summaryPath = Path.Combine(outputDirectory, "audio-overrides.summary.json");
await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));

static (double System, double Microphone) Analyze(byte[] pcm, double centerSeconds, double systemHz, double microphoneHz)
{
    const int sampleRate = 48000, channels = 2;
    var windowSamples = sampleRate / 2;
    var start = (int)((centerSeconds - .25) * sampleRate);
    double Amplitude(double frequency)
    {
        Complex sum = Complex.Zero;
        for (var i = 0; i < windowSamples; i++)
        {
            var frame = start + i;
            var sampleOffset = (frame * channels) * sizeof(float);
            var sample = BitConverter.ToSingle(pcm, sampleOffset);
            var angle = -2 * Math.PI * frequency * i / sampleRate;
            sum += sample * Complex.FromPolarCoordinates(1, angle);
        }
        return 2 * sum.Magnitude / windowSamples;
    }
    return (Amplitude(systemHz), Amplitude(microphoneHz));
}

static async Task<byte[]> DecodeFloatStereo(string path)
{
    using var process = new Process { StartInfo = new ProcessStartInfo(MediaTools.Ffmpeg) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
    foreach (var argument in new[] { "-v", "error", "-i", path, "-f", "f32le", "-ac", "2", "-ar", "48000", "pipe:1" }) process.StartInfo.ArgumentList.Add(argument);
    if (!process.Start()) throw new InvalidOperationException("ffmpeg 未能启动。");
    using var buffer = new MemoryStream();
    var copy = process.StandardOutput.BaseStream.CopyToAsync(buffer);
    var error = process.StandardError.ReadToEndAsync();
    await copy; await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new InvalidOperationException(await error);
    return buffer.ToArray();
}

static Task RunFfmpeg(IReadOnlyList<string> arguments) => MediaTools.RunAsync(MediaTools.Ffmpeg, arguments);
static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("Probe failed: " + message); }
