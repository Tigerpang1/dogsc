using System.Diagnostics;
using System.Runtime.InteropServices;
using ScreenRecorderLib;
using NativeAudioOptions = ScreenRecorderLib.AudioOptions;
using NativeVideoOptions = ScreenRecorderLib.VideoEncoderOptions;

namespace FrameTrail.Capture;

public sealed class FrameCaptureService : IFrameCaptureService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Recorder? _recorder;
    private InputSampler? _inputSampler;
    private RecordingRequest? _request;
    private TaskCompletionSource<bool>? _recordingStarted;
    private TaskCompletionSource<string>? _recordingFinished;
    private CaptureState _state = CaptureState.Idle;
    private long _startTicks, _stopTicks;
    private int _width, _height, _frameRate;
    private string _encoderName = "H.264 Media Foundation";
    private EncoderMode _encoderMode = EncoderMode.Unknown;
    private string? _activeOutputPath;
    private readonly List<string> _warnings = [];

    public CaptureState State => _state;
    public event EventHandler<CaptureStateChanged>? StateChanged;
    public event EventHandler<CaptureError>? ErrorOccurred;
    public event EventHandler<InputEvent>? InputEventRecorded;

    public Task<IReadOnlyList<DisplayInfo>> EnumerateDisplaysAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        return Task.Run<IReadOnlyList<DisplayInfo>>(() =>
        {
            using var dpi = WindowsNative.UsePhysicalCoordinates();
            var native = Recorder.GetDisplays().ToDictionary(d => d.DeviceName, StringComparer.OrdinalIgnoreCase);
            return WindowsNative.EnumerateDisplays().Select(d => native.TryGetValue(d.Id, out var item)
                ? d with { Name = string.IsNullOrWhiteSpace(item.FriendlyName) ? d.Name : item.FriendlyName }
                : d).ToArray();
        }, cancellationToken);
    }

    public Task<IReadOnlyList<WindowInfo>> EnumerateWindowsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        return Task.Run<IReadOnlyList<WindowInfo>>(() =>
        {
            using var dpi = WindowsNative.UsePhysicalCoordinates();
            return Recorder.GetWindows().Where(w => w.Handle != IntPtr.Zero && WindowsNative.IsWindow(w.Handle))
                .Select(w =>
                {
                    WindowsNative.TryGetVisibleWindowRect(w.Handle, out var rect);
                    var title = w.Title ?? string.Empty;
                    var pid = w.Pid;
                    return new WindowInfo(w.Handle.ToInt64().ToString("X"), title, pid, w.Handle, WindowsNative.ToPixelRect(rect));
                })
                .Where(w => w.Bounds.Width > 0 && w.Bounds.Height > 0 && !string.IsNullOrWhiteSpace(w.Title))
                .OrderBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }, cancellationToken);
    }

    public Task<IReadOnlyList<AudioDeviceInfo>> EnumerateAudioDevicesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        return Task.Run<IReadOnlyList<AudioDeviceInfo>>(() => Recorder.GetSystemAudioLoopbackDevices().Select(d => new AudioDeviceInfo(d.DeviceName, d.FriendlyName, AudioDeviceKind.SystemLoopback, d.IsDefaultDevice))
            .Concat(Recorder.GetSystemAudioCaptureDevices().Select(d => new AudioDeviceInfo(d.DeviceName, d.FriendlyName, AudioDeviceKind.Microphone, d.IsDefaultDevice))).ToArray(), cancellationToken);
    }

    public async Task StartAsync(RecordingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state is CaptureState.Starting or CaptureState.Recording or CaptureState.Stopping)
                throw new InvalidOperationException("A recording is already active.");
            EnsureWindows();
            _request = request;
            _warnings.Clear();
            ValidateRequest(request);
            _activeOutputPath = Path.GetFullPath(request.OutputPath);
            SetState(CaptureState.Starting);
            var options = CreateRecorderOptions(request);
            _recordingStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _recordingFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _recorder = Recorder.CreateRecorder(options);
            _recorder.OnStatusChanged += OnStatusChanged;
            _recorder.OnRecordingFailed += OnRecordingFailed;
            _recorder.OnRecordingComplete += OnRecordingComplete;
            var tracking = request.InputTracking ?? new InputTrackingOptions();
            if (tracking.Enabled)
            {
                try
                {
                    _inputSampler = new InputSampler(tracking, () => ResolveTargetBounds(request.Target), evt => InputEventRecorded?.Invoke(this, evt));
                    _inputSampler.Start();
                }
                catch (Exception ex)
                {
                    _warnings.Add($"Input tracking unavailable: {ex.Message}");
                    ErrorOccurred?.Invoke(this, new CaptureError("Input tracking could not be started.", ex));
                }
            }
            _startTicks = Stopwatch.GetTimestamp();
            await Task.Run(() => _recorder.Record(_activeOutputPath), cancellationToken).ConfigureAwait(false);
            await _recordingStarted.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            if (_recorder is not null)
            {
                try { await Task.Run(_recorder.Stop).ConfigureAwait(false); } catch { }
                await DisposeRecordingComponentsAsync().ConfigureAwait(false);
            }
            DeleteIncompleteOutput();
            SetState(CaptureState.Cancelled, ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            if (_recorder is not null)
            {
                try { await Task.Run(_recorder.Stop).ConfigureAwait(false); } catch { }
                await DisposeRecordingComponentsAsync().ConfigureAwait(false);
            }
            DeleteIncompleteOutput();
            SetState(CaptureState.Faulted, ex.Message);
            ErrorOccurred?.Invoke(this, new CaptureError(ex.Message, ex));
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task<RecordingResult> StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state != CaptureState.Recording || _recorder is null || _recordingFinished is null)
                throw new InvalidOperationException("There is no active recording.");
            SetState(CaptureState.Stopping);
            _stopTicks = Stopwatch.GetTimestamp();
            await Task.Run(_recorder.Stop).ConfigureAwait(false);
            var output = await _recordingFinished.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            var inputPath = _inputSampler?.OutputPath;
            await DisposeRecordingComponentsAsync().ConfigureAwait(false);
            SetState(CaptureState.Completed);
            _activeOutputPath = null;
            return new RecordingResult(output, _startTicks, _stopTicks, Stopwatch.Frequency, _encoderMode,
                _encoderName, _width, _height, _frameRate, inputPath, _warnings.ToArray());
        }
        catch (Exception ex)
        {
            SetState(CaptureState.Faulted, ex.Message);
            ErrorOccurred?.Invoke(this, new CaptureError(ex.Message, ex));
            throw;
        }
        finally { _gate.Release(); }
    }

    private void ValidateRequest(RecordingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OutputPath)) throw new ArgumentException("OutputPath is required.", nameof(request));
        var video = request.Video ?? new VideoOptions();
        if (video.FrameRate is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(request), "FrameRate must be between 1 and 120.");
        if (!Enum.IsDefined(request.Target.Kind)) throw new ArgumentOutOfRangeException(nameof(request));
        if (request.Target.Kind == CaptureTargetKind.Region && request.Target.Bounds is null) throw new ArgumentException("A region target requires physical pixel bounds.", nameof(request));
        if (request.Target.Kind == CaptureTargetKind.Window && request.Target.WindowHandle == IntPtr.Zero) throw new ArgumentException("A window target requires a valid HWND.", nameof(request));
        var path = Path.GetFullPath(request.OutputPath);
        if (!path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("OutputPath must end in .mp4.", nameof(request));
        if (File.Exists(path)) throw new IOException($"Output file already exists: {path}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    private RecorderOptions CreateRecorderOptions(RecordingRequest request)
    {
        var video = request.Video ?? new VideoOptions();
        var targetBounds = ResolveTargetBounds(request.Target);
        _width = Even(video.Width ?? (video.Height is int requestedHeight ? (int)Math.Round((double)requestedHeight * targetBounds.Width / targetBounds.Height) : targetBounds.Width));
        _height = Even(video.Height ?? (video.Width is int requestedWidth ? (int)Math.Round((double)requestedWidth * targetBounds.Height / targetBounds.Width) : targetBounds.Height));
        _frameRate = video.FrameRate;
        if (video.EncoderPreference == EncoderPreference.Software) _encoderMode = EncoderMode.Software;
        else _encoderMode = EncoderMode.Unknown;
        var srcOptions = new SourceOptions();
        RecordingSourceBase source;
        switch (request.Target.Kind)
        {
            case CaptureTargetKind.Display:
                source = new DisplayRecordingSource(request.Target.Id) { RecorderApi = RecorderApi.DesktopDuplication, IsCursorCaptureEnabled = false };
                break;
            case CaptureTargetKind.Window:
                source = new WindowRecordingSource(request.Target.WindowHandle) { IsCursorCaptureEnabled = false };
                break;
            case CaptureTargetKind.Region:
                var bounds = request.Target.Bounds!;
                var display = WindowsNative.EnumerateDisplays().FirstOrDefault(d => Contains(d.Bounds, bounds));
                if (display is null) throw new ArgumentException("The selected region must fit inside one display.", nameof(request));
                source = new DisplayRecordingSource(display.Id) { RecorderApi = RecorderApi.DesktopDuplication, IsCursorCaptureEnabled = false,
                    SourceRect = new ScreenRecorderLib.ScreenRect(bounds.X - display.Bounds.X, bounds.Y - display.Bounds.Y, bounds.Width, bounds.Height) };
                break;
            default: throw new ArgumentOutOfRangeException();
        }
        srcOptions.RecordingSources = [source];
        var encoder = new H264VideoEncoder();
        var videoOptions = new NativeVideoOptions
        {
            Encoder = encoder, Framerate = video.FrameRate, Quality = Math.Clamp(video.Quality, 1, 100),
            IsHardwareEncodingEnabled = video.EncoderPreference != EncoderPreference.Software,
            IsFixedFramerate = false, IsMp4FastStartEnabled = false, IsFragmentedMp4Enabled = false, IsLowLatencyEnabled = true
        };
        if (video.Bitrate is int bitrate) videoOptions.Bitrate = bitrate;
        if (video.EncoderPreference == EncoderPreference.Hardware)
            _warnings.Add("Hardware encoding was requested; ScreenRecorderLib does not expose which encoder Media Foundation ultimately selected.");
        else if (video.EncoderPreference == EncoderPreference.Automatic)
            _warnings.Add("Encoder mode is reported as Unknown because ScreenRecorderLib does not expose Media Foundation encoder selection.");
        var outputOptions = new OutputOptions { OutputFrameSize = new ScreenRecorderLib.ScreenSize(_width, _height), RecorderMode = RecorderMode.Video };
        var audioOptions = CreateAudioOptions(request.Audio ?? new AudioOptions());
        var mouse = new MouseOptions { IsMousePointerEnabled = false, IsMouseClicksDetected = false };
        return new RecorderOptions { SourceOptions = srcOptions, VideoEncoderOptions = videoOptions, OutputOptions = outputOptions, AudioOptions = audioOptions, MouseOptions = mouse };
    }

    private static NativeAudioOptions CreateAudioOptions(AudioOptions requested)
    {
        var options = new NativeAudioOptions { IsAudioEnabled = requested.RecordSystemAudio || requested.RecordMicrophone };
        var sources = new List<AudioSourceBase>();
        if (requested.RecordSystemAudio) sources.Add(string.IsNullOrWhiteSpace(requested.SystemAudioDeviceId) ? LoopbackAudioSource.Default : new LoopbackAudioSource(requested.SystemAudioDeviceId));
        if (requested.RecordMicrophone) sources.Add(string.IsNullOrWhiteSpace(requested.MicrophoneDeviceId) ? CaptureAudioSource.Default : new CaptureAudioSource(requested.MicrophoneDeviceId));
        options.AudioSources = sources;
        return options;
    }

    private PixelRect ResolveTargetBounds(CaptureTarget target)
    {
        using var dpi = WindowsNative.UsePhysicalCoordinates();
        if (target.Kind == CaptureTargetKind.Region) return target.Bounds!;
        if (target.Kind == CaptureTargetKind.Window)
        {
            if (!WindowsNative.IsWindow(target.WindowHandle) || !WindowsNative.TryGetVisibleWindowRect(target.WindowHandle, out var rect)) throw new ArgumentException("Window handle is no longer valid.", nameof(target));
            return WindowsNative.ToPixelRect(rect);
        }
        return WindowsNative.EnumerateDisplays().FirstOrDefault(d => string.Equals(d.Id, target.Id, StringComparison.OrdinalIgnoreCase))?.Bounds
            ?? throw new ArgumentException("Display device was not found.", nameof(target));
    }
    private static bool Contains(PixelRect outer, PixelRect inner) => inner.Width > 0 && inner.Height > 0 && inner.X >= outer.X && inner.Y >= outer.Y && inner.X + inner.Width <= outer.X + outer.Width && inner.Y + inner.Height <= outer.Y + outer.Height;
    private static int Even(int value) => Math.Max(2, value - value % 2);
    private static void EnsureWindows() { if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("FrameTrail.Capture requires Windows."); }

    private void OnStatusChanged(object? sender, RecordingStatusEventArgs e)
    {
        if (e.Status == ScreenRecorderLib.RecorderStatus.Recording)
        {
            SetState(CaptureState.Recording);
            _recordingStarted?.TrySetResult(true);
        }
        else if (e.Status == ScreenRecorderLib.RecorderStatus.Finishing) SetState(CaptureState.Stopping);
    }
    private void OnRecordingFailed(object? sender, RecordingFailedEventArgs e)
    {
        var exception = new InvalidOperationException(e.Error);
        _recordingStarted?.TrySetException(exception); _recordingFinished?.TrySetException(exception);
        ErrorOccurred?.Invoke(this, new CaptureError(e.Error, exception));
        SetState(CaptureState.Faulted, e.Error);
    }
    private void OnRecordingComplete(object? sender, RecordingCompleteEventArgs e) => _recordingFinished?.TrySetResult(e.FilePath);
    private void SetState(CaptureState state, string? detail = null)
    {
        _state = state;
        StateChanged?.Invoke(this, new CaptureStateChanged(state, Stopwatch.GetTimestamp(), detail));
    }
    private void DeleteIncompleteOutput()
    {
        if (_activeOutputPath is null) return;
        try { if (File.Exists(_activeOutputPath)) File.Delete(_activeOutputPath); } catch { }
        _activeOutputPath = null;
    }
    private async Task DisposeRecordingComponentsAsync()
    {
        _recorder?.Dispose(); _recorder = null;
        if (_inputSampler is not null) { await _inputSampler.DisposeAsync().ConfigureAwait(false); _inputSampler = null; }
    }
    public async ValueTask DisposeAsync()
    {
        if (_state == CaptureState.Recording) { try { await StopAsync().ConfigureAwait(false); } catch { } }
        await DisposeRecordingComponentsAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}



