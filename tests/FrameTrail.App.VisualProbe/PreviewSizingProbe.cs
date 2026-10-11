using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FrameTrail;
using FrameTrail.App;
using FrameTrail.Media;

namespace FrameTrail.App.VisualProbe;

/// <summary>Validates Auto preview sizing and source-resolution rendering without showing a window.</summary>
internal static class PreviewSizingProbe
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Verify(MainWindow window, string output, Action<bool, string, object?> check)
    {
        Directory.CreateDirectory(output);
        var dispatcher = window.Dispatcher;
        dispatcher.VerifyAccess();
        var oldContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        ScrubFrameDecoder? decoder = null;
        PreviewPlaybackSession? session = null;
        int? mainDecoderPid = null;
        int? playbackDecoderPid = null;
        try
        {
            check(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero,
                "预览尺寸探针不显示 MainWindow，也不创建 HWND", null);

            foreach (var dpiScale in new[] { 1d, 1.5d, 2d })
            {
                var physicalWidth = 1600 * dpiScale;
                var physicalHeight = 900 * dpiScale;
                var size = PreviewSizing.Output(0, 3840, 2160, physicalWidth, physicalHeight);
                check(size == ((int)physicalWidth, (int)physicalHeight), $"Auto 按 {dpiScale:0.##}× DPI 的物理视口输出 {physicalWidth}×{physicalHeight}", new
                {
                    DpiScale = dpiScale,
                    PhysicalViewport = new[] { physicalWidth, physicalHeight },
                    Output = new[] { size.Width, size.Height }
                });
            }

            var sourcePath = Path.Combine(output, "preview-sizing-4k-testsrc2.mp4");
            var projectPath = Path.Combine(output, "preview-sizing.frametrail");
            Pump(MediaTools.RunAsync(MediaTools.Ffmpeg,
                ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=3840x2160:rate=30:duration=0.2",
                 "-frames:v", "6", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "30", "-pix_fmt", "yuv420p", "-y", sourcePath]), dispatcher);
            File.WriteAllText(projectPath, "{}");
            check(File.Exists(sourcePath) && new FileInfo(sourcePath).Length > 0,
                "生成独立 4K CFR30 六帧夹具", new { Path = sourcePath, Bytes = new FileInfo(sourcePath).Length });

            var surface = (FrameworkElement)window.FindName("PreviewSurface");
            Layout(surface, 1600, 900);
            var measuredDpi = VisualTreeHelper.GetDpi(surface);
            var measuredSize = PreviewSizing.Output(0, 3840, 2160,
                surface.ActualWidth * measuredDpi.DpiScaleX, surface.ActualHeight * measuredDpi.DpiScaleY);
            check(measuredSize == (1600, 900), "离屏测量 1600×900 视口得到真实 Auto 输出 1600×900", new
            {
                Surface = new[] { surface.ActualWidth, surface.ActualHeight },
                Dpi = new[] { measuredDpi.DpiScaleX, measuredDpi.DpiScaleY },
                Output = new[] { measuredSize.Width, measuredSize.Height }
            });

            var project = MakeProject(Path.GetFileName(sourcePath));
            Set(window, "project", project);
            Set(window, "projectFile", projectPath);
            Set(window, "previewResolution", 0);
            var playhead = Get<Slider>(window, "Playhead");
            playhead.Maximum = project.Timeline.Duration;
            Set(window, "refreshing", true);
            playhead.Value = .1;
            Set(window, "refreshing", false);
            Set(window, "completedPreview", null);
            var cold = Stopwatch.StartNew();
            Pump(CallRender(window, .1), dispatcher);
            cold.Stop();
            var image = CurrentImage(window);
            var scene = Get<FrameScene>(window, "displayedPreviewScene");
            var decoderKey = GetField(window, "scrubDecoderKey")!;
            var keyType = decoderKey.GetType();
            var inputWidth = (int)keyType.GetField("Item4")!.GetValue(decoderKey)!;
            var inputHeight = (int)keyType.GetField("Item5")!.GetValue(decoderKey)!;
            decoder = GetField(window, "scrubDecoder") as ScrubFrameDecoder;
            mainDecoderPid = decoder?.ActiveProcessId;
            check(image is { PixelWidth: 1600, PixelHeight: 900 } && inputWidth == 3840 && inputHeight == 2160,
                "Auto 主窗口保留 3840×2160 解码输入并渲染 1600×900 输出", new
                {
                    Input = new[] { inputWidth, inputHeight },
                    Output = image is null ? null : new[] { image.PixelWidth, image.PixelHeight },
                    SourceTime = scene.SourceTime,
                    ColdMilliseconds = cold.Elapsed.TotalMilliseconds,
                    DecoderPid = decoder?.ActiveProcessId
                });

            var outputWidth2 = 1280;
            var outputHeight2 = 720;
            Layout(surface, outputWidth2, outputHeight2);
            Set(window, "completedPreview", null);
            var resize = Stopwatch.StartNew();
            Pump(CallRender(window, .1), dispatcher);
            resize.Stop();
            var resized = CurrentImage(window);
            check(resized is { PixelWidth: 1280, PixelHeight: 720 } &&
                  Math.Abs(Get<FrameScene>(window, "displayedPreviewScene").SourceTime - scene.SourceTime) < 1e-9,
                "视口调整后同一 source frame 重新按新宽高合成", new
                {
                    Output = resized is null ? null : new[] { resized.PixelWidth, resized.PixelHeight },
                    SourceTime = Get<FrameScene>(window, "displayedPreviewScene").SourceTime,
                    ResizeMilliseconds = resize.Elapsed.TotalMilliseconds
                });

            Set(window, "previewResolution", 960);
            Set(window, "completedPreview", null);
            Pump(CallRender(window, .1), dispatcher);
            var explicitImage = CurrentImage(window);
            var explicitKey = GetField(window, "scrubDecoderKey")!;
            var explicitWidth = (int)explicitKey.GetType().GetField("Item4")!.GetValue(explicitKey)!;
            var explicitHeight = (int)explicitKey.GetType().GetField("Item5")!.GetValue(explicitKey)!;
            check(explicitImage is { PixelWidth: 960, PixelHeight: 540 } && explicitWidth == 960 && explicitHeight == 540,
                "显式 960 档继续以 960×540 解码和输出", new
                {
                    Input = new[] { explicitWidth, explicitHeight },
                    Output = explicitImage is null ? null : new[] { explicitImage.PixelWidth, explicitImage.PixelHeight }
                });

            // Compare the same scene, full-resolution source frame, zoom effect and GPU renderer.
            // The session uses its public preservation switch, while MainWindow uses Auto sizing.
            Set(window, "previewResolution", 0);
            Layout(surface, 1600, 900);
            Set(window, "completedPreview", null);
            Pump(CallRender(window, .1), dispatcher);
            var mainWindowImage = CurrentImage(window)!;
            var outputPixels = new byte[1600 * 900 * 4];
            session = PumpResult(PreviewPlaybackSession.CreateAsync(project, sourcePath, projectPath,
                1600, 900, 30, preserveSourceResolution: true), dispatcher);
            Pump(session.ReadFrameAsync(.1, outputPixels), dispatcher);
            playbackDecoderPid = session.ActiveDecoderProcessId;
            var sessionBitmap = BitmapSource.Create(1600, 900, 96, 96, PixelFormats.Bgra32, null, outputPixels, 1600 * 4);
            sessionBitmap.Freeze();
            var mainSamples = SamplePixels(mainWindowImage);
            var playbackSamples = SamplePixels(sessionBitmap);
            check(mainSamples.SequenceEqual(playbackSamples),
                "Auto 主窗口与 preserveSourceResolution 播放会话在同一 zoom 场景的采样像素一致", new
                {
                    MainWindow = mainSamples,
                    PlaybackSession = playbackSamples,
                    Scene = new { Get<FrameScene>(window, "displayedPreviewScene").SourceTime },
                    MainDecoderPid = decoder?.ActiveProcessId,
                    SessionDecoderPid = session.ActiveDecoderProcessId
                });

            check(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero,
                "预览尺寸探针结束时 MainWindow 仍未显示且没有 HWND", null);
        }
        finally
        {
            Set(window, "closeReady", true);
            Get<DispatcherTimer>(window, "previewResizeTimer").Stop();
            try
            {
                if (session is not null) Pump(session.DisposeAsync().AsTask(), dispatcher);
                var seek = GetField(window, "seekCancellation") as CancellationTokenSource;
                seek?.Cancel();
                var mainDecoder = GetField(window, "scrubDecoder") as ScrubFrameDecoder;
                if (mainDecoder is not null) Pump(mainDecoder.DisposeAsync().AsTask(), dispatcher);
                Set(window, "scrubDecoder", null);
                Set(window, "scrubDecoderKey", null);
                seek?.Dispose();
                Set(window, "seekCancellation", null);
                (GetField(window, "previewAssets") as IDisposable)?.Dispose();
                Set(window, "previewAssets", null);
                (GetField(window, "previewRenderer") as IDisposable)?.Dispose();
                Set(window, "previewRenderer", null);
                check(!IsProcessAlive(mainDecoderPid) && !IsProcessAlive(playbackDecoderPid),
                    "探针结束后 MainWindow 与播放会话各自的 FFmpeg 进程均已退出", new
                    {
                        MainDecoderPid = mainDecoderPid,
                        PlaybackDecoderPid = playbackDecoderPid
                    });
            }
            finally { SynchronizationContext.SetSynchronizationContext(oldContext); }
        }
    }

    private static FrameTrailProject MakeProject(string sourcePath) => new()
    {
        MediaPath = sourcePath,
        Media = new() { Width = 3840, Height = 2160, Duration = .2, FrameRate = 30 },
        Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = .2 }] },
        Settings = new() { CanvasWidth = 3840, CanvasHeight = 2160, Padding = 0, CornerRadius = 0, Shadow = 0 },
        ZoomClips = [new() { Start = 0, End = .2, Scale = 1.8, Focus = new(.31, .63) }]
    };

    private static Task CallRender(MainWindow window, double time) =>
        (Task)(typeof(MainWindow).GetMethod("RenderPreview", Private)!.Invoke(window, [time])
            ?? throw new InvalidOperationException("RenderPreview returned null."));

    private static void Layout(FrameworkElement element, double width, double height)
    {
        var dpi = VisualTreeHelper.GetDpi(element);
        width /= dpi.DpiScaleX;
        height /= dpi.DpiScaleY;
        element.Width = width;
        element.Height = height;
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static void Pump(Task task, Dispatcher dispatcher)
    {
        if (task.IsCompleted) { task.GetAwaiter().GetResult(); return; }
        var frame = new DispatcherFrame();
        var timedOut = false;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timeout.Tick += (_, _) => { timeout.Stop(); timedOut = true; frame.Continue = false; };
        timeout.Start();
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => frame.Continue = false)),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        try { Dispatcher.PushFrame(frame); }
        finally { timeout.Stop(); }
        if (timedOut) throw new TimeoutException("Preview sizing probe exceeded 60 seconds.");
        task.GetAwaiter().GetResult();
    }

    private static T PumpResult<T>(Task<T> task, Dispatcher dispatcher)
    {
        Pump(task, dispatcher);
        return task.GetAwaiter().GetResult();
    }

    private static BitmapSource CurrentImage(MainWindow window) =>
        (BitmapSource)((Image)window.FindName("PreviewImage")).Source;

    private static T Get<T>(MainWindow window, string name) =>
        (T)(GetField(window, name) ?? throw new InvalidOperationException($"Field {name} has no value."));

    private static object? GetField(MainWindow window, string name) => typeof(MainWindow).GetField(name, Private)!.GetValue(window);
    private static void Set(MainWindow window, string name, object? value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);

    private static uint[] SamplePixels(BitmapSource bitmap)
    {
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);
        var points = new[] { (width / 8, height / 8), (width / 2, height / 2), (width * 7 / 8, height / 3), (width / 3, height * 7 / 8) };
        return points.Select(p => BitConverter.ToUInt32(pixels, p.Item2 * stride + p.Item1 * 4)).ToArray();
    }

    private static bool IsProcessAlive(int? processId)
    {
        if (processId is null) return false;
        try { using var process = Process.GetProcessById(processId.Value); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
}
