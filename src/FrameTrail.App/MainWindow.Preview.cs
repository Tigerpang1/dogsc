using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FrameTrail.Media;

namespace FrameTrail.App;

public partial class MainWindow
{
    private sealed record PreviewTarget(FrameTrailProject Project, string ProjectFile, double Time,
        int Width, int Height, bool NativeSource, TimelineSelection? InspectorTarget);

    private sealed class PreviewRequest(PreviewTarget target, bool hover, CancellationToken cancellation)
    {
        internal readonly PreviewTarget Target = target;
        internal readonly bool Hover = hover;
        internal readonly CancellationTokenSource Cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private PreviewRequest? pendingPreview, activePreview;
    private bool previewPumpRunning;
    private (PreviewTarget Target, BitmapSource Image)? completedPreview;
    private ScrubFrameDecoder? scrubDecoder;
    private (string Path, long Length, DateTime Modified, int Width, int Height, double Rate, double Duration)? scrubDecoderKey;
    private readonly System.Windows.Threading.DispatcherTimer previewResizeTimer = new()
        { Interval = TimeSpan.FromMilliseconds(150) };

    private (int Width, int Height) PreviewOutputSize(FrameTrailProject snapshot)
    {
        var dpi = VisualTreeHelper.GetDpi(PreviewSurface);
        return PreviewSizing.Output(previewResolution, snapshot.Settings.CanvasWidth, snapshot.Settings.CanvasHeight,
            PreviewSurface.ActualWidth * dpi.DpiScaleX, PreviewSurface.ActualHeight * dpi.DpiScaleY);
    }

    private void SchedulePreviewResize()
    {
        if (project is null || previewResolution != 0 || playing) return;
        previewResizeTimer.Stop();
        previewResizeTimer.Start();
    }

    private Task RenderPreview(double time) => RequestPreview(time, timeline: false, hover: false);
    private Task RenderTimelinePreview(double time, bool hover = false) => RequestPreview(time, timeline: true, hover);

    private Task RequestPreview(double time, bool timeline, bool hover)
    {
        Dispatcher.VerifyAccess();
        if (closingInProgress || desktopExiting || closeReady || project is null || projectFile is null) return Task.CompletedTask;
        if (!timeline) ClearTimelineHover();
        if (seekCancellation is null || seekCancellation.IsCancellationRequested)
        {
            seekCancellation?.Dispose();
            seekCancellation = new();
        }
        var output = PreviewOutputSize(project);
        var target = new PreviewTarget(project, projectFile, Math.Clamp(time, 0, project.Timeline.Duration),
            output.Width, output.Height, previewResolution == 0,
            !timeline && !playing && inspectorTab == "zoom" ? selectedEffect : null);

        // Pointer movement replaces only the waiting target. Let the active frame
        // finish so continuous movement cannot starve the image indefinitely.
        // A committed seek/edit interrupts work for a different frame immediately.
        if (!hover && activePreview?.Target != target) activePreview?.Cancellation.Cancel();
        if (pendingPreview is { } replaced)
        {
            pendingPreview = null;
            replaced.Cancellation.Cancel();
            replaced.Cancellation.Dispose();
            replaced.Completion.TrySetResult();
        }
        if (completedPreview is { } completed && completed.Target == target &&
            ReferenceEquals(PreviewImage.Source, completed.Image))
        {
            activePreview?.Cancellation.Cancel();
            AutomationProperties.SetItemStatus(PreviewImage, "ready");
            return Task.CompletedTask;
        }
        if (activePreview is { } active && active.Target == target && !active.Cancellation.IsCancellationRequested)
            return active.Completion.Task;

        var request = new PreviewRequest(target, hover, seekCancellation.Token);
        pendingPreview = request;
        AutomationProperties.SetItemStatus(PreviewImage, "rendering");
        if (!previewPumpRunning)
        {
            previewPumpRunning = true;
            _ = PumpPreviewAsync();
        }
        return request.Completion.Task;
    }

