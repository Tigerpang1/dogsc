using System.Reflection;
using System.IO;
using System.Text.Json;
using FrameTrail;
using FrameTrail.Capture;
using FrameTrail.Media;

internal static class CapturedProjectProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static async Task RunAsync(string summaryPath, string outputDirectory)
    {
        summaryPath = Path.GetFullPath(summaryPath);
        outputDirectory = Path.GetFullPath(outputDirectory);
        if (!File.Exists(summaryPath)) throw new FileNotFoundException("Capture sync summary not found.", summaryPath);
        var json = await File.ReadAllTextAsync(summaryPath);
        var capture = JsonSerializer.Deserialize<CaptureSyncSummary>(json, JsonOptions)
            ?? throw new InvalidDataException("Capture sync summary is empty or invalid.");
        var screen = capture.ScreenResult ?? throw new InvalidDataException("Capture summary lacks the complete ScreenResult.");
        var audio = capture.AudioResult ?? throw new InvalidDataException("Capture summary lacks the complete AudioResult.");
        if (screen.FirstFrameObservedStopwatchTicks is not long firstFrameTicks)
            throw new InvalidDataException("Capture summary lacks ScreenResult.FirstFrameObservedStopwatchTicks; cannot establish source-time zero.");
        if (screen.StopwatchFrequency <= 0 || audio.Handle.StopwatchFrequency != screen.StopwatchFrequency)
            throw new InvalidDataException("Screen and audio Stopwatch frequencies are missing or inconsistent.");
        if (audio.AudioSampleRate is not > 0 || audio.AudioFramesWritten is not > 0 ||
            audio.AudioClockSegments is not { Count: > 0 } clockSegments)
            throw new InvalidDataException("Capture summary lacks AudioSampleRate, AudioFramesWritten, or complete AudioClockSegments; refusing to infer source-rate mapping from legacy summary fields.");

        var inputDirectory = Path.GetDirectoryName(summaryPath)!;
        var sourceVideo = ResolveInputPath(screen.OutputPath, inputDirectory);
        var sourceAudio = ResolveInputPath(audio.Handle.MediaPath, inputDirectory);
        if (!File.Exists(sourceVideo)) throw new FileNotFoundException("Captured screen video is missing.", sourceVideo);
        if (!File.Exists(sourceAudio)) throw new FileNotFoundException("Captured WAV is missing.", sourceAudio);

        var rawSummaryPath = Path.Combine(outputDirectory, "input.capture.sync-playback.json");
        if (Path.GetFullPath(rawSummaryPath).Equals(summaryPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Output directory must not overwrite the input capture summary.");
        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
            throw new IOException($"Refusing to write captured-project output into an existing path: {outputDirectory}");

        Directory.CreateDirectory(outputDirectory);

        var mediaDirectory = Path.Combine(outputDirectory, "media");
        Directory.CreateDirectory(mediaDirectory);
        var videoPath = Path.Combine(mediaDirectory, "screen.mp4");
        var wavPath = Path.Combine(mediaDirectory, "captured-audio.wav");
        File.Copy(sourceVideo, videoPath, overwrite: false);
        File.Copy(sourceAudio, wavPath, overwrite: false);
        File.Copy(summaryPath, rawSummaryPath, overwrite: false);

        var videoInfo = await MediaTools.ProbeAsync(videoPath);
        var audioInfo = await MediaTools.ProbeAsync(wavPath);
        if (!audioInfo.HasAudio || audioInfo.Duration <= 0 || videoInfo.Duration <= 0)
            throw new InvalidDataException("Copied capture media did not probe as a playable video and audio WAV.");

        var projectPath = Path.Combine(outputDirectory, "captured-project.frametrail");
        var audioRelativePath = "media/captured-audio.wav";
        var project = new FrameTrailProject
        {
            MediaPath = "media/screen.mp4",
            Media = new() { Width = videoInfo.Width, Height = videoInfo.Height, Duration = videoInfo.Duration, FrameRate = screen.FrameRate },
            Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = videoInfo.Duration }] }
        };

        var attachMethod = Assembly.Load("FrameTrail").GetType("FrameTrail.App.MainWindow")?
            .GetMethod("AttachMappedAudio", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("无法定位 App 的 AttachMappedAudio 实际入口。");
        var audioSource = audio.Handle.Kind == CaptureTrackKind.SystemAudio ? AudioTrackSource.System : AudioTrackSource.Microphone;
        attachMethod.Invoke(null, [project, screen, audio, audioRelativePath, audioInfo.Duration, audioSource]);
        if (project.AudioTracks.Count != 1 || project.AudioTracks[0].SyncSegments is not { Count: > 0 })
            throw new InvalidDataException("App AttachMappedAudio did not create mapped SyncSegments for the captured WAV.");

        FrameTrailProjectStore.Save(projectPath, project);
        var reopened = FrameTrailProjectStore.Load(projectPath);
        var reopenedTrack = reopened.AudioTracks.Single();
        if (reopenedTrack.SyncSegments is not { Count: > 0 } reopenedSegments ||
            !reopenedSegments.SequenceEqual(project.AudioTracks[0].SyncSegments!))
            throw new InvalidDataException("Captured project lost mapped audio SyncSegments on save/reopen.");

        var m4aPath = Path.Combine(outputDirectory, "captured-project-audio.m4a");
        var export = await new ExportEngine().ExportAsync(reopened, videoPath, m4aPath,
            new ExportOptions(Width: 320, Height: 180, FrameRate: screen.FrameRate is 30 or 60 ? screen.FrameRate : 30,
                AudioOnly: true, ProjectFilePath: projectPath));
        var exportedInfo = await MediaTools.ProbeAsync(export.Path);
        if (!exportedInfo.HasAudio || exportedInfo.Duration <= 0)
            throw new InvalidDataException("ExportEngine output did not contain a readable audio stream.");

        var analysisInputPath = Path.Combine(outputDirectory, "analysis-input.derived.json");
        var analysisInput = new
        {
            OutputPath = videoPath,
            AudioPath = export.Path,
            Fixture = capture.Fixture,
            StopwatchFrequency = screen.StopwatchFrequency,
            FirstFrameObservedStopwatchTicks = firstFrameTicks,
            AudioStartStopwatchTicks = firstFrameTicks,
            AudioStopwatchFrequency = screen.StopwatchFrequency,
            Derived = true,
            DerivedFromRawSummary = rawSummaryPath,
            TimingMeaning = "A/V was exported on the same project timeline; both analysis origins are explicitly set to the captured first-frame source-time anchor. This measures project-mapped/exported audio against captured screen content, while raw capture clocks remain in the preserved summary."
        };
        await File.WriteAllTextAsync(analysisInputPath, JsonSerializer.Serialize(analysisInput, JsonOptions));

        var report = new
        {
            Passed = true,
            Mode = "captured-project-audio-export",
            RawCaptureSummary = rawSummaryPath,
            CopiedVideo = videoPath,
            CopiedWav = wavPath,
            ProjectPath = projectPath,
            ProjectReopened = true,
            AudioTrack = new
            {
                reopenedTrack.MediaPath,
                reopenedTrack.SourceStart,
                reopenedTrack.Duration,
                reopenedTrack.OffsetSeconds,
                reopenedTrack.SourceRate,
                reopenedTrack.Source,
                reopenedTrack.SyncSegments
            },
            CaptureAudioMetadata = new
            {
                audio.AudioSampleRate,
                audio.AudioFramesWritten,
                audio.AudioSourceRate,
                AudioClockSegments = clockSegments
            },
            VideoProbe = new { videoInfo.Width, videoInfo.Height, videoInfo.Duration, screen.FrameRate },
            WavProbe = new { audioInfo.Duration, audioInfo.HasAudio },
            ExportedM4a = export.Path,
            ExportProbe = new { exportedInfo.Duration, exportedInfo.HasAudio },
            AnalysisInput = analysisInputPath,
            AnalysisInputIsDerived = true
        };
        var reportPath = Path.Combine(outputDirectory, "captured-project.summary.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, JsonOptions));
        Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
    }

    private sealed record CaptureSyncSummary(
        RecordingResult? ScreenResult,
        CaptureTrackResult? AudioResult,
        string? Fixture);

    private static string ResolveInputPath(string path, string baseDirectory) =>
        Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(path, baseDirectory);
}
