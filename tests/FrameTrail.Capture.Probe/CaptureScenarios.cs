using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using FrameTrail.Capture;

internal static class CaptureScenarios
{
    internal static async Task RunAsync(ProbeOptions options)
    {
        if (options.SyncStimulus) { await RunSyncStimulusAsync(options); return; }
        if (options.SyncCapture) { await RunSyncCaptureAsync(options); return; }
        if (options.SyncPlayback) { await RunSyncPlaybackAsync(options); return; }
        if (options.LongLoopback) { await RunLongLoopbackAsync(options); return; }
        if (options.AnimateWindow) { await RunAnimateWindowAsync(options); return; }
        if (options.EnumerateInputs) { await EnumerateInputsAsync(); return; }
        if (options.PauseCycle) { await RunPauseCycleAsync(options); return; }
        if (options.MultiPauseCycle) { await RunMultiPauseCycleAsync(options); return; }
        if (options.KillRecoveryTest) { await RunKillRecoveryTestAsync(options); return; }
        if (options.TrackTest) { await RunTrackTestAsync(options); return; }
        if (options.TargetTest) { await RunTargetTestAsync(options); return; }
        if (options.ExclusionTest) { await RunExclusionTestAsync(options); return; }
        throw new ArgumentException("No capture test scenario was selected.");
    }