    private async Task PumpPreviewAsync()
    {
        try
        {
            while (pendingPreview is { } request)
            {
                pendingPreview = null;
                activePreview = request;
                try
                {
                    await RenderPreviewFrameAsync(request.Target, request.Cancellation.Token);
                    request.Completion.TrySetResult();
                }
                catch (Exception) when (request.Cancellation.IsCancellationRequested)
                {
                    request.Completion.TrySetResult();
                }
                catch (Exception error)
                {
                    if (pendingPreview is null) AutomationProperties.SetItemStatus(PreviewImage, "error");
                    request.Completion.TrySetException(error);
                }
                finally
                {
                    activePreview = null;
                    request.Cancellation.Dispose();
                }
            }
        }
        finally { previewPumpRunning = false; }
    }

    private async Task RenderPreviewFrameAsync(PreviewTarget target, CancellationToken token)
    {
        await previewGate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            var snapshot = target.Project;
            if (!ReferenceEquals(evaluatedProject, snapshot))
            {
                previewEvaluator = new PreparedFrameSceneEvaluator(snapshot);
                evaluatedProject = snapshot;
            }
            var scene = previewEvaluator!.Evaluate(target.Time);
            if (target.InspectorTarget is { Kind: "zoom" } zoom && snapshot.ZoomClips.Any(x => x.Id == zoom.Id))
                scene = previewEvaluator.EvaluateTargetForZoom(zoom.Id, target.Time);
            if (target.InspectorTarget is { Kind: "motion" } motion && snapshot.ScreenMotions.Any(x => x.Id == motion.Id))
                scene = scene with { ScreenMotion = previewEvaluator.EvaluateTargetForMotion(motion.Id) };

            var inputWidth = target.NativeSource ? (int)snapshot.Media.Width : Math.Min(target.Width, (int)snapshot.Media.Width);
            var inputHeight = Math.Max(2, (int)(snapshot.Media.Height * inputWidth / snapshot.Media.Width));
            var width = target.Width;
            var height = target.Height;
            var path = FrameTrailProjectStore.ResolveMediaPath(target.ProjectFile, snapshot);
            var source = new FileInfo(path);
            var rate = double.IsFinite(snapshot.Media.FrameRate) && snapshot.Media.FrameRate > 0 ? snapshot.Media.FrameRate : 30;
            var key = (path, source.Length, source.LastWriteTimeUtc, inputWidth, inputHeight, rate, snapshot.Media.Duration);
            if (scrubDecoder is null || scrubDecoderKey != key)
            {
                if (scrubDecoder is not null) await scrubDecoder.DisposeAsync();
                scrubDecoder = new ScrubFrameDecoder(path, inputWidth, inputHeight, rate, snapshot.Media.Duration);
                scrubDecoderKey = key;
            }
            var buffer = await scrubDecoder.ReadFrameAsync(scene.SourceTime, token);
            var size = (inputWidth, inputHeight, width, height);
            if (previewAssets is null || previewAssetsProject != target.ProjectFile || previewSize != size)
            {
                previewAssets?.Dispose();
                previewAssets = new(target.ProjectFile, width, height);
                previewAssetsProject = target.ProjectFile;
            }
            var layers = await previewAssets.LoadAsync(scene, token);
            var image = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                if (previewRenderer is null || previewSize != size)
                {
                    previewRenderer?.Dispose();
                    previewRenderer = new(inputWidth, inputHeight, width, height);
                    previewSize = size;
                }
                var output = new byte[width * height * 4];
                previewRenderer.Render(buffer, scene, output, layers);
                var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, output, width * 4);
                bmp.Freeze();
                return bmp;
            }, token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(project, snapshot) || playing || closingInProgress || closeReady) return;
            PreviewImage.Source = image;
            displayedPreviewScene = scene;
            displayedPreviewProject = snapshot;
            completedPreview = (target, image);
            PreviewImage.Visibility = Visibility.Visible;
            RefreshPreviewSelection();
            AutomationProperties.SetItemStatus(PreviewImage, "ready");
        }
        finally { previewGate.Release(); }
    }

    private void ClearTimelineHover()
    {
        timelineHoverPosition = null;
        TimelineView.ClearHoverPreview(notify: false);
    }

    private void SeekTimeline(double time)
    {
        ClearTimelineHover();
        StopPlayback();
        if (Playhead.Value != time) Playhead.Value = time;
        else { UpdateTime(); _ = Guard(() => RenderTimelinePreview(time)); }
    }
}
