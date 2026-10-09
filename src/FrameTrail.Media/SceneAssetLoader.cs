using System.Diagnostics;
using System.Globalization;
using FrameTrail;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FrameTrail.Media;

/// <summary>Resolves project-relative scene assets into decoded layers shared by preview and export.</summary>
public sealed class SceneAssetLoader : IDisposable
{
    private readonly string projectFile;
    private readonly int outputWidth, outputHeight;
    private readonly Dictionary<string, (int Width, int Height, byte[] Pixels)> images = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MediaInfo> mediaInfo = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CameraFrameStream> cameraStreams = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BackgroundVideoFrameStream> backgroundVideoStreams = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (int Width, int Height, byte[] Pixels)> textLayers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;

    public SceneAssetLoader(string projectFile, int outputWidth, int outputHeight)
    {
        if (string.IsNullOrWhiteSpace(projectFile)) throw new ArgumentException("项目路径不能为空。", nameof(projectFile));
        if (outputWidth < 16 || outputHeight < 16) throw new ArgumentOutOfRangeException(nameof(outputWidth));
        this.projectFile = Path.GetFullPath(projectFile); this.outputWidth = outputWidth; this.outputHeight = outputHeight;
    }

    /// <summary>Loads active background, camera and image layers. Calls are serialized for a shared preview loader.</summary>
    public async Task<IReadOnlyList<FrameLayerInput>> LoadAsync(FrameScene scene, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(scene);
        await gate.WaitAsync(cancellation);
        try
        {
            var result = new List<FrameLayerInput>(2 + (scene.Overlays?.Count ?? 0));
            if (scene.BackgroundStyle is { Kind: BackgroundKind.Image, ImagePath: { Length: > 0 } backgroundPath })
            {
                var path = FrameTrailProjectStore.ResolveAssetPath(projectFile, backgroundPath);
                var info = await Probe(path, cancellation);
                if (info.Width <= 0 || info.Height <= 0) throw new InvalidDataException("背景图片无法解码。");
                var scale = Math.Min(1, 4096d / Math.Max(info.Width, info.Height));
                var w = Math.Max(1, (int)Math.Round(info.Width * scale)); var h = Math.Max(1, (int)Math.Round(info.Height * scale));
                var (_, _, pixels) = await LoadImage(path, w, h, cancellation);
                var style = scene.BackgroundStyle!;
                result.Add(new(w, h, pixels, NormalizedRect.Full, Opacity: 1, Layer: int.MinValue, BehindScreen: true, Fit: style.ImageFit, Blur: style.Blur));
            }
            if (scene.BackgroundStyle is { Kind: BackgroundKind.Video, VideoPath: { Length: > 0 } videoPath } videoStyle)
            {
                var path = FrameTrailProjectStore.ResolveAssetPath(projectFile, videoPath);
                var info = await Probe(path, cancellation);
                if (info.Width < 2 || info.Height < 2 || info.Duration <= 0) throw new InvalidDataException("背景视频没有有效的视频帧。");
                var scale = Math.Min(1, 4096d / Math.Max(info.Width, info.Height));
                var width = Math.Max(2, (int)Math.Round(info.Width * scale / 2) * 2);
                var height = Math.Max(2, (int)Math.Round(info.Height * scale / 2) * 2);
                if (!backgroundVideoStreams.TryGetValue(path, out var stream))
                    backgroundVideoStreams.Add(path, stream = new BackgroundVideoFrameStream(path, width, height));
                var requested = Math.Max(0, videoStyle.VideoStartTime + scene.OutputTime * Math.Clamp(videoStyle.Speed, 0, 10));
                var localTime = videoStyle.VideoLoop ? requested % info.Duration : Math.Min(requested, Math.Max(0, info.Duration - 1d / 30));
                var pixels = await stream.ReadAsync(localTime, cancellation);
                result.Add(new(width, height, pixels, NormalizedRect.Full, Opacity: 1, Layer: int.MinValue,
                    BehindScreen: true, Fit: videoStyle.ImageFit, Blur: videoStyle.Blur));
            }
            if (scene.Canvas is { Frame: not ScreenFrameKind.None } frameCanvas && (!string.IsNullOrWhiteSpace(frameCanvas.Title) || !string.IsNullOrWhiteSpace(frameCanvas.Address)))
            {
                var rect = FrameHeaderRect(scene, frameCanvas);
                var tileWidth = 1024; var tileHeight = Math.Clamp((int)Math.Round(tileWidth * rect.Height * outputHeight / Math.Max(rect.Width * outputWidth, 1)), 24, 128);
                var key = $"{tileWidth}x{tileHeight}|{frameCanvas.Frame}|{frameCanvas.Title}|{frameCanvas.Address}";
                if (!textLayers.TryGetValue(key, out var textTile)) textLayers.Add(key, textTile = BuildFrameText(frameCanvas, tileWidth, tileHeight, cancellation));
                result.Add(new(textTile.Width, textTile.Height, textTile.Pixels, rect, Layer: int.MaxValue, FollowScreenTransform: true));
            }
            foreach (var overlay in scene.Overlays ?? [])
            {
                cancellation.ThrowIfCancellationRequested();
                var path = FrameTrailProjectStore.ResolveAssetPath(projectFile, overlay.MediaPath);
                var info = await Probe(path, cancellation);
                if (info.Width <= 0 || info.Height <= 0) throw new InvalidDataException($"贴图没有可解码的视频帧：{overlay.MediaPath}");
                var scale = Math.Min(1, 2048d / Math.Max(info.Width, info.Height));
                var width = Math.Max(1, (int)Math.Round(info.Width * scale)); var height = Math.Max(1, (int)Math.Round(info.Height * scale));
                var (_, _, pixels) = await LoadImage(path, width, height, cancellation);
                result.Add(new(width, height, pixels, overlay.Rect, overlay.Rotation, overlay.Opacity, overlay.Layer, overlay.CornerRadius, Blur: overlay.Blur));
            }
            if (scene.Camera is { } camera)
            {
                var path = FrameTrailProjectStore.ResolveAssetPath(projectFile, camera.MediaPath);
                var cameraInfo = await Probe(path, cancellation);
                if (cameraInfo.Duration <= 0 || camera.SourceTime < 0 || camera.SourceTime >= cameraInfo.Duration)
                    throw new InvalidDataException($"摄像头素材时间越界：请求 {camera.SourceTime:0.###} 秒，素材时长 {cameraInfo.Duration:0.###} 秒。请缩短摄像头源区间或调整起点。");
                var edge = Math.Clamp(Math.Min(outputWidth, outputHeight) / 3, 96, 720);
                if (!cameraStreams.TryGetValue(path, out var stream)) cameraStreams.Add(path, stream = new CameraFrameStream(path, edge, edge));
                var pixels = await stream.ReadAsync(camera.SourceTime, cancellation);
                result.Add(new(edge, edge, pixels, camera.Rect, Rotation: camera.Rotation, Opacity: camera.Opacity, Layer: int.MaxValue - 1,
                    CornerRadius: camera.CornerRadius, Mirrored: camera.Mirrored, Shape: camera.Shape, SourceCrop: camera.Crop,
                    BorderWidth: camera.BorderWidth, BorderColor: camera.BorderColor));
            }
            return result;
        }
        finally { gate.Release(); }
    }

