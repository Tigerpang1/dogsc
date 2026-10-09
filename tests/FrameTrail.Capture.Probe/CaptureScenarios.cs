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
        var summary = new { result.Handle, result.StopStopwatchTicks, result.Width, result.Height, result.FrameRate, EncoderMode = result.EncoderMode.ToString(), result.PausedIntervals, result.Warnings,
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
        await using var capture = new FrameCaptureService();
        var display = (await capture.EnumerateDisplaysAsync()).First(x => x.IsPrimary);
        var width = Math.Min(640, display.Bounds.Width - 160);
        var height = Math.Min(360, display.Bounds.Height - 160);
        var start = new Point(display.Bounds.X + Math.Max(80, Math.Min(2400, display.Bounds.Width - width - 80)),
            display.Bounds.Y + Math.Max(80, Math.Min(1400, display.Bounds.Height - height - 80)));
        var window = await PatternWindowHost.StartAsync(new Rectangle(start.X, start.Y, width, height));
        capture.StateChanged += (_, e) => { Console.WriteLine($"{DateTimeOffset.UtcNow:O} {e.State} ticks={e.StopwatchTicks} detail={e.Detail}"); Console.Out.Flush(); };
        capture.ErrorOccurred += (_, e) => { Console.WriteLine($"ErrorOccurred={e.Message}"); Console.Out.Flush(); };
        string? completedEventPath = null;
        capture.RecordingCompleted += result => { completedEventPath = result.OutputPath; Console.WriteLine($"RecordingCompleted={result.OutputPath}"); Console.Out.Flush(); };

        var windowRequest = new RecordingRequest(output,
            new CaptureTarget(CaptureTargetKind.Window, window.Handle.ToInt64().ToString("X"), WindowHandle: window.Handle),
            new VideoOptions(30, width, height, EncoderPreference.Software), new AudioOptions(),
            new InputTrackingOptions(true, 60, Path.ChangeExtension(output, ".window-input.jsonl")));
        await capture.StartAsync(windowRequest);
        await Task.Delay(TimeSpan.FromSeconds(3));
        var moved = new Point(start.X + 120, start.Y + 60);
        await window.MoveToAsync(moved);
        await Task.Delay(TimeSpan.FromSeconds(3));
        var windowRect = window.Bounds;
        await capture.StopAsync();
        await window.CloseAsync();
        Console.WriteLine($"Window target capture completed; initial=({start.X},{start.Y},{width},{height}), moved=({moved.X},{moved.Y}), finalBounds={windowRect}.");
        try
        {
            var invalidPath = Path.ChangeExtension(output, ".closed-window.mp4");
            await capture.StartAsync(windowRequest with { OutputPath = invalidPath });
            await capture.StopAsync();
            Console.WriteLine("Closed target unexpectedly accepted.");
        }
        catch (Exception ex) { Console.WriteLine($"Closed target before start: rejected ({ex.GetType().Name}: {ex.Message})"); }

        var closingWindow = await PatternWindowHost.StartAsync(new Rectangle(start.X, start.Y, width, height));
        var closingPath = Path.ChangeExtension(output, ".closed-during-recording.mp4");
        await capture.StartAsync(windowRequest with { OutputPath = closingPath,
            Target = new CaptureTarget(CaptureTargetKind.Window, closingWindow.Handle.ToInt64().ToString("X"), WindowHandle: closingWindow.Handle) });
        await Task.Delay(TimeSpan.FromSeconds(2));
        await closingWindow.CloseAsync();
        await Task.Delay(TimeSpan.FromSeconds(2));
        try
        {
            var closedResult = await capture.StopAsync();
            Console.WriteLine($"Window closed during capture; stop completed with file={closedResult.OutputPath}; eventPath={completedEventPath}; latestResult={capture.LatestCompletedResult?.OutputPath}; eventMatches={string.Equals(completedEventPath, closedResult.OutputPath, StringComparison.OrdinalIgnoreCase)}.");
        }
        catch (Exception ex) { Console.WriteLine($"Window closed during capture; stop reported {ex.GetType().Name}: {ex.Message}"); }

        var minimizingWindow = await PatternWindowHost.StartAsync(new Rectangle(start.X, start.Y, width, height));
        var minimizingPath = Path.ChangeExtension(output, ".minimized-during-recording.mp4");
        await capture.StartAsync(windowRequest with { OutputPath = minimizingPath,
            Target = new CaptureTarget(CaptureTargetKind.Window, minimizingWindow.Handle.ToInt64().ToString("X"), WindowHandle: minimizingWindow.Handle) });
        await Task.Delay(TimeSpan.FromSeconds(1));
        await minimizingWindow.MinimizeAsync();
        await Task.Delay(TimeSpan.FromSeconds(1));
        try
        {
            var minimizedResult = await capture.StopAsync();
            Console.WriteLine($"Window minimized during capture; stop completed with file={minimizedResult.OutputPath}.");
        }
        catch (Exception ex) { Console.WriteLine($"Window minimized during capture; recorder auto-finalized ({ex.GetType().Name}: {ex.Message})."); }
        await minimizingWindow.CloseAsync();

        var regionOutput = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "-region.mp4");
        var regionWindow = await PatternWindowHost.StartAsync(new Rectangle(start.X, start.Y, width, height));
        var bounds = regionWindow.Bounds;
        await capture.StartAsync(new RecordingRequest(regionOutput,
            new CaptureTarget(CaptureTargetKind.Region, display.Id,
                new PixelRect(bounds.X, bounds.Y, bounds.Width, bounds.Height)),
            new VideoOptions(30, width, height, EncoderPreference.Software), new AudioOptions(),
            new InputTrackingOptions(true, 60, Path.ChangeExtension(regionOutput, ".input.jsonl"))));
        await Task.Delay(TimeSpan.FromSeconds(5));
        var regionResult = await capture.StopAsync();
        await regionWindow.CloseAsync();
        Console.WriteLine($"Region target capture completed; physical bounds={bounds}; output={regionResult.OutputPath}; output={regionResult.Width}x{regionResult.Height}.");
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
        await capture.StartAsync(video);
        var audio = await tracks.StartAudioTrackAsync(new AudioTrackRequest(audioPath, AudioDeviceKind.SystemLoopback, loopback.Id));
        using var player = new Process { StartInfo = new ProcessStartInfo(ffplay)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
            ArgumentList = { "-autoexit", "-alwaysontop", "-noborder", "-hide_banner", "-loglevel", "error" }
        }};
        if (options.LoopPlayback) foreach (var argument in new[] { "-loop", "0" }) player.StartInfo.ArgumentList.Add(argument);
        if (options.RegionPlayback) foreach (var argument in new[] { "-x", bounds.Width.ToString(), "-y", bounds.Height.ToString(), "-left", bounds.X.ToString(), "-top", bounds.Y.ToString() }) player.StartInfo.ArgumentList.Add(argument);
        else player.StartInfo.ArgumentList.Add("-fs");
        player.StartInfo.ArgumentList.Add(fixture);
        try
        {
            if (!player.Start()) throw new InvalidOperationException("Could not start ffplay.");
            await Task.Delay(300);
            var playerWindowBounds = GetProcessWindowBounds(player);
            Console.WriteLine($"ffplay PID={player.Id}; fixture={fixture}; ffplay={ffplay}; display={display.Bounds.Width}x{display.Bounds.Height}; target={target.Kind}:{bounds}");
            var stderrTask = player.StandardError.ReadToEndAsync();
            if (options.LoopPlayback)
            {
                await Task.Delay(TimeSpan.FromSeconds(options.DurationSeconds));
                if (!player.HasExited) player.Kill(true);
                await player.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            else await player.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(Math.Max(15, options.DurationSeconds + 5)));
            var stderr = await stderrTask;
            if (!options.LoopPlayback && player.ExitCode != 0) throw new InvalidOperationException($"ffplay exited {player.ExitCode}: {stderr}");
            var screenResult = await capture.StopAsync();
            var audioResult = await tracks.StopTrackAsync(audio.TrackId);
            var summary = new
            {
                screenResult.OutputPath, AudioPath = audioResult.Handle.MediaPath, Fixture = fixture,
                screenResult.StartStopwatchTicks, screenResult.StopStopwatchTicks, screenResult.StopwatchFrequency,
                screenResult.FirstFrameObservedStopwatchTicks, AudioStartStopwatchTicks = audioResult.Handle.StartStopwatchTicks,
                AudioStopStopwatchTicks = audioResult.StopStopwatchTicks, AudioStopwatchFrequency = audioResult.Handle.StopwatchFrequency,
                screenResult.Width, screenResult.Height, screenResult.FrameRate, screenResult.EncoderName,
                CaptureBounds = options.RegionPlayback ? bounds : display.Bounds, DisplayBounds = display.Bounds, PlayerWindowBounds = playerWindowBounds,
                DdagrabOffset = options.RegionPlayback ? new Point(bounds.X - display.Bounds.X, bounds.Y - display.Bounds.Y) : Point.Empty,
                screenResult.Warnings
            };
            var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.ChangeExtension(output, ".sync-playback.json"), json);
            Console.WriteLine(json);
        }
        catch
        {
            try { if (!player.HasExited) player.Kill(true); } catch { }
            try { await capture.StopAsync(); } catch { }
            try { await tracks.StopTrackAsync(audio.TrackId); } catch { }
            throw;
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

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    private delegate bool EnumWindowsCallback(IntPtr hwnd, IntPtr lparam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr lparam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { internal int Left, Top, Right, Bottom; }

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
    private readonly string _affinityResultPath;
    private readonly System.Windows.Forms.Timer? _animationTimer;
    private int _animationFrame;
    internal PatternWindow(Rectangle bounds, bool animate, bool solidRed, string affinityResultPath)
    {
        _animate = animate; _solidRed = solidRed; _affinityResultPath = affinityResultPath;
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
    internal static async Task<PatternWindowController> StartAsync(Rectangle bounds, bool animate = false, bool solidRed = false)
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
        var window = new PatternWindow(bounds, animate, solidRed, affinityResultPath);
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
    internal Task MinimizeAsync()
    {
        ShowWindow(Handle, 6);
        return Task.CompletedTask;
    }
    internal async Task CloseAsync()
    {
        if (process.HasExited) { RestorePreviousForeground(); return; }
        PostMessageW(Handle, WmClose, IntPtr.Zero, IntPtr.Zero);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        process.Dispose();
        RestorePreviousForeground();
    }
    private void RestorePreviousForeground()
    {
        if (_previousForeground != IntPtr.Zero) SetForegroundWindow(_previousForeground);
        _previousForeground = IntPtr.Zero;
    }
    public async ValueTask DisposeAsync() { await CloseAsync().ConfigureAwait(false); }
}

internal sealed record WindowAffinityAck(uint Requested, bool SetSucceeded, int LastError, bool ReadSucceeded, uint Actual);
