namespace FrameTrail.Capture;

public enum CaptureTargetKind { Display, Window, Region }
public enum CaptureState { Idle, Starting, Recording, Paused, Stopping, Completed, Cancelled, Faulted }
public enum EncoderPreference { Automatic, Hardware, Software }
public enum EncoderMode { Unknown, Hardware, Software }
public enum AudioDeviceKind { SystemLoopback, Microphone }
public enum CaptureTrackKind { Camera, SystemAudio, Microphone }
public enum CaptureTrackState { Starting, Recording, Paused, Stopping, Completed, Faulted }
public enum InputEventKind { Move, MouseDown, MouseUp, KeyDown, KeyUp }
public enum MouseButton { None, Left, Right, Middle, X1, X2 }

public sealed record PixelRect(int X, int Y, int Width, int Height);
public sealed record CaptureTarget(CaptureTargetKind Kind, string Id, PixelRect? Bounds = null, IntPtr WindowHandle = default);
public sealed record DisplayInfo(string Id, string Name, PixelRect Bounds, bool IsPrimary);
public sealed record WindowInfo(string Id, string Title, int? ProcessId, IntPtr Handle, PixelRect Bounds);
public sealed record AudioDeviceInfo(string Id, string Name, AudioDeviceKind Kind, bool IsDefault);
public sealed record CameraFormatInfo(int Index, string Format, int Width, int Height, double FrameRate, int AverageBitrate);
public sealed record CameraInfo(string Id, string Name, IReadOnlyList<CameraFormatInfo> Formats);
public sealed record VideoOptions(int FrameRate = 30, int? Width = null, int? Height = null,
    EncoderPreference EncoderPreference = EncoderPreference.Automatic, int Quality = 75, int? Bitrate = null,
    bool FixedFrameRate = false);
public sealed record AudioOptions(bool RecordSystemAudio = false, string? SystemAudioDeviceId = null,
    bool RecordMicrophone = false, string? MicrophoneDeviceId = null);
public sealed record InputTrackingOptions(bool Enabled = true, int SampleRate = 60, string? OutputPath = null);
public sealed record RecordingRequest(string OutputPath, CaptureTarget Target,
    VideoOptions? Video = null, AudioOptions? Audio = null, InputTrackingOptions? InputTracking = null);
public sealed record InputEvent(long StopwatchTicks, InputEventKind Kind, int X, int Y,
    double NormalizedX, double NormalizedY, MouseButton MouseButton, string? KeyName,
    bool Shift, bool Control, bool Alt, bool Windows, string? CursorAsset = null);
public sealed record RecordingResult(string OutputPath, long StartStopwatchTicks, long StopStopwatchTicks,
    long StopwatchFrequency, EncoderMode EncoderMode, string EncoderName,
    int Width, int Height, int FrameRate, string? InputEventsPath, IReadOnlyList<string> Warnings,
    IReadOnlyList<CapturePauseInterval>? PausedIntervals = null,
    long? FirstFrameObservedStopwatchTicks = null, int? FirstFrameNumber = null,
    long? FirstFrameSystemTimestampUnixMilliseconds = null,
    long? ProgressEstimatedFirstFrameStopwatchTicks = null,
    long? StopObservedStopwatchTicks = null);
public sealed record CapturePauseInterval(long PauseStartStopwatchTicks, long ResumeStopwatchTicks);
public sealed record RecoverableRecordingInfo(string ManifestPath, string OriginalOutputPath, IReadOnlyList<string> Segments);
public sealed record CameraRecordingRequest(string OutputPath, string CameraDeviceId, int? FormatIndex = null,
    VideoOptions? Video = null);
public sealed record AudioTrackRequest(string OutputPath, AudioDeviceKind DeviceKind, string? DeviceId = null);
public sealed record CaptureTrackHandle(Guid TrackId, CaptureTrackKind Kind, string MediaPath,
    long StartStopwatchTicks, long StopwatchFrequency);
public sealed record CaptureTrackResult(CaptureTrackHandle Handle, long StopStopwatchTicks,
    int Width, int Height, int FrameRate, EncoderMode EncoderMode, IReadOnlyList<string> Warnings,
    IReadOnlyList<CapturePauseInterval> PausedIntervals);
public sealed record CaptureTrackStateChanged(Guid TrackId, CaptureTrackKind Kind, CaptureTrackState State,
    long StopwatchTicks, string? Detail = null);
public sealed record CaptureStateChanged(CaptureState State, long StopwatchTicks, string? Detail = null);
public sealed record CaptureError(string Message, Exception? Exception = null);

public interface IFrameCaptureService : IAsyncDisposable
{
    CaptureState State { get; }
    RecordingResult? LatestCompletedResult { get; }
    long? CurrentCapturePrivateBytes { get; }
    event EventHandler<CaptureStateChanged>? StateChanged;
    event EventHandler<CaptureError>? ErrorOccurred;
    event EventHandler<InputEvent>? InputEventRecorded;
    event Action<RecordingResult>? RecordingCompleted;
    Task<IReadOnlyList<DisplayInfo>> EnumerateDisplaysAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WindowInfo>> EnumerateWindowsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AudioDeviceInfo>> EnumerateAudioDevicesAsync(CancellationToken cancellationToken = default);
    Task StartAsync(RecordingRequest request, CancellationToken cancellationToken = default);
    Task PauseAsync(CancellationToken cancellationToken = default);
    Task ResumeAsync(CancellationToken cancellationToken = default);
    Task<RecordingResult> StopAsync(CancellationToken cancellationToken = default);
}

public interface ICaptureTrackService : IAsyncDisposable
{
    event EventHandler<CaptureTrackStateChanged>? StateChanged;
    event EventHandler<CaptureError>? ErrorOccurred;
    Task<IReadOnlyList<CameraInfo>> EnumerateCamerasAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AudioDeviceInfo>> EnumerateAudioDevicesAsync(CancellationToken cancellationToken = default);
    Task<CaptureTrackHandle> StartCameraAsync(CameraRecordingRequest request, CancellationToken cancellationToken = default);
    Task<CaptureTrackHandle> StartAudioTrackAsync(AudioTrackRequest request, CancellationToken cancellationToken = default);
    Task PauseTrackAsync(Guid trackId, CancellationToken cancellationToken = default);
    Task ResumeTrackAsync(Guid trackId, CancellationToken cancellationToken = default);
    Task<CaptureTrackResult> StopTrackAsync(Guid trackId, CancellationToken cancellationToken = default);
}