    private async Task<(int Width, int Height, byte[] Pixels)> LoadImage(string path, int width, int height, CancellationToken cancellation)
    {
        var key = $"{path}|{width}x{height}";
        if (images.TryGetValue(key, out var cached)) return cached;
        var pixels = await MediaTools.DecodeFrameAsync(path, 0, width, height, cancellation);
        var entry = (width, height, pixels); images.Add(key, entry); return entry;
    }

    private async Task<MediaInfo> Probe(string path, CancellationToken cancellation)
    {
        if (mediaInfo.TryGetValue(path, out var cached)) return cached;
        var info = await MediaTools.ProbeAsync(path, cancellation);
        mediaInfo.Add(path, info);
        return info;
    }

    public void Dispose()
    {
        if (disposed) return; disposed = true; images.Clear(); mediaInfo.Clear(); textLayers.Clear();
        foreach (var stream in cameraStreams.Values) stream.Dispose();
        foreach (var stream in backgroundVideoStreams.Values) stream.Dispose();
        cameraStreams.Clear(); backgroundVideoStreams.Clear(); gate.Dispose();
    }

    private NormalizedRect FrameHeaderRect(FrameScene scene, CanvasSettings canvas)
    {
        var sw = scene.SourceWidth > 0 ? scene.SourceWidth : canvas.AspectWidth;
        var sh = scene.SourceHeight > 0 ? scene.SourceHeight : canvas.AspectHeight;
        var frameScale = canvas.FrameScale;
        var shortEdge = Math.Min(outputWidth, outputHeight);
        var baseHeader = canvas.Frame switch { ScreenFrameKind.WindowLight or ScreenFrameKind.WindowDark => shortEdge * (46d / 1080), ScreenFrameKind.BrowserLight or ScreenFrameKind.BrowserDark => shortEdge * (46d * 1.08 / 1080), _ => 0 };
        var padding = Math.Clamp(scene.Padding, 0, .49);
        var fit = Math.Min(outputWidth * (1 - 2 * padding) / sw, Math.Max(2, outputHeight * (1 - 2 * padding) - baseHeader * frameScale) / sh);
        var scale = (scene.Canvas?.ContentScale ?? 1) * frameScale * (scene.ScreenMotion?.Scale ?? 1) * (scene.Opening?.Scale ?? 1);
        var width = sw * fit * scale; var height = sh * fit * scale;
        var position = scene.ScreenMotion?.Position ?? canvas.ContentPosition;
        var cx = position.X * outputWidth + (scene.Opening?.OffsetX ?? 0) * outputWidth;
        var header = baseHeader * scale;
        var cy = position.Y * outputHeight + (scene.Opening?.OffsetY ?? 0) * outputHeight + header * .5;
        return new NormalizedRect((cx - width / 2) / outputWidth, (cy - height / 2 - header) / outputHeight, width / outputWidth, header / outputHeight).Clamp();
    }

