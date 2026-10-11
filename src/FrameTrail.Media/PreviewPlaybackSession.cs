using System.Diagnostics;
using FrameTrail;

namespace FrameTrail.Media;

public readonly record struct PreviewFrameInfo(double OutputTime, double SourceTime, int Width, int Height, int FrameRate)
{
    public FrameScene? Scene { get; init; }
}

/// <summary>Persistent FFmpeg decoder plus the same scene evaluator, asset loader and GPU compositor used by export.</summary>
public sealed class PreviewPlaybackSession : IDisposable, IAsyncDisposable
{
    private readonly FrameTrailProject project;
    private readonly string sourcePath;
    private readonly string projectFilePath;
    private readonly int width, height, frameRate, sourceWidth, sourceHeight;
    private readonly double sourceDuration, sourceFrameRate;
    private readonly bool sourceHasAudio;
    private readonly PreparedFrameSceneEvaluator evaluator;
    private readonly SceneAssetLoader assets;
    private readonly GpuFrameRenderer renderer;
    private readonly bool preferNativeDecoder;
    private ScrubFrameDecoder? nativeDecoder;
    private byte[]? nativeFrameBuffer;
    private readonly SemaphoreSlim gate = new(1, 1);
    private Process? decoder;
    private PreviewFramePipe? decoderFrames;
    private Task<string>? decoderErrors;
    private byte[]? lastDecoded;
    private long currentSourceFrame = -1;
    private bool disposed;

    public string Backend => renderer.Backend;
    public int Width => width;
    public int Height => height;
    public int FrameRate => frameRate;
    public string DecoderBackend => nativeDecoder?.DecoderBackend ?? (preferNativeDecoder ? "libav (not initialized)" : "ffmpeg/process");
    public int NativeSessionCount => nativeDecoder?.NativeSessionCount ?? 0;
    public long ProcessStartCount => nativeDecoder?.ProcessStartCount ?? 0;
    public bool SourceHasAudio => sourceHasAudio;
    /// <summary>Decoder PID for diagnostics only. The session owns and closes this process.</summary>
    public int? ActiveDecoderProcessId => decoder is { HasExited: false } active ? active.Id : null;

    private PreviewPlaybackSession(FrameTrailProject project, string sourcePath, string projectFilePath, int width, int height, int frameRate, int sourceWidth, int sourceHeight, double sourceDuration, double sourceFrameRate, bool sourceHasAudio, double? cameraAspectRatio, bool preserveSourceResolution, bool preferNativeDecoder)
    {
        this.project = project;
        this.sourcePath = Path.GetFullPath(sourcePath);
        this.projectFilePath = Path.GetFullPath(projectFilePath);
        this.width = width; this.height = height; this.frameRate = frameRate;
        this.sourceDuration = sourceDuration; this.sourceFrameRate = sourceFrameRate > 0 ? sourceFrameRate : frameRate;
        this.sourceHasAudio = sourceHasAudio;
        this.preferNativeDecoder = preferNativeDecoder;
        var scale = preserveSourceResolution ? 1 : Math.Min(1, Math.Min(width / (double)sourceWidth, height / (double)sourceHeight));
        this.sourceWidth = Math.Max(2, (int)Math.Round(sourceWidth * scale));
        this.sourceHeight = Math.Max(2, (int)Math.Round(sourceHeight * scale));
        evaluator = new PreparedFrameSceneEvaluator(project, cameraAspectRatio);
        assets = new SceneAssetLoader(this.projectFilePath, width, height);
        renderer = new GpuFrameRenderer(this.sourceWidth, this.sourceHeight, width, height);
    }

