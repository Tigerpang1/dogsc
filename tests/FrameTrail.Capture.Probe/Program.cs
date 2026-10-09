using System.Diagnostics;
using System.Runtime.InteropServices;
using FrameTrail.Capture;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
Console.OutputEncoding = new System.Text.UTF8Encoding(false);
if (args.Length > 0 && args[0] == "--pattern-window-standalone")
{
    PatternWindowHost.RunStandalone(args);
    return;
}
var probeOptions = ProbeOptions.Parse(args);
if (probeOptions.SyncStimulus || probeOptions.SyncCapture || probeOptions.SyncPlayback || probeOptions.TargetTest || probeOptions.TrackTest || probeOptions.PauseCycle || probeOptions.MultiPauseCycle || probeOptions.KillRecoveryTest || probeOptions.EnumerateInputs || probeOptions.LongLoopback || probeOptions.AnimateWindow)
{
    await CaptureScenarios.RunAsync(probeOptions);
    return;
}
if (probeOptions.Metrics) { await LongCaptureRunner.RunAsync(probeOptions); return; }
var output = probeOptions.OutputPath ?? Path.Combine(Path.GetTempPath(), $"frametrail-probe-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
var inputPath = Path.ChangeExtension(output, ".input.jsonl");
await using var capture = new FrameCaptureService();
var display = (await capture.EnumerateDisplaysAsync()).FirstOrDefault(d => d.IsPrimary)
    ?? throw new InvalidOperationException("No primary display was enumerated.");
Console.WriteLine($"Display: {display.Name} {display.Bounds.Width}x{display.Bounds.Height} at ({display.Bounds.X},{display.Bounds.Y})");
var audioDevices = await capture.EnumerateAudioDevicesAsync();
Console.WriteLine($"Audio devices: {string.Join("; ", audioDevices.Select(d => $"{d.Kind}:{d.Name}{(d.IsDefault ? " (default)" : "")}"))}");
Console.WriteLine($"Output: {output}");
long statusStarted = 0;
capture.StateChanged += (_, e) =>
{
    Console.WriteLine($"State={e.State}, StopwatchTicks={e.StopwatchTicks}, Detail={e.Detail}");
    if (e.State == CaptureState.Recording) statusStarted = e.StopwatchTicks;
};
capture.ErrorOccurred += (_, e) => Console.Error.WriteLine($"Capture error: {e.Message} {e.Exception}");
var invocationTicks = Stopwatch.GetTimestamp();
await capture.StartAsync(new RecordingRequest(output, new CaptureTarget(CaptureTargetKind.Display, display.Id),
    new VideoOptions(FrameRate: probeOptions.FrameRate, Width: probeOptions.Width, Height: probeOptions.Height, EncoderPreference: EncoderPreference.Automatic, FixedFrameRate: probeOptions.FixedFrameRate),
    new AudioOptions(), new InputTrackingOptions(Enabled: true, SampleRate: 60, OutputPath: inputPath)));
var startReturnedTicks = Stopwatch.GetTimestamp();
Console.WriteLine($"StartAsync returned after {(startReturnedTicks - invocationTicks) * 1000d / Stopwatch.Frequency:F3} ms");
Console.WriteLine($"Recording for {probeOptions.DurationSeconds} seconds...");
await Task.Delay(TimeSpan.FromSeconds(probeOptions.DurationSeconds));
var result = await capture.StopAsync();
Console.WriteLine($"Stop returned: {result.OutputPath}");
Console.WriteLine($"MP4 bytes: {(File.Exists(result.OutputPath) ? new FileInfo(result.OutputPath).Length : 0)}");
Console.WriteLine($"Input events: {result.InputEventsPath} ({(result.InputEventsPath is { } p && File.Exists(p) ? new FileInfo(p).Length : 0)} bytes)");
Console.WriteLine($"Recording state event offset from StartAsync invocation: {(statusStarted - invocationTicks) * 1000d / Stopwatch.Frequency:F3} ms");
Console.WriteLine($"StartAsync return offset from invocation: {(startReturnedTicks - invocationTicks) * 1000d / Stopwatch.Frequency:F3} ms");
Console.WriteLine($"Recording status event offset from Record-call baseline: {(statusStarted - result.StartStopwatchTicks) * 1000d / Stopwatch.Frequency:F3} ms");
Console.WriteLine($"First frame callback: number={result.FirstFrameNumber?.ToString() ?? "unknown"}; monotonic offset from Record-call baseline={(result.FirstFrameObservedStopwatchTicks is long firstFrameTicks ? (firstFrameTicks - result.StartStopwatchTicks) * 1000d / Stopwatch.Frequency : double.NaN):F3} ms; native wall-clock timestamp ms={result.FirstFrameSystemTimestampUnixMilliseconds?.ToString() ?? "unknown"}");
Console.WriteLine($"Progress-derived frame-zero estimate offset from Record-call baseline={(result.ProgressEstimatedFirstFrameStopwatchTicks is long progressEstimate ? (progressEstimate - result.StartStopwatchTicks) * 1000d / Stopwatch.Frequency : double.NaN):F3} ms; print-anchor delta from progress estimate={(result.FirstFrameObservedStopwatchTicks is long observed && result.ProgressEstimatedFirstFrameStopwatchTicks is long progress ? (observed - progress) * 1000d / Stopwatch.Frequency : double.NaN):F3} ms");
Console.WriteLine($"Result API start to Stop call ticks: {(result.StopStopwatchTicks - result.StartStopwatchTicks) * 1000d / Stopwatch.Frequency:F3} ms");
Console.WriteLine($"Encoder: {result.EncoderName}, mode={result.EncoderMode}; output={result.Width}x{result.Height}@{result.FrameRate}");
Console.WriteLine("Warnings:");
foreach (var warning in result.Warnings) Console.WriteLine($"- {warning}");
Console.WriteLine($"Stopwatch frequency: {result.StopwatchFrequency}");

var repeatOutput = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "-repeat.mp4");
var repeatRequest = new RecordingRequest(repeatOutput, new CaptureTarget(CaptureTargetKind.Display, display.Id),
    new VideoOptions(FrameRate: 30), new AudioOptions(), new InputTrackingOptions(false));
