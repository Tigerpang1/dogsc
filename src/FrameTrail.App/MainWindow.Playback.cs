using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FrameTrail.Media;

namespace FrameTrail.App;

public partial class MainWindow
{
    // Opt-in probe measures WPF assignment, not physical monitor presentation.
    internal Action<double>? PlaybackFramePresented;
    private sealed record PlaybackRequest(FrameTrailProject Project, string File, string Source, double Start,
        int Width, int Height, int FrameRate, bool NativeSource, string? AudioProxy, CancellationToken Lifetime);
    private sealed record PlaybackResources(PlaybackRequest Request, long SourceLength, DateTime SourceModified,
        PreviewPlaybackSession Session, bool HasAudio, string? AudioProxy);
    private CancellationTokenSource? playbackCancellation;
    private CancellationTokenSource playbackResourcesLifetime = new();
    // Workers are serialized. Pausing stops presentation; the in-flight frame may
    // finish in the background so the next run can reuse the decoder and renderer.
    private Task playbackWork = Task.CompletedTask;
    private PlaybackResources? playbackResources;
    // Accessed only by the serialized playback worker. Proxies created while a
    // start is cancelled still belong to this project lifetime and are cleaned up.
    private readonly HashSet<string> playbackOwnedProxies = [];
    private int playbackRenderedFrames;
    private int playbackSessionGeneration;
    private int playbackAudioOpenCount;
    private bool playbackStarting;
    private FrameTrailProject? playbackProxyProject;
    private string? playbackAudioSource;
    private Task? playbackAudioReady;
    private TaskCompletionSource? playbackAudioOpened;

    private async Task StartRealtimePlayback()
    {
        if (project is null || projectFile is null || closingInProgress || closeReady) return;
        StopPlayback();
        var owner = new CancellationTokenSource();
        playbackCancellation = owner;
        playing = true;
        playbackStarting = true;
        playbackRenderedFrames = 0;
        completedPreview = null;
        // CancelAsync marks cancellation immediately; process callbacks run away
        // from the input thread.
        if (seekCancellation is { } seek) _ = seek.CancelAsync();
        PlayButton.Content = new EditorIcon { Kind = "pause", Width = 18, Height = 18 };
        PreviewImage.Visibility = Visibility.Visible;
        Player.Visibility = Visibility.Collapsed;
        var size = PreviewOutputSize(project);
        var request = new PlaybackRequest(project, projectFile, FrameTrailProjectStore.ResolveMediaPath(projectFile, project),
            Playhead.Value >= Playhead.Maximum - .03 ? 0 : Playhead.Value, size.Width, size.Height,
            previewFrameRate, previewResolution == 0, ReferenceEquals(playbackProxyProject, project) ? proxyPath : null,
            playbackResourcesLifetime.Token);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = playbackWork;
        playbackWork = Task.Run(() => RunPlaybackAsync(previous, request, owner, ready));
        try { await ready.Task; }
        catch (OperationCanceledException) { }
    }