    private static async Task EnumerateInputsAsync()
    {
        await using var capture = new FrameCaptureService();
        var displays = await capture.EnumerateDisplaysAsync();
        var windows = await capture.EnumerateWindowsAsync();
        var audio = await capture.EnumerateAudioDevicesAsync();
        await using var tracks = new CaptureTrackService();
        var cameras = await tracks.EnumerateCamerasAsync();
        var trackAudioDevices = await tracks.EnumerateAudioDevicesAsync();
        Console.WriteLine(JsonSerializer.Serialize(new { Displays = displays, WindowCount = windows.Count, AudioDevices = audio, TrackAudioDevices = trackAudioDevices, Cameras = cameras }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task RunPauseCycleAsync(ProbeOptions options)
    {
        var output = RequireOutput(options);
        await using var capture = new FrameCaptureService();
        var display = (await capture.EnumerateDisplaysAsync()).First(x => x.IsPrimary);
        capture.StateChanged += (_, e) => { Console.WriteLine($"{DateTimeOffset.UtcNow:O} {e.State} ticks={e.StopwatchTicks}"); Console.Out.Flush(); };
        await capture.StartAsync(new RecordingRequest(output, new CaptureTarget(CaptureTargetKind.Display, display.Id),
            new VideoOptions(Math.Clamp(options.FrameRate, 1, 120), options.Width, options.Height), new AudioOptions(), new InputTrackingOptions(false)));
        await Task.Delay(TimeSpan.FromSeconds(3));
        await capture.PauseAsync();
        await Task.Delay(TimeSpan.FromSeconds(3));
        await capture.ResumeAsync();
        await Task.Delay(TimeSpan.FromSeconds(3));
        var result = await capture.StopAsync();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            result.OutputPath, result.StartStopwatchTicks, result.StopStopwatchTicks, result.StopwatchFrequency,
            result.PausedIntervals, DurationSeconds = (result.StopStopwatchTicks - result.StartStopwatchTicks) / (double)result.StopwatchFrequency
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task RunMultiPauseCycleAsync(ProbeOptions options)
    {
        var output = RequireOutput(options);
        var wavPath = Path.ChangeExtension(output, ".wav");
        await using var capture = new FrameCaptureService();
        await using var tracks = new CaptureTrackService();
        var display = (await capture.EnumerateDisplaysAsync()).First(x => x.IsPrimary);
        var loopback = (await tracks.EnumerateAudioDevicesAsync()).FirstOrDefault(x => x.Kind == AudioDeviceKind.SystemLoopback && x.IsDefault)
            ?? throw new InvalidOperationException("No default loopback audio device was enumerated.");
        capture.StateChanged += (_, e) => { Console.WriteLine($"{DateTimeOffset.UtcNow:O} screen={e.State} ticks={e.StopwatchTicks}"); Console.Out.Flush(); };
        tracks.StateChanged += (_, e) => { Console.WriteLine($"{DateTimeOffset.UtcNow:O} audio={e.State} ticks={e.StopwatchTicks}"); Console.Out.Flush(); };
        var recording = new RecordingRequest(output, new CaptureTarget(CaptureTargetKind.Display, display.Id),
            new VideoOptions(Math.Clamp(options.FrameRate, 1, 120), options.Width, options.Height), new AudioOptions(), new InputTrackingOptions(false));
        await capture.StartAsync(recording);
        var audio = await tracks.StartAudioTrackAsync(new AudioTrackRequest(wavPath, AudioDeviceKind.SystemLoopback, loopback.Id));
        var actions = new List<object>();
        var segmentValidationDirectory = output + ".segments";
        Directory.CreateDirectory(segmentValidationDirectory);
        for (var i = 1; i <= 3; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            var pauseRequest = Stopwatch.GetTimestamp();
            await Task.WhenAll(capture.PauseAsync(), tracks.PauseTrackAsync(audio.TrackId));
            CopyFinalizedSegments(output, segmentValidationDirectory, i);
            var pauseDone = Stopwatch.GetTimestamp();
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            var resumeRequest = Stopwatch.GetTimestamp();
            await Task.WhenAll(capture.ResumeAsync(), tracks.ResumeTrackAsync(audio.TrackId));
            var resumeDone = Stopwatch.GetTimestamp();
            actions.Add(new { cycle = i, pauseRequest, pauseDone, resumeRequest, resumeDone, StopwatchFrequency = Stopwatch.Frequency });
        }
        await Task.Delay(TimeSpan.FromSeconds(1));
        // Finalize and retain the fourth segment too, then stop while paused to validate
        // that the result end tick does not include this trailing interval without media.
        await Task.WhenAll(capture.PauseAsync(), tracks.PauseTrackAsync(audio.TrackId));
        CopyFinalizedSegments(output, segmentValidationDirectory, 4);
        var screen = await capture.StopAsync();
        var wav = await tracks.StopTrackAsync(audio.TrackId);
        var summary = new
        {
            screen.OutputPath, screen.StartStopwatchTicks, screen.StopStopwatchTicks, screen.StopObservedStopwatchTicks,
            screen.FirstFrameObservedStopwatchTicks, screen.StopwatchFrequency,
            screen.PausedIntervals, ScreenMediaSeconds = screen.OutputPath is { } p ? GetMediaDuration(p) : 0,
            VideoSegments = Directory.EnumerateFiles(segmentValidationDirectory, "segment-*.mp4").OrderBy(x => x).Select(path => new { Path = path, Probe = GetMediaProbeLine(path) }).ToArray(),
            AudioPath = wav.Handle.MediaPath, AudioStartStopwatchTicks = wav.Handle.StartStopwatchTicks,
            AudioStopStopwatchTicks = wav.StopStopwatchTicks, AudioStopwatchFrequency = wav.Handle.StopwatchFrequency,
            AudioPausedIntervals = wav.PausedIntervals, AudioWallSeconds = Stopwatch.GetElapsedTime(wav.Handle.StartStopwatchTicks, wav.StopStopwatchTicks).TotalSeconds,
            Actions = actions
        };
        var summaryPath = Path.ChangeExtension(output, ".multi-pause.json");
        await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Multi-pause completed: video={screen.OutputPath}; wav={wav.Handle.MediaPath}; summary={summaryPath}");
        Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void CopyFinalizedSegments(string output, string destinationDirectory, int expectedCount)
    {
        var partsRoot = output + ".parts";
        var segments = Directory.Exists(partsRoot)
            ? Directory.EnumerateFiles(partsRoot, "segment-*.mp4", SearchOption.AllDirectories).Where(path => new FileInfo(path).Length > 0)
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
        if (segments.Length < expectedCount) throw new IOException($"Expected {expectedCount} finalized segments, found {segments.Length}.");
        foreach (var segment in segments.Take(expectedCount))
            File.Copy(segment, Path.Combine(destinationDirectory, Path.GetFileName(segment)), true);
    }

    private static async Task RunKillRecoveryTestAsync(ProbeOptions options)
    {
        var output = RequireOutput(options);
        var resultCompletion = new TaskCompletionSource<RecordingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var capture = new FrameCaptureService();
        capture.ErrorOccurred += (_, e) => { Console.Error.WriteLine($"{DateTimeOffset.UtcNow:O} {e.Message} {e.Exception}"); Console.Error.Flush(); };
        capture.StateChanged += (_, e) => { Console.WriteLine($"{DateTimeOffset.UtcNow:O} {e.State} ticks={e.StopwatchTicks} detail={e.Detail}"); Console.Out.Flush(); };
        capture.RecordingCompleted += result => resultCompletion.TrySetResult(result);
        var display = (await capture.EnumerateDisplaysAsync()).First(x => x.IsPrimary);
        await capture.StartAsync(new RecordingRequest(output, new CaptureTarget(CaptureTargetKind.Display, display.Id),
            new VideoOptions(30, 1280, 720), new AudioOptions(), new InputTrackingOptions(false)));
        await Task.Delay(TimeSpan.FromSeconds(2));
        await capture.PauseAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        await capture.ResumeAsync();
        await Task.Delay(TimeSpan.FromSeconds(2));

        var childId = capture.CurrentCaptureProcessId ?? throw new InvalidOperationException("No active FFmpeg capture child was exposed.");
        using (var child = Process.GetProcessById(childId))
        {
            child.Refresh();
            if (!string.Equals(Path.GetFileName(child.MainModule?.FileName), "ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Refusing to terminate unexpected process {childId}: {child.MainModule?.FileName}");
            Console.WriteLine($"Terminating owned FFmpeg child PID={childId}, path={child.MainModule?.FileName}, start={child.StartTime:O}");
            child.Kill(entireProcessTree: true);
        }

        RecordingResult? result = null;
        try { result = await resultCompletion.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { Console.WriteLine($"Auto-finalization timed out; state={capture.State}; manifest candidates are still scanned."); }
        var parentDirectory = Path.GetDirectoryName(output)!;
        var recoveries = FrameCaptureService.FindRecoverableRecordings(parentDirectory)
            .Where(x => string.Equals(Path.GetFullPath(x.OriginalOutputPath), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase)).ToArray();
        string? recovered = null;
        if (recoveries.FirstOrDefault() is { } recovery)
        {
            recovered = Path.Combine(parentDirectory, Path.GetFileNameWithoutExtension(output) + "-recovered.mp4");
            await FrameCaptureService.RecoverReadableRecordingAsync(recovery, recovered);
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            State = capture.State, childId, Result = result, Recoveries = recoveries, RecoveredPath = recovered,
            RecoveredBytes = recovered is not null && File.Exists(recovered) ? new FileInfo(recovered).Length : 0
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static double GetMediaDuration(string path)
    {
        var ffprobe = Environment.GetEnvironmentVariable("FRAMETRAIL_FFPROBE");
        if (string.IsNullOrWhiteSpace(ffprobe)) ffprobe = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator).Select(folder => Path.Combine(folder, "ffprobe.exe")).FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(ffprobe) || !File.Exists(ffprobe))
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages", "Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe");
            ffprobe = Directory.Exists(root) ? Directory.GetDirectories(root, "ffmpeg-*-full_build").OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(folder => Path.Combine(folder, "bin", "ffprobe.exe")).FirstOrDefault(File.Exists) : null;
        }
        if (string.IsNullOrWhiteSpace(ffprobe) || !File.Exists(ffprobe)) return 0;
        using var process = Process.Start(new ProcessStartInfo(ffprobe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, ArgumentList = { "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path } });
        if (process is null) return 0;
        var text = process.StandardOutput.ReadToEnd(); process.WaitForExit();
        return double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var duration) ? duration : 0;
    }

    private static string GetMediaProbeLine(string path)
    {
        var ffprobe = Environment.GetEnvironmentVariable("FRAMETRAIL_FFPROBE");
        if (string.IsNullOrWhiteSpace(ffprobe)) ffprobe = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator).Select(folder => Path.Combine(folder, "ffprobe.exe")).FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(ffprobe) || !File.Exists(ffprobe))
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages", "Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe");
            ffprobe = Directory.Exists(root) ? Directory.GetDirectories(root, "ffmpeg-*-full_build").OrderByDescending(folder => folder, StringComparer.OrdinalIgnoreCase)
                .Select(folder => Path.Combine(folder, "bin", "ffprobe.exe")).FirstOrDefault(File.Exists) : null;
        }
        if (string.IsNullOrWhiteSpace(ffprobe) || !File.Exists(ffprobe)) return "ffprobe unavailable";
        using var process = Process.Start(new ProcessStartInfo(ffprobe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "-v", "error", "-show_entries", "stream=codec_name,width,height,r_frame_rate,nb_frames,duration", "-of", "csv=p=0", path } });
        if (process is null) return "ffprobe could not start";
        var text = process.StandardOutput.ReadToEnd(); process.WaitForExit();
        return process.ExitCode == 0 ? text.Trim() : process.StandardError.ReadToEnd().Trim();
    }

    private static async Task RunTrackTestAsync(ProbeOptions options)
    {
        var output = RequireOutput(options);
        await using var tracks = new CaptureTrackService();
        tracks.StateChanged += (_, e) => { Console.WriteLine($"{DateTimeOffset.UtcNow:O} Track={e.TrackId} Kind={e.Kind} State={e.State} ticks={e.StopwatchTicks} detail={e.Detail}"); Console.Out.Flush(); };
        tracks.ErrorOccurred += (_, e) => Console.Error.WriteLine($"CaptureError={e.Message} {e.Exception}");
        var cameras = await tracks.EnumerateCamerasAsync();
        Console.WriteLine($"Enumerated cameras={cameras.Count}; count only; no camera stream opened.");
        foreach (var camera in cameras) Console.WriteLine($"Camera {camera.Name} id={camera.Id}; formats={string.Join("; ", camera.Formats.Select(f => $"{f.Width}x{f.Height}@{f.FrameRate:F2}fps index={f.Index}"))}");
        var audio = await tracks.EnumerateAudioDevicesAsync();
        var loopback = audio.FirstOrDefault(x => x.Kind == AudioDeviceKind.SystemLoopback && x.IsDefault);
        if (loopback is null) { Console.WriteLine("No default loopback device; audio track capture skipped."); return; }
        var handle = await tracks.StartAudioTrackAsync(new AudioTrackRequest(output, AudioDeviceKind.SystemLoopback, loopback.Id));
        var tonePath = Path.Combine(Path.GetDirectoryName(output)!, "frametrail-capture-lowlevel-tone.wav");
        WriteTone(tonePath, 3, 0.025);
        NativeAudio.PlaySound(tonePath, IntPtr.Zero, 0x00020001);
        await Task.Delay(TimeSpan.FromMilliseconds(1500));
        await tracks.PauseTrackAsync(handle.TrackId);
        NativeAudio.PlaySound(null, IntPtr.Zero, 1);
        await Task.Delay(TimeSpan.FromSeconds(1));
        await tracks.ResumeTrackAsync(handle.TrackId);
        NativeAudio.PlaySound(tonePath, IntPtr.Zero, 0x00020001);
        await Task.Delay(TimeSpan.FromMilliseconds(1500));
        NativeAudio.PlaySound(null, IntPtr.Zero, 1);
        var result = await tracks.StopTrackAsync(handle.TrackId);
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task RunLongLoopbackAsync(ProbeOptions options)
    {
        var output = RequireOutput(options);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await using var tracks = new CaptureTrackService();
        var devices = await tracks.EnumerateAudioDevicesAsync();
        var device = devices.FirstOrDefault(d => d.Kind == AudioDeviceKind.SystemLoopback && d.IsDefault)
            ?? throw new InvalidOperationException("No default WASAPI loopback endpoint was enumerated.");
        tracks.StateChanged += (_, e) => { Console.WriteLine($"{DateTimeOffset.UtcNow:O} Track={e.TrackId} State={e.State} ticks={e.StopwatchTicks} detail={e.Detail}"); Console.Out.Flush(); };
        tracks.ErrorOccurred += (_, e) => { Console.Error.WriteLine($"{DateTimeOffset.UtcNow:O} CaptureError={e.Message}; exception={e.Exception}"); Console.Error.Flush(); };
        var metricsPath = Path.ChangeExtension(output, ".metrics.csv");
        await using var metrics = new StreamWriter(new FileStream(metricsPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        await metrics.WriteLineAsync("utc,elapsed_seconds,wav_bytes,working_set_mib,private_mib,cpu_percent_of_machine,threads");
        var handle = await tracks.StartAudioTrackAsync(new AudioTrackRequest(output, device.Kind, device.Id));
        var startedUtc = DateTimeOffset.UtcNow;
        Console.WriteLine($"Loopback device={device.Name}; output={output}; handleStartTicks={handle.StartStopwatchTicks}; frequency={handle.StopwatchFrequency}; duration={options.DurationSeconds}s; metrics={metricsPath}"); Console.Out.Flush();
        using var process = Process.GetCurrentProcess();
        process.Refresh(); var lastCpu = process.TotalProcessorTime; var lastTicks = Stopwatch.GetTimestamp();
        for (var elapsed = 0d; elapsed < options.DurationSeconds;)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, options.DurationSeconds - elapsed)));
            process.Refresh(); var now = Stopwatch.GetTimestamp(); var cpu = process.TotalProcessorTime;
            var wall = Stopwatch.GetElapsedTime(lastTicks, now).TotalSeconds;
            elapsed = Stopwatch.GetElapsedTime(handle.StartStopwatchTicks, now).TotalSeconds;
            var cpuPct = wall <= 0 ? 0 : (cpu - lastCpu).TotalSeconds / (wall * Environment.ProcessorCount) * 100;
            var bytes = File.Exists(output) ? new FileInfo(output).Length : 0;
            var line = string.Join(',', DateTimeOffset.UtcNow.ToString("O"), elapsed.ToString("F3", System.Globalization.CultureInfo.InvariantCulture), bytes,
                (process.WorkingSet64 / 1048576d).ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
                (process.PrivateMemorySize64 / 1048576d).ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
                cpuPct.ToString("F2", System.Globalization.CultureInfo.InvariantCulture), process.Threads.Count);
            await metrics.WriteLineAsync(line); Console.WriteLine($"Audio telemetry elapsed={elapsed:F0}s; wav={bytes}; ws={process.WorkingSet64 / 1048576d:F1}MiB; private={process.PrivateMemorySize64 / 1048576d:F1}MiB; cpu={cpuPct:F2}%; threads={process.Threads.Count}"); Console.Out.Flush();
            lastTicks = now; lastCpu = cpu;
        }
        var result = await tracks.StopTrackAsync(handle.TrackId);
        var summary = new { result.Handle, result.StopStopwatchTicks, result.Width, result.Height, result.FrameRate, EncoderMode = result.EncoderMode.ToString(), result.PausedIntervals, result.Warnings, result.Diagnostics,
            DurationSeconds = (result.StopStopwatchTicks - handle.StartStopwatchTicks) / (double)handle.StopwatchFrequency, StartedUtc = startedUtc, StoppedUtc = DateTimeOffset.UtcNow,
            OutputPath = output, OutputBytes = File.Exists(output) ? new FileInfo(output).Length : 0, MetricsPath = metricsPath, Device = device.Name };
        var summaryPath = Path.ChangeExtension(output, ".summary.json");
        await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Loopback completed: {output}; bytes={summary.OutputBytes}; duration={summary.DurationSeconds:F3}s; summary={summaryPath}"); Console.Out.Flush();
    }

    private static async Task RunAnimateWindowAsync(ProbeOptions options)
    {
        var bounds = new Rectangle(1400, 500, 960, 540);
        await using var window = await PatternWindowHost.StartAsync(bounds, animate: true);
        Console.WriteLine($"Animated desktop test window active: ({bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}) for {options.DurationSeconds}s; HWND=0x{window.Handle.ToInt64():X}"); Console.Out.Flush();
        await Task.Delay(TimeSpan.FromSeconds(options.DurationSeconds));
        await window.CloseAsync();
        Console.WriteLine("Animated desktop test window closed."); Console.Out.Flush();
    }

    private static async Task RunTargetTestAsync(ProbeOptions options)
    {
        var output = RequireOutput(options);
        static object BinaryFile(string path) => new { Path = path,
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))) };
        var binaryEvidence = new { Probe = BinaryFile(typeof(CaptureScenarios).Assembly.Location),
            Capture = BinaryFile(typeof(FrameCaptureService).Assembly.Location),
            Recorder = BinaryFile(System.Reflection.Assembly.Load("ScreenRecorderLib").Location) };
        await using var capture = new FrameCaptureService();
        var display = (await capture.EnumerateDisplaysAsync()).First(x => x.IsPrimary);
        var width = Math.Min(640, display.Bounds.Width - 160);
        var height = Math.Min(360, display.Bounds.Height - 160);
        if (width < 160 || height < 120) throw new InvalidOperationException("The primary display is too small for the window-target WGC test.");
        var start = new Point(display.Bounds.X + Math.Max(80, Math.Min(2400, display.Bounds.Width - width - 80)),
            display.Bounds.Y + Math.Max(80, Math.Min(1400, display.Bounds.Height - height - 80)));
        await using var window = await PatternWindowHost.StartAsync(new Rectangle(start.X, start.Y, width, height), colorBars: true);
        var expectedSourceBounds = new Rectangle(start.X, start.Y, width, height);
        var displayBounds = new Rectangle(display.Bounds.X, display.Bounds.Y, display.Bounds.Width, display.Bounds.Height);
        if (window.Bounds != expectedSourceBounds || !displayBounds.Contains(window.Bounds))
            throw new InvalidOperationException($"The controlled source must occupy its requested physical rectangle entirely on the display; requested={expectedSourceBounds}, actual={window.Bounds}, display={displayBounds}.");
        capture.StateChanged += (_, e) => { Console.WriteLine($"{DateTimeOffset.UtcNow:O} {e.State} ticks={e.StopwatchTicks} detail={e.Detail}"); Console.Out.Flush(); };
        var captureErrors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        capture.ErrorOccurred += (_, e) => { captureErrors.Enqueue(e.Message); Console.WriteLine($"ErrorOccurred={e.Message}"); Console.Out.Flush(); };
        string? completedEventPath = null;
        var completedCount = 0;
        capture.RecordingCompleted += result => { completedEventPath = result.OutputPath; Interlocked.Increment(ref completedCount); Console.WriteLine($"RecordingCompleted={result.OutputPath}"); Console.Out.Flush(); };

        var windowRequest = new RecordingRequest(output,
            new CaptureTarget(CaptureTargetKind.Window, window.Handle.ToInt64().ToString("X"), WindowHandle: window.Handle),
            new VideoOptions(30, width, height, EncoderPreference.Software), new AudioOptions(),
            new InputTrackingOptions(true, 60, Path.ChangeExtension(output, ".window-input.jsonl")));
        var phaseLog = new List<object>();
        var testTimer = Stopwatch.StartNew();
        var targetSummaryPath = Path.ChangeExtension(output, ".target.json");
        void Phase(string name, object? details = null)
        {
            var item = new { Name = name, ElapsedSeconds = testTimer.Elapsed.TotalSeconds, Utc = DateTimeOffset.UtcNow, Details = details };
            phaseLog.Add(item);
            var progress = new { Utc = DateTimeOffset.UtcNow, Test = "WindowRecordingSource遮挡/移动/缩放验收", BinaryEvidence = binaryEvidence, PhaseLog = phaseLog };
            File.WriteAllText(targetSummaryPath, JsonSerializer.Serialize(progress, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(item)); Console.Out.Flush();
        }
        var affinityNone = await window.SetDisplayAffinityAsync(0);
        if (!affinityNone.SetSucceeded || !affinityNone.ReadSucceeded || affinityNone.Actual != 0)
            throw new InvalidOperationException($"The controlled source HWND must have WDA_NONE before capture: {JsonSerializer.Serialize(affinityNone)}");
        Phase("source-wda-none-confirmed", affinityNone);
        Phase("window-recording-starting", new { Handle = $"0x{window.Handle.ToInt64():X}", window.Bounds, width, height });
        await capture.StartAsync(windowRequest);
        Phase("window-recording-started", new { Handle = $"0x{window.Handle.ToInt64():X}", window.Bounds, width, height });
        var recordingStartedTicks = Stopwatch.GetTimestamp();
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        var beforeCoverActionTicks = Stopwatch.GetTimestamp();
        var cover = await PatternWindowHost.StartAsync(window.Bounds, solidRed: true);
        await using var coverLifetime = cover;
        cover.BringToTopWithoutActivating();
        var coverShownTicks = Stopwatch.GetTimestamp();
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        var coverCloseTicks = Stopwatch.GetTimestamp();
        var coverBounds = cover.Bounds;
        Phase("opaque-cover-shown", new { Handle = $"0x{cover.Handle.ToInt64():X}", coverBounds, SourceBounds = window.Bounds });
        await cover.CloseAsync();
        await Task.Delay(TimeSpan.FromSeconds(0.8));
        var moved = new Point(start.X + 120, start.Y + 60);
        await window.MoveToAsync(moved);
        await window.ResizeToAsync(width - 40, height - 20);
        var resizeCompleteTicks = Stopwatch.GetTimestamp();
        await Task.Delay(TimeSpan.FromSeconds(2.0));
        var stoppingTicks = Stopwatch.GetTimestamp();
        Phase("window-moved-and-resized", new { moved, Bounds = window.Bounds });
        var windowRect = window.Bounds;
        var result = await capture.StopAsync();
        var windowOutputEvent = completedEventPath;
        Phase("window-recording-stopped", new { result.OutputPath, result.Width, result.Height, result.FirstFrameObservedStopwatchTicks,
            result.FirstFrameNumber, result.FirstFrameSystemTimestampUnixMilliseconds });
        await window.CloseAsync();

        var ffmpeg = ResolveFfmpegExe();
        var videoProbe = GetMediaProbeLine(output);
        var duration = GetMediaDuration(output);
        var failures = new List<string>();
        var phaseSamples = new Dictionary<string, string>(StringComparer.Ordinal);
        var firstFrameTicks = result.FirstFrameObservedStopwatchTicks;
        if (firstFrameTicks is null || result.StopwatchFrequency <= 0)
            failures.Add("The recording result did not provide a first-image Stopwatch anchor and frequency.");
        double ToVideoSeconds(long ticks) => firstFrameTicks is long anchor
            ? (ticks - anchor) / (double)result.StopwatchFrequency
            : double.NaN;
        var beforeCover = ToVideoSeconds(recordingStartedTicks + (beforeCoverActionTicks - recordingStartedTicks) / 2);
        var duringCover = ToVideoSeconds(coverShownTicks + (coverCloseTicks - coverShownTicks) / 2);
        var afterResize = ToVideoSeconds(resizeCompleteTicks + (stoppingTicks - resizeCompleteTicks) / 2);
        var sampleTimes = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["beforeCover"] = beforeCover, ["duringCover"] = duringCover, ["afterResize"] = afterResize
        };
        var expectedPalette = new[]
        {
            (Name: "topCyan", X: width / 4, Y: height / 6, R: 20, G: 160, B: 220),
            (Name: "middleGreen", X: width / 4, Y: height / 2, R: 30, G: 190, B: 50),
            (Name: "bottomOrange", X: width / 4, Y: height * 5 / 6, R: 180, G: 80, B: 30)
        };
        var blackNegativeControlRejected = expectedPalette.All(swatch => !IsNearRgb((0, 0, 0), swatch.R, swatch.G, swatch.B, 40));
        if (!blackNegativeControlRejected) throw new InvalidOperationException("The color assertion accepted a pure-black negative control.");
        async Task ValidatePaletteAsync(string phase, double atSeconds)
        {
            if (!double.IsFinite(atSeconds) || atSeconds < 0.15 || atSeconds > duration - 0.4)
            {
                failures.Add($"The {phase} sample time {atSeconds:F3}s is outside a safe frame range for {duration:F3}s media.");
                return;
            }
            foreach (var swatch in expectedPalette)
            {
                try
                {
                    var pixel = await ReadPixelAtVideoAsync(ffmpeg, output, atSeconds, swatch.X, swatch.Y);
                    phaseSamples[$"{phase}.{swatch.Name}"] = pixel;
                    if (!IsNearRgb(ParseRgb(pixel), swatch.R, swatch.G, swatch.B, 40))
                        failures.Add($"The {phase} {swatch.Name} sample at {atSeconds:F3}s differs from the source color: {pixel}.");
                }
                catch (Exception ex)
                {
                    phaseSamples[$"{phase}.{swatch.Name}"] = "decode-error";
                    failures.Add($"Could not decode {phase} {swatch.Name} pixel at {atSeconds:F3}s: {ex.Message}");
                }
            }
        }
        await ValidatePaletteAsync("beforeCover", beforeCover);
        await ValidatePaletteAsync("duringCover", duringCover);
        await ValidatePaletteAsync("afterResize", afterResize);
        var covered = coverBounds.Width == width && coverBounds.Height == height && coverBounds == new Rectangle(start.X, start.Y, width, height);
        if (!File.Exists(output) || new FileInfo(output).Length == 0) failures.Add("Window capture did not produce a non-empty video file.");
        if (duration < 4.5) failures.Add($"Window capture duration is unexpectedly short: {duration:F3}s.");
        if (!HasExpectedVideoProbe(videoProbe, width, height)) failures.Add($"ffprobe did not confirm an H.264 {width}x{height} video stream: {videoProbe}");
        if (!covered) failures.Add("The red occlusion window did not cover the source test window bounds.");
        if (!string.Equals(completedEventPath, result.OutputPath, StringComparison.OrdinalIgnoreCase)) failures.Add("RecordingCompleted did not report the same output as StopAsync.");
        var targetSummary = new
        {
            Utc = DateTimeOffset.UtcNow, OperatingSystem = Environment.OSVersion.VersionString, BinaryEvidence = binaryEvidence,
            SourceType = "ScreenRecorderLib.WindowRecordingSource", SourceHandle = $"0x{window.Handle.ToInt64():X}",
            WdaNone = affinityNone,
            InitialBounds = new Rectangle(start.X, start.Y, width, height), CoverBounds = coverBounds, FinalBounds = windowRect,
            Move = moved, Resize = new { Width = width - 40, Height = height - 20 }, Output = output,
            RecordingResult = result, RecordingCompletedOutput = windowOutputEvent, DurationSeconds = duration,
            Probe = videoProbe, Samples = phaseSamples, SampleTimes = sampleTimes, BlackNegativeControlRejected = blackNegativeControlRejected,
            PhaseLog = phaseLog, Failures = failures
        };
        await File.WriteAllTextAsync(targetSummaryPath, JsonSerializer.Serialize(targetSummary, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Window WGC target report={targetSummaryPath}; duration={duration:F3}s; probe={videoProbe}; samples={JsonSerializer.Serialize(phaseSamples)}.");
        if (failures.Count > 0) throw new InvalidOperationException("Window WGC target test failed: " + string.Join(" | ", failures));

        string? expectedClosedTargetError = null;
        try
        {
            var invalidPath = Path.ChangeExtension(output, ".closed-window.mp4");
            await capture.StartAsync(windowRequest with { OutputPath = invalidPath });
            try { await capture.StopAsync(); } catch { }
            throw new InvalidOperationException("Closed target before start was accepted; expected request validation to reject it.");
        }
        catch (ArgumentException ex)
        {
            if (!ex.Message.Contains("Window handle is no longer valid or is minimized", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The closed-window start failed for an unrelated reason, so handle rejection was not verified.", ex);
            expectedClosedTargetError = ex.Message;
            Phase("closed-target-rejected", new { ExceptionType = ex.GetType().Name, Message = ex.Message });
        }

        await using var closingWindow = await PatternWindowHost.StartAsync(new Rectangle(start.X, start.Y, width, height));
        var closingPath = Path.ChangeExtension(output, ".closed-during-recording.mp4");
        var beforeClosedCount = Volatile.Read(ref completedCount);
        await capture.StartAsync(windowRequest with { OutputPath = closingPath,
            Target = new CaptureTarget(CaptureTargetKind.Window, closingWindow.Handle.ToInt64().ToString("X"), WindowHandle: closingWindow.Handle) });
        await Task.Delay(TimeSpan.FromSeconds(1));
        await closingWindow.CloseAsync();
        await WaitForAutoCompletionAsync(() => Volatile.Read(ref completedCount), beforeClosedCount, TimeSpan.FromSeconds(10));
        var closedResult = capture.LatestCompletedResult;
        if (capture.State != CaptureState.Completed || closedResult is null || !string.Equals(completedEventPath, closingPath, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(closingPath) || new FileInfo(closingPath).Length == 0)
            throw new InvalidOperationException("Closing a captured window did not auto-finalize a non-empty recording and raise RecordingCompleted.");
        var closedOutputEvent = completedEventPath;
        Phase("window-close-auto-finalized", new { State = capture.State, Output = closedResult.OutputPath, Event = completedEventPath });

        await using var minimizingWindow = await PatternWindowHost.StartAsync(new Rectangle(start.X, start.Y, width, height));
        var minimizingPath = Path.ChangeExtension(output, ".minimized-during-recording.mp4");
        var beforeMinimizedCount = Volatile.Read(ref completedCount);
        await capture.StartAsync(windowRequest with { OutputPath = minimizingPath,
            Target = new CaptureTarget(CaptureTargetKind.Window, minimizingWindow.Handle.ToInt64().ToString("X"), WindowHandle: minimizingWindow.Handle) });
        await Task.Delay(TimeSpan.FromSeconds(1));
        await minimizingWindow.MinimizeAsync();
        await WaitForAutoCompletionAsync(() => Volatile.Read(ref completedCount), beforeMinimizedCount, TimeSpan.FromSeconds(10));
        var minimizedResult = capture.LatestCompletedResult;
        if (capture.State != CaptureState.Completed || minimizedResult is null || !string.Equals(completedEventPath, minimizingPath, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(minimizingPath) || new FileInfo(minimizingPath).Length == 0)
            throw new InvalidOperationException("Minimizing a captured window did not auto-finalize a non-empty recording and raise RecordingCompleted.");
        var minimizedOutputEvent = completedEventPath;
        Phase("window-minimize-auto-finalized", new { State = capture.State, Output = minimizedResult.OutputPath, Event = completedEventPath });
        await minimizingWindow.CloseAsync();

        var regionOutput = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "-region.mp4");
        await using var regionWindow = await PatternWindowHost.StartAsync(new Rectangle(start.X, start.Y, width, height));
        var bounds = regionWindow.Bounds;
        await capture.StartAsync(new RecordingRequest(regionOutput,
            new CaptureTarget(CaptureTargetKind.Region, display.Id,
                new PixelRect(bounds.X, bounds.Y, bounds.Width, bounds.Height)),
            new VideoOptions(30, width, height, EncoderPreference.Software), new AudioOptions(),
            new InputTrackingOptions(true, 60, Path.ChangeExtension(regionOutput, ".input.jsonl"))));
        await Task.Delay(TimeSpan.FromSeconds(5));
        var regionResult = await capture.StopAsync();
        await regionWindow.CloseAsync();
        Phase("region-recording-completed", new { Output = regionResult.OutputPath, regionResult.Width, regionResult.Height });
        Console.WriteLine($"Region target capture completed; physical bounds={bounds}; output={regionResult.OutputPath}; output={regionResult.Width}x{regionResult.Height}.");
        var regionProbe = GetMediaProbeLine(regionOutput);
        if (!File.Exists(regionOutput) || new FileInfo(regionOutput).Length == 0 || !HasExpectedVideoProbe(regionProbe, width, height)
            || GetMediaDuration(regionOutput) < 4 || !string.Equals(completedEventPath, regionOutput, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Region DDA regression did not produce the expected decodable video: {regionProbe}");
        var unexpectedErrors = captureErrors.Where(message => message != "The captured window was closed; recording is being finalized."
            && message != "The captured window was minimized; recording is being finalized."
            && message != expectedClosedTargetError).ToArray();
        var finalTargetSummary = new
        {
            Utc = DateTimeOffset.UtcNow, OperatingSystem = Environment.OSVersion.VersionString,
            Test = "ScreenRecorderLib WindowRecordingSource with controlled pattern and red occlusion window", BinaryEvidence = binaryEvidence,
            WindowCapture = new { Output = output, SourceHandle = $"0x{window.Handle.ToInt64():X}", InitialBounds = new Rectangle(start.X, start.Y, width, height),
                WdaNone = affinityNone,
                CoverBounds = coverBounds, FinalBounds = windowRect, Move = moved, Resize = new { Width = width - 40, Height = height - 20 },
                DurationSeconds = duration, Probe = videoProbe, Samples = phaseSamples, SampleTimes = sampleTimes, BlackNegativeControlRejected = blackNegativeControlRejected,
                RecordingCompletedOutput = windowOutputEvent, Failures = failures },
            ClosedTargetRejected = true, ClosedDuringRecording = new { Output = closedResult.OutputPath, Event = closedOutputEvent },
            MinimizedDuringRecording = new { Output = minimizedResult.OutputPath, Event = minimizedOutputEvent },
            RegionRegression = new { Output = regionResult.OutputPath, regionResult.Width, regionResult.Height, Probe = regionProbe },
            CaptureErrors = captureErrors.ToArray(), UnexpectedErrors = unexpectedErrors, PhaseLog = phaseLog
        };
        await File.WriteAllTextAsync(targetSummaryPath, JsonSerializer.Serialize(finalTargetSummary, new JsonSerializerOptions { WriteIndented = true }));
        if (unexpectedErrors.Length > 0)
            throw new InvalidOperationException("Window target test reported unrelated capture failures: " + string.Join(" | ", unexpectedErrors));
    }

    private static bool HasExpectedVideoProbe(string probe, int width, int height)
    {
        var fields = probe.Split(',', StringSplitOptions.TrimEntries);
        return fields.Length >= 3 && fields[0].Equals("h264", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(fields[1], out var actualWidth) && actualWidth == width
            && int.TryParse(fields[2], out var actualHeight) && actualHeight == height;
    }

    private static async Task WaitForAutoCompletionAsync(Func<int> count, int previousCount, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        while (count() == previousCount && timer.Elapsed < timeout) await Task.Delay(50);
        if (count() == previousCount) throw new TimeoutException("The captured window did not auto-finalize before the timeout.");
    }

    private static async Task RunExclusionTestAsync(ProbeOptions options)
    {
        var output = RequireOutput(options);
        var directory = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(directory);
        await using var capture = new FrameCaptureService();
        var display = (await capture.EnumerateDisplaysAsync()).FirstOrDefault(x => x.IsPrimary)
            ?? throw new InvalidOperationException("No primary display was enumerated.");
        const int width = 320, height = 180;
        var x = display.Bounds.X + Math.Min(640, Math.Max(0, display.Bounds.Width - width));
        var y = display.Bounds.Y + Math.Min(300, Math.Max(0, display.Bounds.Height - height));
        var bounds = new Rectangle(x, y, width, height);
        if (width > display.Bounds.Width || height > display.Bounds.Height)
            throw new InvalidOperationException("The primary display is too small for the exclusion test ROI.");
        await using var window = await PatternWindowHost.StartAsync(bounds, solidRed: true);
        window.BringToTopWithoutActivating();
        await Task.Delay(300);

        var ffmpeg = ResolveFfmpegExe();
        var region = new PixelRect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        var affinityNone = await window.SetDisplayAffinityAsync(0);
        var noneVideo = Path.Combine(directory, Path.GetFileNameWithoutExtension(output) + "-affinity-none.mp4");
        await CaptureRegionSampleAsync(capture, display, region, noneVideo);
        var noneDda = await ReadCenterPixelFromVideoAsync(ffmpeg, noneVideo, width, height);
        var noneGdi = await CaptureGdiCenterPixelAsync(ffmpeg, bounds);

        var affinityExcluded = await window.SetDisplayAffinityAsync(0x11);
        await Task.Delay(300);
        var excludedVideo = Path.Combine(directory, Path.GetFileNameWithoutExtension(output) + "-affinity-excluded.mp4");
        await CaptureRegionSampleAsync(capture, display, region, excludedVideo);
        var excludedDda = await ReadCenterPixelFromVideoAsync(ffmpeg, excludedVideo, width, height);
        var excludedGdi = await CaptureGdiCenterPixelAsync(ffmpeg, bounds);

        var summary = new
        {
            Utc = DateTimeOffset.UtcNow,
            OperatingSystem = Environment.OSVersion.VersionString,
            WindowHandle = $"0x{window.Handle.ToInt64():X}",
            CaptureBounds = bounds,
            WindowColor = "#FF0000",
            AffinityNone = affinityNone,
            AffinityExcludeFromCapture = affinityExcluded,
            FrameTrailDdagrab = new { BaselineCenterRgb = noneDda, ExcludedCenterRgb = excludedDda, BaselineVideo = noneVideo, ExcludedVideo = excludedVideo },
            FfmpegGdiGrab = new { BaselineCenterRgb = noneGdi, ExcludedCenterRgb = excludedGdi, Input = "desktop" },
            Note = "RGB values are the first pixel of a 2x2 crop aligned on the center of the controlled pure-red window ROI; 1x1 crop is unsupported by the H.264 4:2:0 decoder. WDA is best-effort window capture affinity, not a security boundary."
        };
        var summaryPath = Path.ChangeExtension(output, ".exclusion.json");
        await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Window-exclusion sample completed; summary={summaryPath}");
    }

    private static async Task CaptureRegionSampleAsync(FrameCaptureService capture, DisplayInfo display, PixelRect bounds, string output)
    {
        await capture.StartAsync(new RecordingRequest(output,
            new CaptureTarget(CaptureTargetKind.Region, display.Id, bounds),
            new VideoOptions(30, bounds.Width, bounds.Height), new AudioOptions(), new InputTrackingOptions(false)));
        await Task.Delay(TimeSpan.FromSeconds(1));
        await capture.StopAsync();
    }

    private static async Task<string> ReadCenterPixelFromVideoAsync(string ffmpeg, string path, int width, int height)
    {
        var pixel = await RunRawPixelCommandAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-i", path,
            "-frames:v", "1", "-vf", $"crop=2:2:{width / 2}:{height / 2}", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"]);
        return FormatRgb(pixel);
    }

    private static async Task<string> ReadPixelAtVideoAsync(string ffmpeg, string path, double seconds, int x, int y)
    {
        var pixel = await RunRawPixelCommandAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-ss",
            seconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture), "-i", path,
            "-frames:v", "1", "-vf", $"crop=2:2:{x}:{y}", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"]);
        return FormatRgb(pixel);
    }

    private static (int R, int G, int B) ParseRgb(string value) =>
        value.Length == 7 && value[0] == '#'
            ? (Convert.ToInt32(value.Substring(1, 2), 16), Convert.ToInt32(value.Substring(3, 2), 16), Convert.ToInt32(value.Substring(5, 2), 16))
            : throw new FormatException($"Unexpected RGB sample: {value}");

    private static bool IsNearRgb((int R, int G, int B) actual, int red, int green, int blue, int tolerance) =>
        Math.Abs(actual.R - red) <= tolerance && Math.Abs(actual.G - green) <= tolerance && Math.Abs(actual.B - blue) <= tolerance;

    private static async Task<string> CaptureGdiCenterPixelAsync(string ffmpeg, Rectangle bounds)
    {
        var pixel = await RunRawPixelCommandAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-f", "gdigrab", "-framerate", "10",
            "-draw_mouse", "0", "-offset_x", bounds.X.ToString(), "-offset_y", bounds.Y.ToString(),
            "-video_size", $"{bounds.Width}x{bounds.Height}", "-i", "desktop", "-frames:v", "1",
            "-vf", $"crop=2:2:{bounds.Width / 2}:{bounds.Height / 2}", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"]);
        return FormatRgb(pixel);
    }

    private static async Task<byte[]> RunRawPixelCommandAsync(string executable, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {executable}.");
        var stderrTask = process.StandardError.ReadToEndAsync();
        var pixel = new byte[12];
        try { await process.StandardOutput.BaseStream.ReadExactlyAsync(pixel).AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch { try { if (!process.HasExited) process.Kill(true); } catch { } throw; }
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var stderr = await stderrTask;
        if (process.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(executable)} failed (exit {process.ExitCode}): {stderr}");
        return pixel;
    }

    private static string FormatRgb(byte[] pixel) => $"#{pixel[0]:X2}{pixel[1]:X2}{pixel[2]:X2}";

    private static string ResolveFfmpegExe()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim('"'), "ffmpeg.exe");
            if (File.Exists(candidate)) return candidate;
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var winget = Path.Combine(local, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(winget))
            foreach (var candidate in Directory.EnumerateFiles(winget, "ffmpeg.exe", SearchOption.AllDirectories))
                if (candidate.Contains("Gyan.FFmpeg_", StringComparison.OrdinalIgnoreCase)) return candidate;
        throw new FileNotFoundException("ffmpeg.exe was not found on PATH or in the Gyan FFmpeg WinGet package.");
    }

    private static async Task RunSyncStimulusAsync(ProbeOptions options)
    {
        var duration = TimeSpan.FromSeconds(options.DurationSeconds);
        var tonePath = Path.Combine(Path.GetTempPath(), "frametrail-sync-pulse.wav");
        WriteTone(tonePath, 1, 0.03);
        var bounds = new Rectangle(400, 300, 960, 540);
        var window = await PatternWindowHost.StartAsync(bounds, animate: true);
        var logPath = options.OutputPath ?? Path.Combine(Environment.CurrentDirectory, "sync-stimulus.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        await using var writer = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        await writer.WriteLineAsync("pulse,utc,stopwatch_ticks,frequency,scheduled_elapsed_seconds");
        var start = Stopwatch.GetTimestamp();
        var nextPulse = start;
        var interval = TimeSpan.FromSeconds(10);
        var pulse = 0;
        Console.WriteLine($"Sync stimulus active for {duration.TotalSeconds:F0}s; topmost test window=({bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}); low-volume 440Hz pulse every 10s; pulseLog={logPath}");
        Console.Out.Flush();
        while (Stopwatch.GetElapsedTime(start) < duration)
        {
            nextPulse += (long)(interval.TotalSeconds * Stopwatch.Frequency);
            var wait = nextPulse - Stopwatch.GetTimestamp();
            if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait / (double)Stopwatch.Frequency));
            var ticks = Stopwatch.GetTimestamp();
            var elapsed = (ticks - start) / (double)Stopwatch.Frequency;
            pulse++;
            await window.SetFlashAsync(true);
            NativeAudio.PlaySound(tonePath, IntPtr.Zero, 0x00020001);
            await writer.WriteLineAsync($"{pulse},{DateTimeOffset.UtcNow:O},{ticks},{Stopwatch.Frequency},{elapsed:F6}");
            Console.WriteLine($"pulse={pulse}; elapsed={elapsed:F3}s; ticks={ticks}"); Console.Out.Flush();
            await Task.Delay(TimeSpan.FromMilliseconds(120));
            await window.SetFlashAsync(false);
            NativeAudio.PlaySound(null, IntPtr.Zero, 1);
        }
        await window.CloseAsync();
        Console.WriteLine($"Sync stimulus completed; pulses={pulse}; log={logPath}");
    }

    private static async Task RunSyncCaptureAsync(ProbeOptions options)
    {
        var output = RequireOutput(options);
        var audioPath = Path.ChangeExtension(output, ".wav");
        var pulsePath = Path.ChangeExtension(output, ".pulses.csv");
        var tonePath = Path.Combine(Path.GetDirectoryName(output)!, "sync-capture-440hz.wav");
        WriteTone(tonePath, 1, 0.03);
        var bounds = new Rectangle(400, 300, 960, 540);
        await using var window = await PatternWindowHost.StartAsync(bounds);
        window.FocusForTestCapture();
        await using var capture = new FrameCaptureService();
        await using var tracks = new CaptureTrackService();
        var displays = await capture.EnumerateDisplaysAsync();
        var display = displays.FirstOrDefault(d => d.IsPrimary) ?? throw new InvalidOperationException("No primary display was enumerated.");
        if (bounds.Left < display.Bounds.X || bounds.Top < display.Bounds.Y || bounds.Right > display.Bounds.X + display.Bounds.Width || bounds.Bottom > display.Bounds.Y + display.Bounds.Height)
            throw new InvalidOperationException("The sync window is not fully inside the primary display.");
        var loopback = (await tracks.EnumerateAudioDevicesAsync()).FirstOrDefault(d => d.Kind == AudioDeviceKind.SystemLoopback && d.IsDefault)
            ?? throw new InvalidOperationException("No default WASAPI loopback device was enumerated.");
        await capture.StartAsync(new RecordingRequest(output,
            new CaptureTarget(CaptureTargetKind.Region, display.Id, new PixelRect(bounds.X, bounds.Y, bounds.Width, bounds.Height)),
            new VideoOptions(FrameRate: 60, Width: bounds.Width, Height: bounds.Height), new AudioOptions(), new InputTrackingOptions(false)));
        var audio = await tracks.StartAudioTrackAsync(new AudioTrackRequest(audioPath, AudioDeviceKind.SystemLoopback, loopback.Id));
        await using var pulseWriter = new StreamWriter(new FileStream(pulsePath, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        await pulseWriter.WriteLineAsync("pulse,utc,stopwatch_ticks,frequency");
        var stimulusStart = Stopwatch.GetTimestamp();
        const int pulseCount = 5;
        for (var pulse = 1; pulse <= pulseCount; pulse++)
        {
            if (pulse > 1)
            {
                var due = stimulusStart + (long)((pulse - 1) * 4d * Stopwatch.Frequency);
                var wait = due - Stopwatch.GetTimestamp();
                if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait / (double)Stopwatch.Frequency));
            }
            var ticks = Stopwatch.GetTimestamp();
            await window.SetFlashAsync(true);
            NativeAudio.PlaySound(tonePath, IntPtr.Zero, 0x00020001);
            await pulseWriter.WriteLineAsync($"{pulse},{DateTimeOffset.UtcNow:O},{ticks},{Stopwatch.Frequency}");
            Console.WriteLine($"Sync pulse {pulse}/{pulseCount}; ticks={ticks}"); Console.Out.Flush();
            await Task.Delay(TimeSpan.FromMilliseconds(120));
            await window.SetFlashAsync(false);
            NativeAudio.PlaySound(null, IntPtr.Zero, 1);
        }
        var recording = await capture.StopAsync();
        var audioResult = await tracks.StopTrackAsync(audio.TrackId);
        var summary = new
        {
            OutputPath = recording.OutputPath,
            recording.StartStopwatchTicks,
            recording.StopStopwatchTicks,
            recording.StopwatchFrequency,
            recording.FirstFrameObservedStopwatchTicks,
            recording.FirstFrameNumber,
            recording.EncoderName,
            recording.EncoderMode,
            recording.Width,
            recording.Height,
            recording.FrameRate,
            recording.Diagnostics,
            AudioPath = audioResult.Handle.MediaPath,
            AudioStartStopwatchTicks = audioResult.Handle.StartStopwatchTicks,
            AudioStopStopwatchTicks = audioResult.StopStopwatchTicks,
            AudioStopwatchFrequency = audioResult.Handle.StopwatchFrequency,
            recording.Warnings,
            PulsePath = pulsePath,
            StartedUtc = DateTimeOffset.UtcNow.ToString("O")
        };
        var summaryPath = Path.ChangeExtension(output, ".summary.json");
        await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Sync capture completed: video={recording.OutputPath}; wav={audioResult.Handle.MediaPath}; summary={summaryPath}; pulseCsv={pulsePath}");
    }

    private static async Task RunSyncPlaybackAsync(ProbeOptions options)
    {
        var output = RequireOutput(options);
        var fixture = options.FixturePath ?? throw new ArgumentException("--fixture is required for --sync-playback.");
        if (!File.Exists(fixture)) throw new FileNotFoundException("Sync fixture was not found.", fixture);
        var ffplay = ResolveFfplay();
        var audioPath = Path.ChangeExtension(output, ".wav");
        await using var capture = new FrameCaptureService();
        await using var tracks = new CaptureTrackService();
        var display = (await capture.EnumerateDisplaysAsync()).FirstOrDefault(x => x.IsPrimary) ?? throw new InvalidOperationException("No primary display was enumerated.");
        var loopback = (await tracks.EnumerateAudioDevicesAsync()).FirstOrDefault(x => x.Kind == AudioDeviceKind.SystemLoopback && x.IsDefault)
            ?? throw new InvalidOperationException("No default loopback endpoint was enumerated.");
        var bounds = new PixelRect(options.RegionX, options.RegionY, options.Width ?? 320, options.Height ?? 180);
        var target = options.RegionPlayback
            ? new CaptureTarget(CaptureTargetKind.Region, display.Id, bounds)
            : new CaptureTarget(CaptureTargetKind.Display, display.Id);
        var video = new RecordingRequest(output, target,
            new VideoOptions(FrameRate: 60, Width: options.RegionPlayback ? bounds.Width : 1920, Height: options.RegionPlayback ? bounds.Height : 1080), new AudioOptions(), new InputTrackingOptions(false));
        CaptureTrackHandle? audio = null;
        Task<string>? stderrTask = null;
        var ffplayStderrPath = Path.ChangeExtension(output, ".ffplay.stderr.txt");
        var foregroundSampleCount = 0;
        var foregroundObservedCount = 0;
        var foregroundTransitionCount = 0;
        double? firstForegroundElapsedSeconds = null;
        uint? inputActivityBaselineTick = null;
        uint? lastObservedInputActivityTick = null;
        var inputActivitySampleCount = 0;
        double? inputActivityChangeElapsedSeconds = null;
        PlaybackSessionVolumeLease? playbackSessionLease = null;
        PlaybackSessionVolumeResult? playbackSessionVolumeResult = null;
        var playbackSessionObservations = new List<PlaybackSessionVolumeObservation>();
        using var player = new Process { StartInfo = new ProcessStartInfo(ffplay)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
            ArgumentList = { "-autoexit", "-alwaysontop", "-noborder", "-hide_banner", "-loglevel", "error" }
        }};
        if (options.LoopPlayback) foreach (var argument in new[] { "-loop", "0" }) player.StartInfo.ArgumentList.Add(argument);
        var ownsPlaybackSession = options.RestoreOwnPlaybackSession || options.LongRestoreOwnPlaybackSession;
        if (ownsPlaybackSession) foreach (var argument in new[] { "-volume", "3" }) player.StartInfo.ArgumentList.Add(argument);
        if (options.RegionPlayback) foreach (var argument in new[] { "-x", bounds.Width.ToString(), "-y", bounds.Height.ToString(), "-left", bounds.X.ToString(), "-top", bounds.Y.ToString() }) player.StartInfo.ArgumentList.Add(argument);
        else player.StartInfo.ArgumentList.Add("-fs");
        player.StartInfo.ArgumentList.Add(fixture);
        try
        {
            if (!player.Start()) throw new InvalidOperationException("Could not start ffplay.");
            stderrTask = player.StandardError.ReadToEndAsync();
            await Task.Delay(300);
            if (ownsPlaybackSession)
            {
                playbackSessionLease = PlaybackSessionVolumeLease.Apply(
                    new NAudioPlaybackAudioSessionProvider(), (uint)player.Id, loopback.Id, temporaryVolume: 1.0f);
                Console.WriteLine($"Temporarily unmuted only ffplay PID={player.Id} at 100% session volume; ffplay's own volume is 3%, so total playback remains limited to 3%. Original mute/volume will be restored in cleanup.");
            }
            var targetPlayerBounds = options.RegionPlayback
                ? new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height)
                : new Rectangle(display.Bounds.X, display.Bounds.Y, display.Bounds.Width, display.Bounds.Height);
            var playerWindow = PlaceProcessWindow(player, targetPlayerBounds);
            Console.WriteLine($"ffplay PID={player.Id}; fixture={fixture}; ffplay={ffplay}; display={display.Bounds.Width}x{display.Bounds.Height}; target={target.Kind}:{bounds}");
            Console.WriteLine($"ffplay HWND={playerWindow.Handle.ToInt64()}; outer={playerWindow.OuterBounds}; client={playerWindow.ClientBounds}; target={targetPlayerBounds}");
            if (options.InteractivePlayback)
            {
                var baseline = ReadLastInputTick();
                if (!SetForegroundWindow(playerWindow.Handle) || GetForegroundWindow() != playerWindow.Handle)
                    throw new InvalidOperationException("Could not activate the controlled ffplay window once for interactive playback.");
                var afterActivation = ReadLastInputTick();
                if (afterActivation != baseline)
                    throw new InvalidOperationException("System input activity changed during ffplay activation; refusing to start a contaminated sync sample.");
                inputActivityBaselineTick = baseline;
                lastObservedInputActivityTick = baseline;
                Console.WriteLine($"Interactive playback activated own HWND once; GetLastInputInfo baseline tick={baseline}. Any subsequent system input activity will abort the run without reading key or text values.");
            }
            var nextRoiCheck = 5d;
            var nextLeaseCheck = 5d;
            var leaseMonitor = options.LongRestoreOwnPlaybackSession
                ? new PlaybackSessionVolumeMonitor((uint)player.Id, loopback.Id, requestedVolume: 1.0f)
                : null;
            Stopwatch? playbackElapsed = null;
            bool? previousForeground = null;
            void CheckPlayerFocus(double sampleElapsed)
            {
                var playerForeground = GetForegroundWindow() == playerWindow.Handle;
                foregroundSampleCount++;
                if (playerForeground)
                {
                    foregroundObservedCount++;
                    firstForegroundElapsedSeconds ??= sampleElapsed;
                }
                if (previousForeground is bool previous && previous != playerForeground) foregroundTransitionCount++;
                previousForeground = playerForeground;
                if (options.InteractivePlayback)
                {
                    var inputTick = ReadLastInputTick();
                    inputActivitySampleCount++;
                    lastObservedInputActivityTick = inputTick;
                    if (inputTick != inputActivityBaselineTick)
                    {
                        inputActivityChangeElapsedSeconds = sampleElapsed;
                        throw new InvalidOperationException($"GetLastInputInfo detected system input activity at {sampleElapsed:F3}s (baseline={inputActivityBaselineTick}, observed={inputTick}); aborting without recording key or text data.");
                    }
                }
                else if (playerForeground)
                    throw new InvalidOperationException($"The controlled ffplay window became foreground at {sampleElapsed:F3}s. Capture aborted because ffplay accepts keyboard shortcuts such as m for mute; no keystrokes or user text are sampled.");
            }
            void CheckRoi(double sampleElapsed)
            {
                if (sampleElapsed < nextRoiCheck) return;
                var observed = VerifyProcessWindow(player, playerWindow.Handle, targetPlayerBounds);
                if (observed != playerWindow.ClientBounds)
                    throw new InvalidOperationException($"The controlled ffplay client ROI changed during capture: expected {playerWindow.ClientBounds}, observed {observed}.");
                Console.WriteLine($"Verified controlled ROI at {sampleElapsed:F1}s; foreground samples={foregroundSampleCount}, playerForeground={foregroundObservedCount}.");
                nextRoiCheck += 5;
            }
            CheckPlayerFocus(0);
            if (options.LoopPlayback && !options.InteractivePlayback) Console.WriteLine("ffplay remained out of foreground before capture; keyboard interference guard is active.");
            Console.WriteLine("Starting screen capture.");
            await capture.StartAsync(video);
            Console.WriteLine("Screen capture started.");
            audio = await tracks.StartAudioTrackAsync(new AudioTrackRequest(audioPath, AudioDeviceKind.SystemLoopback, loopback.Id));
            Console.WriteLine("Loopback audio capture started.");
            if (options.LoopPlayback)
            {
                playbackElapsed = Stopwatch.StartNew();
                while (playbackElapsed.Elapsed.TotalSeconds < options.DurationSeconds)
                {
                    var remainingSeconds = options.DurationSeconds - playbackElapsed.Elapsed.TotalSeconds;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(0.25, Math.Max(0.05, remainingSeconds))));
                    if (player.HasExited) throw new InvalidOperationException("The controlled ffplay stimulus exited before capture completed.");
                    var sampleElapsed = playbackElapsed.Elapsed.TotalSeconds;
                    CheckPlayerFocus(sampleElapsed);
                    CheckRoi(sampleElapsed);
                    if (leaseMonitor is not null && sampleElapsed >= nextLeaseCheck)
                    {
                        if (sampleElapsed - nextLeaseCheck > 1.0)
                            throw new TimeoutException($"Owned ffplay session monitor missed its five-second deadline by {sampleElapsed - nextLeaseCheck:F3}s.");
                        using var monitorProvider = new NAudioPlaybackAudioSessionProvider();
                        var observation = leaseMonitor.Observe(monitorProvider, sampleElapsed);
                        playbackSessionObservations.Add(observation);
                        nextLeaseCheck += 5;
                        if (playbackSessionObservations.Count % 12 == 0)
                            Console.WriteLine($"Verified owned ffplay PID={observation.ProcessId} route/session volume at {observation.ElapsedSeconds:F0}s; checks={playbackSessionObservations.Count}.");
                    }
                }
            }
            else
            {
                var elapsed = Stopwatch.StartNew();
                var timeout = Math.Max(15, options.DurationSeconds + 5);
                while (!player.HasExited && elapsed.Elapsed.TotalSeconds < timeout)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250));
                    CheckPlayerFocus(elapsed.Elapsed.TotalSeconds);
                    CheckRoi(elapsed.Elapsed.TotalSeconds);
                }
                if (!player.HasExited) throw new TimeoutException("ffplay did not exit within the expected playback duration.");
                await player.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            if (leaseMonitor is not null && (playbackSessionObservations.Count == 0 || playbackSessionObservations[^1].ElapsedSeconds < options.DurationSeconds))
            {
                var finalElapsed = playbackElapsed?.Elapsed.TotalSeconds
                    ?? throw new InvalidOperationException("Long playback lease has no active elapsed-time clock.");
                if (finalElapsed < options.DurationSeconds)
                    throw new InvalidOperationException($"Final playback lease observation occurred before the requested duration: {finalElapsed:F3}s.");
                if (finalElapsed - nextLeaseCheck > 1.0)
                    throw new TimeoutException($"Final owned ffplay session observation missed its five-second deadline by {finalElapsed - nextLeaseCheck:F3}s.");
                using var finalMonitorProvider = new NAudioPlaybackAudioSessionProvider();
                var finalObservation = leaseMonitor.Observe(finalMonitorProvider, finalElapsed);
                playbackSessionObservations.Add(finalObservation);
                Console.WriteLine($"Verified final owned ffplay PID={finalObservation.ProcessId} route/session volume at {finalObservation.ElapsedSeconds:F3}s; checks={playbackSessionObservations.Count}.");
            }
            Console.WriteLine("Playback duration completed; stopping screen capture.");
            var stderr = string.Empty;
            if (!options.LoopPlayback)
            {
                stderr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(5));
                if (player.ExitCode != 0) throw new InvalidOperationException($"ffplay exited {player.ExitCode}: {stderr}");
            }
            var screenResult = await capture.StopAsync().WaitAsync(TimeSpan.FromSeconds(45));
            Console.WriteLine("Screen capture stopped.");
            var audioResult = await tracks.StopTrackAsync(audio.TrackId).WaitAsync(TimeSpan.FromSeconds(30));
            Console.WriteLine("Loopback audio capture stopped.");
            if (playbackSessionLease is not null)
            {
                playbackSessionVolumeResult = playbackSessionLease.Restore();
                playbackSessionLease = null;
            }
            if (!player.HasExited)
            {
                player.Kill(true);
                await player.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            if (playbackSessionVolumeResult is { RestorationErrors.Count: > 0 })
                throw new InvalidOperationException("The owned ffplay session did not restore its original mute/volume state; see the failure report.");
            var firstLeaseObservationSeconds = playbackSessionObservations.Count > 0 ? playbackSessionObservations[0].ElapsedSeconds : (double?)null;
            var lastLeaseObservationSeconds = playbackSessionObservations.Count > 0 ? playbackSessionObservations[^1].ElapsedSeconds : (double?)null;
            var leaseObservationSpanSeconds = playbackSessionObservations.Count > 1
                ? playbackSessionObservations[^1].ElapsedSeconds - playbackSessionObservations[0].ElapsedSeconds
                : 0d;
            if (options.LongRestoreOwnPlaybackSession &&
                (playbackSessionObservations.Count != 360 ||
                 firstLeaseObservationSeconds is not double firstObservation || firstObservation > 6.0 ||
                 lastLeaseObservationSeconds is not double lastObservation || lastObservation < options.DurationSeconds ||
                 leaseObservationSpanSeconds < options.DurationSeconds - 6.0 ||
                 playbackSessionVolumeResult is null ||
                 playbackSessionVolumeResult.RestoredMute != playbackSessionVolumeResult.OriginalMute ||
                 playbackSessionVolumeResult.RestoredVolume is not float restoredPlaybackVolume ||
                 Math.Abs(restoredPlaybackVolume - playbackSessionVolumeResult.OriginalVolume) > 0.0001f))
                throw new InvalidOperationException($"The 1800-second owned ffplay session lease did not complete 360 five-second checks spanning the requested duration and restore its original values; observed {playbackSessionObservations.Count} checks, first={firstLeaseObservationSeconds}, last={lastLeaseObservationSeconds}, span={leaseObservationSpanSeconds:F3}s.");
            if (options.LoopPlayback) stderr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(5));
            await File.WriteAllTextAsync(ffplayStderrPath, stderr);
            Console.WriteLine("Controlled player stopped; building result summary.");
            var diagnosticsPath = audioResult.Handle.MediaPath + ".diagnostics.json";
            JsonElement? audioPacketDiagnostics = null;
            if (File.Exists(diagnosticsPath))
            {
                using var diagnosticsDocument = JsonDocument.Parse(await File.ReadAllTextAsync(diagnosticsPath));
                audioPacketDiagnostics = diagnosticsDocument.RootElement.Clone();
                var diagnosticSummary = diagnosticsDocument.RootElement;
                Console.WriteLine($"Loopback packet diagnostics: packets={diagnosticSummary.GetProperty("PacketCount").GetInt64()}, silentFlagFrames={diagnosticSummary.GetProperty("SilentFlagFrames").GetInt64()}, pumpGeneratedSilenceFrames={diagnosticSummary.GetProperty("PumpGeneratedSilenceFrames").GetInt64()}, lastPacketElapsedMs={diagnosticSummary.GetProperty("LastPacketElapsedMs").ToString()}.");
            }
            else Console.WriteLine($"Loopback packet diagnostics sidecar missing: {diagnosticsPath}");
            var summary = new
            {
                ScreenResult = screenResult, AudioResult = audioResult,
                screenResult.OutputPath, AudioPath = audioResult.Handle.MediaPath, Fixture = fixture,
                screenResult.StartStopwatchTicks, screenResult.StopStopwatchTicks, screenResult.StopwatchFrequency,
                screenResult.FirstFrameObservedStopwatchTicks, AudioStartStopwatchTicks = audioResult.Handle.StartStopwatchTicks,
                AudioStopStopwatchTicks = audioResult.StopStopwatchTicks, AudioStopwatchFrequency = audioResult.Handle.StopwatchFrequency,
                screenResult.Width, screenResult.Height, screenResult.FrameRate, screenResult.EncoderName,
                CaptureBounds = options.RegionPlayback ? bounds : display.Bounds, DisplayBounds = display.Bounds,
                PlayerWindowHandle = playerWindow.Handle.ToInt64(), PlayerWindowBounds = playerWindow.OuterBounds, PlayerWindowClientBounds = playerWindow.ClientBounds,
                DdagrabOffset = options.RegionPlayback ? new Point(bounds.X - display.Bounds.X, bounds.Y - display.Bounds.Y) : Point.Empty,
                AudioWarnings = audioResult.Warnings, AudioDiagnostics = audioResult.Diagnostics,
                screenResult.Warnings, screenResult.Diagnostics, FfplayStderrPath = ffplayStderrPath,
                AudioPacketDiagnosticsPath = diagnosticsPath, AudioPacketDiagnostics = audioPacketDiagnostics,
                PlaybackSessionOverrideRequested = ownsPlaybackSession,
                PlaybackSessionOverride = playbackSessionVolumeResult,
                PlaybackSessionLease = options.LongRestoreOwnPlaybackSession ? new
                {
                    Policy = "exact ffplay PID only; selected loopback endpoint only; 3% ffplay gain; temporary process session unmuted at 100%; verify every five seconds; restore original session state before killing ffplay",
                    DurationSeconds = options.DurationSeconds,
                    MonitorIntervalSeconds = 5,
                    ObservationCount = playbackSessionObservations.Count,
                    FirstObservationElapsedSeconds = firstLeaseObservationSeconds,
                    LastObservationElapsedSeconds = lastLeaseObservationSeconds,
                    ObservationSpanSeconds = leaseObservationSpanSeconds,
                    Observations = playbackSessionObservations,
                    RestorationSucceeded = playbackSessionVolumeResult is { RestorationErrors.Count: 0 }
                        && playbackSessionVolumeResult.RestoredMute == playbackSessionVolumeResult.OriginalMute
                        && playbackSessionVolumeResult.RestoredVolume is float restoredVolume
                        && Math.Abs(restoredVolume - playbackSessionVolumeResult.OriginalVolume) <= 0.0001f
                } : null,
                FfplayExitCode = player.HasExited ? player.ExitCode : (int?)null,
                PlayerForegroundSamples = foregroundSampleCount, PlayerForegroundObservedSamples = foregroundObservedCount,
                PlayerForegroundTransitions = foregroundTransitionCount, FirstForegroundElapsedSeconds = firstForegroundElapsedSeconds,
                PlaybackMode = options.InteractivePlayback ? "interactive-getlastinputinfo" : "nonactivating-foreground-guard",
                InputActivityBaselineTick = inputActivityBaselineTick, LastObservedInputActivityTick = lastObservedInputActivityTick,
                InputActivitySampleCount = inputActivitySampleCount, InputActivityChangeElapsedSeconds = inputActivityChangeElapsedSeconds,
                KeyboardInterferenceGuard = options.InteractivePlayback
                    ? "The probe activates its own ffplay HWND once, then aborts on any GetLastInputInfo timestamp change; no key, mouse value, or user text is read."
                    : "The probe never activates the ffplay window and aborts if its HWND becomes foreground; it does not read keystrokes or user text."
            };
            var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.ChangeExtension(output, ".sync-playback.json"), json);
            if (options.LongRestoreOwnPlaybackSession)
                await File.WriteAllTextAsync(Path.ChangeExtension(output, ".playback-lease.json"), JsonSerializer.Serialize(new
                {
                    Status = "completed",
                    ProcessId = (uint)player.Id,
                    SelectedEndpointId = loopback.Id,
                    Fixture = fixture,
                    DurationSeconds = options.DurationSeconds,
                    MonitorIntervalSeconds = 5,
                    InitialLease = playbackSessionVolumeResult,
                    ObservationCount = playbackSessionObservations.Count,
                    FirstObservationElapsedSeconds = firstLeaseObservationSeconds,
                    LastObservationElapsedSeconds = lastLeaseObservationSeconds,
                    ObservationSpanSeconds = leaseObservationSpanSeconds,
                    Observations = playbackSessionObservations,
                    Restored = playbackSessionVolumeResult is { RestorationErrors.Count: 0 }
                        && playbackSessionVolumeResult.RestoredMute == playbackSessionVolumeResult.OriginalMute
                        && playbackSessionVolumeResult.RestoredVolume is float leaseSidecarRestoredVolume
                        && Math.Abs(leaseSidecarRestoredVolume - playbackSessionVolumeResult.OriginalVolume) <= 0.0001f
                }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(json);
        }
        catch (Exception ex)
        {
            if (ex is PlaybackSessionVolumeApplyException applyException)
                playbackSessionVolumeResult = applyException.RollbackResult;
            Console.Error.WriteLine($"Sync playback probe failed: {ex}");
            try
            {
                if (playbackSessionLease is not null)
                {
                    playbackSessionVolumeResult ??= playbackSessionLease.Restore();
                    playbackSessionLease = null;
                }
            }
            catch (Exception restoreException) { Console.Error.WriteLine($"Could not restore owned ffplay session before process cleanup: {restoreException}"); }
            try { Console.Error.WriteLine("Stopping screen capture after probe failure."); await capture.StopAsync().WaitAsync(TimeSpan.FromSeconds(45)); Console.Error.WriteLine("Screen capture stopped after probe failure."); } catch (Exception stopException) { Console.Error.WriteLine($"Screen capture stop failed: {stopException}"); }
            if (audio is not null) try { Console.Error.WriteLine("Stopping loopback capture after probe failure."); await tracks.StopTrackAsync(audio.TrackId).WaitAsync(TimeSpan.FromSeconds(30)); Console.Error.WriteLine("Loopback capture stopped after probe failure."); } catch (Exception stopException) { Console.Error.WriteLine($"Loopback capture stop failed: {stopException}"); }
            try { if (!player.HasExited) { Console.Error.WriteLine("Stopping controlled player after probe failure."); player.Kill(true); await player.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } } catch (Exception playerException) { Console.Error.WriteLine($"Controlled player stop failed: {playerException}"); }
            try
            {
                if (playbackSessionLease is not null)
                {
                    playbackSessionVolumeResult ??= playbackSessionLease.Restore();
                    playbackSessionLease = null;
                }
            }
            catch (Exception restoreException) { Console.Error.WriteLine($"Could not restore owned ffplay session after process cleanup: {restoreException}"); }
            try
            {
                var stderr = stderrTask is null ? string.Empty : await stderrTask.WaitAsync(TimeSpan.FromSeconds(5));
                await File.WriteAllTextAsync(ffplayStderrPath, stderr);
                Console.Error.WriteLine($"ffplay stderr saved: {ffplayStderrPath}; exitCode={(player.HasExited ? player.ExitCode.ToString() : "unknown")}");
            }
            catch (Exception logException) { Console.Error.WriteLine($"Could not save ffplay stderr: {logException.Message}"); }
            try
            {
                var failureReport = new
                {
                    Status = "aborted-or-failed", Error = ex.ToString(), FfplayStderrPath = ffplayStderrPath,
                    PlaybackSessionOverrideRequested = ownsPlaybackSession,
                    PlaybackSessionOverride = playbackSessionVolumeResult,
                    PlaybackSessionLease = options.LongRestoreOwnPlaybackSession ? new
                    {
                        Policy = "exact ffplay PID only; selected loopback endpoint only; verify unmuted 100% session volume every five seconds; restore original state",
                        DurationSeconds = options.DurationSeconds,
                        MonitorIntervalSeconds = 5,
                        ObservationCount = playbackSessionObservations.Count,
                        Observations = playbackSessionObservations
                    } : null,
                    PlayerForegroundSamples = foregroundSampleCount, PlayerForegroundObservedSamples = foregroundObservedCount,
                    PlayerForegroundTransitions = foregroundTransitionCount, FirstForegroundElapsedSeconds = firstForegroundElapsedSeconds,
                    PlaybackMode = options.InteractivePlayback ? "interactive-getlastinputinfo" : "nonactivating-foreground-guard",
                    InputActivityBaselineTick = inputActivityBaselineTick, LastObservedInputActivityTick = lastObservedInputActivityTick,
                    InputActivitySampleCount = inputActivitySampleCount, InputActivityChangeElapsedSeconds = inputActivityChangeElapsedSeconds,
                    KeyboardInterferenceGuard = options.InteractivePlayback
                        ? "Any GetLastInputInfo timestamp change aborts the run; no key, mouse value, or user text is read."
                        : "No keystrokes or user text are sampled; any observed ffplay foreground state aborts the controlled run."
                };
                await File.WriteAllTextAsync(Path.ChangeExtension(output, ".sync-playback.failure.json"), JsonSerializer.Serialize(failureReport, new JsonSerializerOptions { WriteIndented = true }));
                if (options.LongRestoreOwnPlaybackSession)
                    await File.WriteAllTextAsync(Path.ChangeExtension(output, ".playback-lease.json"), JsonSerializer.Serialize(new
                    {
                        Status = "aborted-or-failed",
                        Error = ex.ToString(),
                        ProcessId = (uint)player.Id,
                        SelectedEndpointId = loopback.Id,
                        Fixture = fixture,
                        DurationSeconds = options.DurationSeconds,
                        MonitorIntervalSeconds = 5,
                        InitialLease = playbackSessionVolumeResult,
                        ObservationCount = playbackSessionObservations.Count,
                        Observations = playbackSessionObservations,
                        RestoreErrors = playbackSessionVolumeResult?.RestorationErrors ?? []
                    }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
            throw;
        }
        finally
        {
            // Safety net for unexpected exceptions in either the success or failure report path.
            if (playbackSessionLease is not null)
            {
                try { playbackSessionVolumeResult ??= playbackSessionLease.Restore(); }
                catch (Exception restoreException) { Console.Error.WriteLine($"Final owned ffplay session restore failed: {restoreException}"); }
                playbackSessionLease = null;
            }
        }
    }

    private static string ResolveFfplay()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim('"'), "ffplay.exe");
            if (File.Exists(candidate)) return candidate;
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var winget = Path.Combine(local, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(winget))
        {
            foreach (var candidate in Directory.EnumerateFiles(winget, "ffplay.exe", SearchOption.AllDirectories))
                if (candidate.Contains("Gyan.FFmpeg_", StringComparison.OrdinalIgnoreCase)) return candidate;
        }
        throw new FileNotFoundException("ffplay.exe was not found on PATH or in the Gyan FFmpeg WinGet package.");
    }

    private static Rectangle? GetProcessWindowBounds(Process process)
    {
        var previousDpiContext = IntPtr.Zero;
        try
        {
            previousDpiContext = SetThreadDpiAwarenessContext(new IntPtr(-4));
            Rectangle? result = null;
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out var ownerPid);
                if (ownerPid != process.Id || !IsWindowVisible(hwnd) || !GetWindowRect(hwnd, out var bounds)) return true;
                result = Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
                return false;
            }, IntPtr.Zero);
            return result;
        }
        catch { return null; }
        finally { if (previousDpiContext != IntPtr.Zero) SetThreadDpiAwarenessContext(previousDpiContext); }
    }

    private static (IntPtr Handle, Rectangle OuterBounds, Rectangle ClientBounds) PlaceProcessWindow(Process process, Rectangle target)
    {
        var previousDpiContext = IntPtr.Zero;
        try
        {
            previousDpiContext = SetThreadDpiAwarenessContext(new IntPtr(-4));
            var hwnd = IntPtr.Zero;
            for (var attempt = 0; attempt < 80 && hwnd == IntPtr.Zero; attempt++)
            {
                process.Refresh();
                hwnd = process.MainWindowHandle;
                if (hwnd == IntPtr.Zero) Thread.Sleep(50);
            }
            if (hwnd == IntPtr.Zero) throw new InvalidOperationException("ffplay did not create a visible window handle.");
            GetWindowThreadProcessId(hwnd, out var ownerPid);
            if (ownerPid != process.Id || !IsWindowVisible(hwnd))
                throw new InvalidOperationException("The selected ffplay HWND is not a visible window owned by the fixture player process.");
            const uint flags = 0x0010 | 0x0040; // SWP_NOACTIVATE | SWP_SHOWWINDOW
            if (!SetWindowPos(hwnd, new IntPtr(-1), target.X, target.Y, target.Width, target.Height, flags))
                throw new InvalidOperationException($"Could not place the controlled ffplay window at the capture ROI (Win32 {Marshal.GetLastWin32Error()}).");
            Thread.Sleep(100);
            var outer = GetWindowBounds(hwnd);
            var client = GetClientScreenBounds(hwnd);
            if (client != target)
            {
                var x = outer.X + target.X - client.X;
                var y = outer.Y + target.Y - client.Y;
                var width = outer.Width + target.Width - client.Width;
                var height = outer.Height + target.Height - client.Height;
                if (!SetWindowPos(hwnd, new IntPtr(-1), x, y, width, height, flags))
                    throw new InvalidOperationException($"Could not align the ffplay client area with the capture ROI (Win32 {Marshal.GetLastWin32Error()}).");
                Thread.Sleep(100);
                outer = GetWindowBounds(hwnd);
                client = GetClientScreenBounds(hwnd);
            }
            if (client != target)
                throw new InvalidOperationException($"ffplay physical client bounds {client} do not match the requested capture ROI {target}; outer={outer}.");
            RaiseControlledWindow(hwnd, outer);
            Thread.Sleep(150);
            outer = GetWindowBounds(hwnd);
            client = GetClientScreenBounds(hwnd);
            if (client != target)
                throw new InvalidOperationException($"Raising ffplay changed its client bounds: expected {target}, observed {client}; outer={outer}.");
            VerifyRoiHitTests(hwnd, target);
            return (hwnd, outer, client);
        }
        finally { if (previousDpiContext != IntPtr.Zero) SetThreadDpiAwarenessContext(previousDpiContext); }
    }

    private static Rectangle VerifyProcessWindow(Process process, IntPtr hwnd, Rectangle expectedClientBounds)
    {
        var previousDpiContext = IntPtr.Zero;
        try
        {
            previousDpiContext = SetThreadDpiAwarenessContext(new IntPtr(-4));
            if (process.HasExited) throw new InvalidOperationException("The controlled ffplay process has exited.");
            GetWindowThreadProcessId(hwnd, out var ownerPid);
            if (ownerPid != process.Id || !IsWindowVisible(hwnd))
                throw new InvalidOperationException("The controlled ffplay HWND lost ownership or visibility.");
            var client = GetClientScreenBounds(hwnd);
            if (client != expectedClientBounds)
                throw new InvalidOperationException($"The controlled ffplay client ROI moved or resized: expected {expectedClientBounds}, observed {client}.");
            VerifyRoiHitTests(hwnd, expectedClientBounds);
            return client;
        }
        finally { if (previousDpiContext != IntPtr.Zero) SetThreadDpiAwarenessContext(previousDpiContext); }
    }

    private static Rectangle GetWindowBounds(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect))
            throw new InvalidOperationException($"Could not read a Win32 window rectangle (Win32 {Marshal.GetLastWin32Error()}).");
        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    private static Rectangle GetClientScreenBounds(IntPtr hwnd)
    {
        if (!GetClientRect(hwnd, out var rect))
            throw new InvalidOperationException($"Could not read a Win32 client rectangle (Win32 {Marshal.GetLastWin32Error()}).");
        var origin = new NativePoint { X = rect.Left, Y = rect.Top };
        if (!ClientToScreen(hwnd, ref origin))
            throw new InvalidOperationException($"Could not map the ffplay client origin to screen coordinates (Win32 {Marshal.GetLastWin32Error()}).");
        return new Rectangle(origin.X, origin.Y, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    private static void VerifyRoiHitTests(IntPtr expectedWindow, Rectangle roi)
    {
        var points = new[]
        {
            new Point(roi.Left + 2, roi.Top + 2), new Point(roi.Right - 3, roi.Top + 2),
            new Point(roi.Left + 2, roi.Bottom - 3), new Point(roi.Right - 3, roi.Bottom - 3),
            new Point(roi.Left + roi.Width / 2, roi.Top + roi.Height / 2)
        };
        foreach (var point in points)
        {
            var hit = WindowFromPoint(point);
            var root = hit == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hit, 2); // GA_ROOT
            if (root != expectedWindow)
                throw new InvalidOperationException($"The controlled ffplay window does not cover the capture ROI sample point ({point.X},{point.Y}); hit HWND={hit.ToInt64()}, root={root.ToInt64()}, expected={expectedWindow.ToInt64()}.");
        }
    }

    private static void RaiseControlledWindow(IntPtr hwnd, Rectangle outer)
    {
        const uint flags = 0x0010 | 0x0040; // SWP_NOACTIVATE | SWP_SHOWWINDOW
        if (!SetWindowPos(hwnd, new IntPtr(-1), outer.X, outer.Y, outer.Width, outer.Height, flags))
            throw new InvalidOperationException($"Could not raise the controlled ffplay window above other windows without activation (Win32 {Marshal.GetLastWin32Error()}).");
    }

    private static uint ReadLastInputTick()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) throw new InvalidOperationException($"GetLastInputInfo failed (Win32 {Marshal.GetLastWin32Error()}).");
        return info.Time;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct LastInputInfo { internal uint Size; internal uint Time; }
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr WindowFromPoint(Point point);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    private delegate bool EnumWindowsCallback(IntPtr hwnd, IntPtr lparam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr lparam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { internal int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint { internal int X, Y; }

    private static string RequireOutput(ProbeOptions options) => options.OutputPath
        ?? throw new ArgumentException("This scenario requires --output <path>.");

    private static void WriteTone(string path, int seconds, double amplitude)
    {
        const int sampleRate = 44100;
        var sampleCount = sampleRate * seconds;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + sampleCount * 2);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1);
        writer.Write((short)1); writer.Write(sampleRate); writer.Write(sampleRate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(sampleCount * 2);
        for (var i = 0; i < sampleCount; i++) writer.Write((short)(Math.Sin(2 * Math.PI * 440 * i / sampleRate) * short.MaxValue * amplitude));
    }
}