await capture.StartAsync(repeatRequest);
await Task.Delay(TimeSpan.FromSeconds(2));
var repeatResult = await capture.StopAsync();
Console.WriteLine($"Repeated recording: {repeatResult.OutputPath}, bytes={(File.Exists(repeatOutput) ? new FileInfo(repeatOutput).Length : 0)}");

var cancelOutput = Path.ChangeExtension(output, ".cancelled.mp4");
using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
{
    try
    {
        await capture.StartAsync(repeatRequest with { OutputPath = cancelOutput }, cancellation.Token);
        await capture.StopAsync();
        Console.WriteLine("Startup cancellation: not triggered before recording started.");
    }
    catch (OperationCanceledException) { Console.WriteLine("Startup cancellation: observed and recorder cleanup completed."); }
}

try
{
    await capture.StartAsync(repeatRequest with { OutputPath = Path.ChangeExtension(output, ".invalid.mp4"),
        Target = new CaptureTarget(CaptureTargetKind.Window, "1", WindowHandle: new IntPtr(1)) });
    throw new InvalidOperationException("Invalid window unexpectedly started.");
}
catch (ArgumentException ex) { Console.WriteLine($"Invalid target failure: handled before native start ({ex.Message})"); }

if (audioDevices.Any(d => d.Kind == AudioDeviceKind.SystemLoopback && d.IsDefault))
{
    var audioOutput = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "-loopback.mp4");
    var tonePath = Path.Combine(Path.GetTempPath(), "frametrail-capture-probe-tone.wav");
    WriteTone(tonePath, seconds: 3);
    var audioRequest = repeatRequest with { OutputPath = audioOutput, Audio = new AudioOptions(RecordSystemAudio: true) };
    await capture.StartAsync(audioRequest);
    NativeAudio.PlaySound(tonePath, IntPtr.Zero, 0x00020001);
    await Task.Delay(TimeSpan.FromSeconds(3));
    NativeAudio.PlaySound(null, IntPtr.Zero, 1);
    var audioResult = await capture.StopAsync();
    Console.WriteLine($"Default system loopback recording: {audioResult.OutputPath}, bytes={(File.Exists(audioOutput) ? new FileInfo(audioOutput).Length : 0)}");
}
else Console.WriteLine("System loopback probe skipped: no default loopback device was enumerated.");

static void WriteTone(string path, int seconds)
{
    const int sampleRate = 44100;
    var sampleCount = sampleRate * seconds;
    using var writer = new BinaryWriter(File.Create(path));
    writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + sampleCount * 2);
    writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1);
    writer.Write((short)1); writer.Write(sampleRate); writer.Write(sampleRate * 2); writer.Write((short)2); writer.Write((short)16);
    writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(sampleCount * 2);
    for (var i = 0; i < sampleCount; i++) writer.Write((short)(Math.Sin(2 * Math.PI * 440 * i / sampleRate) * short.MaxValue * 0.15));
}

internal static class NativeAudio
{
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool PlaySound(string? sound, IntPtr module, uint flags);
}
