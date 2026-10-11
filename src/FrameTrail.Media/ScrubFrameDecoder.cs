using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FrameTrail.Media;

/// <summary>
/// Timestamp-driven, seekable frame reader for timeline scrubbing. It follows each decoded
/// frame's FFmpeg PTS, so variable-frame-rate media is not mapped through an assumed CFR index.
/// </summary>
public sealed class ScrubFrameDecoder : IAsyncDisposable
{
    private const long CacheBudgetBytes = 96L * 1024 * 1024;
    private const int MaxIndexedRuns = 16;
    private const int MaxIndexedFramesPerRun = 4096;
    private static readonly Regex ShowInfoPts = new(@"\bn:\s*\d+\s+pts:\s*(?<pts>-?\d+)\s+pts_time:", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ShowInfoFrame = new(@"\bn:\s*\d+\s+pts:", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ShowInfoTimeBase = new(@"config in time_base:\s*(?<num>\d+)/(?<den>\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly string sourcePath;
    private readonly int width;
    private readonly int height;
    private readonly double sourceFrameRate;
    private readonly double duration;
    private readonly ByteBudgetLruCache<long, byte[]> cache = new(CacheBudgetBytes);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly LinkedList<FrameRunIndex> indexedRuns = new();
    private readonly StringBuilder errorTail = new();
    private Process? process;
    private PreviewFramePipe? framePipe;
    private FrameRunIndex? activeRun;
    private long nextFrameKey;
    private long cacheBytes;
    private long cacheHits;
    private long processStartCount;
    private int activeProcessId;
    private long timeBaseNumerator;
    private long timeBaseDenominator;
    private int disposed;
    private bool timestampStreamFailed;
    private bool preferNative;
    private NativeSeekFrameDecoder? nativeDecoder;
    private readonly ByteBudgetLruCache<double, byte[]> nativeCache = new(CacheBudgetBytes);
    public string DecoderBackend => nativeDecoder?.BackendName ?? "ffmpeg/process";
    public string? NativeFailureReason { get; private set; }
    public int NativeSessionCount { get; private set; }

    public ScrubFrameDecoder(string sourcePath, int width, int height, double sourceFrameRate, double sourceDuration,
        bool preferNative = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("找不到待预览的视频素材。", sourcePath);
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (!double.IsFinite(sourceFrameRate) || sourceFrameRate <= 0) throw new ArgumentOutOfRangeException(nameof(sourceFrameRate));
        if (!double.IsFinite(sourceDuration) || sourceDuration <= 0) throw new ArgumentOutOfRangeException(nameof(sourceDuration));
        _ = checked(width * height * 4);

        this.sourcePath = Path.GetFullPath(sourcePath);
        this.width = width;
        this.height = height;
        this.sourceFrameRate = sourceFrameRate;
        duration = sourceDuration;
        this.preferNative = preferNative;
    }

    /// <summary>当前保留的帧像素总字节数，最多 96 MiB。</summary>
    public long CacheBytes => Interlocked.Read(ref cacheBytes);

    /// <summary>从缓存返回帧的次数。</summary>
    public long CacheHits => Interlocked.Read(ref cacheHits);

    /// <summary>FFmpeg 解码进程启动次数。</summary>
    public long ProcessStartCount => Interlocked.Read(ref processStartCount);

    /// <summary>PTS 流异常后是否已退回逐帧单次解码；正常 seek 不应触发此状态。</summary>
    public bool OneShotFallbackActive => Volatile.Read(ref timestampStreamFailed);

    /// <summary>当前 FFmpeg 进程 ID；没有活动进程时为 null。</summary>
    public int? ActiveProcessId
    {
        get { var id = Volatile.Read(ref activeProcessId); return id == 0 ? null : id; }
    }

    /// <summary>读取 sourceTime 处的首个实际视频帧 PTS，返回由调用方独占的 BGRA 缓冲区。</summary>
    public async Task<byte[]> ReadFrameAsync(double sourceTime, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(sourceTime) || sourceTime < 0) throw new ArgumentOutOfRangeException(nameof(sourceTime));
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (preferNative)
            {
                try
                {
                    if (!LibavRuntime.TryInitialize()) throw new NotSupportedException(LibavRuntime.UnavailableReason);
                    var target = Math.Min(sourceTime, duration);
                    if (nativeCache.TryGetValue(target, out var pixels))
                    {
                        Interlocked.Increment(ref cacheHits);
                        return (byte[])pixels.Clone();
                    }
                    if (nativeDecoder is null)
                    {
                        nativeDecoder = new(sourcePath, width, height, sourceFrameRate, duration);
                        NativeSessionCount++;
                    }
                    pixels = nativeDecoder.ReadFrame(target, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    nativeCache.Set(target, pixels, pixels.Length);
                    Interlocked.Exchange(ref cacheBytes, nativeCache.CachedBytes);
                    return (byte[])pixels.Clone();
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or NotSupportedException)
                {
                    // Unavailable codecs/runtimes retain the established CLI semantics.
                    // Cancellation must propagate and must never disable the fast path.
                    cancellationToken.ThrowIfCancellationRequested();
                    nativeDecoder?.Dispose();
                    nativeDecoder = null;
                    nativeCache.Clear();
                    Interlocked.Exchange(ref cacheBytes, 0);
                    preferNative = false;
                    NativeFailureReason = error.Message;
                }
            }
            if (timestampStreamFailed)
            {
                var fallbackTime = sourceTime >= duration ? Math.Max(0, duration - 1 / sourceFrameRate) : sourceTime;
                return await MediaTools.DecodeFrameAsync(sourcePath, fallbackTime, width, height, cancellationToken).ConfigureAwait(false);
            }

            if (sourceTime >= duration)
                return await ReadLastFrameAsync(cancellationToken).ConfigureAwait(false);

            var clampedTime = Math.Min(sourceTime, Math.Max(0, duration - 1e-9));
            if (TryGetCachedAtOrAfter(clampedTime, out var cached)) return cached;

            if (process is null || activeRun is null || activeRun.LastSeconds > clampedTime
                || clampedTime - activeRun.LastSeconds > .25)
                await RestartAsync(clampedTime, cancellationToken).ConfigureAwait(false);

            while (activeRun!.LastSeconds < clampedTime)
                await ReadAndIndexFrameAsync(cancellationToken).ConfigureAwait(false);

            if (!TryGetCachedAtOrAfter(clampedTime, out var result))
            {
                // The timestamp index can outlive a pixel entry after LRU eviction. Restarting
                // gives a correct frame without trusting an adjacent cached timestamp.
                await RestartAsync(clampedTime, cancellationToken).ConfigureAwait(false);
                while (activeRun!.LastSeconds < clampedTime)
                    await ReadAndIndexFrameAsync(cancellationToken).ConfigureAwait(false);
                if (!TryGetCachedAtOrAfter(clampedTime, out result))
                    throw new IOException("FFmpeg 没有生成请求时间对应的有效帧。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ResetStreamAsync().ConfigureAwait(false);
            throw new OperationCanceledException(cancellationToken);
        }
        catch (IOException) when (cancellationToken.IsCancellationRequested)
        {
            await ResetStreamAsync().ConfigureAwait(false);
            throw new OperationCanceledException(cancellationToken);
        }
        catch (IOException)
        {
            // Missing or unusable frame timestamps fall back to the established one-shot seek
            // semantics. The fallback is sticky for this decoder instance and cannot mis-map VFR.
            await ResetStreamAsync().ConfigureAwait(false);
            timestampStreamFailed = true;
            var fallbackTime = sourceTime >= duration ? Math.Max(0, duration - 1 / sourceFrameRate) : sourceTime;
            return await MediaTools.DecodeFrameAsync(sourcePath, fallbackTime, width, height, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task<byte[]> ReadLastFrameAsync(CancellationToken cancellationToken)
    {
        for (var runNode = indexedRuns.Last; runNode is not null; runNode = runNode.Previous)
        {
            var run = runNode.Value;
            if (run.ReachedEnd && run.Frames.Count > 0 && cache.TryGetValue(run.Frames[^1].Key, out var cachedLast))
            {
                Interlocked.Increment(ref cacheHits);
                return (byte[])cachedLast.Clone();
            }
        }

        var seekTime = Math.Max(0, duration - Math.Max(1, 2 / sourceFrameRate));
        if (process is null || activeRun is null || activeRun.LastSeconds < seekTime)
            await RestartAsync(seekTime, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            try { await ReadAndIndexFrameAsync(cancellationToken).ConfigureAwait(false); }
            catch (EndOfStreamException)
            {
                activeRun!.ReachedEnd = true;
                break;
            }
        }

        if (activeRun!.Frames.Count == 0) throw new IOException("视频素材没有可读取的帧。");
        if (cache.TryGetValue(activeRun.Frames[^1].Key, out var lastPixels))
        {
            Interlocked.Increment(ref cacheHits);
            return (byte[])lastPixels.Clone();
        }

        var fallbackTime = Math.Max(0, duration - 1 / sourceFrameRate);
        return await MediaTools.DecodeFrameAsync(sourcePath, fallbackTime, width, height, cancellationToken).ConfigureAwait(false);
    }

    private bool TryGetCachedAtOrAfter(double targetTime, out byte[] frame)
    {
        for (var runNode = indexedRuns.Last; runNode is not null; runNode = runNode.Previous)
        {
            var run = runNode.Value;
            // Input-side -ss may begin on the first frame after the requested timestamp.
            // That first PTS is still the correct answer for requests inside this run's
            // seek coverage, even when targetTime is earlier than the first decoded PTS.
            if (run.Frames.Count == 0 || targetTime < run.SeekStartSeconds || targetTime > run.Frames[^1].TimestampSeconds)
                continue;
            var low = 0;
            var high = run.Frames.Count;
            while (low < high)
            {
                var middle = low + ((high - low) / 2);
                if (run.Frames[middle].TimestampSeconds < targetTime) low = middle + 1;
                else high = middle;
            }
            if (low >= run.Frames.Count) continue;
            if (!cache.TryGetValue(run.Frames[low].Key, out var pixels)) continue;
            Interlocked.Increment(ref cacheHits);
            frame = (byte[])pixels.Clone();
            return true;
        }
        frame = [];
        return false;
    }

    private async Task RestartAsync(double targetTime, CancellationToken cancellationToken)
    {
        await StopProcessAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        errorTail.Clear();
        timeBaseNumerator = 0;
        timeBaseDenominator = 0;
        framePipe = new PreviewFramePipe();
        try
        {
            process = MediaTools.Start(MediaTools.Ffmpeg,
                ["-hide_banner", "-loglevel", "info", "-copyts", "-start_at_zero", "-ss", MediaTools.Number(targetTime), "-i", sourcePath,
                 "-an", "-sn", "-dn", "-vf", $"scale={width}:{height},showinfo=checksum=0", "-pix_fmt", "bgra",
                 "-fps_mode", "passthrough", "-f", "rawvideo", "-y", framePipe.OutputPath],
                output: false);
            Interlocked.Exchange(ref activeProcessId, process.Id);
            Interlocked.Increment(ref processStartCount);
            await framePipe.ConnectAsync(process, cancellationToken).ConfigureAwait(false);
            var run = new FrameRunIndex(targetTime);
            activeRun = run;
            indexedRuns.AddLast(run);
            while (indexedRuns.Count > MaxIndexedRuns) indexedRuns.RemoveFirst();

            await ReadAndIndexFrameAsync(cancellationToken).ConfigureAwait(false);
            while (activeRun.LastSeconds < targetTime)
                await ReadAndIndexFrameAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await StopProcessAsync().ConfigureAwait(false);
            activeRun = null;
            throw;
        }
    }

    private async Task ReadAndIndexFrameAsync(CancellationToken cancellationToken)
    {
        var (pixels, timestampSeconds) = await ReadFrameWithPtsAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (activeRun is null) throw new InvalidOperationException("当前解码流没有时间戳索引。");
        if (activeRun.Frames.Count > 0 && timestampSeconds < activeRun.Frames[^1].TimestampSeconds)
            throw new IOException("FFmpeg 输出了非单调视频时间戳。");

        var key = Interlocked.Increment(ref nextFrameKey);
        activeRun.Append(new(timestampSeconds, key));
        cache.Set(key, (byte[])pixels.Clone(), pixels.Length);
        Interlocked.Exchange(ref cacheBytes, cache.CachedBytes);
    }

    private async Task<(byte[] Pixels, double TimestampSeconds)> ReadFrameWithPtsAsync(CancellationToken cancellationToken)
    {
        var active = process ?? throw new InvalidOperationException("时间轴解码进程尚未启动。");
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var registration = cancellationToken.Register(() => KillDecoder(active));
        var output = ReadPixelsAsync(linkedCancellation.Token);
        var timestamp = ReadNextTimestampAsync(active, linkedCancellation.Token);
        try
        {
            // If either half fails, stop FFmpeg immediately so the sibling pipe read does
            // not wait for frame bytes or a showinfo line that can no longer arrive.
            var first = await Task.WhenAny(output, timestamp).ConfigureAwait(false);
            if (first.IsFaulted || first.IsCanceled)
            {
                linkedCancellation.Cancel();
                KillDecoder(active);
            }
            await Task.WhenAll(output, timestamp).ConfigureAwait(false);
            return (await output.ConfigureAwait(false), await timestamp.ConfigureAwait(false));
        }
        catch
        {
            linkedCancellation.Cancel();
            KillDecoder(active);
            try { await Task.WhenAll(output, timestamp).ConfigureAwait(false); }
            catch { /* Observe both pipe tasks after killing the process. */ }
            if (cancellationToken.IsCancellationRequested) throw;

            var endOfStream = output.Exception?.GetBaseException() as EndOfStreamException
                ?? timestamp.Exception?.GetBaseException() as EndOfStreamException;
            if (endOfStream is not null)
            {
                await active.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                throw new EndOfStreamException("视频素材已到末尾：" + MediaTools.Tail(errorTail.ToString()), endOfStream);
            }
            throw;
        }
    }

    private async Task<byte[]> ReadPixelsAsync(CancellationToken cancellationToken)
    {
        var pixels = new byte[checked(width * height * 4)];
        var currentPipe = framePipe ?? throw new InvalidOperationException("预览帧管道尚未建立。");
        await currentPipe.ReadExactlyAsync(pixels, cancellationToken).ConfigureAwait(false);
        return pixels;
    }

    private async Task<double> ReadNextTimestampAsync(Process active, CancellationToken cancellationToken)
    {
        while (await active.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            AppendError(line);
            var timeBase = ShowInfoTimeBase.Match(line);
            if (timeBase.Success)
            {
                if (!long.TryParse(timeBase.Groups["num"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out timeBaseNumerator)
                    || !long.TryParse(timeBase.Groups["den"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out timeBaseDenominator)
                    || timeBaseNumerator <= 0 || timeBaseDenominator <= 0)
                    throw new IOException("无法解析 FFmpeg 视频时间基准。");
                continue;
            }
            var match = ShowInfoPts.Match(line);
            if (!match.Success)
            {
                if (ShowInfoFrame.IsMatch(line)) throw new IOException("FFmpeg 视频帧没有有效 PTS。");
                continue;
            }
            if (timeBaseNumerator <= 0 || timeBaseDenominator <= 0
                || !long.TryParse(match.Groups["pts"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var pts))
                throw new IOException("无法解析 FFmpeg 视频帧时间戳。");
            var seconds = (double)pts * timeBaseNumerator / timeBaseDenominator;
            if (!double.IsFinite(seconds)) throw new IOException("FFmpeg 视频帧时间戳超出支持范围。");
            return seconds;
        }
        throw new EndOfStreamException("FFmpeg 在输出视频帧时间戳前结束。");
    }

    private void AppendError(string line)
    {
        errorTail.AppendLine(line);
        if (errorTail.Length > 6_000) errorTail.Remove(0, errorTail.Length - 6_000);
    }

    private async Task ResetStreamAsync()
    {
        await StopProcessAsync().ConfigureAwait(false);
        activeRun = null;
    }

    private async Task StopProcessAsync()
    {
        var old = process;
        var oldPipe = framePipe;
        process = null;
        framePipe = null;
        Interlocked.Exchange(ref activeProcessId, 0);
        try
        {
            if (old is not null)
            {
                KillDecoder(old);
                await old.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
        finally
        {
            oldPipe?.Dispose();
            old?.Dispose();
        }
    }

    private static void KillDecoder(Process decoder)
    {
        // This is the FFmpeg executable we launched directly, with no shell or child
        // workers. Tree enumeration costs tens of milliseconds on Windows and runs
        // synchronously when a new pointer input cancels the previous request.
        try { if (!decoder.HasExited) decoder.Kill(entireProcessTree: false); }
        catch (InvalidOperationException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ResetStreamAsync().ConfigureAwait(false);
            cache.Clear();
            nativeCache.Clear();
            nativeDecoder?.Dispose();
            nativeDecoder = null;
            indexedRuns.Clear();
            errorTail.Clear();
            Interlocked.Exchange(ref cacheBytes, 0);
        }
        finally { gate.Release(); }
        // Leave the semaphore valid for calls that raced disposal; they acquire it and receive
        // ObjectDisposedException instead of racing a disposed synchronization primitive.
    }

    private readonly record struct FrameStamp(double TimestampSeconds, long Key);

    private sealed class FrameRunIndex(double seekStartSeconds)
    {
        public List<FrameStamp> Frames { get; } = [];
        public double SeekStartSeconds { get; private set; } = seekStartSeconds;
        public bool ReachedEnd { get; set; }
        public double LastSeconds => Frames.Count == 0 ? double.NegativeInfinity : Frames[^1].TimestampSeconds;

        public void Append(FrameStamp frame)
        {
            Frames.Add(frame);
            if (Frames.Count > MaxIndexedFramesPerRun + 256)
            {
                var removeCount = Frames.Count - MaxIndexedFramesPerRun;
                var lastRemovedTimestamp = Frames[removeCount - 1].TimestampSeconds;
                var firstRetainedTimestamp = Frames[removeCount].TimestampSeconds;
                Frames.RemoveRange(0, removeCount);

                // Requests at the removed last PTS need the removed frame. Requests strictly
                // after it can use the first retained frame as the next-PTS match. Preserve
                // equal-PTS duplicates still present at the front of the retained index.
                var newCoverageStart = lastRemovedTimestamp < firstRetainedTimestamp
                    ? Math.BitIncrement(lastRemovedTimestamp)
                    : firstRetainedTimestamp;
                SeekStartSeconds = Math.Max(SeekStartSeconds, newCoverageStart);
            }
        }
    }
}