internal sealed class PatternWindow : Form
{
    internal const int FlashMessage = 0x8001;
    internal const int DisplayAffinityMessage = 0x8002;
    private bool _flash;
    private readonly bool _animate;
    private readonly bool _solidRed;
    private readonly bool _colorBars;
    private readonly string _affinityResultPath;
    private readonly System.Windows.Forms.Timer? _animationTimer;
    private int _animationFrame;
    internal PatternWindow(Rectangle bounds, bool animate, bool solidRed, string affinityResultPath, bool colorBars)
    {
        _animate = animate; _solidRed = solidRed; _affinityResultPath = affinityResultPath; _colorBars = colorBars;
        FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual; Bounds = bounds;
        Text = "FrameTrail Capture Probe Pattern";
        TopMost = true; ShowInTaskbar = false; BackColor = solidRed ? Color.Red : Color.FromArgb(12, 20, 36);
        DoubleBuffered = true;
        if (animate)
        {
            _animationTimer = new System.Windows.Forms.Timer { Interval = 16 };
            _animationTimer.Tick += (_, _) => { _animationFrame++; Invalidate(); };
            _animationTimer.Start();
        }
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == FlashMessage) { _flash = m.WParam != IntPtr.Zero; Invalidate(); }
        else if (m.Msg == DisplayAffinityMessage)
        {
            var requested = unchecked((uint)m.WParam.ToInt64());
            var succeeded = SetWindowDisplayAffinity(Handle, requested);
            var error = succeeded ? 0 : Marshal.GetLastWin32Error();
            var readSucceeded = GetWindowDisplayAffinity(Handle, out var actual);
            File.WriteAllText(_affinityResultPath, JsonSerializer.Serialize(new WindowAffinityAck(requested, succeeded, error, readSucceeded, actual)));
        }
        base.WndProc(ref m);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_solidRed) { e.Graphics.Clear(Color.Red); return; }
        if (_colorBars)
        {
            using var cyan = new SolidBrush(Color.FromArgb(20, 160, 220));
            using var green = new SolidBrush(Color.FromArgb(30, 190, 50));
            using var orange = new SolidBrush(Color.FromArgb(180, 80, 30));
            var bandHeight = Height / 3;
            e.Graphics.FillRectangle(cyan, 0, 0, Width, bandHeight);
            e.Graphics.FillRectangle(green, 0, bandHeight, Width, bandHeight);
            e.Graphics.FillRectangle(orange, 0, bandHeight * 2, Width, Height - bandHeight * 2);
            return;
        }
        e.Graphics.Clear(_flash ? Color.White : Color.FromArgb(12, 20, 36));
        using var brush = new SolidBrush(_flash ? Color.Black : Color.LimeGreen);
        var x = _animate ? (_animationFrame * 13) % Math.Max(1, Width - Width / 5) : Width / 3;
        e.Graphics.FillEllipse(brush, x, Height / 4, Width / 5, Height / 2);
        using var font = new Font("Segoe UI", 18, FontStyle.Bold);
        e.Graphics.DrawString(_flash ? "PULSE" : "CAPTURE PROBE", font, _flash ? Brushes.Black : Brushes.White, 12, 12);
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
}