    private (int Width, int Height, byte[] Pixels) BuildFrameText(CanvasSettings canvas, int textWidth, int textHeight, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var buffer = new byte[checked(textWidth * textHeight * 4)];
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    var light = canvas.Frame is ScreenFrameKind.WindowLight or ScreenFrameKind.BrowserLight;
                    var titleBrush = new SolidColorBrush(light ? Color.FromRgb(153, 153, 153) : Color.FromRgb(226, 229, 235));
                    var addressBrush = new SolidColorBrush(light ? Color.FromRgb(130, 136, 146) : Color.FromRgb(190, 195, 204));
                    var hasTitle = !string.IsNullOrWhiteSpace(canvas.Title);
                    var hasAddress = !string.IsNullOrWhiteSpace(canvas.Address);
                    if (hasTitle)
                    {
                        var titleTypeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                        var title = new FormattedText(canvas.Title, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, titleTypeface, Math.Clamp(textHeight * (hasAddress ? .31 : .32), 9, 28), titleBrush, 1.0);
                        var x = (textWidth - title.Width) * .5;
                        var y = hasAddress ? textHeight * .015 : (textHeight - title.Height) * .5;
                        dc.DrawText(title, new Point(x, y));
                    }
                    if (hasAddress)
                    {
                        var address = new FormattedText(canvas.Address, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), Math.Clamp(textHeight * .23, 8, 20), addressBrush, 1.0);
                        var x = (textWidth - address.Width) * .5;
                        var y = hasTitle ? textHeight * .56 : (textHeight - address.Height) * .5;
                        dc.DrawText(address, new Point(x, y));
                    }
                }
                var bitmap = new RenderTargetBitmap(textWidth, textHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var premultiplied = new byte[buffer.Length]; bitmap.CopyPixels(premultiplied, textWidth * 4, 0);
                for (var i = 0; i < premultiplied.Length; i += 4)
                {
                    var a = premultiplied[i + 3]; buffer[i + 3] = a;
                    if (a == 0) continue;
                    buffer[i] = (byte)Math.Min(255, premultiplied[i] * 255 / a);
                    buffer[i + 1] = (byte)Math.Min(255, premultiplied[i + 1] * 255 / a);
                    buffer[i + 2] = (byte)Math.Min(255, premultiplied[i + 2] * 255 / a);
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new InvalidOperationException("无法生成窗口标题图层。", failure);
        return (textWidth, textHeight, buffer);
    }
}

/// <summary>Sequential 30 fps decoder; backwards seeks and large jumps restart FFmpeg at the requested timestamp.</summary>
internal sealed class CameraFrameStream : IDisposable
{
    private const int FrameRate = 30;
    private readonly string path;
    private readonly int width, height;
    private readonly SemaphoreSlim gate = new(1, 1);
    private Process? process;
    private Task<string>? errorText;
    private long currentFrame = -1;
    private byte[]? lastFrame;
    private bool disposed;

    public CameraFrameStream(string path, int width, int height) { this.path = path; this.width = width; this.height = height; }