    private async Task RunPlaybackAsync(Task previous, PlaybackRequest request, CancellationTokenSource owner,
        TaskCompletionSource ready)
    {
        var token = owner.Token;
        try
        {
            await previous.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            request.Lifetime.ThrowIfCancellationRequested();
            var resources = await GetPlaybackResourcesAsync(request).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Task audioReady = Task.CompletedTask;
            if (resources.HasAudio)
                audioReady = await Dispatcher.InvokeAsync(() =>
                {
                    token.ThrowIfCancellationRequested();
                    if (!IsCurrentPlayback(owner, request)) throw new OperationCanceledException(token);
                    proxyPath = resources.AudioProxy;
                    playbackProxyProject = request.Project;
                    return EnsurePlaybackAudioLoadedAsync(resources.AudioProxy!);
                });
            var pixels = new byte[checked(request.Width * request.Height * 4)];
            var info = await resources.Session.ReadFrameAsync(request.Start, pixels, request.Lifetime).ConfigureAwait(false);
            var firstFrame = CreatePlaybackBitmap(pixels, request);
            await audioReady.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var clock = new Stopwatch();
            var presented = await Dispatcher.InvokeAsync(() =>
            {
                if (!IsCurrentPlayback(owner, request)) return false;
                ApplyPlaybackFrame(firstFrame, info.OutputTime, request.Project);
                if (resources.HasAudio)
                {
                    Player.Position = TimeSpan.FromSeconds(request.Start);
                    Player.Play();
                }
                clock.Start();
                playbackStarting = false;
                return true;
            });
            if (!presented) { ready.TrySetCanceled(); return; }
            token.ThrowIfCancellationRequested();
            ready.TrySetResult();
            var lastFrame = (long)Math.Floor(info.OutputTime * request.FrameRate + 1e-7);
            var audioCorrection = 0d;
            var nextAudioCheck = 1d;
            while (!token.IsCancellationRequested)
            {
                var time = request.Start + clock.Elapsed.TotalSeconds + audioCorrection;
                if (resources.HasAudio && clock.Elapsed.TotalSeconds >= nextAudioCheck)
                {
                    var audioTime = await Dispatcher.InvokeAsync(() => Player.Position.TotalSeconds);
                    if (Math.Abs(audioTime - time) > .08) audioCorrection += audioTime - time;
                    nextAudioCheck = clock.Elapsed.TotalSeconds + 1;
                    time = request.Start + clock.Elapsed.TotalSeconds + audioCorrection;
                }
                if (time >= request.Project.Timeline.Duration - .015) break;
                // Prepare one frame ahead while the displayed frame is held. Waiting
                // until its deadline to decode adds codec/render time to every frame.
                var frameIndex = Math.Max(lastFrame + 1, (long)Math.Floor(time * request.FrameRate));
                if (frameIndex / (double)request.FrameRate >= request.Project.Timeline.Duration)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(.001, request.Project.Timeline.Duration - time)), token).ConfigureAwait(false);
                    continue;
                }
                lastFrame = frameIndex;
                time = frameIndex / (double)request.FrameRate;
                info = await resources.Session.ReadFrameAsync(time, pixels, request.Lifetime).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var frame = CreatePlaybackBitmap(pixels, request);
                var remaining = time - (request.Start + clock.Elapsed.TotalSeconds + audioCorrection);
                if (remaining > 0) await Task.Delay(TimeSpan.FromSeconds(remaining), token).ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (IsCurrentPlayback(owner, request)) ApplyPlaybackFrame(frame, info.OutputTime, request.Project);
                });
            }
            if (!token.IsCancellationRequested)
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!IsCurrentPlayback(owner, request)) return;
                    StopPlayback();
                    Playhead.Value = Playhead.Maximum;
                });
        }
        catch (Exception) when (token.IsCancellationRequested || request.Lifetime.IsCancellationRequested)
        {
            ready.TrySetCanceled();
        }
        catch (Exception error)
        {
            App.Log(error);
            await ReleasePlaybackSessionAsync().ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                if (!IsCurrentPlayback(owner, request)) return;
                StopPlayback();
                AutomationProperties.SetItemStatus(PreviewImage, "error");
                Status.Text = "预览播放未完成：" + error.Message;
            });
            ready.TrySetException(error);
        }
        finally
        {
            ready.TrySetCanceled();
            await Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(playbackCancellation, owner)) StopPlayback();
            });
            owner.Dispose();
        }
    }

    private async Task<PlaybackResources> GetPlaybackResourcesAsync(PlaybackRequest request)
    {
        var source = new FileInfo(request.Source);
        var sourceChanged = playbackResources is { } previous && previous.Request.Source == request.Source &&
            (previous.SourceLength != source.Length || previous.SourceModified != source.LastWriteTimeUtc);
        if (playbackResources is { } existing && ReferenceEquals(existing.Request.Project, request.Project) &&
            existing.Request.File == request.File && existing.Request.Source == request.Source &&
            existing.Request.Width == request.Width && existing.Request.Height == request.Height &&
            existing.Request.FrameRate == request.FrameRate && existing.Request.NativeSource == request.NativeSource &&
            existing.SourceLength == source.Length && existing.SourceModified == source.LastWriteTimeUtc)
            return existing;
        await ReleasePlaybackSessionAsync().ConfigureAwait(false);
        var session = await PreviewPlaybackSession.CreateAsync(request.Project, request.Source, request.File,
            request.Width, request.Height, request.FrameRate, request.Lifetime,
            preserveSourceResolution: request.NativeSource, preferNativeDecoder: true).ConfigureAwait(false);
        string? newProxy = null;
        try
        {
            var snapshot = request.Project;
            var hasAudio = session.SourceHasAudio && MediaTools.HasAudibleMainAudio(snapshot.Timeline, snapshot.Settings.MainAudioVolume, snapshot.Settings.MainAudioMuted)
                || MediaTools.HasAudibleAudioTracks(snapshot.Timeline, snapshot.AudioTracks, snapshot.Settings.MainAudioVolume, snapshot.Settings.MainAudioMuted);
            if (snapshot.Camera is { Muted: false, Volume: > 0 } camera)
            {
                var cameraInfo = await MediaTools.ProbeAsync(FrameTrailProjectStore.ResolveAssetPath(request.File, camera.MediaPath), request.Lifetime).ConfigureAwait(false);
                if (cameraInfo.HasAudio)
                {
                    IReadOnlyList<CameraSyncSegment> mappings = camera.SyncSegments is { Count: > 0 } sync ? sync : [new CameraSyncSegment(camera.SourceStart, camera.TimelineStart, camera.Duration)];
                    var track = new AudioTrack { MediaPath = camera.MediaPath, SourceStart = camera.SourceStart, Duration = camera.Duration,
                        OffsetSeconds = camera.TimelineStart, Volume = camera.Volume, SourceRate = mappings[0].SourceRate, SyncSegments = mappings.ToList(), Source = AudioTrackSource.Camera };
                    hasAudio |= MediaTools.HasAudibleAudioTrack(snapshot.Timeline, track);
                }
            }
            var audioProxy = sourceChanged ? null : request.AudioProxy;
            if (hasAudio && (audioProxy is null || !File.Exists(audioProxy)))
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameTrail", "cache");
                Directory.CreateDirectory(directory);
                newProxy = Path.Combine(directory, Guid.NewGuid() + ".m4a");
                await new ExportEngine().ExportAsync(snapshot, request.Source, newProxy,
                    new(960, 540, 30, true, true, request.File), cancellation: request.Lifetime).ConfigureAwait(false);
                audioProxy = newProxy;
            }
            request.Lifetime.ThrowIfCancellationRequested();
            playbackResources = new(request, source.Length, source.LastWriteTimeUtc, session, hasAudio, audioProxy);
            if (newProxy is not null) playbackOwnedProxies.Add(newProxy);
            Interlocked.Increment(ref playbackSessionGeneration);
            return playbackResources;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            if (newProxy is not null) try { File.Delete(newProxy); } catch (IOException) { }
            throw;
        }
    }

    private bool IsCurrentPlayback(CancellationTokenSource owner, PlaybackRequest request) =>
        ReferenceEquals(playbackCancellation, owner) && !owner.IsCancellationRequested && playing &&
        ReferenceEquals(project, request.Project) && !closingInProgress && !closeReady;

    private static BitmapSource CreatePlaybackBitmap(byte[] pixels, PlaybackRequest request)
    {
        var frame = BitmapSource.Create(request.Width, request.Height, 96, 96, PixelFormats.Bgra32, null, pixels, request.Width * 4);
        frame.Freeze();
        return frame;
    }

    private void ApplyPlaybackFrame(BitmapSource frame, double time, FrameTrailProject snapshot)
    {
        playbackRenderedFrames++;
        PreviewImage.Source = frame;
        if (!ReferenceEquals(evaluatedProject, snapshot))
        {
            previewEvaluator = new PreparedFrameSceneEvaluator(snapshot);
            evaluatedProject = snapshot;
        }
        displayedPreviewScene = previewEvaluator!.Evaluate(time);
        displayedPreviewProject = snapshot;
        AutomationProperties.SetItemStatus(PreviewImage, "ready");
        var wasRefreshing = refreshing;
        refreshing = true;
        try { Playhead.Value = time; }
        finally { refreshing = wasRefreshing; }
        PlaybackFramePresented?.Invoke(time);
        UpdateTime();
    }

    private Task EnsurePlaybackAudioLoadedAsync(string path)
    {
        if (playbackAudioSource == path && playbackAudioReady is { IsFaulted: false, IsCanceled: false }) return playbackAudioReady;
        ClosePlaybackAudio();
        playbackAudioSource = path;
        return playbackAudioReady = LoadPlaybackAudioAsync(path);
    }

    private async Task LoadPlaybackAudioAsync(string path)
    {
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        playbackAudioOpened = opened;
        RoutedEventHandler onOpened = (_, _) => opened.TrySetResult();
        EventHandler<ExceptionRoutedEventArgs> onFailed = (_, error) => opened.TrySetException(error.ErrorException);
        Player.MediaOpened += onOpened;
        Player.MediaFailed += onFailed;
        try
        {
            playbackAudioOpenCount++;
            Player.Source = new Uri(path);
            Player.Pause(); // Preroll silently; only the current playback owner starts audio.
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            Player.MediaOpened -= onOpened;
            Player.MediaFailed -= onFailed;
            if (ReferenceEquals(playbackAudioOpened, opened)) playbackAudioOpened = null;
        }
    }

    private void ClosePlaybackAudio()
    {
        playbackAudioOpened?.TrySetCanceled();
        playbackAudioOpened = null;
        Player.Close();
        Player.Source = null;
        playbackAudioReady = null;
        playbackAudioSource = null;
    }

    private async Task ReleasePlaybackSessionAsync()
    {
        var old = playbackResources;
        playbackResources = null;
        if (old is not null) await old.Session.DisposeAsync().ConfigureAwait(false);
    }

    private Task DisposePlaybackResourcesAsync()
    {
        StopPlayback();
        var lifetime = playbackResourcesLifetime;
        playbackResourcesLifetime = new();
        var canceled = lifetime.CancelAsync();
        var previous = playbackWork;
        playbackWork = Task.Run(async () =>
        {
            await canceled.ConfigureAwait(false);
            await previous.ConfigureAwait(false);
            await ReleasePlaybackSessionAsync().ConfigureAwait(false);
            foreach (var path in playbackOwnedProxies)
            {
                try { File.Delete(path); }
                catch (IOException error) { App.Log(error); }
                catch (UnauthorizedAccessException error) { App.Log(error); }
            }
            playbackOwnedProxies.Clear();
            lifetime.Dispose();
        });
        return playbackWork;
    }
}
