using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using FrameTrail.Capture;
using FrameTrail.Media;
using PointerEvent = FrameTrail.PointerEvent;

namespace FrameTrail.App;

public partial class MainWindow : Window
{
    private readonly FrameCaptureService capture = new();
    private FrameTrailProject? project;
    private string? projectFile;
    private UndoRedoHistory<FrameTrailProject>? history;
    private CancellationTokenSource? seekCancellation, operationCancellation;
    private readonly SemaphoreSlim previewGate = new(1,1);
    private GpuFrameRenderer? previewRenderer;
    private (int SW,int SH,int OW,int OH) previewSize;
    private FrameTrailProject? evaluatedProject;
    private PreparedFrameSceneEvaluator? previewEvaluator;
    private TimelineAssetData? timelineAssets;
    private CancellationTokenSource? assetsCancellation;
    private bool refreshing, playing, closeReady;
    private string? proxyPath, recordingFolder;
    private string? diagnosticRecordingRoot;
    private PixelRect? region;
    private RecordingBar? recordingBar;
    private readonly List<(long Tick, string Label)> recordingMarkers = [];
    private readonly DispatcherTimer playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private long recordingStart;
    private record TargetItem(string Name, CaptureTarget Target);
    private record ClipItem(Guid Id, string Title, string Detail, BitmapSource? Thumbnail);
    private record MarkerItem(Guid Id, double SourceTime, string Label) { public override string ToString() => $"{SourceTime:0.00}s  {Label}"; }

    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) => { seekCancellation?.Cancel(); assetsCancellation?.Cancel(); operationCancellation?.Cancel(); recordingBar?.Close(); };
        capture.ErrorOccurred += (_, error) => Dispatcher.BeginInvoke(() => { Status.Text = "录制提示：" + error.Message; App.Log(error.Exception ?? new Exception(error.Message)); });
        capture.StateChanged += (_, state) => Dispatcher.BeginInvoke(() =>
        {
            if (state.State == CaptureState.Recording) recordingStart = state.StopwatchTicks;
            if (state.State == CaptureState.Faulted) { RecordButton.Content = "●  重新录制"; RecordButton.IsEnabled = true; recordingBar?.Close(); recordingBar = null; }
        });
        playbackTimer.Tick += (_, _) => { if (!playing) return; refreshing = true; Playhead.Value = Math.Min(Player.Position.TotalSeconds, Playhead.Maximum); refreshing = false; UpdateTime(); };
    }
    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        await Guard(RefreshDevices);
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WindowMessage);
        if (!NativeWindows.RegisterHotKey(handle, 17, 0x0001 | 0x0002 | 0x4000, 0x4D)) Status.Text = "Ctrl+Alt+M 已被其他应用占用；仍可使用录制条标记按钮。";
        var args = Environment.GetCommandLineArgs();
        var open = Array.IndexOf(args, "--open");
        if (open >= 0 && args.Length > open + 1) await Guard(() => OpenProject(args[open + 1]));
        var workflow = Array.IndexOf(args, "--workflow-smoke");
        if (workflow >= 0 && args.Length > workflow + 1)
        {
            diagnosticRecordingRoot = Path.GetFullPath(args[workflow + 1]); Directory.CreateDirectory(diagnosticRecordingRoot);
            try
            {
                SystemSound.IsChecked=true; await ToggleRecording();
                await Task.Delay(1200); AddMarker(); await Task.Delay(2300); await StopRecording();
                var originalDuration=project!.Timeline.Duration;
                Edit(p => { var split=p.Timeline.Split(p.Timeline.Segments[0].Id,originalDuration/2); p.Timeline.SetPlaybackRate(split.Right.Id,2); p.Timeline.Reorder(split.Right.Id,0); });
                await OpenProject(projectFile!);
                await RunExport(Path.Combine(diagnosticRecordingRoot,"desktop-edited.mp4"),new(1280,720,30,true),false);
                await RenderPreview(project!.Timeline.Duration/3);
                await File.WriteAllTextAsync(Path.Combine(diagnosticRecordingRoot,"workflow-result.json"),JsonSerializer.Serialize(new { Passed=true, Project=projectFile, OriginalDuration=originalDuration, EditedDuration=project.Timeline.Duration, PointerEvents=project.PointerEvents.Count, Markers=project.Markers.Count, Export=Path.Combine(diagnosticRecordingRoot,"desktop-edited.mp4") },new JsonSerializerOptions { WriteIndented=true }));
                SaveSnapshot(Path.Combine(diagnosticRecordingRoot,"editor-project.png"));
            }
            catch(Exception ex) { App.Log(ex); await File.WriteAllTextAsync(Path.Combine(diagnosticRecordingRoot,"workflow-result.json"),JsonSerializer.Serialize(new { Passed=false,Error=ex.ToString() })); }
            await capture.DisposeAsync(); closeReady=true; Close(); return;
        }
        var smoke = Array.IndexOf(args, "--smoke");
        if (smoke >= 0 && args.Length > smoke + 1)
        {
            await Task.Delay(1600); // Only the diagnostic snapshot waits for the app to lay out.
            SaveSnapshot(args[smoke + 1]);
            closeReady = true; Close();
        }
    }
    private void SaveSnapshot(string path)
    {
        var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(this);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var stream=File.Create(path); png.Save(stream);
    }
    private IntPtr WindowMessage(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled)
    { if (msg == 0x0312 && wp.ToInt32() == 17) { AddMarker(); handled = true; } return IntPtr.Zero; }
    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { Status.Text = "处理已取消，原始素材和项目已保留。"; }
        catch (Exception ex) { App.Log(ex); Status.Text = ex.Message; MessageBox.Show(this, ex.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private async Task RefreshDevices()
    {
        if (!IsLoaded) return;
        var list = new List<TargetItem>();
        if (CaptureKind.SelectedIndex == 1)
            list.AddRange((await capture.EnumerateWindowsAsync()).Select(x => new TargetItem(x.Title, new(CaptureTargetKind.Window, x.Id, x.Bounds, x.Handle))));
        else list.AddRange((await capture.EnumerateDisplaysAsync()).Select(x => new TargetItem($"{x.Name} · {x.Bounds.Width}×{x.Bounds.Height}", new(CaptureTargetKind.Display, x.Id, x.Bounds))));
        Targets.ItemsSource = list; Targets.SelectedIndex = 0;
        MicDevices.ItemsSource = (await capture.EnumerateAudioDevicesAsync()).Where(x => x.Kind == AudioDeviceKind.Microphone).ToArray(); MicDevices.SelectedIndex = 0;
        RegionButton.Visibility = CaptureKind.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void CaptureKindChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) await Guard(RefreshDevices); }
    private async void RefreshDevicesClick(object sender, RoutedEventArgs e) => await Guard(RefreshDevices);
    private void SelectRegionClick(object sender, RoutedEventArgs e)
    {
        if (Targets.SelectedItem is not TargetItem target || target.Target.Bounds is null) return;
        var selector = new RegionSelector(target.Target.Bounds); if (selector.ShowDialog() == true) { region = selector.Region; RegionLabel.Text = $"{region!.X},{region.Y} · {region.Width}×{region.Height} px"; }
    }
    private async void RecordClick(object sender, RoutedEventArgs e) => await Guard(ToggleRecording);
    private async Task ToggleRecording()
    {
        if (capture.State == CaptureState.Recording) { await StopRecording(); return; }
        if (Targets.SelectedItem is not TargetItem item) throw new InvalidOperationException("请选择录制来源。");
        var target = item.Target;
        if (CaptureKind.SelectedIndex == 2) target = new(CaptureTargetKind.Region, item.Target.Id, region ?? throw new InvalidOperationException("请先框选录制区域。"));
        recordingFolder = Path.Combine(diagnosticRecordingRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "FrameTrail"), DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(recordingFolder, "media"));
        var output = Path.Combine(recordingFolder, "media", "screen.mp4");
        var request = new RecordingRequest(output, target, new VideoOptions(FrameRate: CaptureFps.SelectedIndex == 1 ? 60 : 30), new AudioOptions(SystemSound.IsChecked == true, RecordMicrophone: Microphone.IsChecked == true, MicrophoneDeviceId: (MicDevices.SelectedItem as AudioDeviceInfo)?.Id), new InputTrackingOptions(OutputPath: Path.Combine(recordingFolder, "pointer.jsonl")));
        await File.WriteAllTextAsync(Path.Combine(recordingFolder, "recording-recovery.json"), JsonSerializer.Serialize(new { request.OutputPath, TargetKind = request.Target.Kind.ToString(), request.Target.Id, request.Target.Bounds, WindowHandle = request.Target.WindowHandle.ToInt64(), request.Video, request.Audio }, new JsonSerializerOptions { WriteIndented = true }));
        recordingMarkers.Clear(); RecordButton.IsEnabled = false; Status.Text = "正在启动原生采集…";
        try
        {
            await capture.StartAsync(request); recordingBar = new RecordingBar(AddMarker, async () => await Guard(StopRecording)); recordingBar.Show();
            RecordButton.Content = "■  结束录制"; Status.Text = "正在录制，素材与鼠标事件写入本机。";
        }
        finally { RecordButton.IsEnabled = true; }
    }
    private void AddMarker()
    {
        if (capture.State != CaptureState.Recording) return;
        recordingMarkers.Add((Stopwatch.GetTimestamp(), $"标记 {recordingMarkers.Count + 1}"));
        if (recordingFolder is not null) File.AppendAllText(Path.Combine(recordingFolder, "markers.jsonl"), JsonSerializer.Serialize(new { Tick = recordingMarkers[^1].Tick, Label = recordingMarkers[^1].Label }) + Environment.NewLine);
        recordingBar?.SetMarkerCount(recordingMarkers.Count);
    }
    private async Task StopRecording()
    {
        RecordButton.IsEnabled = false;
        try
        {
            var result = await capture.StopAsync(); recordingBar?.Close(); recordingBar = null; RecordButton.Content = "●  开始录制";
            await File.WriteAllTextAsync(Path.Combine(recordingFolder!, "capture-result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            var info = await MediaTools.ProbeAsync(result.OutputPath);
            var created = MakeProject("media/screen.mp4", info);
            if (result.InputEventsPath is not null && File.Exists(result.InputEventsPath))
            {
                foreach (var line in File.ReadLines(result.InputEventsPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var ev = JsonSerializer.Deserialize<InputEvent>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); if (ev == null) continue;
                    var time = (ev.StopwatchTicks - result.StartStopwatchTicks) / (double)result.StopwatchFrequency;
                    if (time < 0 || time > info.Duration || ev.Kind is not (InputEventKind.Move or InputEventKind.MouseDown)) continue;
                    var kind = ev.Kind == InputEventKind.MouseDown ? ev.MouseButton == FrameTrail.Capture.MouseButton.Right ? PointerEventKind.RightClick : PointerEventKind.LeftClick : PointerEventKind.Move;
                    created.PointerEvents.Add(new(time, new(ev.NormalizedX, ev.NormalizedY), kind));
                }
            }
            created.Markers = recordingMarkers.Select(x => new RecordingMarker(Guid.NewGuid(), Math.Clamp((x.Tick - result.StartStopwatchTicks)/(double)result.StopwatchFrequency, 0, info.Duration), x.Label)).ToList();
            created.ZoomClips = AutoZoomPlanner.Plan(created.PointerEvents);
            var path = Path.Combine(recordingFolder!, "project.frametrail"); FrameTrailProjectStore.Save(path, created);
            var journal = Path.Combine(recordingFolder!, "recording-recovery.json"); if (File.Exists(journal)) File.Delete(journal);
            await OpenProject(path); Status.Text = $"录制完成 · {info.Duration:0.0}s · {created.PointerEvents.Count} 条鼠标事件。时钟偏移可在右侧校准。";
        }
        finally { RecordButton.IsEnabled = true; }
    }
    private static FrameTrailProject MakeProject(string media, MediaInfo info) => new()
    {
        MediaPath = media, Media = new() { Width = info.Width, Height = info.Height, Duration = info.Duration },
        Timeline = new() { Segments = [new() { SourceDuration = info.Duration }] }, Settings = new() { Background = "#253B45", CanvasWidth = info.Width, CanvasHeight = info.Height }
    };
    private async void OpenProjectClick(object sender, RoutedEventArgs e) => await Guard(async () => { var dialog = new OpenFileDialog { Filter = "FrameTrail 项目|*.frametrail|项目备份|*.frametrail.bak" }; if (dialog.ShowDialog(this) == true) await OpenProject(dialog.FileName); });
    private async Task OpenProject(string path)
    {
        StopPlayback(); seekCancellation?.Cancel();
        FrameTrailProject opened;
        try { opened = FrameTrailProjectStore.Load(path); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException && File.Exists(path + ".bak"))
        {
            if (MessageBox.Show(this, "项目文件未能读取。是否保留损坏文件并恢复最近备份？\n" + ex.Message, "恢复项目", MessageBoxButton.YesNo) != MessageBoxResult.Yes) throw;
            File.Copy(path, path + ".damaged-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); opened = FrameTrailProjectStore.RestoreBackup(path);
        }
        var media = FrameTrailProjectStore.ResolveMediaPath(path, opened);
        if (!File.Exists(media))
        {
            var dialog = new OpenFileDialog { Title = "素材缺失：请选择原视频，副本将保存到项目目录", Filter = "视频|*.mp4;*.mov;*.mkv;*.avi" };
            if (dialog.ShowDialog(this) != true) throw new FileNotFoundException("项目素材缺失，未打开项目。", media);
            var newRelative = "media/relocated-" + Guid.NewGuid().ToString("N") + Path.GetExtension(dialog.FileName); media = Path.Combine(Path.GetDirectoryName(path)!, newRelative); Directory.CreateDirectory(Path.GetDirectoryName(media)!); File.Copy(dialog.FileName, media); opened.MediaPath = newRelative;
            var info = await MediaTools.ProbeAsync(media); if (info.Duration + .05 < opened.Timeline.Segments.Max(x => x.SourceEnd)) throw new InvalidDataException("重新定位的视频过短，无法匹配时间线。");
            FrameTrailProjectStore.Save(path, opened);
        }
        project = opened; projectFile = path; history = new(project, Clone); timelineAssets=null; InvalidateProxy(); RefreshProject(); Playhead.Value = 0; _=LoadTimelineAssets(media,opened); await RenderPreview(0);
    }
    private async void ImportClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var pick = new OpenFileDialog { Filter = "视频文件|*.mp4;*.mov;*.mkv;*.avi;*.webm" }; if (pick.ShowDialog(this) != true) return;
        var info = await MediaTools.ProbeAsync(pick.FileName); if (info.Width == 0 || info.Duration <= 0) throw new InvalidDataException("文件没有可用视频。");
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "FrameTrail", "Import-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(Path.Combine(dir,"media"));
        var relative = "media/source" + Path.GetExtension(pick.FileName); File.Copy(pick.FileName, Path.Combine(dir, relative));
        var imported = MakeProject(relative, info); var file = Path.Combine(dir, "project.frametrail"); FrameTrailProjectStore.Save(file, imported); await OpenProject(file);
        Status.Text = "已复制素材并建立项目：" + dir;
    });
    private static FrameTrailProject Clone(FrameTrailProject p) => JsonSerializer.Deserialize<FrameTrailProject>(JsonSerializer.Serialize(p))!;
    private void SaveProject() { if (project != null && projectFile != null) { FrameTrailProjectStore.Save(projectFile, project); Status.Text = "已保存 · " + projectFile; } }
    private async void SaveClick(object sender, RoutedEventArgs e) => await Guard(() => { SaveProject(); return Task.CompletedTask; });
    private void RefreshProject()
    {
        if (project is null) return; refreshing = true;
        var selected = (Clips.SelectedItem as ClipItem)?.Id;
        Clips.ItemsSource = project.Timeline.Segments.Select((s,i) => new ClipItem(s.Id, $"片段 {i+1} · {s.OutputDuration:0.00}s", $"{s.SourceStart:0.00}–{s.SourceEnd:0.00}s    ×{s.PlaybackRate:0.##}",timelineAssets?.Thumbnails.OrderBy(x=>Math.Abs(x.SourceTime-s.SourceStart)).FirstOrDefault()?.Image)).ToArray();
        Clips.SelectedItem = Clips.Items.Cast<ClipItem>().FirstOrDefault(x => x.Id == selected) ?? Clips.Items.Cast<ClipItem>().FirstOrDefault();
        Playhead.Maximum = project.Timeline.Duration; Playhead.Value = Math.Min(Playhead.Value, Playhead.Maximum);
        Markers.ItemsSource = project.Markers.Select(x => new MarkerItem(x.Id, x.SourceTime, x.Label)).ToArray();
        PaddingSlider.Value = project.Settings.Padding; RadiusSlider.Value = project.Settings.CornerRadius; ShadowSlider.Value = project.Settings.Shadow; BackgroundColor.Text = project.Settings.Background; ShowCursor.IsChecked = project.Settings.CursorVisible; MouseOffset.Text = (project.MouseClock.OffsetSeconds*1000).ToString("0.###", CultureInfo.CurrentCulture);
        EmptyState.Visibility = Visibility.Collapsed; ProjectTitle.Text = Path.GetFileName(Path.GetDirectoryName(projectFile));
        TimelineInfo.Text = $"{project.Timeline.Segments.Count} 个片段 · {project.ZoomClips.Count} 段运镜 · {project.PointerEvents.Count} 条鼠标事件 · 编辑后自动保存";
        refreshing = false; UpdateTime(); DrawWaveform();
    }
    private async Task LoadTimelineAssets(string path,FrameTrailProject opened)
    {
        assetsCancellation?.Cancel(); assetsCancellation=new(); var token=assetsCancellation.Token;
        try
        {
            var data=await TimelineAssets.BuildAsync(path,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FrameTrail","thumbnail-cache"),token);
            if(token.IsCancellationRequested || project?.ProjectId!=opened.ProjectId)return;
            timelineAssets=data; RefreshProject();
        }
        catch(Exception ex) when(ex is OperationCanceledException || token.IsCancellationRequested) { }
        catch(Exception ex) { App.Log(ex); Status.Text="缩略图或波形生成失败："+ex.Message; }
    }
    private void WaveformSizeChanged(object sender,SizeChangedEventArgs e)=>DrawWaveform();
    private void DrawWaveform()
    {
        if(Waveform==null)return; Waveform.Children.Clear();
        if(project==null||timelineAssets==null||timelineAssets.Peaks.Count==0||Waveform.ActualWidth<1)return;
        var points=new PointCollection(); var width=Waveform.ActualWidth; var bins=Math.Min(1000,(int)width);
        for(var i=0;i<bins;i++)
        {
            var sourceTime=project.Timeline.MapOutputToSource(project.Timeline.Duration*i/bins);
            var index=Math.Clamp((int)(sourceTime/timelineAssets.Duration*timelineAssets.Peaks.Count),0,timelineAssets.Peaks.Count-1);
            var peak=timelineAssets.Peaks[index]*11; points.Add(new(i*width/bins,12-peak)); points.Add(new(i*width/bins,12+peak));
        }
        Waveform.Children.Add(new System.Windows.Shapes.Polyline { Points=points,Stroke=new SolidColorBrush(Color.FromRgb(126,175,162)),StrokeThickness=1 });
    }
    private void Edit(Action<FrameTrailProject> edit)
    {
        if (project is null || history is null) return; StopPlayback(); var next = Clone(project); edit(next); next.Timeline.Validate();
        FrameTrailProjectStore.Save(projectFile!, next); history.Commit(next); project = next; InvalidateProxy(); RefreshProject(); _ = Guard(() => RenderPreview(Playhead.Value));
    }
    private TimelineSegment Selected(FrameTrailProject p) => p.Timeline.Segments.FirstOrDefault(x => x.Id == (Clips.SelectedItem as ClipItem)?.Id) ?? throw new InvalidOperationException("请选择一个片段。");
    private double SelectedSourceAtPlayhead(FrameTrailProject p,TimelineSegment selected)
    {
        var start=0d; foreach(var s in p.Timeline.Segments) { if(s.Id==selected.Id)break; start+=s.OutputDuration; }
        if(Playhead.Value<start-1e-8||Playhead.Value>start+selected.OutputDuration+1e-8) throw new InvalidOperationException("请把播放头放在选中片段内，再执行分割或裁剪。");
        return selected.SourceStart+Math.Clamp(Playhead.Value-start,0,selected.OutputDuration)*selected.PlaybackRate;
    }
    private async void SplitClick(object sender, RoutedEventArgs e) => await Guard(() => { Edit(p => { var s = Selected(p); p.Timeline.Split(s.Id, SelectedSourceAtPlayhead(p,s)); }); return Task.CompletedTask; });
    private async void TrimLeftClick(object sender, RoutedEventArgs e) => await Guard(() => { Edit(p => { var s = Selected(p); p.Timeline.Trim(s.Id, SelectedSourceAtPlayhead(p,s), s.SourceEnd); }); return Task.CompletedTask; });
    private async void TrimRightClick(object sender, RoutedEventArgs e) => await Guard(() => { Edit(p => { var s = Selected(p); p.Timeline.Trim(s.Id, s.SourceStart, SelectedSourceAtPlayhead(p,s)); }); return Task.CompletedTask; });
    private async void MoveLeftClick(object sender, RoutedEventArgs e) => await Guard(() => { Edit(p => { var s = Selected(p); p.Timeline.Reorder(s.Id, Math.Max(0,p.Timeline.Segments.IndexOf(s)-1)); }); return Task.CompletedTask; });
    private async void MoveRightClick(object sender, RoutedEventArgs e) => await Guard(() => { Edit(p => { var s = Selected(p); p.Timeline.Reorder(s.Id, Math.Min(p.Timeline.Segments.Count-1,p.Timeline.Segments.IndexOf(s)+1)); }); return Task.CompletedTask; });
    private async void SpeedClick(object sender, RoutedEventArgs e) => await Guard(() => { Edit(p => p.Timeline.SetPlaybackRate(Selected(p).Id, new[] {1d,1.5,2,4}[Speed.SelectedIndex])); return Task.CompletedTask; });
    private async void RestoreClick(object sender, RoutedEventArgs e) => await Guard(() => { Edit(p => p.Timeline.Segments = [new() { SourceDuration = p.Media.Duration }]); return Task.CompletedTask; });
    private void ClipSelected(object sender, SelectionChangedEventArgs e) { if (refreshing || project == null || Clips.SelectedItem is not ClipItem item) return; var time = 0d; foreach (var s in project.Timeline.Segments) { if (s.Id == item.Id) break; time += s.OutputDuration; } Playhead.Value = time; }
    private void UndoClick(object sender, RoutedEventArgs e) { if (history?.CanUndo != true) return; project = history.Undo(); SaveProject(); InvalidateProxy(); RefreshProject(); _ = Guard(() => RenderPreview(Playhead.Value)); }
    private void RedoClick(object sender, RoutedEventArgs e) { if (history?.CanRedo != true) return; project = history.Redo(); SaveProject(); InvalidateProxy(); RefreshProject(); _ = Guard(() => RenderPreview(Playhead.Value)); }
    private void MarkerJump(object sender, MouseButtonEventArgs e) { if (Markers.SelectedItem is MarkerItem m && project != null) { var at = project.Timeline.MapSourceToOutput(m.SourceTime); if (at.HasValue) Playhead.Value = at.Value; else Status.Text = "此标记所在素材已从时间线裁去。"; } }
    private void DeleteMarkerClick(object sender, RoutedEventArgs e) { if (Markers.SelectedItem is MarkerItem m) Edit(p => p.Markers.RemoveAll(x => x.Id == m.Id)); }
    private async void ApplySettingsClick(object sender, RoutedEventArgs e) => await Guard(() => { if (!System.Text.RegularExpressions.Regex.IsMatch(BackgroundColor.Text,"^#[0-9a-fA-F]{6}$")) throw new ArgumentException("背景色请使用 #RRGGBB 格式。"); Edit(p => { p.Settings.Background = BackgroundColor.Text; p.Settings.Padding = PaddingSlider.Value; p.Settings.CornerRadius = RadiusSlider.Value; p.Settings.Shadow = ShadowSlider.Value; p.Settings.CursorVisible = ShowCursor.IsChecked == true; }); return Task.CompletedTask; });
    private void AutoZoomClick(object sender, RoutedEventArgs e) => Edit(p => { p.Settings.AutoZoom = true; p.ZoomClips = AutoZoomPlanner.Plan(p.PointerEvents.Select(x => x with { Time = p.MouseClock.ToSourceTime(x.Time) }), new AutoZoomOptions(Scale: new[] {1.3,1.6,2}[ZoomScale.SelectedIndex])); });
    private void ClearZoomClick(object sender, RoutedEventArgs e) => Edit(p => { p.Settings.AutoZoom = false; p.ZoomClips.Clear(); });
    private async void CalibrateClick(object sender, RoutedEventArgs e) => await Guard(() => { var value = double.Parse(MouseOffset.Text, CultureInfo.CurrentCulture); if (!double.IsFinite(value) || Math.Abs(value)>10000) throw new ArgumentException("时间校准范围为 ±10000 毫秒。"); Edit(p => { p.MouseClock = p.MouseClock with { OffsetSeconds = value/1000 }; if (p.ZoomClips.All(x => x.Automatic)) p.ZoomClips = AutoZoomPlanner.Plan(p.PointerEvents.Select(x=>x with { Time=p.MouseClock.ToSourceTime(x.Time) })); }); return Task.CompletedTask; });
    private void PreviewFocusClick(object sender, MouseButtonEventArgs e)
    {
        if (project == null || ManualFocus.IsChecked != true || PreviewImage.Source is null) return;
        var point = e.GetPosition(PreviewImage); var imageAspect = PreviewImage.Source.Width/PreviewImage.Source.Height; var w = PreviewImage.ActualWidth; var h = w/imageAspect; if (h>PreviewImage.ActualHeight) { h=PreviewImage.ActualHeight; w=h*imageAspect; }
        var nx = (point.X-(PreviewImage.ActualWidth-w)/2)/w; var ny = (point.Y-(PreviewImage.ActualHeight-h)/2)/h;
        var scene = FrameSceneEvaluator.Evaluate(project,Playhead.Value); var pad = scene.Padding;
        // Preview already displays the current crop; transform the selected point back into source coordinates.
        var fit = Math.Min(w*(1-2*pad)/project.Media.Width,h*(1-2*pad)/project.Media.Height); var screenW = project.Media.Width*fit/w; var screenH = project.Media.Height*fit/h;
        var focus = new NormalizedPoint(scene.CropOrigin.X + Math.Clamp((nx-(1-screenW)/2)/screenW,0,1)*scene.CropSize.X, scene.CropOrigin.Y + Math.Clamp((ny-(1-screenH)/2)/screenH,0,1)*scene.CropSize.Y);
        Edit(p => { var start = p.Timeline.MapOutputToSource(Playhead.Value); p.ZoomClips.Add(new() { Start=start,End=start+1.5,Focus=focus,Scale=new[]{1.3,1.6,2}[ZoomScale.SelectedIndex],EnterDuration=.5,ExitDuration=.5 }); });
    }
    private void PlayheadChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (refreshing || project == null) return;
        if(SnapBoundaries.IsChecked==true && Playhead.IsMouseOver && Mouse.LeftButton==MouseButtonState.Pressed)
        {
            var edges=new List<double>{0}; var total=0d; foreach(var segment in project.Timeline.Segments) { total+=segment.OutputDuration; edges.Add(total); }
            var nearest=edges.MinBy(x=>Math.Abs(x-e.NewValue)); var tolerance=Math.Min(.15,project.Timeline.Duration*8/Math.Max(Playhead.ActualWidth,1));
            if(Math.Abs(nearest-e.NewValue)<tolerance) { refreshing=true;Playhead.Value=nearest;refreshing=false; }
        }
        StopPlayback(); UpdateTime(); _ = Guard(() => RenderPreview(Playhead.Value));
    }
    private void UpdateTime() => TimeLabel.Text = $"{TimeSpan.FromSeconds(Playhead.Value):mm\\:ss\\.fff} / {TimeSpan.FromSeconds(Playhead.Maximum):mm\\:ss\\.fff}";
    private async Task RenderPreview(double time)
    {
        if (project == null || projectFile == null) return; seekCancellation?.Cancel(); seekCancellation = new(); var token = seekCancellation.Token;
        var snapshot = project; var path = FrameTrailProjectStore.ResolveMediaPath(projectFile,snapshot);
        try
        {
            await Task.Delay(90,token);
            if (!ReferenceEquals(evaluatedProject,snapshot)) { previewEvaluator=new PreparedFrameSceneEvaluator(snapshot); evaluatedProject=snapshot; }
            var scene = previewEvaluator!.Evaluate(Math.Clamp(time,0,snapshot.Timeline.Duration));
            var inputWidth = Math.Min(960,(int)snapshot.Media.Width); var inputHeight = Math.Max(2,(int)(snapshot.Media.Height*inputWidth/snapshot.Media.Width));
            var width = 960; var height = Math.Max(2,(int)Math.Round(width*(double)snapshot.Settings.CanvasHeight/snapshot.Settings.CanvasWidth));
            var buffer = await MediaTools.DecodeFrameAsync(path,Math.Min(scene.SourceTime,Math.Max(0,snapshot.Media.Duration-1d/30)),inputWidth,inputHeight,token);
            await previewGate.WaitAsync(token); BitmapSource image;
            try
            {
                image = await Task.Run(() => { token.ThrowIfCancellationRequested(); var size=(inputWidth,inputHeight,width,height); if(previewRenderer==null || previewSize!=size) { previewRenderer?.Dispose(); previewRenderer=new(inputWidth,inputHeight,width,height); previewSize=size; } var output = new byte[width*height*4]; previewRenderer.Render(buffer,scene,output); var bmp = BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,output,width*4); bmp.Freeze(); return bmp; },token);
            }
            finally { previewGate.Release(); }
            token.ThrowIfCancellationRequested(); PreviewImage.Source = image; PreviewImage.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) { }
        catch (Exception) when (token.IsCancellationRequested) { }
    }
    private void PreviousFrame(object sender, RoutedEventArgs e) => Playhead.Value = Math.Max(0,Playhead.Value-1d/30);
    private void NextFrame(object sender, RoutedEventArgs e) => Playhead.Value = Math.Min(Playhead.Maximum,Playhead.Value+1d/30);
    private void InvalidateProxy() { StopPlayback(); if (proxyPath != null && File.Exists(proxyPath)) { Player.Close(); try { File.Delete(proxyPath); } catch (IOException) { } } proxyPath=null; }
    private void StopPlayback() { playing=false; playbackTimer.Stop(); Player.Pause(); Player.Visibility=Visibility.Collapsed; PreviewImage.Visibility=Visibility.Visible; PlayButton.Content="▶ 播放"; }
    private async void PlayClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (playing) { StopPlayback(); await RenderPreview(Playhead.Value); return; } if (project == null || projectFile == null || operationCancellation != null) return;
        if (proxyPath == null || !File.Exists(proxyPath))
        {
            var dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FrameTrail","cache"); Directory.CreateDirectory(dir); var candidate=Path.Combine(dir,Guid.NewGuid()+".mp4");
            var width=960; var height=Math.Max(2,(int)(960d*project.Settings.CanvasHeight/project.Settings.CanvasWidth)/2*2); var previewProject=project;
            await RunExport(candidate,new(width,height,30,true),true);
            if(!ReferenceEquals(previewProject,project)) { File.Delete(candidate); Status.Text="项目已改变，请重新生成预览。"; return; } proxyPath=candidate;
        }
        Player.Source=new Uri(proxyPath); Player.Position=TimeSpan.FromSeconds(Playhead.Value >= Playhead.Maximum-.03 ? 0 : Playhead.Value); Player.Visibility=Visibility.Visible; PreviewImage.Visibility=Visibility.Collapsed; Player.Play(); playing=true; PlayButton.Content="Ⅱ 暂停"; playbackTimer.Start();
    });
    private void PlaybackEnded(object sender,RoutedEventArgs e) { StopPlayback(); Playhead.Value=Playhead.Maximum; }
    private ExportOptions Options(bool audio) { var w=Resolution.SelectedIndex switch {0=>(int)(project?.Media.Width??1920),2=>2560,3=>3840,_=>1920}; var h=Resolution.SelectedIndex==0?(int)(project?.Media.Height??1080):(int)Math.Round(w*(double)(project?.Settings.CanvasHeight??1080)/(project?.Settings.CanvasWidth??1920)); return new(w/2*2,Math.Max(2,h/2*2),ExportFps.SelectedIndex==1?60:30,HardwareEncoder.IsChecked==true,audio); }
    private async void ExportClick(object sender,RoutedEventArgs e) => await Guard(() => ChooseExport(false));
    private async void ExportAudioClick(object sender,RoutedEventArgs e) => await Guard(() => ChooseExport(true));
    private async Task ChooseExport(bool audio)
    {
        if (project==null || operationCancellation!=null) return; var dialog=new SaveFileDialog { Filter=audio?"M4A 音频|*.m4a":"MP4 视频|*.mp4",FileName=audio?"FrameTrail-audio.m4a":"FrameTrail-export.mp4" };
        if (dialog.ShowDialog(this)!=true) return; await RunExport(dialog.FileName,Options(audio),false);
        if (MessageBox.Show(this,"导出完成。打开所在文件夹？","FrameTrail",MessageBoxButton.YesNo,MessageBoxImage.Information)==MessageBoxResult.Yes) Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList={"/select,",dialog.FileName},UseShellExecute=true });
    }
    private async Task RunExport(string path,ExportOptions options,bool preview)
    {
        if (project==null || projectFile==null) return; if (operationCancellation!=null) throw new InvalidOperationException("已有媒体处理任务运行。");
        StopPlayback(); seekCancellation?.Cancel(); operationCancellation=new(); CancelButton.Visibility=Visibility.Visible; Progress.Visibility=Visibility.Visible; ExportButton.IsEnabled=false; PlayButton.IsEnabled=false;
        var snapshot=Clone(project); var source=FrameTrailProjectStore.ResolveMediaPath(projectFile,snapshot);
        try
        {
            var report=new Progress<ExportProgress>(p=>{ Progress.Value=p.Fraction; Status.Text=$"{(preview?"准备有声预览 · ":"")}{p.Stage} · 已用 {p.Elapsed:mm\\:ss}"+(p.Remaining.HasValue?$" · 预计剩余 {p.Remaining:mm\\:ss}":""); });
            var result=await Task.Run(()=>new ExportEngine().ExportAsync(snapshot,source,path,options,report,operationCancellation.Token)); Status.Text=$"完成 · {result.Encoder} · {result.Renderer} · {result.Elapsed.TotalSeconds:0.0}s";
        }
        finally { operationCancellation.Dispose(); operationCancellation=null; CancelButton.Visibility=Visibility.Collapsed; Progress.Visibility=Visibility.Collapsed; ExportButton.IsEnabled=true; PlayButton.IsEnabled=true; }
    }
    private void CancelClick(object sender,RoutedEventArgs e) => operationCancellation?.Cancel();
    private void MemoClick(object sender,RoutedEventArgs e) => new MemoWindow().Show();
    private async void RecoverRecordingClick(object sender,RoutedEventArgs e) => await Guard(async()=>
    {
        var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),"FrameTrail");
        var dialog=new OpenFileDialog { Title="选择未完成录制目录里的 recording-recovery.json",Filter="未完成录制记录|recording-recovery.json",InitialDirectory=root };
        if(dialog.ShowDialog(this)!=true)return; var folder=Path.GetDirectoryName(dialog.FileName)!; var media=Path.Combine(folder,"media","screen.mp4");
        if(!File.Exists(media))throw new FileNotFoundException("录制中断前未生成视频，无法恢复画面。鼠标日志仍保留在该目录。",media);
        MediaInfo info;
        try { info=await MediaTools.ProbeAsync(media); }
        catch(Exception ex) { throw new InvalidDataException("此录制的视频封装未完成，当前版本不能修复。原始文件与鼠标日志已保留："+folder,ex); }
        if(info.Duration<=0||info.Width==0)throw new InvalidDataException("素材没有完整可读的视频帧，无法恢复项目。");
        var path=Path.Combine(folder,"recovered-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".frametrail");
        var recovered=MakeProject("media/screen.mp4",info); FrameTrailProjectStore.Save(path,recovered); await OpenProject(path);
        Status.Text="已恢复可读视频。中断会话的指针首帧时间未验证，鼠标原始日志保留供后续恢复。";
    });
    private void WindowKeyDown(object sender,KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is ComboBox) return;
        if (Keyboard.Modifiers==ModifierKeys.Control && e.Key==Key.S) { SaveProject(); e.Handled=true; }
        else if (Keyboard.Modifiers==ModifierKeys.Control && e.Key==Key.Z) { UndoClick(this,new()); e.Handled=true; }
        else if (Keyboard.Modifiers==ModifierKeys.Control && e.Key==Key.Y) { RedoClick(this,new()); e.Handled=true; }
        else if (Keyboard.Modifiers==ModifierKeys.None) { if(e.Key==Key.Space) PlayClick(this,new()); else if(e.Key==Key.S) SplitClick(this,new()); else if(e.Key==Key.Left) PreviousFrame(this,new()); else if(e.Key==Key.Right) NextFrame(this,new()); else return; e.Handled=true; }
    }
    private async void WindowClosing(object? sender,CancelEventArgs e)
    {
        if(closeReady) return; e.Cancel=true;
        if(capture.State is CaptureState.Recording or CaptureState.Starting) { MessageBox.Show(this,"请先结束录制，确保素材完成封装。","录制进行中"); return; }
        if(operationCancellation!=null) { operationCancellation.Cancel(); Status.Text="正在取消处理，请稍后关闭。"; return; }
        await Guard(async()=>{ SaveProject(); seekCancellation?.Cancel(); StopPlayback(); Player.Close(); NativeWindows.UnregisterHotKey(new WindowInteropHelper(this).Handle,17); await capture.DisposeAsync(); closeReady=true; Application.Current.Shutdown(); });
    }
}
