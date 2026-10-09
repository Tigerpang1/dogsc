using System.Diagnostics;
using System.Collections.Concurrent;
using System.Collections.Generic;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ScreenRecorderLib;
using NativeAudioOptions = ScreenRecorderLib.AudioOptions;
using NativeVideoOptions = ScreenRecorderLib.VideoEncoderOptions;

namespace FrameTrail.Capture;

/// <summary>Records cameras as MP4 assets and individual audio inputs as PCM WAV assets.</summary>
public sealed class CaptureTrackService : ICaptureTrackService
{
    private readonly ConcurrentDictionary<Guid, TrackSession> _sessions = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly object _disposeSync = new();
    private volatile bool _disposeRequested;
    private Task? _disposeTask;

    public event EventHandler<CaptureTrackStateChanged>? StateChanged;
    public event EventHandler<CaptureError>? ErrorOccurred;

    public Task<IReadOnlyList<CameraInfo>> EnumerateCamerasAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        return Task.Run<IReadOnlyList<CameraInfo>>(() => Recorder.GetSystemVideoCaptureDevices()
            .Select(device => new CameraInfo(device.DeviceName, device.FriendlyName,
                Recorder.GetSupportedVideoCaptureFormatsForDevice(device.DeviceName).Select(format => new CameraFormatInfo(
                    format.Index, format.VideoFormatName, (int)Math.Round(format.FrameSize.Width), (int)Math.Round(format.FrameSize.Height),
                    format.Framerate, format.AverageBitrate)).ToArray()))
            .ToArray(), cancellationToken);
    }

    public Task<IReadOnlyList<AudioDeviceInfo>> EnumerateAudioDevicesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        return Task.Run<IReadOnlyList<AudioDeviceInfo>>(() =>
        {
            using var enumerator = new MMDeviceEnumerator();
            using var renderDefault = TryGetDefault(enumerator, DataFlow.Render);
            using var captureDefault = TryGetDefault(enumerator, DataFlow.Capture);
            var render = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName, AudioDeviceKind.SystemLoopback,
                    string.Equals(d.ID, renderDefault?.ID, StringComparison.OrdinalIgnoreCase)));
            var capture = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName, AudioDeviceKind.Microphone,
                    string.Equals(d.ID, captureDefault?.ID, StringComparison.OrdinalIgnoreCase)));
            return render.Concat(capture).ToArray();
        }, cancellationToken);
    }

    public async Task<CaptureTrackHandle> StartCameraAsync(CameraRecordingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnterStartGateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var camera = Recorder.GetSystemVideoCaptureDevices().FirstOrDefault(x =>
                string.Equals(x.DeviceName, request.CameraDeviceId, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException("Camera device was not found.", nameof(request));
            var formats = Recorder.GetSupportedVideoCaptureFormatsForDevice(camera.DeviceName);
            var format = request.FormatIndex is int index
                ? formats.FirstOrDefault(x => x.Index == index) ?? throw new ArgumentException("Camera format index was not found.", nameof(request))
                : formats.OrderByDescending(x => x.FrameSize.Width * (long)x.FrameSize.Height)
                    .ThenByDescending(x => x.Framerate).FirstOrDefault();
            if (format is null) throw new InvalidOperationException("The camera did not report a supported capture format.");
            var source = new VideoCaptureRecordingSource(camera.DeviceName, format);
            var video = request.Video is null
                ? new VideoOptions(FrameRate: Math.Max(1, (int)Math.Round(format.Framerate)),
                    Width: (int)Math.Round(format.FrameSize.Width), Height: (int)Math.Round(format.FrameSize.Height))
                : request.Video with
                {
                    Width = request.Video.Width ?? (int)Math.Round(format.FrameSize.Width),
                    Height = request.Video.Height ?? (int)Math.Round(format.FrameSize.Height)
                };
            var options = CreateOptions(CaptureTrackKind.Camera, source, null, video);
            return await StartCoreAsync(CaptureTrackKind.Camera, request.OutputPath, options, cancellationToken).ConfigureAwait(false);
        }
        finally { _startGate.Release(); }
    }

    public async Task<CaptureTrackHandle> StartAudioTrackAsync(AudioTrackRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.DeviceKind is not (AudioDeviceKind.SystemLoopback or AudioDeviceKind.Microphone))
            throw new ArgumentOutOfRangeException(nameof(request));
        await EnterStartGateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var devices = await EnumerateAudioDevicesAsync(cancellationToken).ConfigureAwait(false);
            var device = string.IsNullOrWhiteSpace(request.DeviceId)
                ? devices.FirstOrDefault(x => x.Kind == request.DeviceKind && x.IsDefault)
                : devices.FirstOrDefault(x => x.Kind == request.DeviceKind && string.Equals(x.Id, request.DeviceId, StringComparison.OrdinalIgnoreCase));
            if (device is null) throw new ArgumentException("Audio device was not found.", nameof(request));
            var kind = request.DeviceKind == AudioDeviceKind.SystemLoopback ? CaptureTrackKind.SystemAudio : CaptureTrackKind.Microphone;
            ValidateAudioOutput(request.OutputPath);
            using var enumerator = new MMDeviceEnumerator();
            using var endpoint = enumerator.GetDevice(device.Id);
            TimestampedWasapiCapture? audioCapture = null;
            WaveFileWriter? waveWriter = null;
            FileStream? outputStream = null;
            var ownsOutputFile = false;
            TrackSession? session = null;
            var registered = false;
            try
            {
                audioCapture = new TimestampedWasapiCapture(endpoint, request.DeviceKind == AudioDeviceKind.SystemLoopback);
                outputStream = new FileStream(Path.GetFullPath(request.OutputPath), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                ownsOutputFile = true;
                waveWriter = new WaveFileWriter(outputStream, audioCapture.WaveFormat);
                outputStream = null; // The WaveFileWriter now owns the stream.
                session = new TrackSession(kind, request.OutputPath) { AudioCapture = audioCapture, WaveFileWriter = waveWriter };
                session.Warnings.Add($"WASAPI audio format: {audioCapture.WaveFormat}; standalone WAV contains no video.");
                session.Warnings.Add("WASAPI packet QPC timestamps are used for audio sample placement; timestamp-error packets fall back to callback arrival time.");
                audioCapture.PacketAvailable += (packet, frames, devicePosition, qpcPosition, arrivalTicks, flags) =>
                {
                    try
                    {
                        var packetDuration = frames * 1000d / Math.Max(1, audioCapture.WaveFormat.SampleRate);
                        if (packetDuration > session.MaxAudioPacketMilliseconds) session.MaxAudioPacketMilliseconds = packetDuration;
                        var timing = AudioPacketClock.Resolve(qpcPosition, arrivalTicks, frames,
                            audioCapture.WaveFormat.SampleRate, flags, Stopwatch.Frequency);
                        lock (session.AudioWriteGate)
                        {
                            if (session.AudioStartTicks == 0) return;
                            session.AudioDiagnostics ??= new AudioPacketDiagnostics(session.AudioStartTicks, Stopwatch.Frequency);
                            var startFrame = (long)((decimal)(timing.StartStopwatchTicks - session.AudioStartTicks) * audioCapture.WaveFormat.SampleRate / Stopwatch.Frequency);
                            var mismatch = session.ExpectedDevicePosition is long expected && devicePosition != expected && !timing.DataDiscontinuity;
                            session.AudioDiagnostics.Packet(arrivalTicks, frames, (flags & AudioClientBufferFlags.Silent) != 0,
                                timing.ArrivalLatencyMilliseconds, timing.UsedArrivalFallback, timing.TimestampError,
                                timing.DataDiscontinuity, mismatch);
                            session.AudioPackets.Enqueue(new AudioPacket(startFrame, packet, timing, arrivalTicks,
                                (flags & AudioClientBufferFlags.Silent) != 0));
                            session.AudioPacketCount++;
                            if (session.FirstDevicePosition is null)
                            {
                                session.FirstDevicePosition = devicePosition;
                                session.FirstPacketOffsetMilliseconds = (timing.StartStopwatchTicks - session.AudioStartTicks) * 1000d / Stopwatch.Frequency;
                            }
                            if (mismatch)
                                session.DevicePositionMismatchCount++;
                            session.ExpectedDevicePosition = checked(devicePosition + frames);
                            if (timing.UsedArrivalFallback) session.TimestampFallbackCount++;
                            if (timing.TimestampError) session.TimestampErrorCount++;
                            if (timing.DataDiscontinuity) session.DataDiscontinuityCount++;
                            if (double.IsFinite(timing.ArrivalLatencyMilliseconds)) AddLatencySample(session, timing.ArrivalLatencyMilliseconds);
                        }
                    }
                    catch (Exception ex) { NotifyError(new CaptureError($"WASAPI audio write failed: {ex.Message}", ex)); }
                };
                audioCapture.RecordingStopped += exception =>
                {
                    session.AudioRecordingStopped = true;
                    if (exception is not null)
                    {
                        session.AudioFailure = exception;
                        session.AudioStopped.TrySetException(exception);
                        try { SetState(session, CaptureTrackState.Faulted, Stopwatch.GetTimestamp(), exception.Message); } catch { }
                        NotifyError(new CaptureError($"Audio capture stopped unexpectedly: {exception.Message}", exception));
                    }
                    else session.AudioStopped.TrySetResult(true);
                };
                if (!_sessions.TryAdd(session.Id, session)) throw new InvalidOperationException("Could not register audio track.");
                registered = true;
                SetState(session, CaptureTrackState.Starting);
                session.AudioStartTicks = Stopwatch.GetTimestamp();
                session.AudioDiagnostics = new AudioPacketDiagnostics(session.AudioStartTicks, Stopwatch.Frequency);
                audioCapture.StartRecording();
                var startedTicks = session.AudioStartTicks;
                session.Handle = new CaptureTrackHandle(session.Id, kind, session.OutputPath, startedTicks, Stopwatch.Frequency);
                session.AudioPumpCancellation = new CancellationTokenSource();
                session.AudioPumpTask = Task.Run(() => PumpAudioTimelineAsync(session, session.AudioPumpCancellation.Token));
                SetState(session, CaptureTrackState.Recording, startedTicks);
                return session.Handle;
            }
            catch
            {
                if (registered && session is not null) _sessions.TryRemove(session.Id, out _);
                if (session?.AudioPumpCancellation is { } pumpCancellation)
                {
                    pumpCancellation.Cancel();
                    if (session.AudioPumpTask is not null)
                    {
                        try { await session.AudioPumpTask.ConfigureAwait(false); } catch { }
                    }
                    pumpCancellation.Dispose();
                    session.AudioPumpCancellation = null;
                    session.AudioPumpTask = null;
                }
                try { audioCapture?.StopRecording(); } catch { }
                try { audioCapture?.Dispose(); } catch { }
                try { outputStream?.Dispose(); } catch { }
                try { waveWriter?.Dispose(); } catch { }
                if (ownsOutputFile)
                {
                    try { if (File.Exists(request.OutputPath)) File.Delete(request.OutputPath); } catch { }
                }
                if (session is not null) try { SetState(session, CaptureTrackState.Faulted, "Audio track startup failed."); } catch { }
                throw;
            }
        }
        finally { _startGate.Release(); }
    }

    public Task PauseTrackAsync(Guid trackId, CancellationToken cancellationToken = default) => SetTrackPausedAsync(trackId, true, cancellationToken);
    public Task ResumeTrackAsync(Guid trackId, CancellationToken cancellationToken = default) => SetTrackPausedAsync(trackId, false, cancellationToken);

    public async Task<CaptureTrackResult> StopTrackAsync(Guid trackId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(trackId, out var session)) throw new KeyNotFoundException($"Capture track {trackId} was not found.");
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!CaptureFailurePolicy.CanStopTrack(session.State))
                throw new InvalidOperationException("The capture track is not active.");
            var backendFailure = session.FailureException;
            var stopTicks = Stopwatch.GetTimestamp();
            lock (session.AudioWriteGate)
            {
                if (session.PauseStartTicks != 0)
                {
                    session.PausedIntervals.Add(new CapturePauseInterval(session.PauseStartTicks, stopTicks));
                    session.PauseStartTicks = 0;
                }
                if (session.AudioCapture is not null) session.AudioPaused = true;
            }
            SetState(session, CaptureTrackState.Stopping);
            if (session.AudioCapture is not null)
            {
                session.AudioPumpCancellation?.Cancel();
                if (session.AudioPumpTask is not null)
                {
                    try { await session.AudioPumpTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
                }
                if (!session.AudioRecordingStopped) session.AudioCapture.StopRecording();
                try { await session.AudioStopped.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (session.AudioFailure is not null && ex is not OperationCanceledException)
                {
                    session.Warnings.Add($"Audio capture ended unexpectedly; WAV was finalized with clock-filled silence where needed: {session.AudioFailure.Message}");
                }
                lock (session.AudioWriteGate)
                {
                    var blockAlign = Math.Max(1, session.AudioCapture.WaveFormat.BlockAlign);
                    var activeTicks = GetAudioActiveTicks(session, stopTicks);
                    var targetFrames = Math.Max(0, (long)(activeTicks * (double)session.AudioCapture.WaveFormat.SampleRate / Stopwatch.Frequency));
                    var sourceStopFrame = Math.Max(0, (long)((decimal)(stopTicks - session.AudioStartTicks) * session.AudioCapture.WaveFormat.SampleRate / Stopwatch.Frequency));
                    DrainAudioPackets(session, long.MaxValue, sourceStopFrame, targetFrames);
                    WriteAudioSilence(session, Math.Max(0, targetFrames - session.AudioFramesWritten), blockAlign,
                        stopTicks, AudioSilenceSource.Finalization);
                }
                session.AudioPumpCancellation?.Dispose(); session.AudioPumpCancellation = null; session.AudioPumpTask = null;
                session.WaveFileWriter?.Dispose();
                session.AudioCapture.Dispose();
                if (session.AudioDiagnostics is { } diagnostics)
                {
                    var diagnosticsPath = session.OutputPath + ".diagnostics.json";
                    var diagnosticTicks = Stopwatch.GetTimestamp();
                    var totals = diagnostics.Snapshot(diagnosticTicks);
                    try
                    {
                        await diagnostics.WriteJsonAsync(diagnosticsPath, diagnosticTicks, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception diagnosticsException)
                    {
                        session.Warnings.Add($"Audio packet diagnostics file could not be written: {diagnosticsException.Message}; totals packets={totals.PacketCount}, packetFrames={totals.PacketFrames}, silentFlagFrames={totals.SilentFlagFrames}, writtenFrames={totals.ActuallyWrittenFrames}, overlapDroppedFrames={totals.OverlapDroppedFrames}, pumpSilenceFrames={totals.PumpGeneratedSilenceFrames}.");
                    }
                }
                if (session.PausedIntervals.Count > 0)
                    session.Warnings.Add($"Audio pause boundaries were clipped to WASAPI packet QPC timestamps; largest packet was {session.MaxAudioPacketMilliseconds:F1} ms.");
                session.Warnings.Add($"WASAPI packet timing: packets={session.AudioPacketCount}, firstPacketOffset={session.FirstPacketOffsetMilliseconds:F3} ms, firstDevicePosition={session.FirstDevicePosition?.ToString() ?? "unknown"}, timestampFallbacks={session.TimestampFallbackCount}, timestampErrors={session.TimestampErrorCount}, discontinuities={session.DataDiscontinuityCount}, devicePositionMismatches={session.DevicePositionMismatchCount}, sampledArrivalLatencyP95={Percentile(session.ArrivalLatencySamples, 0.95):F3} ms (reservoir {session.ArrivalLatencySamples.Count}/{session.ArrivalLatencySampleCount}).");
            }
            else
            {
                try { await Task.Run(session.Recorder.Stop, cancellationToken).ConfigureAwait(false); }
                catch (Exception stopException)
                {
                    backendFailure ??= session.FailureException;
                    if (backendFailure is null) throw;
                    session.Warnings.Add("Camera recorder stop reported an error after backend failure: " + stopException.Message);
                }
                backendFailure ??= session.FailureException;
                try
                {
                    if (backendFailure is null)
                    {
                        var finished = session.Finished.Task;
                        var failure = session.FailureSignal.Task;
                        var timeout = Task.Delay(TimeSpan.FromSeconds(30));
                        var winner = await Task.WhenAny(finished, failure, timeout).ConfigureAwait(false);
                        if (winner == timeout) throw new TimeoutException("The camera recorder did not report completion within 30 seconds.");
                        if (winner == failure) backendFailure = await failure.ConfigureAwait(false);
                        else await finished.ConfigureAwait(false);
                    }
                    if (backendFailure is not null)
                        await session.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                }
                catch when (backendFailure is not null && CaptureFailurePolicy.HasNonEmptyCandidate(session.OutputPath))
                {
                    session.Warnings.Add("The capture backend failed; a non-empty partial output was retained for media validation.");
                }
                if (backendFailure is not null && !CaptureFailurePolicy.HasNonEmptyCandidate(session.OutputPath))
                    throw new InvalidOperationException("Camera capture failed before producing a non-empty output: " + backendFailure.Message, backendFailure);
                if (backendFailure is not null) session.Warnings.Add("Camera capture failure: " + backendFailure.Message);
                session.Recorder.Dispose();
            }
            _sessions.TryRemove(trackId, out _);
            SetState(session, CaptureTrackState.Completed);
            return new CaptureTrackResult(session.Handle!, stopTicks, session.Width, session.Height,
                session.FrameRate, session.EncoderMode, session.Warnings.ToArray(), session.PausedIntervals.ToArray());
        }
        catch (Exception ex)
        {
            NotifyError(new CaptureError($"Capture track failed: {ex.Message}", ex));
            try { session.AudioCapture?.StopRecording(); } catch { }
            if (session.AudioPumpCancellation is { } pumpCancellation)
            {
                try { pumpCancellation.Cancel(); } catch (Exception cleanupException) { Trace.TraceError("Audio pump cancellation failed: {0}", cleanupException); }
                if (session.AudioPumpTask is { } pumpTask)
                {
                    try { await pumpTask.ConfigureAwait(false); }
                    catch (Exception cleanupException) { Trace.TraceError("Audio pump completion after track failure failed: {0}", cleanupException); }
                }
                try { pumpCancellation.Dispose(); } catch (Exception cleanupException) { Trace.TraceError("Audio pump cancellation disposal failed: {0}", cleanupException); }
                session.AudioPumpCancellation = null;
                session.AudioPumpTask = null;
            }
            try { session.WaveFileWriter?.Dispose(); } catch { }
            try { session.AudioCapture?.Dispose(); } catch { }
            try { session.Recorder?.Dispose(); } catch { }
            _sessions.TryRemove(trackId, out _);
            SetState(session, CaptureTrackState.Faulted, ex.Message);
            throw;
        }
        finally { session.Gate.Release(); }
    }

    private async Task<CaptureTrackHandle> StartCoreAsync(CaptureTrackKind kind, string outputPath,
        RecorderOptions options, CancellationToken cancellationToken)
    {
        ValidateOutput(outputPath);
        var session = new TrackSession(kind, outputPath);
        var registered = false;
        try
        {
            session.Recorder = Recorder.CreateRecorder(options);
            session.Recorder.OnStatusChanged += (_, args) => OnStatusChanged(session, args);
            session.Recorder.OnRecordingFailed += (_, args) =>
            {
                var ex = new InvalidOperationException(args.Error);
                session.FailureException = ex;
                session.FailureSignal.TrySetResult(ex);
                if (session.State == CaptureTrackState.Starting)
                {
                    session.Started.TrySetException(ex);
                    session.Finished.TrySetException(ex);
                }
                else session.Warnings.Add("Capture backend failure: " + args.Error);
                NotifyError(new CaptureError(args.Error, ex));
                SetState(session, CaptureTrackState.Faulted, args.Error);
            };
            session.Recorder.OnRecordingComplete += (_, args) => session.Finished.TrySetResult(args.FilePath);
            ConfigureOutputMetadata(session, options);
            if (!_sessions.TryAdd(session.Id, session)) throw new InvalidOperationException("Could not register capture track.");
            registered = true;
            SetState(session, CaptureTrackState.Starting);
            await Task.Run(() => session.Recorder.Record(session.OutputPath), cancellationToken).ConfigureAwait(false);
            var startedTicks = await session.Started.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            session.Handle = new CaptureTrackHandle(session.Id, kind, session.OutputPath, startedTicks, Stopwatch.Frequency);
            return session.Handle;
        }
        catch (Exception ex)
        {
            try { await Task.Run(session.Recorder.Stop).ConfigureAwait(false); } catch { }
            try { session.Recorder?.Dispose(); } catch (Exception cleanupException) { Trace.TraceError("Recorder disposal after startup failure failed: {0}", cleanupException); }
            if (registered) _sessions.TryRemove(session.Id, out _);
            try { if (File.Exists(session.OutputPath)) File.Delete(session.OutputPath); } catch { }
            SetState(session, CaptureTrackState.Faulted, ex.Message);
            throw;
        }
    }

    private async Task SetTrackPausedAsync(Guid trackId, bool pause, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(trackId, out var session)) throw new KeyNotFoundException($"Capture track {trackId} was not found.");
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var expected = pause ? CaptureTrackState.Recording : CaptureTrackState.Paused;
            if (session.State != expected) throw new InvalidOperationException(pause ? "The track is not recording." : "The track is not paused.");
            if (session.AudioCapture is not null)
            {
                var now = Stopwatch.GetTimestamp();
                if (pause)
                {
                    lock (session.AudioWriteGate) { session.AudioPaused = true; session.PauseStartTicks = now; }
                    SetState(session, CaptureTrackState.Paused, now);
                }
                else
                {
                    lock (session.AudioWriteGate)
                    {
                        session.AudioPaused = false;
                        session.PausedIntervals.Add(new CapturePauseInterval(session.PauseStartTicks, now));
                        session.PauseStartTicks = 0;
                    }
                    SetState(session, CaptureTrackState.Recording, now);
                }
                return;
            }
            session.ExpectedStatus = pause ? CaptureTrackState.Paused : CaptureTrackState.Recording;
            session.StatusChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await Task.Run(pause ? session.Recorder.Pause : session.Recorder.Resume, cancellationToken).ConfigureAwait(false);
            await session.StatusChanged.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        finally { session.StatusChanged = null; session.Gate.Release(); }
    }

    private RecorderOptions CreateOptions(CaptureTrackKind kind, RecordingSourceBase source,
        NativeAudioOptions? audio, VideoOptions video)
    {
        var camera = kind == CaptureTrackKind.Camera;
        var sourceOptions = new SourceOptions { RecordingSources = [source] };
        var options = new NativeVideoOptions
        {
            Encoder = new H264VideoEncoder(), Framerate = Math.Clamp(video.FrameRate, 1, 120),
            Quality = Math.Clamp(video.Quality, 1, 100), Bitrate = video.Bitrate ?? (camera ? 4_000_000 : 128_000),
            IsHardwareEncodingEnabled = video.EncoderPreference != EncoderPreference.Software,
            IsFixedFramerate = video.FixedFrameRate, IsMp4FastStartEnabled = false, IsFragmentedMp4Enabled = false,
            IsLowLatencyEnabled = true
        };
        var cameraFormat = camera && source is VideoCaptureRecordingSource cameraSource
            ? Recorder.GetSupportedVideoCaptureFormatsForDevice(cameraSource.DeviceName).FirstOrDefault() : null;
        var width = Even(video.Width ?? (cameraFormat is null ? 640 : (int)Math.Round(cameraFormat.FrameSize.Width)));
        var height = Even(video.Height ?? (cameraFormat is null ? 480 : (int)Math.Round(cameraFormat.FrameSize.Height)));
        var output = new OutputOptions
        {
            OutputFrameSize = new ScreenRecorderLib.ScreenSize(width, height),
            RecorderMode = RecorderMode.Video,
            IsVideoCaptureEnabled = camera
        };
        var mouse = new MouseOptions { IsMousePointerEnabled = false, IsMouseClicksDetected = false };
        return new RecorderOptions
        {
            SourceOptions = sourceOptions, VideoEncoderOptions = options, OutputOptions = output,
            AudioOptions = audio ?? new NativeAudioOptions(), MouseOptions = mouse
        };
    }

    private void ConfigureOutputMetadata(TrackSession session, RecorderOptions options)
    {
        session.Width = (int)Math.Round(options.OutputOptions.OutputFrameSize.Width);
        session.Height = (int)Math.Round(options.OutputOptions.OutputFrameSize.Height);
        session.FrameRate = options.VideoEncoderOptions.Framerate;
        session.EncoderMode = options.VideoEncoderOptions.IsHardwareEncodingEnabled ? EncoderMode.Unknown : EncoderMode.Software;
        if (session.EncoderMode == EncoderMode.Unknown)
            session.Warnings.Add("Media Foundation does not expose which encoder was selected.");
    }

    private void OnStatusChanged(TrackSession session, RecordingStatusEventArgs args)
    {
        var ticks = Stopwatch.GetTimestamp();
        switch (args.Status)
        {
            case RecorderStatus.Recording:
                if (session.State == CaptureTrackState.Paused && session.PauseStartTicks != 0)
                {
                    session.PausedIntervals.Add(new CapturePauseInterval(session.PauseStartTicks, ticks));
                    session.PauseStartTicks = 0;
                }
                if (session.State == CaptureTrackState.Starting) session.Started.TrySetResult(ticks);
                SetState(session, CaptureTrackState.Recording, ticks);
                break;
            case RecorderStatus.Paused:
                session.PauseStartTicks = ticks;
                SetState(session, CaptureTrackState.Paused, ticks);
                break;
            case RecorderStatus.Finishing:
                SetState(session, CaptureTrackState.Stopping, ticks);
                break;
        }
        if (session.State == session.ExpectedStatus) session.StatusChanged?.TrySetResult(session.State);
    }

    private void SetState(TrackSession session, CaptureTrackState state, long? ticks = null, string? detail = null)
    {
        session.State = state;
        NotifySubscribersSafely(StateChanged, this,
            new CaptureTrackStateChanged(session.Id, session.Kind, state, ticks ?? Stopwatch.GetTimestamp(), detail), nameof(StateChanged));
    }
    private void SetState(TrackSession session, CaptureTrackState state, string? detail) =>
        SetState(session, state, Stopwatch.GetTimestamp(), detail);

    private void NotifyError(CaptureError error) =>
        NotifySubscribersSafely(ErrorOccurred, this, error, nameof(ErrorOccurred));

    internal static void NotifySubscribersSafely<T>(EventHandler<T>? subscribers, object sender, T args, string eventName)
    {
        if (subscribers is null) return;
        foreach (EventHandler<T> subscriber in subscribers.GetInvocationList())
        {
            try { subscriber(sender, args); }
            catch (Exception ex) { Trace.TraceError("CaptureTrackService {0} subscriber failed: {1}", eventName, ex); }
        }
    }

    private static void ValidateOutput(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("OutputPath is required.", nameof(outputPath));
        var path = Path.GetFullPath(outputPath);
        if (!path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("OutputPath must end in .mp4.", nameof(outputPath));
        if (File.Exists(path)) throw new IOException($"Output file already exists: {path}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    private static void ValidateAudioOutput(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("OutputPath is required.", nameof(outputPath));
        var path = Path.GetFullPath(outputPath);
        if (!path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Audio track OutputPath must end in .wav.", nameof(outputPath));
        if (File.Exists(path)) throw new IOException($"Output file already exists: {path}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    private static MMDevice? TryGetDefault(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        try { return enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia); }
        catch { return null; }
    }

    private static async Task PumpAudioTimelineAsync(TrackSession session, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        var format = session.AudioCapture!.WaveFormat;
        var blockAlign = Math.Max(1, format.BlockAlign);
        var reorderFrames = Math.Max(1, format.SampleRate / 10); // 100 ms
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                lock (session.AudioWriteGate)
                {
                    if (session.AudioStartTicks == 0) continue;
                    var nowTicks = Stopwatch.GetTimestamp();
                    var targetSourceFrame = Math.Max(0, (long)((decimal)(nowTicks - session.AudioStartTicks) * format.SampleRate / Stopwatch.Frequency) - reorderFrames);
                    DrainAudioPackets(session, targetSourceFrame);
                    var activeTicks = GetAudioActiveTicks(session, nowTicks);
                    var targetOutputFrame = Math.Max(0, (long)(activeTicks * (double)format.SampleRate / Stopwatch.Frequency) - reorderFrames);
                    WriteAudioSilence(session, Math.Max(0, targetOutputFrame - session.AudioFramesWritten), blockAlign,
                        nowTicks, AudioSilenceSource.Pump);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static void DrainAudioPackets(TrackSession session, long dueThroughFrame, long? sourceStopFrame = null, long? outputStopFrame = null)
    {
        var writer = session.WaveFileWriter;
        var blockAlign = Math.Max(1, session.AudioCapture!.WaveFormat.BlockAlign);
        while (session.AudioPackets.TryPeek(out var packet) && packet.StartFrame <= dueThroughFrame)
        {
            session.AudioPackets.Dequeue();
            var packetFrames = packet.Data.Length / blockAlign;
            var pauseRanges = session.PausedIntervals.Select(interval => (
                StartFrame: Math.Max(0, (long)((decimal)(interval.PauseStartStopwatchTicks - session.AudioStartTicks) * session.AudioCapture.WaveFormat.SampleRate / Stopwatch.Frequency)),
                EndFrame: Math.Max(0, (long)((decimal)(interval.ResumeStopwatchTicks - session.AudioStartTicks) * session.AudioCapture.WaveFormat.SampleRate / Stopwatch.Frequency)))).ToList();
            if (session.AudioPaused && session.PauseStartTicks > 0)
            {
                var start = Math.Max(0, (long)((decimal)(session.PauseStartTicks - session.AudioStartTicks) * session.AudioCapture.WaveFormat.SampleRate / Stopwatch.Frequency));
                pauseRanges.Add((start, long.MaxValue));
            }
            foreach (var segment in AudioPacketTimeline.MapPacket(packet.StartFrame, packetFrames, pauseRanges,
                         sourceStopFrame ?? long.MaxValue, outputStopFrame ?? long.MaxValue))
            {
                var segmentFrameCount = segment.FrameCount;
                WriteAudioSilence(session, Math.Max(0, segment.OutputStartFrame - session.AudioFramesWritten), blockAlign,
                    packet.ArrivalStopwatchTicks, AudioSilenceSource.PacketGap);
                var overlapFrames = Math.Max(0, session.AudioFramesWritten - segment.OutputStartFrame);
                var remainingFrames = Math.Max(0, segmentFrameCount - overlapFrames);
                if (overlapFrames > 0) session.AudioDiagnostics?.OverlapDropped(packet.ArrivalStopwatchTicks, Math.Min(overlapFrames, segmentFrameCount));
                if (remainingFrames == 0 || writer is null) continue;
                var offset = checked((int)((segment.InputFrameOffset + overlapFrames) * blockAlign));
                var count = checked((int)(remainingFrames * blockAlign));
                writer.Write(packet.Data, offset, count);
                session.AudioFramesWritten += remainingFrames;
                session.AudioDiagnostics?.Written(packet.ArrivalStopwatchTicks, remainingFrames, packet.EndpointSilent);
            }
        }
    }

    private static void WriteAudioSilence(TrackSession session, long frameCount, int blockAlign, long eventTicks, AudioSilenceSource source)
    {
        var writer = session.WaveFileWriter;
        if (frameCount <= 0 || writer is null) return;
        var format = session.AudioCapture!.WaveFormat;
        const int maximumChunkFrames = 8192;
        var silenceFrame = new byte[blockAlign];
        if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 8) Array.Fill(silenceFrame, (byte)128);
        while (frameCount > 0)
        {
            var frames = (int)Math.Min(frameCount, maximumChunkFrames);
            var bytes = new byte[checked(frames * blockAlign)];
            for (var i = 0; i < bytes.Length; i += blockAlign) Buffer.BlockCopy(silenceFrame, 0, bytes, i, blockAlign);
            writer.Write(bytes, 0, bytes.Length);
            session.AudioFramesWritten += frames;
            session.AudioDiagnostics?.SilenceWritten(eventTicks, frames, source);
            frameCount -= frames;
        }
    }

    private static long GetAudioActiveTicks(TrackSession session, long nowTicks)
    {
        if (session.AudioStartTicks == 0) return 0;
        var pausedTicks = session.PausedIntervals.Sum(interval => interval.ResumeStopwatchTicks - interval.PauseStartStopwatchTicks);
        if (session.AudioPaused && session.PauseStartTicks != 0)
            pausedTicks += Math.Max(0, nowTicks - session.PauseStartTicks);
        return Math.Max(0, nowTicks - session.AudioStartTicks - pausedTicks);
    }

    private static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0) return double.NaN;
        var sorted = values.OrderBy(value => value).ToArray();
        return sorted[(int)Math.Clamp(Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1)];
    }

    private static void AddLatencySample(TrackSession session, double value)
    {
        session.ArrivalLatencySampleCount++;
        const int reservoirSize = 4096;
        if (session.ArrivalLatencySamples.Count < reservoirSize)
        {
            session.ArrivalLatencySamples.Add(value);
            return;
        }
        var replacement = Random.Shared.NextInt64(session.ArrivalLatencySampleCount);
        if (replacement < reservoirSize) session.ArrivalLatencySamples[(int)replacement] = value;
    }

    private static int Even(int value) => Math.Max(2, value - value % 2);
    private static void EnsureWindows() { if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("FrameTrail.Capture requires Windows."); }
    private async Task EnterStartGateAsync(CancellationToken cancellationToken)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ThrowIfDisposed(); }
        catch { _startGate.Release(); throw; }
    }

    private void ThrowIfDisposed()
    {
        if (_disposeRequested) throw new ObjectDisposedException(nameof(CaptureTrackService));
    }

    internal async Task RunStartGateProbeAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        await EnterStartGateAsync(CancellationToken.None).ConfigureAwait(false);
        try { await action().ConfigureAwait(false); }
        finally { _startGate.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            if (_disposeTask is not null) return new ValueTask(_disposeTask);
            _disposeRequested = true;
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
            _ = DisposeCoreAsync(completion);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(TaskCompletionSource<bool> completion)
    {
        try
        {
            await _startGate.WaitAsync().ConfigureAwait(false);
            TrackSession[] sessions;
            try
            {
                sessions = _sessions.Values.ToArray();
            }
            finally { _startGate.Release(); }
            foreach (var session in sessions)
            {
                try { await StopTrackAsync(session.Id).ConfigureAwait(false); } catch { }
            }
            completion.TrySetResult(true);
        }
        catch (Exception ex) { completion.TrySetException(ex); }
    }

    private sealed class TrackSession(CaptureTrackKind kind, string outputPath)
    {
        internal Guid Id { get; } = Guid.NewGuid();
        internal CaptureTrackKind Kind { get; } = kind;
        internal string OutputPath { get; } = Path.GetFullPath(outputPath);
        internal Recorder Recorder { get; set; } = null!;
        internal TimestampedWasapiCapture? AudioCapture { get; set; }
        internal WaveFileWriter? WaveFileWriter { get; set; }
        internal object AudioWriteGate { get; } = new();
        internal Queue<AudioPacket> AudioPackets { get; } = new();
        internal long AudioStartTicks { get; set; }
        internal long AudioFramesWritten { get; set; }
        internal CancellationTokenSource? AudioPumpCancellation { get; set; }
        internal Task? AudioPumpTask { get; set; }
        internal TaskCompletionSource<bool> AudioStopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<Exception> FailureSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal volatile bool AudioRecordingStopped;
        internal Exception? AudioFailure { get; set; }
        internal Exception? FailureException { get; set; }
        internal volatile bool AudioPaused;
        internal CaptureTrackHandle? Handle { get; set; }
        internal CaptureTrackState State { get; set; }
        internal CaptureTrackState ExpectedStatus { get; set; }
        internal TaskCompletionSource<long> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<string> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<CaptureTrackState>? StatusChanged { get; set; }
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal List<CapturePauseInterval> PausedIntervals { get; } = [];
        internal List<string> Warnings { get; } = [];
        internal long PauseStartTicks { get; set; }
        internal int Width { get; set; }
        internal int Height { get; set; }
        internal int FrameRate { get; set; }
        internal EncoderMode EncoderMode { get; set; }
        internal double MaxAudioPacketMilliseconds { get; set; }
        internal long AudioPacketCount { get; set; }
        internal long TimestampFallbackCount { get; set; }
        internal long TimestampErrorCount { get; set; }
        internal long DataDiscontinuityCount { get; set; }
        internal long DevicePositionMismatchCount { get; set; }
        internal long? FirstDevicePosition { get; set; }
        internal long? ExpectedDevicePosition { get; set; }
        internal double FirstPacketOffsetMilliseconds { get; set; } = double.NaN;
        internal long ArrivalLatencySampleCount { get; set; }
        internal List<double> ArrivalLatencySamples { get; } = [];
        internal AudioPacketDiagnostics? AudioDiagnostics { get; set; }
    }
    private sealed record AudioPacket(long StartFrame, byte[] Data, AudioPacketTiming Timing, long ArrivalStopwatchTicks, bool EndpointSilent);
}
