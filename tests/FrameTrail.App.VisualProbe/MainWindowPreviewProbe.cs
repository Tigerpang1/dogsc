using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FrameTrail;
using FrameTrail.App;
using FrameTrail.Media;

namespace FrameTrail.App.VisualProbe;

internal static class MainWindowPreviewProbe
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Verify(MainWindow window, string output, Action<bool, string, object?> check)
    {
        Directory.CreateDirectory(output);
        var dispatcher = window.Dispatcher;
        dispatcher.VerifyAccess();
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        try
        {
            Stage("fixture generation");
            check(!window.IsVisible && new System.Windows.Interop.WindowInteropHelper(window).Handle == IntPtr.Zero,
                "real MainWindow is unshown and has no HWND before preview", null);

            var sourcePath = Path.Combine(output, "preview-testsrc2.mp4");
            var projectPath = Path.Combine(output, "preview-project.frametrail");
            Pump(MediaTools.RunAsync(MediaTools.Ffmpeg,
            ["-v", "error", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30:duration=6",
             "-frames:v", "180", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-y", sourcePath]), dispatcher);

            var mediaName = Path.GetFileName(sourcePath);
            var project = MakeProject(mediaName, 0, 6, 1);
            Set(window, "project", project);
            Set(window, "projectFile", projectPath);
            Set(window, "previewResolution", 1440);
            var playhead = Get<Slider>(window, "Playhead");
            playhead.Maximum = project.Timeline.Duration;
            playhead.Value = 0;

            Stage("click seek and passive movement");
            var timeline = (EditorTimelineControl)window.FindName("TimelineView");
            typeof(EditorTimelineControl).GetProperty("Project", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(timeline, project);
            timeline.Snap = false;
            timeline.Refresh();
            timeline.Measure(new Size(1152, 400));
            timeline.Arrange(new Rect(0, 0, 1152, timeline.Height));
            timeline.UpdateLayout();
            timeline.HandlePointerDown(new Point(352, 12), System.Windows.Input.MouseButton.Left,
                System.Windows.Input.ModifierKeys.None, false);
            timeline.HandlePointerUp(System.Windows.Input.MouseButton.Left, new Point(352, 12), false);
            Pump(CallRender(window, "RenderTimelinePreview", 1.2, false), dispatcher);
            var clickImage = CurrentImage(window);
            var clickScene = CurrentScene(window);
            timeline.HandlePointerMove(new Point(800, 12), false);
            Pump(Task.Delay(50), dispatcher);
            dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            var activeAfterPassiveMove = GetNullable(window, "activePreview");
            var pendingAfterPassiveMove = GetNullable(window, "pendingPreview");
            check(clickImage is not null && Math.Abs(playhead.Value - 1.2) < 1e-9 &&
                  Math.Abs(clickScene.OutputTime - 1.2) < 1e-6 && ReferenceEquals(clickImage, CurrentImage(window)) &&
                  Math.Abs(CurrentScene(window).OutputTime - 1.2) < 1e-6 && activeAfterPassiveMove is null && pendingAfterPassiveMove is null,
                "real timeline click renders the frame; passive movement leaves its bitmap and preview queue unchanged",
                new { Playhead = playhead.Value, Scene = SceneIdentity(clickScene),
                    SourceFrame = SourceFrameIdentity(clickScene, project.Media.FrameRate),
                    ActivePreviewAfterMove = activeAfterPassiveMove is not null, PendingPreviewAfterMove = pendingAfterPassiveMove is not null,
                    MediaPath = sourcePath, MediaBytes = new FileInfo(sourcePath).Length, MediaSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath))) });

            var burstTimes = new[] { 1.4, 1.7, 2.15, 2.6, 2.2 };
            Stage("coalesced scrub burst");
            var previewGate = Get<SemaphoreSlim>(window, "previewGate");
            previewGate.Wait();
            bool activeScrubWasKept;
            try
            {
                Invoke(window, "ScrubTimeline", burstTimes[0]);
                var activeRequest = GetNullable(window, "activePreview")
                    ?? throw new InvalidOperationException("Scrub did not start an active preview request.");
                var cancellation = (CancellationTokenSource)(activeRequest.GetType()
                    .GetField("Cancellation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(activeRequest)
                    ?? throw new InvalidOperationException("Active scrub request has no cancellation source."));
                foreach (var time in burstTimes.Skip(1)) Invoke(window, "ScrubTimeline", time);
                activeScrubWasKept = !cancellation.IsCancellationRequested;
            }
            finally { previewGate.Release(); }
            Pump(CallRender(window, "RenderTimelinePreview", burstTimes[^1], true), dispatcher);
            var burstScene = CurrentScene(window);
            check(activeScrubWasKept && Math.Abs(playhead.Value - burstTimes[^1]) < 1e-9 && Math.Abs(burstScene.OutputTime - burstTimes[^1]) < 1e-6,
                "rapid scrub keeps the active decode alive and finishes on the latest requested scene",
                new { Requested = burstTimes, ActiveDecodeCancelled = !activeScrubWasKept, Playhead = playhead.Value, Scene = SceneIdentity(burstScene), Identity = SourceFrameIdentity(burstScene, project.Media.FrameRate) });

            var forward = Stopwatch.StartNew();
            Stage("forward and reverse scrub");
            Pump(CallRender(window, "RenderTimelinePreview", 4.1, false), dispatcher);
            forward.Stop();
            var forwardScene = CurrentScene(window);
            var backward = Stopwatch.StartNew();
            Pump(CallRender(window, "RenderTimelinePreview", .55, false), dispatcher);
            backward.Stop();
            var backwardScene = CurrentScene(window);
            check(Math.Abs(forwardScene.OutputTime - 4.1) < 1e-6 && Math.Abs(backwardScene.OutputTime - .55) < 1e-6 &&
                  Math.Abs(forwardScene.SourceTime - 4.1) < 1e-6 && Math.Abs(backwardScene.SourceTime - .55) < 1e-6,
                "real composed preview supports forward and backward scrubbing with matching source-frame identity",
                new { ForwardMilliseconds = forward.Elapsed.TotalMilliseconds, Forward = SceneIdentity(forwardScene), ForwardFrame = SourceFrameIdentity(forwardScene, 30),
                    BackwardMilliseconds = backward.Elapsed.TotalMilliseconds, Backward = SceneIdentity(backwardScene), BackwardFrame = SourceFrameIdentity(backwardScene, 30) });

            var referenceTime = 1.35;
            Stage("standard and timeline composition comparison");
            Pump(CallRender(window, "RenderPreview", .8), dispatcher);
            Pump(CallRender(window, "RenderPreview", referenceTime), dispatcher);
            var standardSamples = SamplePixels(CurrentImage(window)!);
            Set(window, "completedPreview", (object?)null);
            Pump(CallRender(window, "RenderTimelinePreview", referenceTime, false), dispatcher);
            var referenceImage = CurrentImage(window)!;
            var referenceSamples = SamplePixels(referenceImage);
            check(referenceSamples.SequenceEqual(standardSamples),
                "fresh timeline composition matches a separately rendered standard MainWindow preview at the same scene time",
                new { Timeline = referenceSamples, Standard = standardSamples, Scene = SceneIdentity(CurrentScene(window)) });

            Set(window, "selectedEffect", new TimelineSelection("zoom", project.ZoomClips[0].Id));
            Stage("selected-effect isolation");
            Set(window, "inspectorTab", "zoom");
            Set(window, "completedPreview", (object?)null);
            Pump(CallRender(window, "RenderPreview", referenceTime), dispatcher);
            var inspectorTargetScene = CurrentScene(window);
            Set(window, "completedPreview", (object?)null);
            Pump(CallRender(window, "RenderTimelinePreview", referenceTime, false), dispatcher);
            var selectedTimelineImage = CurrentImage(window)!;
            var selectedTimelineScene = CurrentScene(window);
            check(SamplePixels(selectedTimelineImage).SequenceEqual(referenceSamples) &&
                  Math.Abs(selectedTimelineScene.SourceTime - referenceTime) < 1e-6,
                "selected inspector target does not change the actual timeline frame",
                new { Samples = SamplePixels(selectedTimelineImage), Expected = referenceSamples,
                    InspectorScene = SceneIdentity(inspectorTargetScene), Scene = SceneIdentity(selectedTimelineScene) });

            Set(window, "selectedEffect", (object?)null);
            Set(window, "inspectorTab", "");
            Set(window, "refreshing", true);
            playhead.Value = 0;
            Set(window, "refreshing", false);
            var sameFrameImage = CurrentImage(window);
            Stage("same-frame commit");
            var clickLatency = Stopwatch.StartNew();
            Invoke(window, "SeekTimeline", referenceTime);
            clickLatency.Stop();
            check(Math.Abs(playhead.Value - referenceTime) < 1e-9 && ReferenceEquals(sameFrameImage, CurrentImage(window)),
                "committing the already previewed frame moves the playhead immediately and reuses its BitmapSource",
                new { Playhead = playhead.Value, BitmapReused = ReferenceEquals(sameFrameImage, CurrentImage(window)), ClickMilliseconds = clickLatency.Elapsed.TotalMilliseconds });

            Invoke(window, "SeekTimeline", .72);
            Pump(CallRender(window, "RenderTimelinePreview", .72, false), dispatcher);
            var seekImage = CurrentImage(window);
            var seekScene = CurrentScene(window);
            var seekPlayhead = playhead.Value;
            timeline.HandlePointerMove(new Point(900, 12), false);
            Pump(Task.Delay(50), dispatcher);
            dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            check(Math.Abs(seekPlayhead - .72) < 1e-9 && ReferenceEquals(seekImage, CurrentImage(window)) &&
                  Math.Abs(CurrentScene(window).OutputTime - .72) < 1e-6 && GetNullable(window, "activePreview") is null &&
                  GetNullable(window, "pendingPreview") is null,
                "passive movement after a .72 seek keeps the displayed bitmap and frame", new { Playhead = playhead.Value, Scene = SceneIdentity(CurrentScene(window)) });

            var clickedTime = playhead.Value;
            ClickTimeline(timeline, referenceTime, project.Timeline.Duration);
            var clickedFrame = Math.Floor(referenceTime * project.Media.FrameRate) / project.Media.FrameRate;
            Pump(CallRender(window, "RenderTimelinePreview", clickedFrame, false), dispatcher);
            var clickedImage = CurrentImage(window);
            timeline.HandlePointerMove(new Point(900, 12), false);
            Pump(Task.Delay(50), dispatcher);
            dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            check(Math.Abs(playhead.Value - clickedFrame) < 1e-9 && Math.Abs(CurrentScene(window).OutputTime - clickedFrame) < 1e-6 &&
                  !ReferenceEquals(seekImage, clickedImage) && ReferenceEquals(clickedImage, CurrentImage(window)) &&
                  Math.Abs(CurrentScene(window).OutputTime - clickedFrame) < 1e-6,
                "real click at another frame updates preview and later passive movement keeps that frame", new
                { PreviousPlayhead = clickedTime, Playhead = playhead.Value, Frame = SceneIdentity(CurrentScene(window)), BitmapChangedOnClick = !ReferenceEquals(seekImage, clickedImage) });

            Stage("playback, project replacement, and cancellation races");
            VerifySupersededRequests(window, dispatcher, project, projectPath, check);
            check(Application.Current.Windows.Count == 1 && !window.IsVisible &&
                  new System.Windows.Interop.WindowInteropHelper(window).Handle == IntPtr.Zero,
                "preview probe leaves only its unshown MainWindow with no HWND or desktop startup", null);
        }
        finally
        {
            try
            {
                Stage("resource cleanup");
                DisposePreviewResources(window, dispatcher);
            }
            finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        }
    }

    private static void VerifySupersededRequests(MainWindow window, Dispatcher dispatcher,
        FrameTrailProject project, string projectPath, Action<bool, string, object?> check)
    {
        var gate = Get<SemaphoreSlim>(window, "previewGate");
        gate.Wait();
        try
        {
            var beforePlayback = CurrentImage(window);
            var playbackTask = CallRender(window, "RenderTimelinePreview", 3.15, false);
            Set(window, "playing", true);
            gate.Release();
            Pump(playbackTask, dispatcher);
            Set(window, "playing", false);
            check(ReferenceEquals(beforePlayback, CurrentImage(window)),
                "a frame finishing after playback starts cannot replace the displayed preview",
                new { Retained = ReferenceEquals(beforePlayback, CurrentImage(window)), Scene = SceneIdentity(CurrentScene(window)) });

            gate.Wait();
            var oldProjectTask = CallRender(window, "RenderTimelinePreview", 2.9, false);
            var nextProject = MakeProject(project.MediaPath, 1, 2, 2);
            Set(window, "project", nextProject);
            Set(window, "projectFile", projectPath);
            Get<Slider>(window, "Playhead").Maximum = nextProject.Timeline.Duration;
            var nextProjectTask = CallRender(window, "RenderTimelinePreview", .4, false);
            gate.Release();
            Pump(Task.WhenAll(oldProjectTask, nextProjectTask), dispatcher);
            var newProjectScene = CurrentScene(window);
            check(ReferenceEquals(Get<FrameTrailProject>(window, "displayedPreviewProject"), nextProject) &&
                  Math.Abs(newProjectScene.OutputTime - .4) < 1e-6 && Math.Abs(newProjectScene.SourceTime - 1.8) < 1e-6,
                "a superseded project frame cannot overwrite the latest project's composed frame",
                new { ExpectedProject = nextProject.ProjectId, SceneProject = Get<FrameTrailProject>(window, "displayedPreviewProject").ProjectId,
                    Scene = SceneIdentity(newProjectScene), Identity = SourceFrameIdentity(newProjectScene, project.Media.FrameRate) });

            gate.Wait();
            var beforeCancel = CurrentImage(window);
            var cancelTask = CallRender(window, "RenderTimelinePreview", .7, false);
            var seek = Get<CancellationTokenSource>(window, "seekCancellation");
            seek.Cancel();
            gate.Release();
            Pump(cancelTask, dispatcher);
            check(ReferenceEquals(beforeCancel, CurrentImage(window)),
                "explicitly cancelled stale work cannot overwrite the current bitmap",
                new { Retained = ReferenceEquals(beforeCancel, CurrentImage(window)), CurrentScene = SceneIdentity(CurrentScene(window)) });
        }
        finally
        {
            if (gate.CurrentCount == 0) gate.Release();
            Set(window, "project", project);
        }
    }

    private static FrameTrailProject MakeProject(string mediaPath, double sourceStart, double sourceDuration, double playbackRate) => new()
    {
        MediaPath = mediaPath,
        Media = new() { Width = 1280, Height = 720, Duration = 6, FrameRate = 30 },
        Timeline = new() { Segments = [new() { SourceStart = sourceStart, SourceDuration = sourceDuration, PlaybackRate = playbackRate }] },
        Settings = new() { CanvasWidth = 1280, CanvasHeight = 720, Padding = 0, CornerRadius = 0, Shadow = 0 },
        ZoomClips = [new() { Start = sourceStart, End = sourceStart + Math.Min(sourceDuration, 2), Scale = 2.1, Focus = new(.24, .66) }]
    };

    private static Task CallRender(MainWindow window, string method, double time, bool coalesce = false)
    {
        var info = typeof(MainWindow).GetMethod(method, PrivateInstance)
            ?? throw new MissingMethodException(typeof(MainWindow).FullName, method);
        var arguments = method == "RenderPreview" ? new object?[] { time } : new object?[] { time, coalesce };
        return (Task)(info.Invoke(window, arguments) ?? throw new InvalidOperationException($"{method} returned null."));
    }

    private static void ClickTimeline(EditorTimelineControl timeline, double time, double duration)
    {
        var x = 152 + time / duration * 1000;
        var point = new Point(x, 12);
        timeline.HandlePointerDown(point, System.Windows.Input.MouseButton.Left,
            System.Windows.Input.ModifierKeys.None, false);
        timeline.HandlePointerUp(System.Windows.Input.MouseButton.Left, point, false);
    }

    private static void Invoke(MainWindow window, string method, params object?[] arguments)
    {
        var info = typeof(MainWindow).GetMethod(method, PrivateInstance)
            ?? throw new MissingMethodException(typeof(MainWindow).FullName, method);
        info.Invoke(window, arguments);
    }

    private static void Pump(Task task, Dispatcher dispatcher)
    {
        if (task.IsCompleted) { task.GetAwaiter().GetResult(); return; }
        var frame = new DispatcherFrame();
        var timedOut = false;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timeout.Tick += (_, _) =>
        {
            timeout.Stop();
            timedOut = true;
            frame.Continue = false;
        };
        timeout.Start();
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => frame.Continue = false)),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        try { Dispatcher.PushFrame(frame); }
        finally { timeout.Stop(); }
        if (timedOut) throw new TimeoutException("Preview probe dispatcher pump exceeded 60 seconds.");
        task.GetAwaiter().GetResult();
    }

    private static void Stage(string stage) => Console.WriteLine($"preview-interaction: {stage}");

    private static void DisposePreviewResources(MainWindow window, Dispatcher dispatcher)
    {
        Set(window, "closeReady", true);
        Get<DispatcherTimer>(window, "previewResizeTimer").Stop();
        var seekCancellation = GetNullable(window, "seekCancellation") as CancellationTokenSource;
        seekCancellation?.Cancel();
        List<Exception>? errors = null;
        void Attempt(Action action)
        {
            try { action(); }
            catch (Exception error) { (errors ??= []).Add(error); }
        }

        Attempt(() =>
        {
            var decoder = GetNullable(window, "scrubDecoder");
            if (decoder is IAsyncDisposable asyncDisposable)
                Pump(asyncDisposable.DisposeAsync().AsTask(), dispatcher);
        });
        Attempt(() => seekCancellation?.Dispose());
        Attempt(() => Set(window, "seekCancellation", (object?)null));
        Attempt(() => Set(window, "scrubDecoder", (object?)null));
        Attempt(() => Set(window, "scrubDecoderKey", (object?)null));
        Attempt(() => (GetNullable(window, "previewAssets") as IDisposable)?.Dispose());
        Attempt(() => Set(window, "previewAssets", (object?)null));
        Attempt(() => (GetNullable(window, "previewRenderer") as IDisposable)?.Dispose());
        Attempt(() => Set(window, "previewRenderer", (object?)null));
        if (errors is not null) throw new AggregateException("Failed to clean preview probe resources.", errors);
    }

    private static BitmapSource? CurrentImage(MainWindow window) => ((System.Windows.Controls.Image)window.FindName("PreviewImage")).Source as BitmapSource;
    private static FrameScene CurrentScene(MainWindow window) => Get<FrameScene>(window, "displayedPreviewScene");

    private static object SceneIdentity(FrameScene scene) => new { scene.OutputTime, scene.SourceTime };

    private static int SourceFrameIdentity(FrameScene scene, double frameRate) =>
        (int)Math.Ceiling(scene.SourceTime * frameRate - 1e-7);

    private static uint[] SamplePixels(BitmapSource bitmap)
    {
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var stride = width * 4;
        var bytes = new byte[stride * height];
        bitmap.CopyPixels(bytes, stride, 0);
        var points = new[] { (width / 8, height / 8), (width / 2, height / 2), (width * 7 / 8, height / 3), (width / 3, height * 7 / 8) };
        return points.Select(point => BitConverter.ToUInt32(bytes, point.Item2 * stride + point.Item1 * 4)).ToArray();
    }

    private static T Get<T>(MainWindow window, string name) =>
        (T)(typeof(MainWindow).GetField(name, PrivateInstance)!.GetValue(window)
            ?? throw new InvalidOperationException($"Field {name} has no value."));

    private static object? GetNullable(MainWindow window, string name) =>
        typeof(MainWindow).GetField(name, PrivateInstance)!.GetValue(window);

    private static void Set<T>(MainWindow window, string name, T value) =>
        typeof(MainWindow).GetField(name, PrivateInstance)!.SetValue(window, value);
}