    public static async Task<PreviewPlaybackSession> CreateAsync(FrameTrailProject project, string sourcePath, string projectFilePath, int width = 960, int height = 540, int frameRate = 30, CancellationToken cancellation = default, bool preserveSourceResolution = false, bool preferNativeDecoder = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (width < 16 || height < 16 || width % 2 != 0 || height % 2 != 0) throw new ArgumentOutOfRangeException(nameof(width), "预览尺寸必须是大于等于 16 的偶数。" );
        if (frameRate is not (30 or 60)) throw new ArgumentOutOfRangeException(nameof(frameRate));
        project.Timeline.Validate();
        var fullSourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSourcePath)) throw new FileNotFoundException("找不到预览源视频。", fullSourcePath);
        if (!File.Exists(projectFilePath)) throw new FileNotFoundException("找不到 FrameTrail 项目文件。", projectFilePath);
        var info = await MediaTools.ProbeAsync(fullSourcePath, cancellation);
        if (info.Width < 2 || info.Height < 2 || info.Duration <= 0) throw new InvalidDataException("预览源视频没有有效视频流。" );
        var cameraAspectRatio = await SceneAssetLoader.ProbeCameraAspectRatioAsync(project, projectFilePath, cancellation);
        return new PreviewPlaybackSession(project, fullSourcePath, projectFilePath, width, height, frameRate, info.Width, info.Height, info.Duration, info.FrameRate, info.HasAudio, cameraAspectRatio, preserveSourceResolution, preferNativeDecoder);
    }

    /// <summary>Reads and composes one output-time frame. Calls on a shared session are serialized.</summary>
    public async Task<PreviewFrameInfo> ReadFrameAsync(double outputTime, byte[] outputBgra, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(outputBgra);
        if (outputBgra.Length != checked(width * height * 4)) throw new ArgumentException($"输出缓冲区长度应为 {width * height * 4} 字节。", nameof(outputBgra));
        if (!double.IsFinite(outputTime) || outputTime < 0 || outputTime > project.Timeline.Duration) throw new ArgumentOutOfRangeException(nameof(outputTime));
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellation.ThrowIfCancellationRequested();
            var safeOutputTime = Math.Min(outputTime, Math.Max(0, project.Timeline.Duration - 1d / frameRate));
            var scene = evaluator.Evaluate(safeOutputTime);
            var targetFrame = (long)Math.Round(scene.SourceTime * frameRate, MidpointRounding.AwayFromZero);
            byte[] sourceFrame;
            if (preferNativeDecoder)
            {
                nativeDecoder ??= new ScrubFrameDecoder(sourcePath, sourceWidth, sourceHeight, sourceFrameRate, sourceDuration, preferNative: true);
                // NativeSeekFrameDecoder is synchronous; keep codec work off the caller (often UI) thread.
                var reusableSourceFrame = nativeFrameBuffer ??= GC.AllocateUninitializedArray<byte>(checked(sourceWidth * sourceHeight * 4));
                await Task.Run(() => nativeDecoder.ReadFrameIntoAsync(scene.SourceTime, reusableSourceFrame, cancellation), cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                sourceFrame = reusableSourceFrame;
                currentSourceFrame = targetFrame;
            }
            else
            {
                if (decoder is null || targetFrame < currentSourceFrame || targetFrame - currentSourceFrame > frameRate * 2L)
                    await StartDecoder(targetFrame, cancellation).ConfigureAwait(false);
                if (targetFrame != currentSourceFrame)
                {
                    while (currentSourceFrame < targetFrame)
                    {
                        lastDecoded = await ReadDecodedFrame(cancellation).ConfigureAwait(false);
                        currentSourceFrame++;
                    }
                }
                sourceFrame = lastDecoded ?? throw new IOException("预览解码器没有返回视频帧。");
            }
            var layers = await assets.LoadAsync(scene, cancellation).ConfigureAwait(false);
            renderer.Render(sourceFrame, scene, outputBgra, layers);
            return new PreviewFrameInfo(safeOutputTime, scene.SourceTime, width, height, frameRate) { Scene = scene };
        }
        catch (Exception) when (cancellation.IsCancellationRequested)
        {
            await StopDecoder().ConfigureAwait(false);
            throw new OperationCanceledException("预览帧读取已取消。", cancellation);
        }
        catch
        {
            if (decoder is { HasExited: true }) await StopDecoder().ConfigureAwait(false);
            throw;
        }
        finally { gate.Release(); }
    }

    /// <summary>Closes the decode process; the next ReadFrameAsync starts at its requested source time.</summary>
    public async Task SeekAsync(double outputTime, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!double.IsFinite(outputTime) || outputTime < 0 || outputTime > project.Timeline.Duration) throw new ArgumentOutOfRangeException(nameof(outputTime));
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try { await StopDecoder().ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task StartDecoder(long targetFrame, CancellationToken cancellation)
    {
        await StopDecoder().ConfigureAwait(false);
        var targetTime = Math.Max(0, targetFrame / (double)frameRate);
        decoderFrames = new PreviewFramePipe();
        try
        {
            decoder = MediaTools.Start(MediaTools.Ffmpeg,
                ["-v", "error", "-ss", MediaTools.Number(targetTime), "-i", sourcePath, "-an", "-vf", $"fps={frameRate},scale={sourceWidth}:{sourceHeight}:flags=bilinear,format=bgra", "-f", "rawvideo", "-pix_fmt", "bgra", "-y", decoderFrames.OutputPath]);
            decoderErrors = decoder.StandardError.ReadToEndAsync();
            await decoderFrames.ConnectAsync(decoder, cancellation).ConfigureAwait(false);
            lastDecoded = await ReadDecodedFrame(cancellation).ConfigureAwait(false);
            currentSourceFrame = targetFrame;
        }
        catch { await StopDecoder().ConfigureAwait(false); throw; }
    }

    private async Task<byte[]> ReadDecodedFrame(CancellationToken cancellation)
    {
        var p = decoder ?? throw new InvalidOperationException("预览解码器尚未启动。" );
        // The session gate and synchronous compositor own this buffer throughout a
        // frame. Reuse it instead of allocating and zeroing 33 MiB for every 4K frame.
        var frame = lastDecoded ?? GC.AllocateUninitializedArray<byte>(checked(sourceWidth * sourceHeight * 4));
        using var registration = cancellation.Register(() => KillDecoderProcess(p));
        try { await decoderFrames!.ReadExactlyAsync(frame, cancellation).ConfigureAwait(false); return frame; }
        catch (EndOfStreamException)
        {
            await p.WaitForExitAsync(cancellation).ConfigureAwait(false);
            var errors = decoderErrors is null ? "" : await decoderErrors.ConfigureAwait(false);
            throw new IOException("预览源视频提前结束或无法解码：" + MediaTools.Tail(errors));
        }
    }

    private async Task StopDecoder()
    {
        var old = decoder; var errors = decoderErrors;
        decoder = null; decoderErrors = null; currentSourceFrame = -1; lastDecoded = null;
        decoderFrames?.Dispose(); decoderFrames = null;
        if (old is null) return;
        try
        {
            KillDecoderProcess(old);
            try { await old.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException) { }
            catch (InvalidOperationException) { }
            if (errors is not null) try { _ = await errors.ConfigureAwait(false); } catch { }
        }
        finally { old.Dispose(); }
    }

    private static void KillDecoderProcess(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: false); }
        catch (InvalidOperationException) { }
    }

    public void Dispose()
    {
        if (disposed) return;
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            await StopDecoder().ConfigureAwait(false);
            if (nativeDecoder is not null) await nativeDecoder.DisposeAsync().ConfigureAwait(false);
            nativeDecoder = null;
            nativeFrameBuffer = null;
            assets.Dispose(); renderer.Dispose();
        }
        finally { gate.Release(); }
    }
}
