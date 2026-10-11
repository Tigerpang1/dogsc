using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Security.Cryptography;
using FrameTrail;
using FrameTrail.App;
using FrameTrail.Media;

namespace FrameTrail.App.VisualProbe;

/// <summary>Measures the real MainWindow video playback path without showing a window or enabling audio.</summary>
internal static class PlaybackLatencyProbe
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Verify(MainWindow window, string output, string? sourceProject, bool enableAudio = false, int frameRate = 30, int previewWidth = 1440)
    {
        if (frameRate is not (30 or 60)) throw new ArgumentOutOfRangeException(nameof(frameRate));
        if (previewWidth is not (1440 or 2560 or 3840)) throw new ArgumentOutOfRangeException(nameof(previewWidth));
        Directory.CreateDirectory(output);
        var dispatcher = window.Dispatcher;
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var report = new Dictionary<string, object?>
        {
            ["Scope"] = enableAudio
                ? "MainWindow 真实视频与静音音频 proxy / MediaElement 路径；后台、未显示且无 HWND，Player.Volume=0 并 IsMuted=true；首次帧时间由 1ms Dispatcher 轮询新图像引用并确认 playbackRenderedFrames 大于零得到，属于上界。"
                : "MainWindow 真实视频播放路径；后台 App、未显示且无 HWND；真实项目仅在内存副本中禁用音频轨道。音频播放未测试。首帧轮询值为1ms Dispatcher采样上界，前进帧回调记录图像赋值并更新Playhead后的提交时间，不代表物理显示器呈现。",
            ["AudioTested"] = enableAudio,
            ["NoHwnd"] = true
        };
        var projectPath = Path.Combine(output, "playback-latency-project.frametrail");
        var sourcePath = Path.Combine(output, "playback-latency-testsrc.mp4");
        string? sourceHash = null;
        string? projectFileHashBefore = null;
        var generatedProxies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        FieldInfo? framePresentedHookField = null;
        object? originalFramePresentedHook = null;
        Action<double>? framePresentedHook = null;
        Stopwatch? advancingFrameClock = null;
        double advancingFrameStart = 0;
        double? inputToFirstForwardFrameCommitMs = null;
        double? expectedResumeTime = null;
        double? firstResumeOutputTime = null;
        double? firstResumeCommitMs = null;
        Stopwatch? resumeFrameClock = null;
        bool captureSustainedFrames = false;
        var sustainedSamples = new List<(double OutputTime, long Timestamp, double AudioSeconds)>();
        bool? proxiesRemovedByProduction = null;
        bool? playbackResourcesReleasedByProduction = null;
        try
        {
            RequireOffscreen(window);
            FrameTrailProject project;
            if (sourceProject is null)
            {
                var arguments = enableAudio
                    ? new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30:duration=8", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=8", "-map", "0:v:0", "-map", "1:a:0", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "64k", "-y", sourcePath }
                    : new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30:duration=8", "-an", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-y", sourcePath };
                Pump(dispatcher, MediaTools.RunAsync(MediaTools.Ffmpeg, arguments));
                project = new FrameTrailProject
                {
                    DisplayName = "Playback latency synthetic fixture",
                    MediaPath = Path.GetFileName(sourcePath),
                    Media = new MediaAsset { Width = 320, Height = 180, Duration = 8, FrameRate = 30 },
                    Timeline = new ProjectTimeline { Segments = [new TimelineSegment { SourceStart = 0, SourceDuration = 8 }] },
                    Settings = new FrameTrailSettings { MainAudioMuted = !enableAudio }
                };
                FrameTrailProjectStore.Save(projectPath, project);
            }
            else
            {
                var input = Path.GetFullPath(sourceProject);
                sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input)));
                project = FrameTrailProjectStore.Load(input);
                var source = FrameTrailProjectStore.ResolveMediaPath(input, project);
                if (enableAudio)
                {
                    project.Settings.MainAudioMuted = false;
                    foreach (var track in project.AudioTracks) track.Muted = false;
                    if (project.Camera is not null) project.Camera = project.Camera with { Muted = false, Volume = Math.Max(.8, project.Camera.Volume) };
                }
                else
                {
                    project.AudioTracks.Clear();
                    project.Settings.MainAudioMuted = true;
                    if (project.Camera is not null) project.Camera = project.Camera with { Muted = true };
                }
                // This is an in-memory clone. Keeping the original project location as the
                // asset root preserves relative image/camera paths without writing beside it.
                projectPath = input;
                sourcePath = source;
            }

            if (project.Timeline.Duration < 2) throw new InvalidDataException("Playback latency probe needs a project with at least two seconds of video.");
            projectFileHashBefore = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(projectPath)));
            if (!enableAudio)
            {
                project.Settings.MainAudioMuted = true;
                project.AudioTracks.Clear();
                if (project.Camera is not null) project.Camera = project.Camera with { Muted = true };
            }
            Set(window, "project", project);
            Set(window, "projectFile", projectPath);
            var player = Get<MediaElement>(window, "Player");
            player.Volume = 0;
            player.IsMuted = true;
            framePresentedHookField = typeof(MainWindow).GetField("PlaybackFramePresented", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                ?? throw new MissingFieldException(typeof(MainWindow).FullName, "PlaybackFramePresented");
            originalFramePresentedHook = framePresentedHookField.GetValue(window);
            framePresentedHook = outputTime =>
            {
                if (expectedResumeTime is not null && firstResumeOutputTime is null)
                {
                    firstResumeOutputTime = outputTime;
                    firstResumeCommitMs = resumeFrameClock?.Elapsed.TotalMilliseconds;
                }
                if (advancingFrameClock is not null && inputToFirstForwardFrameCommitMs is null && outputTime > advancingFrameStart + 1e-5)
                    inputToFirstForwardFrameCommitMs = advancingFrameClock.Elapsed.TotalMilliseconds;
                if (captureSustainedFrames)
                    sustainedSamples.Add((outputTime, Stopwatch.GetTimestamp(), player.Position.TotalSeconds));
            };
            framePresentedHookField.SetValue(window, framePresentedHook);
            Set(window, "previewResolution", previewWidth);
            Set(window, "previewFrameRate", frameRate);
            report["PreviewFrameRate"] = frameRate;
            report["RequestedPreviewWidth"] = previewWidth;

            var timelineView = (EditorTimelineControl)window.FindName("TimelineView");
            var timelineProjectCopy = Clone(project);
            timelineView.Project = timelineProjectCopy;
            timelineView.Refresh();
            var layoutWidth = Math.Max(1280, window.ActualWidth);
            var layoutHeight = Math.Max(240, window.ActualHeight);
            timelineView.Measure(new Size(layoutWidth, layoutHeight));
            timelineView.Arrange(new Rect(0, 0, layoutWidth, Math.Max(240, timelineView.DesiredSize.Height)));
            timelineView.UpdateLayout();
            report["OffscreenTimelineLayout"] = new
            {
                InMemoryProjectCopy = !ReferenceEquals(timelineView.Project, project),
                ProjectDurationSeconds = timelineView.Project?.Timeline.Duration,
                MeasuredWidth = timelineView.DesiredSize.Width,
                MeasuredHeight = timelineView.DesiredSize.Height,
                ArrangedWidth = timelineView.ActualWidth,
                ArrangedHeight = timelineView.ActualHeight,
                HasHwnd = new WindowInteropHelper(window).Handle != IntPtr.Zero
            };
            RequireOffscreen(window);
            var playhead = Get<Slider>(window, "Playhead");
            playhead.Maximum = project.Timeline.Duration;
            playhead.Value = 0;

            var startRows = new List<object>();
            var frameRows = new List<object>();
            var initialCount = Get<int>(window, "playbackRenderedFrames");
            var initialImage = CurrentImage(window);
            var start = Stopwatch.StartNew();
            var firstCall = StartMeasured(window);
            Pump(dispatcher, firstCall.Task);
            CaptureProxy(window, generatedProxies);
            var audioOpenCountAfterFirst = AudioOpenCount(window);
            var resources = typeof(MainWindow).GetField("playbackResources", Private)?.GetValue(window);
            report["AudioPathDiagnostic"] = new
            {
                PlaybackResourcesPresent = resources is not null,
                HasAudio = resources?.GetType().GetProperty("HasAudio")?.GetValue(resources),
                SourceHasAudio = resources?.GetType().GetProperty("Session")?.GetValue(resources)?.GetType().GetProperty("SourceHasAudio")?.GetValue(resources.GetType().GetProperty("Session")!.GetValue(resources)),
                AudioOpenCount = audioOpenCountAfterFirst,
                AudioSource = typeof(MainWindow).GetField("playbackAudioSource", Private)?.GetValue(window),
                PlayerSource = player.Source?.ToString()
            };
            if (enableAudio && audioOpenCountAfterFirst <= 0) throw new InvalidOperationException("Audio proxy did not open through MediaElement.");
            var inputReturnMs = start.Elapsed.TotalMilliseconds;
            WaitUntil(dispatcher, () => Get<int>(window, "playbackRenderedFrames") > initialCount && CurrentImage(window) is not null, TimeSpan.FromSeconds(20));
            var firstFrameMs = start.Elapsed.TotalMilliseconds;
            var firstImageSize = CurrentImage(window)!;
            report["ActualBitmapSize"] = new { PixelWidth = firstImageSize.PixelWidth, PixelHeight = firstImageSize.PixelHeight };
            frameRows.Add(new { Scenario = "first-frame", InputToFirstFrameUpperBoundMs = firstFrameMs, PollIntervalMs = 1, ImageAssigned = CurrentImage(window) is not null, ImageReferenceChanged = !ReferenceEquals(initialImage, CurrentImage(window)), BitmapPixelWidth = firstImageSize.PixelWidth, BitmapPixelHeight = firstImageSize.PixelHeight });
            startRows.Add(new { Scenario = "initial-start", InputToSynchronousReturnMs = firstCall.SynchronousReturnMs, InputToStartTaskCompletionMs = inputReturnMs, RenderedFramesAtReturn = Get<int>(window, "playbackRenderedFrames"), SessionGeneration = Generation(window) });
            RequireOffscreen(window);

            Pause(window);
            var pausedTime = playhead.Value;
            var pausedImage = CurrentImage(window);
            var pausedFrames = Get<int>(window, "playbackRenderedFrames");
            Pump(dispatcher, Task.Delay(180));
            var stable = playhead.Value == pausedTime && ReferenceEquals(pausedImage, CurrentImage(window)) && Get<int>(window, "playbackRenderedFrames") == pausedFrames;
            if (!stable) throw new InvalidOperationException("Pause changed the playhead, image, or rendered frame count.");
            frameRows.Add(new { Scenario = "paused-stability", StableForMs = 180, Playhead = pausedTime, RenderedFrames = pausedFrames, Passed = stable });

            for (var i = 1; i <= 3; i++)
            {
                var beforeTime = playhead.Value;
                var beforeImage = CurrentImage(window);
                var generationBeforeResume = Generation(window);
                var audioOpensBeforeResume = AudioOpenCount(window);
                start.Restart();
                advancingFrameStart = beforeTime;
                inputToFirstForwardFrameCommitMs = null;
                expectedResumeTime = beforeTime;
                firstResumeOutputTime = null;
                firstResumeCommitMs = null;
                advancingFrameClock = start;
                resumeFrameClock = start;
                var resumeCall = StartMeasured(window);
                Pump(dispatcher, resumeCall.Task);
                var returnMs = start.Elapsed.TotalMilliseconds;
                WaitUntil(dispatcher, () => firstResumeOutputTime is not null && Get<int>(window, "playbackRenderedFrames") > 0 && Get<bool>(window, "playing"), TimeSpan.FromSeconds(20));
                var firstResumedMs = firstResumeCommitMs ?? throw new InvalidOperationException("Resume callback did not capture its first frame commit time.");
                var resumedAtPausedFrame = Math.Abs(firstResumeOutputTime!.Value - beforeTime) <= 1e-7;
                if (!resumedAtPausedFrame)
                    throw new InvalidOperationException($"Resume cycle {i} first submitted outputTime {firstResumeOutputTime.Value:R} did not equal paused position {beforeTime:R}.");
                WaitUntil(dispatcher, () => playhead.Value > beforeTime + 1e-5 && Get<int>(window, "playbackRenderedFrames") > 0, TimeSpan.FromSeconds(20));
                var firstAdvanceMs = start.Elapsed.TotalMilliseconds;
                var firstForwardCommitMs = inputToFirstForwardFrameCommitMs;
                advancingFrameClock = null;
                if (firstForwardCommitMs is null) throw new InvalidOperationException($"Resume cycle {i} did not invoke PlaybackFramePresented for an advancing frame.");
                var advanced = playhead.Value > beforeTime && !ReferenceEquals(beforeImage, CurrentImage(window));
                if (!advanced) throw new InvalidOperationException($"Resume cycle {i} did not advance the video.");
                var generationAfterResume = Generation(window);
                var audioOpensAfterResume = AudioOpenCount(window);
                var generationStable = generationBeforeResume is null || generationBeforeResume == generationAfterResume;
                if (!generationStable) throw new InvalidOperationException($"Resume cycle {i} recreated the playback session.");
                var audioReused = !enableAudio || audioOpensBeforeResume == audioOpensAfterResume;
                if (!audioReused) throw new InvalidOperationException($"Resume cycle {i} reopened the audio proxy.");
                frameRows.Add(new { Scenario = "resume", Cycle = i, InputToSynchronousReturnMs = resumeCall.SynchronousReturnMs, InputToStartTaskCompletionMs = returnMs, InputToFirstFrameUpperBoundMs = firstResumedMs, FirstSubmittedOutputTime = firstResumeOutputTime, PausedPosition = beforeTime, FirstSubmissionMatchesPausedPosition = resumedAtPausedFrame, InputToFirstForwardFrameCommitMs = firstForwardCommitMs, InputToFirstPlayheadAdvanceUpperBoundMs = firstAdvanceMs, PollIntervalMs = 1, FrameCommitMeans = "PlaybackFramePresented在PreviewImage.Source赋值并更新Playhead后回调；不代表物理显示器呈现。", PlayheadBefore = beforeTime, PlayheadAfter = playhead.Value, SessionGenerationBefore = generationBeforeResume, SessionGenerationAfter = generationAfterResume, SessionReused = generationStable, AudioOpenCountBefore = audioOpensBeforeResume, AudioOpenCountAfter = audioOpensAfterResume, AudioProxyReused = audioReused, Passed = advanced && audioReused && resumedAtPausedFrame });
                expectedResumeTime = null;
                resumeFrameClock = null;
                Pause(window);
                Pump(dispatcher, Task.Delay(80));
            }

            // Change the project identity while stopped. A playback session bound to the
            // previous project object must not be reused for this run.
            Pause(window);
            var oldProject = Get<FrameTrailProject>(window, "project");
            var replacement = Clone(oldProject);
            replacement.DisplayName = "Playback latency replacement project";
            Set(window, "project", replacement);
            playhead.Value = Math.Min(.4, replacement.Timeline.Duration / 3);
            var beforeReplacementImage = CurrentImage(window);
            var oldGeneration = Generation(window);
            var audioOpensBeforeReplacement = AudioOpenCount(window);
            start.Restart();
            var replacementCall = StartMeasured(window);
            var replacementTask = replacementCall.Task;
            Pump(dispatcher, replacementTask);
            CaptureProxy(window, generatedProxies);
            var replacementReturnMs = start.Elapsed.TotalMilliseconds;
            WaitUntil(dispatcher, () => !ReferenceEquals(beforeReplacementImage, CurrentImage(window)) && Get<int>(window, "playbackRenderedFrames") > 0 && Get<bool>(window, "playing"), TimeSpan.FromSeconds(20));
            var newGeneration = Generation(window);
            var generationChanged = oldGeneration is null || newGeneration is null || oldGeneration != newGeneration;
            if (!generationChanged) throw new InvalidOperationException("Changed project reused the previous playback session.");
            frameRows.Add(new { Scenario = "changed-project", InputToSynchronousReturnMs = replacementCall.SynchronousReturnMs, InputToStartTaskCompletionMs = replacementReturnMs, NewProjectObjectUsed = ReferenceEquals(replacement, Get<FrameTrailProject>(window, "project")), SessionGenerationBefore = oldGeneration, SessionGenerationAfter = newGeneration, SessionRecreated = generationChanged, AudioOpenCountBefore = audioOpensBeforeReplacement, AudioOpenCountAfter = AudioOpenCount(window), FirstFrameObserved = !ReferenceEquals(beforeReplacementImage, CurrentImage(window)) });
            Pause(window);

            // Exercise immediate cancellation and restart while the first start may still
            // be initializing. This is diagnostic evidence; do not assume an immediate
            // second start succeeds if the implementation deliberately serializes starts.
            var raceImage = CurrentImage(window);
            var racePlayhead = playhead.Value;
            start.Restart();
            advancingFrameStart = racePlayhead;
            inputToFirstForwardFrameCommitMs = null;
            advancingFrameClock = start;
            var raceStart = StartMeasured(window);
            Pause(window);
            var immediateRestart = StartMeasured(window);
            Pump(dispatcher, immediateRestart.Task);
            var raceReturnedMs = start.Elapsed.TotalMilliseconds;
            var raceObserved = false;
            try
            {
                WaitUntil(dispatcher, () => !ReferenceEquals(raceImage, CurrentImage(window)) && Get<int>(window, "playbackRenderedFrames") > 0 && Get<bool>(window, "playing") && playhead.Value > racePlayhead + 1e-5, TimeSpan.FromSeconds(10));
                raceObserved = true;
            }
            catch (TimeoutException) { }
            var raceForwardCommitMs = inputToFirstForwardFrameCommitMs;
            advancingFrameClock = null;
            CaptureProxy(window, generatedProxies);
            frameRows.Add(new { Scenario = "rapid-play-pause-play", FirstStartSynchronousReturnMs = raceStart.SynchronousReturnMs, RestartSynchronousReturnMs = immediateRestart.SynchronousReturnMs, RestartTaskCompletionMs = raceReturnedMs, InputToFirstForwardFrameCommitMs = raceForwardCommitMs, FrameCommitMeans = "PlaybackFramePresented在PreviewImage.Source赋值并更新Playhead后回调；不代表物理显示器呈现。", RenderedFrameObserved = raceObserved, Passed = raceObserved && raceForwardCommitMs is not null, PlayingAfter = Get<bool>(window, "playing"), AudioOpenCount = AudioOpenCount(window) });
            report["StartMeasurements"] = startRows;
            report["PlaybackMeasurements"] = frameRows;
            if (!raceObserved) throw new InvalidOperationException("Rapid play-pause-play did not produce a frame from the final playback request.");
            var sustainedStart = playhead.Value;
            var audioPositionStart = player.Position;
            sustainedSamples.Clear();
            captureSustainedFrames = true;
            Pump(dispatcher, Task.Delay(TimeSpan.FromSeconds(3)));
            captureSustainedFrames = false;
            var sustainedAdvanced = playhead.Value > sustainedStart + .5;
            var audioClockAdvanced = !enableAudio || player.Position > audioPositionStart + TimeSpan.FromMilliseconds(500);
            var sustainedMetrics = SummarizeSustainedFrames(sustainedSamples, frameRate);
            report["SustainedFrameMetrics"] = sustainedMetrics;
            if (enableAudio && sustainedSamples.Count > 0)
            {
                var audioVideoDeltaMs = sustainedSamples.Select(sample => Math.Abs(sample.AudioSeconds - sample.OutputTime) * 1000).ToArray();
                var maxAvDeltaMs = audioVideoDeltaMs.Max();
                report["AudioVideoDifference"] = new { MaxAbsoluteMs = maxAvDeltaMs, P95AbsoluteMs = Percentile(audioVideoDeltaMs, .95), Samples = audioVideoDeltaMs.Length, LimitMs = 120 };
                if (maxAvDeltaMs >= 120) throw new InvalidOperationException($"Audio/video position difference exceeded 120 ms: max={maxAvDeltaMs:F1} ms.");
            }
            else report["AudioVideoDifference"] = null;
            frameRows.Add(new { Scenario = "sustained-playback", DurationMs = 3000, PlayheadBefore = sustainedStart, PlayheadAfter = playhead.Value, MediaElementPositionBefore = audioPositionStart, MediaElementPositionAfter = player.Position, SustainedFrameMetrics = sustainedMetrics, Passed = sustainedAdvanced && audioClockAdvanced });
            if (!sustainedAdvanced || !audioClockAdvanced) throw new InvalidOperationException("Sustained playback did not advance the video and audio clock as expected.");

            Pause(window);
            var endPosition = Math.Max(0, project.Timeline.Duration - .25);
            playhead.Value = endPosition;
            start.Restart();
            var endStart = StartMeasured(window);
            Pump(dispatcher, endStart.Task);
            WaitUntil(dispatcher, () => !Get<bool>(window, "playing"), TimeSpan.FromSeconds(8));
            var naturallyEndedAtEnd = playhead.Value >= playhead.Maximum - 1e-5;
            frameRows.Add(new { Scenario = "natural-end", StartPosition = endPosition, PlayheadAfter = playhead.Value, PlayheadMaximum = playhead.Maximum, PlayingAfterEnd = Get<bool>(window, "playing"), Passed = naturallyEndedAtEnd });
            if (!naturallyEndedAtEnd) throw new InvalidOperationException("Playback did not naturally stop at the timeline end.");
            start.Restart();
            var loopStart = StartMeasured(window);
            Pump(dispatcher, loopStart.Task);
            WaitUntil(dispatcher, () => Get<bool>(window, "playing") && playhead.Value < .5, TimeSpan.FromSeconds(5));
            WaitUntil(dispatcher, () => playhead.Value > .05, TimeSpan.FromSeconds(5));
            frameRows.Add(new { Scenario = "restart-after-natural-end", PlayheadAfterRestart = playhead.Value, PlayingAfterRestart = Get<bool>(window, "playing"), Passed = Get<bool>(window, "playing") && playhead.Value > .05 });
            Pause(window);
            typeof(MainWindow).GetMethod("ClosePlaybackAudio", Private)?.Invoke(window, null);
            CaptureOwnedProxies(window, generatedProxies);
            var dispose = typeof(MainWindow).GetMethod("DisposePlaybackResourcesAsync", Private)
                ?? throw new MissingMethodException(typeof(MainWindow).FullName, "DisposePlaybackResourcesAsync");
            if (dispose.Invoke(window, null) is Task disposeTask) Pump(dispatcher, disposeTask);
            proxiesRemovedByProduction = generatedProxies.All(path => !File.Exists(path));
            playbackResourcesReleasedByProduction = typeof(MainWindow).GetField("playbackResources", Private)?.GetValue(window) is null;
            var playerSourceCleared = player.Source is null;
            report["GeneratedAudioProxyCount"] = generatedProxies.Count;
            report["GeneratedAudioProxiesRemovedByProduction"] = proxiesRemovedByProduction;
            report["PlaybackResourcesReleasedByProduction"] = playbackResourcesReleasedByProduction;
            report["MediaElementSourceCleared"] = playerSourceCleared;
            if (proxiesRemovedByProduction != true || playbackResourcesReleasedByProduction != true || !playerSourceCleared)
                throw new InvalidOperationException("Playback/audio resources were not fully disposed by the probe.");
            Set(window, "closeReady", true);
            RequireOffscreen(window);
            report["Passed"] = true;
            report["PlayerVolumeDuringProbe"] = player.Volume;
            report["PlayerMutedDuringProbe"] = player.IsMuted;
            report["PlaybackAudioOpenCountAfterFirst"] = audioOpenCountAfterFirst;
            report["PlaybackAudioOpenCountFinal"] = AudioOpenCount(window);
            report["SyntheticVideo"] = sourceProject is null;
            report["ProjectClone"] = sourceProject is null ? null : "in-memory clone; original project path retained as the relative asset root";
            report["SourceProject"] = sourceProject is null ? null : Path.GetFullPath(sourceProject);
            report["SourceProjectSha256Before"] = sourceHash;
            report["SourceProjectSha256After"] = sourceProject is null ? null : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.GetFullPath(sourceProject))));
            report["SourceProjectUnchanged"] = sourceHash is null || sourceHash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.GetFullPath(sourceProject!))));
            report["MediaPath"] = sourcePath;
            report["TimelineDurationSeconds"] = project.Timeline.Duration;
            report["StartMeasurements"] = startRows;
            report["PlaybackMeasurements"] = frameRows;
            report["NoHwnd"] = new WindowInteropHelper(window).Handle == IntPtr.Zero;
            report["WindowVisible"] = window.IsVisible;
        }
        catch (Exception error)
        {
            report["Passed"] = false;
            report["Error"] = error.ToString();
            throw;
        }
        finally
        {
            try { Pause(window); } catch { }
            try { typeof(MainWindow).GetMethod("ClosePlaybackAudio", Private)?.Invoke(window, null); } catch { }
            CaptureOwnedProxies(window, generatedProxies);
            try
            {
                if (typeof(MainWindow).GetMethod("DisposePlaybackResourcesAsync", Private)?.Invoke(window, null) is Task disposeTask)
                    Pump(dispatcher, disposeTask);
            }
            catch { }
            proxiesRemovedByProduction ??= generatedProxies.All(path => !File.Exists(path));
            playbackResourcesReleasedByProduction ??= typeof(MainWindow).GetField("playbackResources", Private)?.GetValue(window) is null;
            var orphanProxies = generatedProxies.Where(File.Exists).ToArray();
            foreach (var proxy in orphanProxies) try { File.Delete(proxy); } catch (IOException) { }
            report["GeneratedAudioProxyCount"] = generatedProxies.Count;
            report["GeneratedAudioProxiesRemovedByProduction"] = proxiesRemovedByProduction;
            report["PlaybackResourcesReleasedByProduction"] = playbackResourcesReleasedByProduction;
            report["ProbeDeletedOrphanProxyCount"] = orphanProxies.Count(path => !File.Exists(path));
            report["GeneratedAudioProxiesAbsentAfterProbeCleanup"] = generatedProxies.All(path => !File.Exists(path));
            report["PlaybackFramePresentedHookAvailable"] = framePresentedHookField is not null;
            try { framePresentedHookField?.SetValue(window, originalFramePresentedHook); } catch { }
            try { Set(window, "closeReady", true); } catch { }
            var projectIntegrityFailed = false;
            if (sourceProject is not null)
            {
                try
                {
                    var finalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.GetFullPath(sourceProject))));
                    report["SourceProjectSha256Before"] = sourceHash;
                    report["SourceProjectSha256After"] = finalHash;
                    report["SourceProjectUnchanged"] = sourceHash == finalHash;
                    if (sourceHash != finalHash) projectIntegrityFailed = true;
                }
                catch (Exception hashError) { report["SourceProjectHashError"] = hashError.ToString(); projectIntegrityFailed = true; }
            }
            try
            {
                var finalProjectHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(projectPath)));
                report["ProjectFilePath"] = projectPath;
                report["ProjectFileSha256Before"] = projectFileHashBefore;
                report["ProjectFileSha256After"] = finalProjectHash;
                report["ProjectFileUnchanged"] = projectFileHashBefore == finalProjectHash;
                if (projectFileHashBefore != finalProjectHash) projectIntegrityFailed = true;
            }
            catch (Exception hashError) { report["ProjectFileHashError"] = hashError.ToString(); projectIntegrityFailed = true; }
            SynchronizationContext.SetSynchronizationContext(previousContext);
            if (projectIntegrityFailed)
            {
                report["Passed"] = false;
                report["Error"] = "Project file SHA-256 changed during the playback probe.";
            }
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            if (projectIntegrityFailed) throw new InvalidOperationException("Project file SHA-256 changed during the playback probe.");
        }
    }

    private static (Task Task, double SynchronousReturnMs) StartMeasured(MainWindow window)
    {
        var clock = Stopwatch.StartNew();
        var task = (Task)(typeof(MainWindow).GetMethod("StartRealtimePlayback", Private)!.Invoke(window, null) ?? throw new InvalidOperationException("Playback start returned no task."));
        return (task, clock.Elapsed.TotalMilliseconds);
    }
    private static void Pause(MainWindow window) => typeof(MainWindow).GetMethod("StopPlayback", Private)!.Invoke(window, null);
    private static object SummarizeSustainedFrames(IReadOnlyList<(double OutputTime, long Timestamp, double AudioSeconds)> samples, int frameRate)
    {
        var intervals = new double[Math.Max(0, samples.Count - 1)];
        var dropped = 0;
        var frames = new object[samples.Count];
        for (var i = 0; i < samples.Count; i++)
        {
            var elapsedMs = (samples[i].Timestamp - samples[0].Timestamp) * 1000d / Stopwatch.Frequency;
            double? intervalMs = null;
            int? outputFrameDelta = null;
            if (i > 0)
            {
                intervalMs = (samples[i].Timestamp - samples[i - 1].Timestamp) * 1000d / Stopwatch.Frequency;
                intervals[i - 1] = intervalMs.Value;
                outputFrameDelta = Math.Max(1, (int)Math.Round((samples[i].OutputTime - samples[i - 1].OutputTime) * frameRate));
                dropped += Math.Max(0, outputFrameDelta.Value - 1);
            }
            frames[i] = new { OutputTime = samples[i].OutputTime, ElapsedMs = elapsedMs, IntervalMs = intervalMs, OutputFrameDelta = outputFrameDelta, AudioPositionSeconds = samples[i].AudioSeconds };
        }
        var elapsedSeconds = samples.Count > 1 ? (samples[^1].Timestamp - samples[0].Timestamp) / (double)Stopwatch.Frequency : 0;
        return new
        {
            CallbackCount = samples.Count,
            MeasuredSubmittedFps = elapsedSeconds > 0 ? (samples.Count - 1) / elapsedSeconds : 0,
            P95CallbackGapMs = Percentile(intervals, .95),
            EstimatedDroppedFrames = dropped,
            EstimatedDroppedFramesMeans = "按相邻 PlaybackFramePresented outputTime 的帧率索引差估算，不代表物理显示器丢帧。",
            FrameCallbacks = frames
        };
    }
    private static double Percentile(double[] values, double percentile)
    {
        if (values.Length == 0) return 0;
        Array.Sort(values);
        var index = Math.Clamp((int)Math.Ceiling(percentile * values.Length) - 1, 0, values.Length - 1);
        return values[index];
    }
    private static BitmapSource? CurrentImage(MainWindow window) => ((Image)window.FindName("PreviewImage")).Source as BitmapSource;
    private static T Get<T>(MainWindow window, string name) => (T)(typeof(MainWindow).GetField(name, Private)!.GetValue(window) ?? throw new InvalidOperationException($"{name} is null."));
    private static void Set<T>(MainWindow window, string name, T value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
    private static int? Generation(MainWindow window) => typeof(MainWindow).GetField("playbackSessionGeneration", Private)?.GetValue(window) as int?;
    private static int AudioOpenCount(MainWindow window) => (int)(typeof(MainWindow).GetField("playbackAudioOpenCount", Private)?.GetValue(window) ?? 0);
    private static void CaptureProxy(MainWindow window, ISet<string> generatedProxies)
    {
        if (typeof(MainWindow).GetField("proxyPath", Private)?.GetValue(window) is string path && File.Exists(path)) generatedProxies.Add(path);
    }
    private static void CaptureOwnedProxies(MainWindow window, ISet<string> generatedProxies)
    {
        if (typeof(MainWindow).GetField("playbackOwnedProxies", Private)?.GetValue(window) is IEnumerable<string> owned)
            foreach (var path in owned) generatedProxies.Add(path);
        CaptureProxy(window, generatedProxies);
    }
    private static void RequireOffscreen(MainWindow window)
    {
        if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero || Application.Current.Windows.Count != 1)
            throw new InvalidOperationException("Playback latency probe requires exactly one unshown MainWindow and no HWND.");
    }
    private static FrameTrailProject Clone(FrameTrailProject project) => System.Text.Json.JsonSerializer.Deserialize<FrameTrailProject>(System.Text.Json.JsonSerializer.Serialize(project))!;

    private static void WaitUntil(Dispatcher dispatcher, Func<bool> condition, TimeSpan timeout)
    {
        var frame = new DispatcherFrame();
        var poll = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(1) };
        var limit = new DispatcherTimer(DispatcherPriority.Send, dispatcher) { Interval = timeout };
        poll.Tick += (_, _) => { if (condition()) { poll.Stop(); limit.Stop(); frame.Continue = false; } };
        limit.Tick += (_, _) => { poll.Stop(); limit.Stop(); frame.Continue = false; };
        poll.Start(); limit.Start();
        try { if (!condition()) Dispatcher.PushFrame(frame); }
        finally { poll.Stop(); limit.Stop(); }
        if (!condition()) throw new TimeoutException("Playback probe timed out waiting for a state change.");
    }

    private static void Pump(Dispatcher dispatcher, Action action)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1) };
        timer.Tick += (_, _) => { timer.Stop(); action(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
    }

    private static void Pump(Dispatcher dispatcher, Task task)
    {
        if (task.IsCompleted) { task.GetAwaiter().GetResult(); return; }
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => frame.Continue = false)));
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
}
