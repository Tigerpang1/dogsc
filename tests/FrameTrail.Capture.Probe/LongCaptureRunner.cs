using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FrameTrail.Capture;

internal static class LongCaptureRunner
{
    internal static async Task RunAsync(ProbeOptions options)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var output = options.OutputPath ?? Path.Combine(Environment.CurrentDirectory, $"frametrail-long-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var telemetryPath = Path.ChangeExtension(output, ".metrics.csv");
        var summaryPath = Path.ChangeExtension(output, ".summary.json");
        await using var capture = new FrameCaptureService();
        var display = (await capture.EnumerateDisplaysAsync()).FirstOrDefault(d => d.IsPrimary)
            ?? throw new InvalidOperationException("No primary display was enumerated.");
        var audioDevices = await capture.EnumerateAudioDevicesAsync();
        var loopback = options.NoAudio ? null : audioDevices.FirstOrDefault(d => d.Kind == AudioDeviceKind.SystemLoopback && d.IsDefault);
        Console.WriteLine($"Display={display.Name}; bounds={display.Bounds.X},{display.Bounds.Y},{display.Bounds.Width},{display.Bounds.Height}");
        Console.WriteLine($"Audio devices={string.Join("; ", audioDevices.Select(d => $"{d.Kind}:{d.Name}{(d.IsDefault ? " (default)" : "")}"))}");
        Console.WriteLine($"Loopback={(loopback?.Name ?? "unavailable")}; output={output}; duration={options.DurationSeconds}s; fps={options.FrameRate}; requestedWidth={options.Width}");
        var stateTicks = new Dictionary<CaptureState, long>();
        capture.StateChanged += (_, e) =>
        {
            stateTicks[e.State] = e.StopwatchTicks;
            Console.WriteLine($"{DateTimeOffset.UtcNow:O} State={e.State}; ticks={e.StopwatchTicks}; detail={e.Detail}");
            Console.Out.Flush();
        };
        capture.ErrorOccurred += (_, e) => Console.Error.WriteLine($"{DateTimeOffset.UtcNow:O} CaptureError={e.Message}; exception={e.Exception}");
        var video = new VideoOptions(FrameRate: options.FrameRate, Width: options.Width, Height: options.Height,
            EncoderPreference: options.Software ? EncoderPreference.Software : EncoderPreference.Automatic, Quality: 75,
            FixedFrameRate: options.FixedFrameRate);
        var audio = loopback is null ? new AudioOptions() : new AudioOptions(RecordSystemAudio: true, SystemAudioDeviceId: loopback.Id);
        var input = new InputTrackingOptions(Enabled: true, SampleRate: Math.Clamp(options.FrameRate, 1, 120),
            OutputPath: Path.ChangeExtension(output, ".input.jsonl"));
        var request = new RecordingRequest(output, new CaptureTarget(CaptureTargetKind.Display, display.Id), video, audio, input);
        var callTicks = Stopwatch.GetTimestamp();
        await capture.StartAsync(request);
        var returnedTicks = Stopwatch.GetTimestamp();
        Console.WriteLine($"StartAsync elapsed={(returnedTicks - callTicks) * 1000d / Stopwatch.Frequency:F3}ms; Recording state offset={(stateTicks[CaptureState.Recording] - callTicks) * 1000d / Stopwatch.Frequency:F3}ms");
        Console.Out.Flush();

        var process = Process.GetCurrentProcess();
        var lastCpu = process.TotalProcessorTime;
        var lastWall = Stopwatch.GetTimestamp();
        var startedWall = lastWall;
        var startedUtc = DateTimeOffset.UtcNow;
        await using var telemetry = new StreamWriter(new FileStream(telemetryPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        await telemetry.WriteLineAsync("utc,elapsed_seconds,mp4_bytes,working_set_mib,private_mib,available_physical_mib,cpu_percent_of_machine,threads");
        Console.WriteLine($"Telemetry={telemetryPath}");
        const long privateMemoryGuardBytes = 2L * 1024 * 1024 * 1024;
        var stopReason = "requested_duration_reached";
        while (true)
        {
            var elapsed = Stopwatch.GetElapsedTime(startedWall).TotalSeconds;
            if (elapsed >= options.DurationSeconds) break;
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, options.DurationSeconds - elapsed)));
            process.Refresh();
            var now = Stopwatch.GetTimestamp();
            var cpu = process.TotalProcessorTime;
            var wallSeconds = Stopwatch.GetElapsedTime(lastWall, now).TotalSeconds;
            var cpuPercent = wallSeconds <= 0 ? 0 : (cpu - lastCpu).TotalSeconds / (wallSeconds * Environment.ProcessorCount) * 100;
            elapsed = Stopwatch.GetElapsedTime(startedWall, now).TotalSeconds;
            var availablePhysicalMiB = GetAvailablePhysicalMemoryMiB();
            var mp4Bytes = File.Exists(output) ? new FileInfo(output).Length : 0;
            var line = string.Join(',', DateTimeOffset.UtcNow.ToString("O"), elapsed.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                mp4Bytes,
                (process.WorkingSet64 / 1048576d).ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
                (process.PrivateMemorySize64 / 1048576d).ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
                availablePhysicalMiB.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
                cpuPercent.ToString("F2", System.Globalization.CultureInfo.InvariantCulture), process.Threads.Count);
            await telemetry.WriteLineAsync(line);
            Console.WriteLine($"Telemetry elapsed={elapsed:F0}s; mp4={mp4Bytes}; ws={process.WorkingSet64 / 1048576d:F1}MiB; private={process.PrivateMemorySize64 / 1048576d:F1}MiB; availablePhysical={availablePhysicalMiB:F1}MiB; cpu={cpuPercent:F2}%; threads={process.Threads.Count}");
            Console.Out.Flush();
            lastWall = now; lastCpu = cpu;
            if (process.PrivateMemorySize64 >= privateMemoryGuardBytes)
            {
                stopReason = "private_memory_guard_2gib";
                Console.WriteLine($"Memory guard reached at {process.PrivateMemorySize64 / 1048576d:F1}MiB private bytes; requesting graceful stop.");
                Console.Out.Flush();
                break;
            }
        }
        var result = await capture.StopAsync();
        process.Refresh();
        var ffprobeDiagnostics = await RunFfprobeAsync(output);
        var summary = new
        {
            OutputPath = result.OutputPath,
            InputEventsPath = result.InputEventsPath,
            TelemetryPath = telemetryPath,
            StartedUtc = startedUtc,
            StoppedUtc = DateTimeOffset.UtcNow,
            RequestedDurationSeconds = options.DurationSeconds,
            StopReason = stopReason,
            StopwatchElapsedSeconds = (double)(result.StopStopwatchTicks - result.StartStopwatchTicks) / result.StopwatchFrequency,
            RecordingStateOffsetMs = (double)(stateTicks[CaptureState.Recording] - callTicks) * 1000 / result.StopwatchFrequency,
            RecordCallBaselineToStatusMs = (double)(stateTicks[CaptureState.Recording] - result.StartStopwatchTicks) * 1000 / result.StopwatchFrequency,
            StopwatchFrequency = result.StopwatchFrequency,
            OutputWidth = result.Width,
            OutputHeight = result.Height,
            FrameRate = result.FrameRate,
            Encoder = result.EncoderName,
            EncoderMode = result.EncoderMode.ToString(),
            Mp4Bytes = File.Exists(output) ? new FileInfo(output).Length : 0,
            HasFtyp = ContainsMarker(output, "ftyp"),
            HasMoov = ContainsMarker(output, "moov"),
            HasAvc1 = ContainsMarker(output, "avc1"),
            HasAudioTrackMarker = ContainsMarker(output, "soun"),
            LoopbackDevice = loopback?.Name,
            FfprobeDiagnostics = ffprobeDiagnostics,
            Warnings = result.Warnings
        };
        await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Finished: {output}; bytes={summary.Mp4Bytes}; stopReason={stopReason}; ffprobe={ffprobeDiagnostics}; audioTrackMarker={summary.HasAudioTrackMarker}; summary={summaryPath}");
        Console.Out.Flush();
        foreach (var warning in result.Warnings) Console.WriteLine($"Warning: {warning}");
    }

    private static bool ContainsMarker(string path, string marker)
    {
        if (!File.Exists(path)) return false;
        var needle = Encoding.ASCII.GetBytes(marker);
        var buffer = new byte[1024 * 1024 + needle.Length - 1];
        using var stream = File.OpenRead(path);
        var overlap = 0;
        while (true)
        {
            var read = stream.Read(buffer, overlap, 1024 * 1024);
            if (read == 0) return false;
            var length = overlap + read;
            for (var i = 0; i <= length - needle.Length; i++)
            {
                var matches = true;
                for (var j = 0; j < needle.Length; j++) if (buffer[i + j] != needle[j]) { matches = false; break; }
                if (matches) return true;
            }
            overlap = Math.Min(needle.Length - 1, length);
            Buffer.BlockCopy(buffer, length - overlap, buffer, 0, overlap);
        }
    }

    private static async Task<string> RunFfprobeAsync(string output)
    {
        var executable = Environment.GetEnvironmentVariable("FRAMETRAIL_FFPROBE");
        if (string.IsNullOrWhiteSpace(executable))
            executable = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
                .Select(folder => Path.Combine(folder, "ffprobe.exe")).FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            var wingetRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages", "Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe");
            executable = Directory.Exists(wingetRoot)
                ? Directory.GetDirectories(wingetRoot, "ffmpeg-*-full_build").OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                    .Select(path => Path.Combine(path, "bin", "ffprobe.exe")).FirstOrDefault(File.Exists)
                : null;
        }
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return "unavailable: ffprobe not found";
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var arg in new[] { "-v", "error", "-show_entries", "format=duration,size:stream=codec_type,codec_name,width,height,r_frame_rate,avg_frame_rate,nb_frames,duration", "-of", "json", output }) start.ArgumentList.Add(arg);
        using var probe = Process.Start(start);
        if (probe is null) return "unavailable: ffprobe could not start";
        var stdoutTask = probe.StandardOutput.ReadToEndAsync();
        var stderrTask = probe.StandardError.ReadToEndAsync();
        await probe.WaitForExitAsync();
        var stdout = await stdoutTask; var stderr = await stderrTask;
        return probe.ExitCode == 0 ? stdout.Trim() : $"failed ({probe.ExitCode}): {stderr.Trim()}";
    }

    private static double GetAvailablePhysicalMemoryMiB()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? status.AvailablePhysical / 1048576d : double.NaN;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
