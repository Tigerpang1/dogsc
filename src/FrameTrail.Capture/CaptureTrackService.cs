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
    private bool _disposed;

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
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            IWaveIn audioCapture = request.DeviceKind == AudioDeviceKind.SystemLoopback
                ? new WasapiLoopbackCapture(endpoint)
                : new WasapiCapture(endpoint);
            var session = new TrackSession(kind, request.OutputPath)
            {
                AudioCapture = audioCapture,
                WaveFileWriter = new WaveFileWriter(Path.GetFullPath(request.OutputPath), audioCapture.WaveFormat)
            };
            session.Warnings.Add($"WASAPI audio format: {audioCapture.WaveFormat}; standalone WAV contains no video.");
            session.Warnings.Add("WASAPI DataAvailable arrival times are approximate. A 100 ms clocked reorder window fills silent gaps; no endpoint sample timestamp is exposed by this capture API.");
            audioCapture.DataAvailable += (_, args) =>
            {
                try
                {
                    var packetDuration = args.BytesRecorded * 1000d / Math.Max(1, audioCapture.WaveFormat.AverageBytesPerSecond);
                    if (packetDuration > session.MaxAudioPacketMilliseconds) session.MaxAudioPacketMilliseconds = packetDuration;
                    var blockAlign = Math.Max(1, audioCapture.WaveFormat.BlockAlign);
                    var bytesRecorded = args.BytesRecorded - args.BytesRecorded % blockAlign;
                    if (bytesRecorded <= 0) return;
                    var packet = new byte[bytesRecorded];
                    Buffer.BlockCopy(args.Buffer, 0, packet, 0, bytesRecorded);
                    lock (session.AudioWriteGate)
                    {
                        if (session.AudioPaused || session.AudioStartTicks == 0) return;
                        var frames = bytesRecorded / blockAlign;
                        var activeTicks = GetAudioActiveTicks(session, Stopwatch.GetTimestamp());
                        var startFrame = Math.Max(0, (long)(activeTicks * (double)audioCapture.WaveFormat.SampleRate / Stopwatch.Frequency) - frames);
                        session.AudioPackets.Enqueue(new AudioPacket(startFrame, packet));
                    }
                }
                catch (Exception ex) { ErrorOccurred?.Invoke(this, new CaptureError($"WASAPI audio write failed: {ex.Message}", ex)); }
            };
            audioCapture.RecordingStopped += (_, args) =>
            {
                session.AudioRecordingStopped = true;
                if (args.Exception is not null)
                {
                    session.AudioFailure = args.Exception;
                    session.AudioStopped.TrySetException(args.Exception);
                    SetState(session, CaptureTrackState.Faulted, Stopwatch.GetTimestamp(), args.Exception.Message);
                    try { ErrorOccurred?.Invoke(this, new CaptureError($"Audio capture stopped unexpectedly: {args.Exception.Message}", args.Exception)); } catch { }
                }
                else session.AudioStopped.TrySetResult(true);
            };
            if (!_sessions.TryAdd(session.Id, session))
            {
                audioCapture.Dispose(); session.WaveFileWriter.Dispose();
                throw new InvalidOperationException("Could not register audio track.");
            }
            SetState(session, CaptureTrackState.Starting);
            try
            {
                session.AudioStartTicks = Stopwatch.GetTimestamp();
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
                _sessions.TryRemove(session.Id, out _);
                try { audioCapture.Dispose(); } catch { }
                try { session.WaveFileWriter.Dispose(); } catch { }
                try { File.Delete(session.OutputPath); } catch { }
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
                    DrainAudioPackets(session, long.MaxValue);
                    var activeTicks = GetAudioActiveTicks(session, stopTicks);
                    var targetFrames = Math.Max(0, (long)(activeTicks * (double)session.AudioCapture.WaveFormat.SampleRate / Stopwatch.Frequency));
                    WriteAudioSilence(session, Math.Max(0, targetFrames - session.AudioFramesWritten), blockAlign);
                }
                session.AudioPumpCancellation?.Dispose(); session.AudioPumpCancellation = null; session.AudioPumpTask = null;
                session.WaveFileWriter?.Dispose();
                session.AudioCapture.Dispose();
                if (session.PausedIntervals.Count > 0)
                    session.Warnings.Add($"Pause drops whole WASAPI packets; the largest observed packet was {session.MaxAudioPacketMilliseconds:F1} ms, so pause boundaries have no finer sample timestamp.");
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
            ErrorOccurred?.Invoke(this, new CaptureError($"Capture track failed: {ex.Message}", ex));
            try { session.AudioCapture?.StopRecording(); } catch { }
            try { session.AudioCapture?.Dispose(); } catch { }
            try { session.WaveFileWriter?.Dispose(); } catch { }
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
            ErrorOccurred?.Invoke(this, new CaptureError(args.Error, ex));
            SetState(session, CaptureTrackState.Faulted, args.Error);
        };
        session.Recorder.OnRecordingComplete += (_, args) => session.Finished.TrySetResult(args.FilePath);
        ConfigureOutputMetadata(session, options);
        if (!_sessions.TryAdd(session.Id, session)) throw new InvalidOperationException("Could not register capture track.");
        SetState(session, CaptureTrackState.Starting);
        try
        {
            await Task.Run(() => session.Recorder.Record(session.OutputPath), cancellationToken).ConfigureAwait(false);
            var startedTicks = await session.Started.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            session.Handle = new CaptureTrackHandle(session.Id, kind, session.OutputPath, startedTicks, Stopwatch.Frequency);
            return session.Handle;
        }
        catch (Exception ex)
        {
            try { await Task.Run(session.Recorder.Stop).ConfigureAwait(false); } catch { }
            session.Recorder.Dispose();
            _sessions.TryRemove(session.Id, out _);
            try { if (File.Exists(session.OutputPath)) File.Delete(session.OutputPath); } catch { }
            SetState(session, ex is OperationCanceledException ? CaptureTrackState.Faulted : CaptureTrackState.Faulted, ex.Message);
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
        StateChanged?.Invoke(this, new CaptureTrackStateChanged(session.Id, session.Kind, state, ticks ?? Stopwatch.GetTimestamp(), detail));
    }
    private void SetState(TrackSession session, CaptureTrackState state, string? detail) =>
        SetState(session, state, Stopwatch.GetTimestamp(), detail);

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
                    if (session.AudioPaused || session.AudioStartTicks == 0) continue;
                    var activeTicks = GetAudioActiveTicks(session, Stopwatch.GetTimestamp());
                    var targetFrame = Math.Max(0, (long)(activeTicks * (double)format.SampleRate / Stopwatch.Frequency) - reorderFrames);
                    DrainAudioPackets(session, targetFrame);
                    WriteAudioSilence(session, Math.Max(0, targetFrame - session.AudioFramesWritten), blockAlign);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static void DrainAudioPackets(TrackSession session, long dueThroughFrame)
    {
        var writer = session.WaveFileWriter;
        var blockAlign = Math.Max(1, session.AudioCapture!.WaveFormat.BlockAlign);
        while (session.AudioPackets.TryPeek(out var packet) && packet.StartFrame <= dueThroughFrame)
        {
            session.AudioPackets.Dequeue();
            var packetFrames = packet.Data.Length / blockAlign;
            WriteAudioSilence(session, Math.Max(0, packet.StartFrame - session.AudioFramesWritten), blockAlign);
            var overlapFrames = Math.Max(0, session.AudioFramesWritten - packet.StartFrame);
            var remainingFrames = Math.Max(0, packetFrames - overlapFrames);
            if (remainingFrames == 0 || writer is null) continue;
            var offset = checked((int)(overlapFrames * blockAlign));
            var count = checked((int)(remainingFrames * blockAlign));
            writer.Write(packet.Data, offset, count);
            session.AudioFramesWritten += remainingFrames;
        }
    }

    private static void WriteAudioSilence(TrackSession session, long frameCount, int blockAlign)
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

    private static int Even(int value) => Math.Max(2, value - value % 2);
    private static void EnsureWindows() { if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("FrameTrail.Capture requires Windows."); }
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(CaptureTrackService)); }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var session in _sessions.Values.ToArray())
        {
            try { await StopTrackAsync(session.Id).ConfigureAwait(false); } catch { }
        }
        _startGate.Dispose();
    }

    private sealed class TrackSession(CaptureTrackKind kind, string outputPath)
    {
        internal Guid Id { get; } = Guid.NewGuid();
        internal CaptureTrackKind Kind { get; } = kind;
        internal string OutputPath { get; } = Path.GetFullPath(outputPath);
        internal Recorder Recorder { get; set; } = null!;
        internal IWaveIn? AudioCapture { get; set; }
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
    }
    private sealed record AudioPacket(long StartFrame, byte[] Data);
}
