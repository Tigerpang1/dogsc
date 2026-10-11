using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FrameTrail;
using FrameTrail.App;
using FrameTrail.Media;

namespace FrameTrail.App.VisualProbe;

internal static class PreviewLatencyProbe
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Verify(MainWindow window, string output, string projectFile)
    {
        Directory.CreateDirectory(output);
        var dispatcher = window.Dispatcher;
        var originalContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var rows = new List<object>();
        var timings = new List<object>();
        var sourceHashBefore = HashFile(projectFile);
        var project = FrameTrailProjectStore.Load(projectFile);
        var hook = typeof(MainWindow).GetField("PreviewFrameMeasured", Private)
            ?? throw new MissingFieldException(typeof(MainWindow).FullName, "PreviewFrameMeasured");
        Stopwatch? clickClock = null;
        double? bitmapAssignedMs = null;
        var hookDelegate = new Action<PreviewFrameTiming>(timing =>
        {
            timings.Add(timing);
            if (clickClock is not null && bitmapAssignedMs is null)
                bitmapAssignedMs = clickClock.Elapsed.TotalMilliseconds;
        });

        try
        {
            if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("Latency probe requires an unshown MainWindow with no HWND.");
            if (project.Timeline.Segments.Count == 0 || project.Media.FrameRate <= 0 || project.Timeline.Duration <= 0)
                throw new InvalidDataException("Project must contain timeline segments and a valid frame rate.");
            if (hook.GetValue(window) is not null) throw new InvalidOperationException("Preview timing hook must be unset before probe.");

            Set(window, "project", project);
            Set(window, "projectFile", Path.GetFullPath(projectFile));
            Set(window, "inspectorTab", "");
            window.Measure(new Size(1600, 900));
            window.Arrange(new Rect(0, 0, 1600, 900));
            window.UpdateLayout();
            var previewSurface = (FrameworkElement)window.FindName("PreviewSurface");
            previewSurface.Measure(new Size(1600, 900));
            previewSurface.Arrange(new Rect(0, 0, 1600, 900));
            previewSurface.UpdateLayout();
            var timeline = (EditorTimelineControl)window.FindName("TimelineView");
            typeof(EditorTimelineControl).GetProperty("Project", Private)!.SetValue(timeline, project);
            timeline.Snap = false;
            timeline.Zoom = 12;
            timeline.Refresh();
            timeline.Measure(new Size(1152, 400));
            timeline.Arrange(new Rect(0, 0, 1152, timeline.Height));
            timeline.UpdateLayout();
            var hitLayout = new RenderTargetBitmap(1152, (int)Math.Ceiling(timeline.ActualHeight), 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32);
            hitLayout.Render(timeline);

            var playhead = Get<Slider>(window, "Playhead");
            playhead.Maximum = project.Timeline.Duration;
            Set(window, "refreshing", true);
            try { playhead.Value = project.Timeline.Duration; }
            finally { Set(window, "refreshing", false); }

            var fps = project.Media.FrameRate;
            var totalFrames = Math.Max(2L, (long)Math.Floor(project.Timeline.Duration * fps));
            long ToFrame(double fraction) => Math.Clamp((long)Math.Round(totalFrames * fraction), 1, totalFrames - 1);
            var cold = ToFrame(.1);
            var adjacent = Math.Min(totalFrames - 1, cold + 1);
            var nearby = Math.Min(totalFrames - 1, cold + 3);
            var forwardJump = ToFrame(.7);
            var reverseJump = ToFrame(.3);
            var forward1 = Math.Min(totalFrames - 1, reverseJump + 1);
            var forward2 = Math.Min(totalFrames - 1, reverseJump + 2);
            var forward3 = Math.Min(totalFrames - 1, reverseJump + 3);
            var alternateA = ToFrame(.58);
            var alternateB = ToFrame(.44);
            var alternateC = ToFrame(.63);
            var sequence = new (string Scenario, long Frame, string Source)[]
            {
                ("cold", cold, "ruler"),
                ("same-frame", cold, "main-clip"),
                ("adjacent", adjacent, "ruler"),
                ("nearby", nearby, "main-clip"),
                ("jump-forward", forwardJump, "ruler"),
                ("jump-reverse", reverseJump, "main-clip"),
                ("revisit-previous-target", nearby, "ruler"),
                ("forward-neighbor-1", forward1, "main-clip"),
                ("forward-neighbor-2", forward2, "ruler"),
                ("forward-neighbor-3", forward3, "main-clip"),
                ("alternate-direction-a", alternateA, "ruler"),
                ("alternate-direction-b", alternateB, "main-clip"),
                ("alternate-direction-c", alternateC, "ruler")
            };

            hook.SetValue(window, hookDelegate);
            foreach (var resolution in new[] { 1440, 0 })
            {
                WaitForIdle(window, dispatcher);
                DisposeTierResources(window, dispatcher);
                Set(window, "completedPreview", (object?)null);
                Set(window, "displayedPreviewScene", (object?)null);
                Set(window, "displayedPreviewProject", (object?)null);
                ((Image)window.FindName("PreviewImage")).Source = null;
                Set(window, "previewResolution", resolution);

                var firstRowIndex = rows.Count;
                for (var index = 0; index < sequence.Length; index++)
                {
                    var item = sequence[index];
                    var intendedOutput = item.Frame / fps;
                    var clickPoint = FindClickPoint(timeline, project.Timeline.Duration, intendedOutput, item.Source == "main-clip");
                    var oldBitmap = CurrentImage(window);
                    var beforeStarts = Decoder(window)?.ProcessStartCount ?? 0;
                    var beforeHits = Decoder(window)?.CacheHits ?? 0;
                    var timingIndexBefore = timings.Count;
                    bitmapAssignedMs = null;
                    clickClock = Stopwatch.StartNew();
                    timeline.HandlePointerDown(clickPoint, MouseButton.Left, ModifierKeys.None, false);
                    timeline.HandlePointerUp(MouseButton.Left, clickPoint, false);
                    var clickHandlerMs = clickClock.Elapsed.TotalMilliseconds;
                    var requested = playhead.Value;
                    var timeUntilBitmap = WaitForBitmapAndIdle(dispatcher, window, requested, clickClock,
                        () => bitmapAssignedMs, out var handlerEndFallback);
                    clickClock.Stop();
                    var scene = CurrentScene(window);
                    var expectedSourceFrameTime = Math.Floor(project.Timeline.MapOutputToSource(requested) * fps + 1e-7) / fps;
                    if (Math.Abs(scene.OutputTime - requested) > 1e-6 || Math.Abs(scene.SourceTime - expectedSourceFrameTime) > 1.1 / fps)
                        throw new InvalidOperationException($"{item.Scenario} scene/playhead mismatch: requested={requested}, output={scene.OutputTime}, source={scene.SourceTime}.");
                    var currentBitmap = CurrentImage(window)!;
                    rows.Add(new
                    {
                        Resolution = resolution == 0 ? "auto" : "1440p",
                        Scenario = item.Scenario,
                        Input = item.Source,
                        TargetFrame = item.Frame,
                        ActualPlayheadFrame = (long)Math.Round(requested * fps),
                        ClickHandlerMs = clickHandlerMs,
                        UntilBitmapReadyMs = timeUntilBitmap,
                        UntilBitmapReadySource = handlerEndFallback ? "click-handler-return (same-target cache early return)" : "PreviewFrameMeasured callback after PreviewImage.Source assignment",
                        Playhead = requested,
                        SceneOutputTime = scene.OutputTime,
                        SceneSourceTime = scene.SourceTime,
                        BitmapChanged = !ReferenceEquals(oldBitmap, currentBitmap),
                        BitmapSameReference = ReferenceEquals(oldBitmap, currentBitmap),
                        ProcessStartCountBefore = beforeStarts,
                        ProcessStartCountAfter = Decoder(window)?.ProcessStartCount ?? 0,
                        CacheHitsBefore = beforeHits,
                        CacheHitsAfter = Decoder(window)?.CacheHits ?? 0,
                        HookTimingCount = timings.Count,
                        HookTiming = timings.Skip(timingIndexBefore).LastOrDefault()
                    });
                }
                WaitForIdle(window, dispatcher);
                var tierRows = rows.Skip(firstRowIndex).ToArray();
                File.WriteAllText(Path.Combine(output, $"report-{(resolution == 0 ? "auto" : "1440p")}.json"),
                    JsonSerializer.Serialize(new
                    {
                        Project = Path.GetFullPath(projectFile),
                        ProjectId = project.ProjectId,
                        ProjectSha256Before = sourceHashBefore,
                        ProjectSha256After = HashFile(projectFile),
                        SourceProjectUnchanged = sourceHashBefore == HashFile(projectFile),
                        Resolution = resolution == 0 ? "auto" : "1440p",
                        Duration = project.Timeline.Duration,
                        FrameRate = fps,
                        Surface = new { Width = previewSurface.ActualWidth, Height = previewSurface.ActualHeight, Dpi = 96 },
                        SurfaceWasLaidOutAt = new { Width = 1600, Height = 900 },
                        TimelineLayout = new { Width = 1152, Height = timeline.ActualHeight },
                        Clicks = tierRows,
                        BitmapReadinessMeans = "PreviewImage.Source was assigned and the matching timeline queue became idle; physical display presentation is not measured.",
                        NoHwnd = new WindowInteropHelper(window).Handle == IntPtr.Zero,
                        WindowCount = Application.Current.Windows.Count,
                        closeReady = false
                    }, new JsonSerializerOptions { WriteIndented = true }));
            }
            if (Application.Current.Windows.Count != 1 || window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("Probe must leave exactly one unshown MainWindow and no HWND.");
            if (sourceHashBefore != HashFile(projectFile))
                throw new InvalidOperationException("Read-only latency probe changed the source project.");
        }
        finally
        {
            clickClock?.Stop();
            Set(window, "closeReady", true);
            Get<DispatcherTimer>(window, "previewResizeTimer").Stop();
            WaitForIdle(window, dispatcher);
            Set(window, "PreviewFrameMeasured", (object?)null);
            var seek = GetNullable(window, "seekCancellation") as CancellationTokenSource;
            seek?.Cancel();
            DisposeTierResources(window, dispatcher);
            seek?.Dispose();
            Set(window, "seekCancellation", (object?)null);
            if (rows.Count > 0)
                File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
                {
                    Project = Path.GetFullPath(projectFile),
                    ProjectSha256Before = sourceHashBefore,
                    ProjectSha256After = HashFile(projectFile),
                    SourceProjectUnchanged = sourceHashBefore == HashFile(projectFile),
                    ResolutionReports = new[] { "report-1440p.json", "report-auto.json" },
                    BitmapReadinessMeans = "PreviewImage.Source was assigned; physical display presentation is not measured.",
                    NoHwnd = new WindowInteropHelper(window).Handle == IntPtr.Zero,
                    WindowCount = Application.Current.Windows.Count,
                    closeReady = Get<bool>(window, "closeReady")
                }, new JsonSerializerOptions { WriteIndented = true }));
            SynchronizationContext.SetSynchronizationContext(originalContext);
        }
    }

    private static Point FindClickPoint(EditorTimelineControl timeline, double duration, double targetTime, bool mainClip)
    {
        var labelWidth = 152d;
        var viewport = Math.Max(1, timeline.ActualWidth - labelWidth);
        var document = Math.Max(1, timeline.DocumentWidth);
        var scale = document / Math.Max(duration, .01);
        var pixelInDocument = targetTime * scale;
        var offset = Math.Clamp(pixelInDocument - viewport / 2, 0, Math.Max(0, document - viewport));
        timeline.Offset = offset;
        timeline.Refresh();
        timeline.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(timeline.ActualWidth), (int)Math.Ceiling(timeline.ActualHeight), 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(timeline);
        var x = Math.Clamp(labelWidth + pixelInDocument - offset, labelWidth + 1, timeline.ActualWidth - 1);
        return new Point(x, mainClip ? 85 : 12);
    }

    private static double WaitForBitmapAndIdle(Dispatcher dispatcher, MainWindow window, double requested,
        Stopwatch clickClock, Func<double?> getHookMs, out bool handlerEndFallback)
    {
        PumpUntil(dispatcher, () => IsQueueIdle(window) && GetNullable(window, "displayedPreviewScene") is FrameScene scene &&
            Math.Abs(scene.OutputTime - requested) <= 1e-6 && CurrentImage(window) is not null);
        handlerEndFallback = getHookMs() is null;
        return getHookMs() ?? clickClock.Elapsed.TotalMilliseconds;
    }

    private static void WaitForIdle(MainWindow window, Dispatcher dispatcher) => PumpUntil(dispatcher, () => IsQueueIdle(window));
    private static void DisposeTierResources(MainWindow window, Dispatcher dispatcher)
    {
        WaitForIdle(window, dispatcher);
        if (GetNullable(window, "scrubDecoder") is IAsyncDisposable decoder) PumpTask(decoder.DisposeAsync().AsTask(), dispatcher);
        Set(window, "scrubDecoder", (object?)null);
        Set(window, "scrubDecoderKey", (object?)null);
        (GetNullable(window, "previewAssets") as IDisposable)?.Dispose();
        Set(window, "previewAssets", (object?)null);
        Set(window, "previewAssetsProject", (object?)null);
        (GetNullable(window, "previewRenderer") as IDisposable)?.Dispose();
        Set(window, "previewRenderer", (object?)null);
        Set(window, "completedPreview", (object?)null);
        Set(window, "previewSize", (0, 0, 0, 0));
    }

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static ScrubFrameDecoder? Decoder(MainWindow window) => GetNullable(window, "scrubDecoder") as ScrubFrameDecoder;
    private static BitmapSource? CurrentImage(MainWindow window) => ((Image)window.FindName("PreviewImage")).Source as BitmapSource;
    private static FrameScene CurrentScene(MainWindow window) => (FrameScene)(GetNullable(window, "displayedPreviewScene") ?? throw new InvalidOperationException("No displayed preview scene."));
    private static bool IsQueueIdle(MainWindow window) => GetNullable(window, "activePreview") is null && GetNullable(window, "pendingPreview") is null && Get<bool>(window, "previewPumpRunning") == false;
    private static void PumpUntil(Dispatcher dispatcher, Func<bool> done)
    {
        var frame = new DispatcherFrame();
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
        timeout.Tick += (_, _) => { timeout.Stop(); frame.Continue = false; };
        var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
        poll.Tick += (_, _) => { if (done()) { poll.Stop(); frame.Continue = false; } };
        timeout.Start(); poll.Start();
        try { if (!done()) Dispatcher.PushFrame(frame); }
        finally { timeout.Stop(); poll.Stop(); }
        if (!done()) throw new TimeoutException("Preview queue did not become idle.");
    }
    private static void PumpTask(Task task, Dispatcher dispatcher)
    {
        if (task.IsCompleted) { task.GetAwaiter().GetResult(); return; }
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => frame.Continue = false)));
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
    private static T Get<T>(MainWindow window, string name) => (T)(typeof(MainWindow).GetField(name, Private)!.GetValue(window) ?? throw new InvalidOperationException($"{name} is null."));
    private static object? GetNullable(MainWindow window, string name) => typeof(MainWindow).GetField(name, Private)!.GetValue(window);
    private static void Set<T>(MainWindow window, string name, T value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
}