    public async Task<byte[]> ReadAsync(double sourceTime, CancellationToken cancellation)
    {
        if (!double.IsFinite(sourceTime) || sourceTime < 0) throw new ArgumentOutOfRangeException(nameof(sourceTime));
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellation);
        try
        {
            var target = (long)Math.Round(sourceTime * FrameRate);
            if (process is null || process.HasExited || target < currentFrame || target - currentFrame > FrameRate * 2L)
            {
                await Restart(target, cancellation);
            }
            if (target == currentFrame && lastFrame is not null) return lastFrame;
            while (currentFrame < target)
            {
                lastFrame = await ReadFrame(cancellation); currentFrame++;
            }
            return lastFrame!;
        }
        catch when (cancellation.IsCancellationRequested)
        {
            // A cancelled preview seek must not leave a killed FFmpeg process cached for the next seek.
            await StopProcess(); currentFrame = -1; lastFrame = null;
            throw new OperationCanceledException(cancellation);
        }
        finally { gate.Release(); }
    }

    private async Task Restart(long target, CancellationToken cancellation)
    {
        await StopProcess();
        currentFrame = target - 1; lastFrame = null;
        var time = target / (double)FrameRate;
        process = MediaTools.Start(MediaTools.Ffmpeg,
            ["-v", "error", "-ss", MediaTools.Number(time), "-i", path, "-an", "-vf", $"fps={FrameRate},scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height},format=bgra", "-f", "rawvideo", "pipe:1"], output: true);
        errorText = process.StandardError.ReadToEndAsync();
        lastFrame = await ReadFrame(cancellation); currentFrame = target;
    }

    private async Task<byte[]> ReadFrame(CancellationToken cancellation)
    {
        var p = process ?? throw new InvalidOperationException("相机解码进程尚未启动。");
        var frame = new byte[checked(width * height * 4)];
        try { await p.StandardOutput.BaseStream.ReadExactlyAsync(frame, cancellation); return frame; }
        catch (EndOfStreamException)
        {
            await p.WaitForExitAsync(cancellation); var errors = errorText is null ? "" : await errorText;
            throw new IOException("相机素材提前结束或无法解码：" + MediaTools.Tail(errors));
        }
    }

    private async Task StopProcess()
    {
        if (process is null) return;
        var old = process; process = null;
        await MediaTools.StopAsync(old); old.Dispose(); errorText = null;
    }

    public void Dispose()
    {
        if (disposed) return; disposed = true;
        var old = process; process = null;
        if (old is not null) { MediaTools.Kill(old); old.Dispose(); }
        lastFrame = null; gate.Dispose();
    }
}

/// <summary>Sequential loopable decoder for project background video; it retains only one decoded frame.</summary>
internal sealed class BackgroundVideoFrameStream : IDisposable
{
    private const int FrameRate = 30;
    private readonly string path;
    private readonly int width, height;
    private readonly SemaphoreSlim gate = new(1, 1);
    private Process? process;
    private Task<string>? errorText;
    private long currentFrame = -1;
    private byte[]? lastFrame;
    private bool disposed;

    public BackgroundVideoFrameStream(string path, int width, int height)
    { this.path = path; this.width = width; this.height = height; }

    public async Task<byte[]> ReadAsync(double time, CancellationToken cancellation)
    {
        if (!double.IsFinite(time) || time < 0) throw new ArgumentOutOfRangeException(nameof(time));
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellation);
        try
        {
            var target = (long)Math.Floor(time * FrameRate + 1e-7);
            if (process is null || target < currentFrame || target - currentFrame > FrameRate * 2L)
                await Restart(target, cancellation);
            if (target == currentFrame && lastFrame is not null) return lastFrame;
            while (currentFrame < target)
            {
                lastFrame = await ReadFrame(cancellation);
                currentFrame++;
            }
            return lastFrame!;
        }
        catch when (cancellation.IsCancellationRequested)
        {
            await StopProcess();
            throw new OperationCanceledException(cancellation);
        }
        finally { gate.Release(); }
    }

    private async Task Restart(long target, CancellationToken cancellation)
    {
        await StopProcess();
        currentFrame = target - 1; lastFrame = null;
        var time = target / (double)FrameRate;
        process = MediaTools.Start(MediaTools.Ffmpeg,
            ["-v", "error", "-ss", MediaTools.Number(time), "-i", path, "-an", "-vf",
             $"fps={FrameRate},scale={width}:{height}:force_original_aspect_ratio=decrease:flags=bilinear,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,format=bgra",
             "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1"], output: true);
        errorText = process.StandardError.ReadToEndAsync();
        lastFrame = await ReadFrame(cancellation); currentFrame = target;
    }

    private async Task<byte[]> ReadFrame(CancellationToken cancellation)
    {
        var active = process ?? throw new InvalidOperationException("背景视频解码进程尚未启动。");
        using var registration = cancellation.Register(() => MediaTools.Kill(active));
        var frame = new byte[checked(width * height * 4)];
        try { await active.StandardOutput.BaseStream.ReadExactlyAsync(frame, cancellation); return frame; }
        catch (EndOfStreamException)
        {
            await active.WaitForExitAsync(cancellation);
            var errors = errorText is null ? "" : await errorText;
            throw new IOException("背景视频提前结束或无法解码：" + MediaTools.Tail(errors));
        }
    }

    private async Task StopProcess()
    {
        var old = process; process = null; errorText = null;
        if (old is null) return;
        await MediaTools.StopAsync(old); old.Dispose();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        var old = process; process = null;
        if (old is not null) { MediaTools.Kill(old); old.Dispose(); }
        lastFrame = null; gate.Dispose();
    }
}
