using FrameTrail.Capture;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;

internal static class CaptureFailureLifecycleProbe
{
    internal static void Run()
    {
        var runningStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runningCompleted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? remembered = null;
        var runtimeFailure = new IOException("injected mid-recording failure");
        CaptureFailurePolicy.ApplyWindowFailure(runtimeFailure, CaptureState.Recording, runningStarted, runningCompleted, x => remembered = x);
        Require(ReferenceEquals(remembered, runtimeFailure) && !runningStarted.Task.IsFaulted && !runningCompleted.Task.IsFaulted,
            "mid-recording failure remains finalizable and does not poison the completion callback");

        var starting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startupCompletion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        CaptureFailurePolicy.ApplyWindowFailure(runtimeFailure, CaptureState.Starting, starting, startupCompletion, _ => { });
        Require(starting.Task.IsFaulted && startupCompletion.Task.IsFaulted,
            "startup failure faults the start and completion waiters");
        Require(CaptureFailurePolicy.CanStopTrack(CaptureTrackState.Faulted)
            && !CaptureFailurePolicy.CanStopTrack(CaptureTrackState.Completed),
            "faulted device tracks enter cleanup while completed tracks remain closed");

        var path = Path.Combine(Path.GetTempPath(), "frametrail-fault-candidate-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            File.WriteAllBytes(path, [0, 1, 2]);
            Require(CaptureFailurePolicy.HasNonEmptyCandidate(path), "non-empty partial output is retained as a salvage candidate");
            File.WriteAllBytes(path, []);
            Require(!CaptureFailurePolicy.HasNonEmptyCandidate(path), "empty output is rejected as a salvage candidate");
        }
        finally { try { File.Delete(path); } catch { } }
        Console.WriteLine("Capture failure lifecycle probe passed: startup/runtime fault signaling, faulted-track cleanup, and partial-output candidate checks.");
    }

    internal static async Task RunNativeAsync(string output)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var ffprobe = ResolveFfprobe();
        Require(ffprobe is not null, "ffprobe is available to verify the resulting media");

        await using var capture = new FrameCaptureService();
        var display = (await capture.EnumerateDisplaysAsync()).First(x => x.IsPrimary);
        var width = Math.Min(640, display.Bounds.Width - 120);
        var height = Math.Min(360, display.Bounds.Height - 120);
        var bounds = new Rectangle(display.Bounds.X + 40, display.Bounds.Y + 40, width, height);
        await using var window = await PatternWindowHost.StartAsync(bounds, animate: true);
        var completedCount = 0;
        capture.RecordingCompleted += _ => completedCount++;
        await capture.StartAsync(new RecordingRequest(output,
            new CaptureTarget(CaptureTargetKind.Window, window.Handle.ToInt64().ToString("X"), WindowHandle: window.Handle),
            new VideoOptions(30, width, height, EncoderPreference.Software), new AudioOptions(),
            new InputTrackingOptions(false)));
        await Task.Delay(TimeSpan.FromSeconds(2));

        var injected = new IOException("probe injected runtime failure");
        capture.InjectRecordingFailureForProbe(injected);
        Require(capture.State == CaptureState.Faulted, "injected runtime failure moves a real active window recording to Faulted");
        var result = await capture.StopAsync();
        Require(capture.State == CaptureState.Completed, "StopAsync finalizes a faulted active recorder");
        Require(completedCount == 1, "RecordingCompleted is raised exactly once");
        Require(File.Exists(output) && new FileInfo(output).Length > 0, "the finalized recording is non-empty");
        Require(result.Warnings.Any(x => x.Contains(injected.Message, StringComparison.Ordinal)), "the synthetic failure is preserved in result warnings");
        var recorderField = typeof(FrameCaptureService).GetField("_recorder", BindingFlags.Instance | BindingFlags.NonPublic);
        Require(recorderField?.GetValue(capture) is null, "native recorder is released after the faulted stop path");

        using var probe = Process.Start(new ProcessStartInfo(ffprobe!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", output }
        }) ?? throw new InvalidOperationException("ffprobe did not start.");
        var durationText = await probe.StandardOutput.ReadToEndAsync();
        var errors = await probe.StandardError.ReadToEndAsync();
        await probe.WaitForExitAsync();
        var parsedDuration = double.TryParse(durationText.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var duration);
        Require(probe.ExitCode == 0 && parsedDuration && duration >= 1,
            $"ffprobe decodes at least one second of media (duration='{durationText.Trim()}', error='{errors.Trim()}')");
        Console.WriteLine($"Native fault lifecycle test passed: state={capture.State}; completedEvents={completedCount}; duration={duration:F3}s; bytes={new FileInfo(output).Length}; output={output}");
    }

    private static string? ResolveFfprobe()
    {
        var configured = Environment.GetEnvironmentVariable("FRAMETRAIL_FFPROBE");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var fromPath = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory, "ffprobe.exe")).FirstOrDefault(File.Exists);
        if (fromPath is not null) return fromPath;
        var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        if (!Directory.Exists(winget)) return null;
        return Directory.GetDirectories(winget, "Gyan.FFmpeg*").SelectMany(package =>
            Directory.GetDirectories(package, "ffmpeg-*-full_build")).Select(folder => Path.Combine(folder, "bin", "ffprobe.exe"))
            .FirstOrDefault(File.Exists);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Failure lifecycle probe failed: " + message);
    }
}
