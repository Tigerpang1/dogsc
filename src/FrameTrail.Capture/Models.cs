namespace FrameTrail.Capture;

public enum CaptureTargetKind { Display, Window, Region }
public enum CaptureState { Idle, Starting, Recording, Stopping, Completed, Cancelled, Faulted }
public enum EncoderPreference { Automatic, Hardware, Software }
public enum EncoderMode { Unknown, Hardware, Software }
public enum AudioDeviceKind { SystemLoopback, Microphone }
public enum InputEventKind { Move, MouseDown, MouseUp, KeyDown, KeyUp }
public enum MouseButton { None, Left, Right, Middle, X1, X2 }

public sealed record PixelRect(int X, int Y, int Width, int Height);
public sealed record CaptureTarget(CaptureTargetKind Kind, string Id, PixelRect? Bounds = null, IntPtr WindowHandle = default);
public sealed record DisplayInfo(string Id, string Name, PixelRect Bounds, bool IsPrimary);
public sealed record WindowInfo(string Id, string Title, int? ProcessId, IntPtr Handle, PixelRect Bounds);
public sealed record AudioDeviceInfo(string Id, string Name, AudioDeviceKind Kind, bool IsDefault);
public sealed record VideoOptions(int FrameRate = 30, int? Width = null, int? Height = null,
    EncoderPreference EncoderPreference = EncoderPreference.Automatic, int Quality = 75, int? Bitrate = null);
public sealed record AudioOptions(bool RecordSystemAudio = false, string? SystemAudioDeviceId = null,
    bool RecordMicrophone = false, string? MicrophoneDeviceId = null);
public sealed record InputTrackingOptions(bool Enabled = true, int SampleRate = 60, string? OutputPath = null);
public sealed record RecordingRequest(string OutputPath, CaptureTarget Target,
    VideoOptions? Video = null, AudioOptions? Audio = null, InputTrackingOptions? InputTracking = null);
public sealed record InputEvent(long StopwatchTicks, InputEventKind Kind, int X, int Y,
    double NormalizedX, double NormalizedY, MouseButton MouseButton, string? KeyName,
    bool Shift, bool Control, bool Alt, bool Windows);
public sealed record RecordingResult(string OutputPath, long StartStopwatchTicks, long StopStopwatchTicks,
    long StopwatchFrequency, EncoderMode EncoderMode, string EncoderName,
    int Width, int Height, int FrameRate, string? InputEventsPath, IReadOnlyList<string> Warnings);
public sealed record CaptureStateChanged(CaptureState State, long StopwatchTicks, string? Detail = null);
public sealed record CaptureError(string Message, Exception? Exception = null);

public interface IFrameCaptureService : IAsyncDisposable
{
    CaptureState State { get; }
    event EventHandler<CaptureStateChanged>? StateChanged;
    event EventHandler<CaptureError>? ErrorOccurred;
    event EventHandler<InputEvent>? InputEventRecorded;
    Task<IReadOnlyList<DisplayInfo>> EnumerateDisplaysAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WindowInfo>> EnumerateWindowsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AudioDeviceInfo>> EnumerateAudioDevicesAsync(CancellationToken cancellationToken = default);
    Task StartAsync(RecordingRequest request, CancellationToken cancellationToken = default);
    Task<RecordingResult> StopAsync(CancellationToken cancellationToken = default);
}