internal static class PatternWindowHost
{
    internal static async Task<PatternWindowController> StartAsync(Rectangle bounds, bool animate = false, bool solidRed = false, bool colorBars = false)
    {
        var readyPath = Path.Combine(Path.GetTempPath(), $"frametrail-pattern-{Guid.NewGuid():N}.txt");
        var affinityResultPath = readyPath + ".affinity.json";
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Current executable path is unavailable.");
        var info = new ProcessStartInfo(processPath) { UseShellExecute = false, CreateNoWindow = true };
        var commandLine = Environment.GetCommandLineArgs();
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(commandLine[0]);
        info.ArgumentList.Add("--pattern-window-standalone");
        info.ArgumentList.Add(readyPath);
        info.ArgumentList.Add(bounds.X.ToString()); info.ArgumentList.Add(bounds.Y.ToString());
        info.ArgumentList.Add(bounds.Width.ToString()); info.ArgumentList.Add(bounds.Height.ToString());
        info.ArgumentList.Add(animate ? "--animate" : "--static");
        info.ArgumentList.Add(solidRed ? "--solid-red" : "--pattern");
        info.ArgumentList.Add(affinityResultPath);
        info.ArgumentList.Add(colorBars ? "--color-bars" : "--default-pattern");
        var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start the controlled test window process.");
        var timeout = Stopwatch.StartNew();
        while (!File.Exists(readyPath) && !process.HasExited && timeout.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(50).ConfigureAwait(false);
        try
        {
            if (!File.Exists(readyPath)) throw new InvalidOperationException("The controlled test window did not become ready.");
            var handle = new IntPtr(Convert.ToInt64(await File.ReadAllTextAsync(readyPath).ConfigureAwait(false), 16));
            return new PatternWindowController(process, handle, affinityResultPath);
        }
        catch { try { process.Kill(true); } catch { } process.Dispose(); throw; }
        finally { try { File.Delete(readyPath); } catch { } }
    }

    internal static void RunStandalone(string[] args)
    {
        var readyPath = args[1];
        var bounds = new Rectangle(int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4]), int.Parse(args[5]));
        var animate = args.Length > 6 && args[6] == "--animate";
        var solidRed = args.Length > 7 && args[7] == "--solid-red";
        var affinityResultPath = args.Length > 8 ? args[8] : Path.Combine(Path.GetTempPath(), $"frametrail-affinity-{Guid.NewGuid():N}.json");
        var colorBars = args.Length > 9 && args[9] == "--color-bars";
        var window = new PatternWindow(bounds, animate, solidRed, affinityResultPath, colorBars);
        window.Shown += (_, _) => File.WriteAllText(readyPath, window.Handle.ToInt64().ToString("X"));
        Application.Run(window);
    }
}

internal sealed class PatternWindowController(Process process, IntPtr handle, string affinityResultPath) : IAsyncDisposable
{
    private const uint WmClose = 0x0010;
    private const uint WmApp = 0x8000;
    private const uint SwpNoSize = 0x0001, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;
    private const uint SwpNoMove = 0x0002, SwpShowWindow = 0x0040;
    private IntPtr _previousForeground;
    private int _closed;
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }
    internal IntPtr Handle { get; } = handle;
    internal Rectangle Bounds
    {
        get { GetWindowRect(Handle, out var r); return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom); }
    }
    internal Task SetFlashAsync(bool flash)
    {
        if (!PostMessageW(Handle, WmApp + 1, flash ? new IntPtr(1) : IntPtr.Zero, IntPtr.Zero)) throw new InvalidOperationException("Could not signal the controlled test window.");
        return Task.CompletedTask;
    }
    internal async Task<WindowAffinityAck> SetDisplayAffinityAsync(uint affinity)
    {
        try { File.Delete(affinityResultPath); } catch { }
        if (!PostMessageW(Handle, PatternWindow.DisplayAffinityMessage, new IntPtr(unchecked((long)affinity)), IntPtr.Zero))
            throw new InvalidOperationException("Could not request display affinity from the controlled window process.");
        var timeout = Stopwatch.StartNew();
        while (!File.Exists(affinityResultPath) && !process.HasExited && timeout.Elapsed < TimeSpan.FromSeconds(3))
            await Task.Delay(20).ConfigureAwait(false);
        if (!File.Exists(affinityResultPath)) throw new TimeoutException("The controlled window did not acknowledge display-affinity change.");
        return JsonSerializer.Deserialize<WindowAffinityAck>(await File.ReadAllTextAsync(affinityResultPath).ConfigureAwait(false))
            ?? throw new InvalidDataException("Display-affinity acknowledgement was empty.");
    }
    internal void BringToTopWithoutActivating()
    {
        if (!SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow))
            throw new InvalidOperationException("Could not place the controlled test window at the top of the desktop z-order.");
    }
    internal void FocusForTestCapture()
    {
        _previousForeground = GetForegroundWindow();
        BringToTopWithoutActivating();
        if (!SetForegroundWindow(Handle)) throw new InvalidOperationException("Could not focus the controlled test window for a short capture validation.");
    }
    internal Task MoveToAsync(Point location)
    {
        if (!SetWindowPos(Handle, IntPtr.Zero, location.X, location.Y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate))
            throw new InvalidOperationException("Could not move the controlled test window.");
        return Task.CompletedTask;
    }
    internal Task ResizeToAsync(int width, int height)
    {
        if (width <= 0 || height <= 0 || !SetWindowPos(Handle, IntPtr.Zero, 0, 0, width, height, SwpNoMove | SwpNoZOrder | SwpNoActivate))
            throw new InvalidOperationException("Could not resize the controlled test window.");
        return Task.CompletedTask;
    }
    internal Task MinimizeAsync()
    {
        ShowWindow(Handle, 6);
        return Task.CompletedTask;
    }
    internal async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        try
        {
            if (!process.HasExited)
            {
                PostMessageW(Handle, WmClose, IntPtr.Zero, IntPtr.Zero);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
        finally
        {
            process.Dispose();
            RestorePreviousForeground();
        }
    }
    private void RestorePreviousForeground()
    {
        if (_previousForeground != IntPtr.Zero) SetForegroundWindow(_previousForeground);
        _previousForeground = IntPtr.Zero;
    }
    public async ValueTask DisposeAsync() { await CloseAsync().ConfigureAwait(false); }
}

internal sealed record WindowAffinityAck(uint Requested, bool SetSucceeded, int LastError, bool ReadSucceeded, uint Actual);
