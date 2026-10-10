using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FrameTrail.Capture;
using FrameTrail.Media;

namespace FrameTrail.App;

public partial class MainWindow
{
    private static readonly Color IsolationBackdropColor = Color.FromRgb(208, 42, 196);

    private async Task RunRecordingIsolationSmoke(string artifactRootArgument)
    {
        var artifactRoot = Path.GetFullPath(artifactRootArgument);
        EnsureArtifactsRoot(artifactRoot);
        Directory.CreateDirectory(artifactRoot);
        if ((File.GetAttributes(artifactRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("隔离诊断目录不能是 reparse point。");
        var reportPath = Path.Combine(artifactRoot, "recording-isolation-smoke.json");
        var progressPath = Path.Combine(artifactRoot, "recording-isolation-smoke.progress.log");
        var previousDiagnosticRecordingRoot = diagnosticRecordingRoot;
        diagnosticRecordingRoot = artifactRoot;
        var phases = new List<object>();
        var checks = new List<object>();
        var failures = new List<string>();
        var expectedBackdrop = new Rgb(IsolationBackdropColor.R, IsolationBackdropColor.G, IsolationBackdropColor.B);
        var reportData = new Dictionary<string, object?>();
        var previous = new
        {
            CaptureKind = CaptureKind.SelectedIndex,
            CaptureResolution = CaptureResolution.SelectedIndex,
            CaptureFps = CaptureFps.SelectedIndex,
            DiagnosticRecordingRoot = previousDiagnosticRecordingRoot,
            SystemSound = SystemSound.IsChecked,
            Microphone = Microphone.IsChecked,
            Camera = RecordCamera.IsChecked,
            WindowState,
            Left,
            Top,
            Width,
            Height,
            Topmost
        };
        IsolationBackdropWindow? backdrop = null;
        MemoWindow? memo = null;
        FileSnapshot? memoTextSnapshot = null;
        FileSnapshot? memoSettingsSnapshot = null;
        string? memoTextPath = null;
        string? memoSettingsPath = null;
        long baselineTick = 0;
        long excludedTick = 0;
        RecordingResult? result = null;
        WindowEvidence? mainEvidence = null;
        WindowEvidence? barEvidence = null;
        WindowEvidence? memoEvidence = null;
        object? baselinePixels = null;
        object? baselineScreenPixels = null;
        object? baselineNativeWindowPoints = null;
        object? excludedNativeWindowPoints = null;
        object? nativeZOrder = null;
        object? excludedPixels = null;
        string? originalProjectPath = projectFile;
        Guid? originalProjectId = project?.ProjectId;
        double originalPadding = project?.Settings.Padding ?? 0;
        double editedPadding = originalPadding;
        string? mediaPath = null;
        string? mediaHashBefore = null;
        string? mediaHashAfter = null;
        string? projectHashBefore = null;
        string? projectHashAfter = null;
        string? recordingDirectory = null;
        long mainHandleValue = 0;
        long barHandleValue = 0;
        long memoHandleValue = 0;
        uint? mainAffinityAfterStop = null;
        uint? memoAffinityAfterStop = null;
        bool? recordingBarDestroyedAfterStop = null;
        uint? mainAffinityAtRecordingStart = null;
        uint? barAffinityAtRecordingStart = null;
        uint? memoAffinityAtRecordingStart = null;

        try
        {
            var isolatedProjectPath = projectFile;
            if (project is null || string.IsNullOrWhiteSpace(isolatedProjectPath))
                throw new InvalidOperationException("请先通过 --open 打开隔离项目，再启动录制隔离诊断。");
            originalProjectPath = isolatedProjectPath;
            EnsurePathInside(artifactRoot, isolatedProjectPath, "项目文件");
            if (!Path.GetExtension(isolatedProjectPath).Equals(".frametrail", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("诊断只接受 .frametrail 隔离项目。");
            if (!excludeEditorDuringRecording)
                throw new InvalidOperationException("此诊断要求测试偏好已启用“录制时排除编辑器”。");

            var initialProject = FrameTrailProjectStore.Load(isolatedProjectPath);
            originalProjectId = initialProject.ProjectId;
            mediaPath = FrameTrailProjectStore.ResolveMediaPath(isolatedProjectPath, initialProject);
            EnsurePathInside(artifactRoot, mediaPath, "主视频素材");
            EnsureNoReparsePath(artifactRoot, isolatedProjectPath);
            EnsureNoReparsePath(artifactRoot, mediaPath);
            projectHashBefore = await HashFileAsync(isolatedProjectPath);
            mediaHashBefore = await HashFileAsync(mediaPath);

            var displays = await capture.EnumerateDisplaysAsync();
            var primary = displays.FirstOrDefault(x => x.IsPrimary) ?? displays.FirstOrDefault()
                ?? throw new InvalidOperationException("没有可录制的显示器。");
            changingRecorderMode = true;
            CaptureKind.SelectedIndex = 0;
            changingRecorderMode = false;
            await RefreshDevices();
            var selectedDisplay = Targets.Items.Cast<TargetItem>()
                .FirstOrDefault(x => string.Equals(x.Target.Id, primary.Id, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("主显示器没有出现在录制来源列表中。");
            Targets.SelectedItem = selectedDisplay;
            CaptureResolution.SelectedIndex = 0;
            CaptureFps.SelectedIndex = 0;
            SystemSound.IsChecked = false;
            Microphone.IsChecked = false;
            RecordCamera.IsChecked = false;

            var work = SystemParameters.WorkArea;
            if (work.Width < 1240 || work.Height < 680)
                throw new InvalidOperationException($"主显示器可用工作区过小，无法放置三个互不重叠的真实窗口：{work.Width:0}×{work.Height:0} DIP。");

            backdrop = new IsolationBackdropWindow(IsolationBackdropColor, work);
            backdrop.Show();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

            WindowState = WindowState.Normal;
            Width = Math.Min(work.Width * .68, Math.Max(840, work.Width * .58));
            Height = Math.Min(work.Height * .88, Math.Max(600, work.Height * .78));
            Left = work.Left + work.Width * .02;
            Top = work.Top + work.Height * .04;
            Topmost = true;
            UpdateLayout();
            SetTopmostWithoutActivation(new WindowInteropHelper(this).Handle);

            memoTextPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameTrail", "memo.txt");
            memoSettingsPath = memoTextPath + ".settings.json";
            memoTextSnapshot = CaptureFileSnapshot(memoTextPath);
            memoSettingsSnapshot = CaptureFileSnapshot(memoSettingsPath);
            memo = new MemoWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Width = Math.Min(380, work.Right - Math.Max(work.Left + work.Width * .72, Left + Width + 24)),
                Height = Math.Min(440, work.Height * .46),
                Left = Math.Max(work.Left + work.Width * .72, Left + Width + 24),
                Top = work.Top + work.Height * .10,
                ShowActivated = false
            };
            memo.Closed += (_, _) => RestoreMemoFiles();
            memo.Show();

            var displayRect = primary.Bounds;
            var mainHandle = new WindowInteropHelper(this).Handle;
            var memoHandle = new WindowInteropHelper(memo).Handle;
            mainHandleValue = mainHandle.ToInt64();
            memoHandleValue = memoHandle.ToInt64();
            phases.Add(new { Name = "foreground-gate-wait-start", Qpc = Stopwatch.GetTimestamp(), Window = mainHandleValue, ProcessId = Environment.ProcessId, ProgressLog = progressPath });
            await WaitForDiagnosticForegroundAsync(mainHandle, progressPath, TimeSpan.FromMinutes(3));
            phases.Add(new { Name = "foreground-gate-passed", Qpc = Stopwatch.GetTimestamp(), Window = mainHandleValue });
            if (capture.State is CaptureState.Recording or CaptureState.Paused)
                throw new InvalidOperationException("启动隔离诊断前已有录制正在运行。");

            await ToggleRecording();
            var bar = recordingBar;
            if (capture.State != CaptureState.Recording || bar is null)
                throw new InvalidOperationException("应用未进入实际录制状态。");
            recordingDirectory = recordingFolder;
            EnsurePathInside(artifactRoot, recordingDirectory!, "录制输出目录");

            bar.Left = memo.Left;
            bar.Top = work.Top + work.Height * .69;
            bar.Width = memo.Width;
            var barHandle = new WindowInteropHelper(bar).Handle;
            barHandleValue = barHandle.ToInt64();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(500);
            nativeZOrder = PlaceDiagnosticWindowsAboveBackdrop(backdrop, mainHandle, memoHandle, barHandle);

            mainAffinityAtRecordingStart = ReadAffinity(mainHandle);
            barAffinityAtRecordingStart = ReadAffinity(barHandle);
            memoAffinityAtRecordingStart = ReadAffinity(memoHandle);
            checks.Add(new { Name = "product-excludes-editor-on-recording-start", Expected = 0x11, Actual = mainAffinityAtRecordingStart, Passed = mainAffinityAtRecordingStart == 0x11 });
            checks.Add(new { Name = "product-excludes-recording-bar-on-window-create", Expected = 0x11, Actual = barAffinityAtRecordingStart, Passed = barAffinityAtRecordingStart == 0x11 });
            checks.Add(new { Name = "product-excludes-memo-on-window-create", Expected = 0x11, Actual = memoAffinityAtRecordingStart, Passed = memoAffinityAtRecordingStart == 0x11 });
            phases.Add(new { Name = "product-affinity-before-diagnostic-changes", Qpc = Stopwatch.GetTimestamp(), Main = mainAffinityAtRecordingStart, RecordingBar = barAffinityAtRecordingStart, Memo = memoAffinityAtRecordingStart });
            if (mainAffinityAtRecordingStart != 0x11 || barAffinityAtRecordingStart != 0x11 || memoAffinityAtRecordingStart != 0x11)
                throw new InvalidOperationException($"录制启动后的产品排除状态不完整：Main={mainAffinityAtRecordingStart}、Bar={barAffinityAtRecordingStart}、Memo={memoAffinityAtRecordingStart}，期望全部为 17。诊断不会用手动设置掩盖此失败。");

            mainEvidence = DescribeWindow("编辑器", mainHandle, displayRect);
            barEvidence = DescribeWindow("录制条", barHandle, displayRect);
            memoEvidence = DescribeWindow("备忘录", memoHandle, displayRect);
            if (Intersects(mainEvidence.Bounds, barEvidence.Bounds) || Intersects(mainEvidence.Bounds, memoEvidence.Bounds) || Intersects(barEvidence.Bounds, memoEvidence.Bounds))
                throw new InvalidOperationException("编辑器、录制条和备忘录没有按诊断布局分开。");
            baselineNativeWindowPoints = SampleWindowOwners(mainEvidence, barEvidence, memoEvidence);
            phases.Add(new { Name = "native-window-owner-before-baseline", Qpc = Stopwatch.GetTimestamp(), Points = baselineNativeWindowPoints, ZOrder = nativeZOrder });
            RequireOwnWindowsAtSamplePoints(baselineNativeWindowPoints);

            SetAndVerifyAffinity(mainHandle, 0);
            SetAndVerifyAffinity(barHandle, 0);
            SetAndVerifyAffinity(memoHandle, 0);
            phases.Add(new { Name = "visible-baseline-affinity-none", QpcStart = Stopwatch.GetTimestamp(), Handles = new[] { mainHandleValue, barHandleValue, memoHandleValue } });
            await Task.Delay(1100);
            FlushWindowPaint(mainHandle, barHandle, memoHandle);
            await Task.Delay(250);
            baselineTick = Stopwatch.GetTimestamp();
            var mainScreenPixel = SampleDesktopPixel(mainEvidence!);
            var barScreenPixel = SampleDesktopPixel(barEvidence!);
            var memoScreenPixel = SampleDesktopPixel(memoEvidence!);
            baselineScreenPixels = new[] { mainScreenPixel, barScreenPixel, memoScreenPixel };
            baselineNativeWindowPoints = SampleWindowOwners(mainEvidence, barEvidence, memoEvidence);
            phases.Add(new { Name = "visible-baseline-sample", Qpc = baselineTick, Main = ReadAffinity(mainHandle), RecordingBar = ReadAffinity(barHandle), Memo = ReadAffinity(memoHandle), ScreenPixels = baselineScreenPixels, WindowOwners = baselineNativeWindowPoints });
            RequireOwnWindowsAtSamplePoints(baselineNativeWindowPoints);

            SetAndVerifyAffinity(mainHandle, mainAffinityAtRecordingStart.Value);
            SetAndVerifyAffinity(barHandle, barAffinityAtRecordingStart.Value);
            SetAndVerifyAffinity(memoHandle, memoAffinityAtRecordingStart.Value);
            await Task.Delay(700);

            var currentPadding = project!.Settings.Padding;
            editedPadding = currentPadding < .49 ? currentPadding + .01 : currentPadding - .01;
            Edit(p => p.Settings.Padding = editedPadding);
            await RenderPreview(Playhead.Value);
            SaveProject();
            var savedDuringRecording = FrameTrailProjectStore.Load(originalProjectPath!);
            if (savedDuringRecording.ProjectId != originalProjectId || Math.Abs(savedDuringRecording.Settings.Padding - editedPadding) > 1e-9)
                throw new InvalidDataException("录制期间保存的隔离工程未保留项目 ID 或画布 padding 编辑。");
            if (capture.State != CaptureState.Recording || !string.Equals(projectFile, originalProjectPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("录制期间编辑没有继续作用于原隔离项目。");

            await Task.Delay(700);
            excludedTick = Stopwatch.GetTimestamp();
            var mainAffinityAtExcludedSample = ReadAffinity(mainHandle);
            var barAffinityAtExcludedSample = ReadAffinity(barHandle);
            var memoAffinityAtExcludedSample = ReadAffinity(memoHandle);
            checks.Add(new { Name = "original-product-affinity-restored-for-excluded-sample", Expected = 0x11, Main = mainAffinityAtExcludedSample, RecordingBar = barAffinityAtExcludedSample, Memo = memoAffinityAtExcludedSample, Passed = mainAffinityAtExcludedSample == mainAffinityAtRecordingStart && barAffinityAtExcludedSample == barAffinityAtRecordingStart && memoAffinityAtExcludedSample == memoAffinityAtRecordingStart });
            phases.Add(new { Name = "excluded-window-sample", Qpc = excludedTick, Main = mainAffinityAtExcludedSample, RecordingBar = barAffinityAtExcludedSample, Memo = memoAffinityAtExcludedSample, ProjectPadding = editedPadding });
            excludedNativeWindowPoints = SampleWindowOwners(mainEvidence!, barEvidence!, memoEvidence!);
            phases.Add(new { Name = "excluded-window-point-owners", Qpc = excludedTick, Points = excludedNativeWindowPoints });
            RequireOwnWindowsAtSamplePoints(excludedNativeWindowPoints);
            await Task.Delay(900);
            await StopRecording();
            result = capture.LatestCompletedResult ?? throw new InvalidOperationException("停止录制后没有可读取的完成结果。");
            if (capture.State is CaptureState.Recording or CaptureState.Paused or CaptureState.Stopping)
                throw new InvalidOperationException("停止流程返回时采集仍处于活动状态。");

            mainAffinityAfterStop = ReadAffinity(mainHandle);
            memoAffinityAfterStop = IsWindow(memoHandle) ? ReadAffinity(memoHandle) : null;
            recordingBarDestroyedAfterStop = !IsWindow(barHandle);
            if (mainAffinityAfterStop != 0) failures.Add($"停止后主窗口 affinity 应为 0，实际为 {mainAffinityAfterStop}。");
            if (memoAffinityAfterStop != 0x11) failures.Add($"停止后仍打开的备忘录 affinity 应保持 17，实际为 {memoAffinityAfterStop?.ToString() ?? "窗口已关闭"}。");
            if (recordingBarDestroyedAfterStop != true) failures.Add("停止后录制条 HWND 仍然存在。");

            if (result.FirstFrameObservedStopwatchTicks is null || result.StopwatchFrequency <= 0)
                throw new InvalidDataException("录制结果没有首帧 QPC 锚点，无法把窗口样本映射到实际视频帧。");
            if (!string.Equals(Path.GetFullPath(result.OutputPath), Path.GetFullPath(Path.Combine(recordingDirectory!, "media", "screen.mp4")), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("录制输出路径与隔离录制目录不符。");
            var videoInfo = await MediaTools.ProbeAsync(result.OutputPath);
            var baselineSeconds = MediaTime(baselineTick, result);
            var excludedSeconds = MediaTime(excludedTick, result);
            if (baselineSeconds < 0 || excludedSeconds <= baselineSeconds || excludedSeconds >= videoInfo.Duration)
                throw new InvalidDataException($"QPC 样本时间不在录制视频范围内：baseline={baselineSeconds:0.000}s, excluded={excludedSeconds:0.000}s, duration={videoInfo.Duration:0.000}s。");

            var baselineFrame = await MediaTools.DecodeFrameAsync(result.OutputPath, baselineSeconds, result.Width, result.Height, CancellationToken.None);
            var excludedFrame = await MediaTools.DecodeFrameAsync(result.OutputPath, excludedSeconds, result.Width, result.Height, CancellationToken.None);
            var mainBaselinePixel = SampleWindowPixel(mainEvidence!, primary.Bounds, result, baselineFrame);
            var barBaselinePixel = SampleWindowPixel(barEvidence!, primary.Bounds, result, baselineFrame);
            var memoBaselinePixel = SampleWindowPixel(memoEvidence!, primary.Bounds, result, baselineFrame);
            baselinePixels = new[]
            {
                mainBaselinePixel, barBaselinePixel, memoBaselinePixel
            };
            var mainExcludedPixel = SampleWindowPixel(mainEvidence!, primary.Bounds, result, excludedFrame);
            var barExcludedPixel = SampleWindowPixel(barEvidence!, primary.Bounds, result, excludedFrame);
            var memoExcludedPixel = SampleWindowPixel(memoEvidence!, primary.Bounds, result, excludedFrame);
            excludedPixels = new[]
            {
                mainExcludedPixel, barExcludedPixel, memoExcludedPixel
            };
            var baselineSamples = (PixelEvidence[])baselinePixels!;
            var directBaselineSamples = (ScreenPixelEvidence[])baselineScreenPixels!;
            var excludedSamples = (PixelEvidence[])excludedPixels!;
            for (var i = 0; i < baselineSamples.Length; i++)
            {
                var name = i == 0 ? mainEvidence!.Name : i == 1 ? barEvidence!.Name : memoEvidence!.Name;
                if (Near(directBaselineSamples[i].Pixel, expectedBackdrop, 15)) failures.Add($"{name} 的屏幕采样没有看到覆盖在背景上的窗口：{directBaselineSamples[i].Pixel}。");
                if (!Near(baselineSamples[i].Pixel, directBaselineSamples[i].Pixel, 45)) failures.Add($"{name} 的 WDA_NONE 录像像素与屏幕像素不符：video={baselineSamples[i].Pixel}, screen={directBaselineSamples[i].Pixel}。");
                if (!Near(excludedSamples[i].Pixel, expectedBackdrop, 15)) failures.Add($"窗口排除后没有显出受控背景色：{excludedSamples[i].Pixel}，期望 {expectedBackdrop}。");
            }

            await OpenProject(originalProjectPath!);
            if (project?.ProjectId != originalProjectId || Math.Abs(project.Settings.Padding - editedPadding) > 1e-9)
                failures.Add("停止录制后重开隔离旧工程未保留项目 ID 或录制期间保存的画布编辑。");
            mediaHashAfter = await HashFileAsync(FrameTrailProjectStore.ResolveMediaPath(originalProjectPath!, project!));
            projectHashAfter = await HashFileAsync(originalProjectPath!);
            if (!string.Equals(mediaHashBefore, mediaHashAfter, StringComparison.Ordinal))
                failures.Add("隔离工程的原始主视频素材哈希发生变化。");

            reportData["TargetDisplay"] = primary;
            reportData["Dpi"] = VisualTreeHelper.GetDpi(this);
            reportData["BackdropRgb"] = expectedBackdrop;
            reportData["BaselineVideoSeconds"] = baselineSeconds;
            reportData["ExcludedVideoSeconds"] = excludedSeconds;
        }
        catch (Exception error)
        {
            failures.Add(error.ToString());
            if (capture.State is CaptureState.Starting or CaptureState.Recording or CaptureState.Paused or CaptureState.Faulted)
            {
                try { await StopRecording(); }
                catch (Exception stopError) { failures.Add("清理时停止录制失败：" + stopError); }
            }
            try { NativeWindows.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, 0); } catch (Exception affinityError) { failures.Add("清理主窗口 affinity 失败：" + affinityError.Message); }
            reportData["Error"] = error.ToString();
            App.Log(error);
        }
        finally
        {
            if (recordingBar is not null && capture.State is not (CaptureState.Recording or CaptureState.Paused or CaptureState.Stopping))
            {
                try { recordingBar.CloseAfterRecording(); } catch (Exception closeError) { App.Log(closeError); }
                recordingBar = null;
            }
            if (memo is not null)
            {
                try { memo.Close(); } catch (Exception closeError) { App.Log(closeError); }
            }
            RestoreMemoFiles();
            if (backdrop is not null)
            {
                try { backdrop.Close(); } catch (Exception closeError) { App.Log(closeError); }
            }
            try { NativeWindows.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, 0); } catch (Exception ex) { App.Log(ex); }
            SystemSound.IsChecked = previous.SystemSound;
            Microphone.IsChecked = previous.Microphone;
            RecordCamera.IsChecked = previous.Camera;
            CaptureKind.SelectedIndex = previous.CaptureKind;
            CaptureResolution.SelectedIndex = previous.CaptureResolution;
            CaptureFps.SelectedIndex = previous.CaptureFps;
            diagnosticRecordingRoot = previous.DiagnosticRecordingRoot;
            if (previous.WindowState == WindowState.Normal)
            {
                WindowState = WindowState.Normal;
                Left = previous.Left; Top = previous.Top; Width = previous.Width; Height = previous.Height;
            }
            else WindowState = previous.WindowState;
            Topmost = previous.Topmost;
        }

        void RestoreMemoFiles()
        {
            if (memoTextPath is null || memoSettingsPath is null) return;
            try { if (memoTextSnapshot is not null) RestoreFileSnapshot(memoTextPath, memoTextSnapshot); } catch (Exception ex) { failures.Add("恢复备忘录文本失败：" + ex.Message); App.Log(ex); }
            try { if (memoSettingsSnapshot is not null) RestoreFileSnapshot(memoSettingsPath, memoSettingsSnapshot); } catch (Exception ex) { failures.Add("恢复备忘录设置失败：" + ex.Message); App.Log(ex); }
        }
        reportData["Passed"] = failures.Count == 0;
        reportData["Version"] = 1;
        reportData["Utc"] = DateTimeOffset.UtcNow;
        reportData["ArtifactRoot"] = artifactRoot;
        reportData["IsolatedProject"] = originalProjectPath;
        reportData["ProjectId"] = originalProjectId;
        reportData["ProjectSha256Before"] = projectHashBefore;
        reportData["ProjectSha256After"] = projectHashAfter;
        reportData["PrimaryMediaPath"] = mediaPath;
        reportData["PrimaryMediaSha256Before"] = mediaHashBefore;
        reportData["PrimaryMediaSha256After"] = mediaHashAfter;
        reportData["EditedPadding"] = editedPadding;
        reportData["RecordingDirectory"] = recordingDirectory;
        reportData["Recording"] = result;
        reportData["MainWindow"] = mainEvidence;
        reportData["RecordingBar"] = barEvidence;
        reportData["MemoWindow"] = memoEvidence;
        reportData["BaselineQpc"] = baselineTick;
        reportData["ExcludedQpc"] = excludedTick;
        reportData["BaselinePixels"] = baselinePixels;
        reportData["BaselineScreenPixels"] = baselineScreenPixels;
        reportData["BaselineWindowOwners"] = baselineNativeWindowPoints;
        reportData["ExcludedWindowOwners"] = excludedNativeWindowPoints;
        reportData["NativeZOrder"] = nativeZOrder;
        reportData["ExcludedPixels"] = excludedPixels;
        reportData["AffinityAfterStop"] = new { MainWindow = mainAffinityAfterStop, RecordingBarDestroyed = recordingBarDestroyedAfterStop, MemoWindow = memoAffinityAfterStop };
        reportData["AffinityAtProductStart"] = new { MainWindow = mainAffinityAtRecordingStart, RecordingBar = barAffinityAtRecordingStart, MemoWindow = memoAffinityAtRecordingStart };
        reportData["ForegroundProgressLog"] = progressPath;
        reportData["Checks"] = checks;
        reportData["Phases"] = phases;
        reportData["Failures"] = failures;
        try { await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(reportData, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception reportError) { failures.Add("写入诊断报告失败：" + reportError); App.Log(reportError); }
        Environment.ExitCode = failures.Count == 0 ? 0 : 1;
    }

    private static void EnsureArtifactsRoot(string path)
    {
        var full = Path.GetFullPath(path);
        var marker = $"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}";
        if (!full.Contains(marker, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("隔离诊断输出必须位于仓库 artifacts 目录下。");
    }

    private static void EnsurePathInside(string root, string path, string description)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException($"{description}必须位于隔离 artifact 目录内：{path}");
    }

    private static void EnsureNoReparsePath(string root, string filePath)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(filePath);
        EnsurePathInside(fullRoot, fullPath, "隔离文件");
        var current = fullRoot;
        foreach (var segment in Path.GetRelativePath(fullRoot, fullPath).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"隔离工程路径不能包含 reparse point：{current}");
        }
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static FileSnapshot CaptureFileSnapshot(string path) => File.Exists(path)
        ? new FileSnapshot(true, File.ReadAllBytes(path))
        : new FileSnapshot(false, []);

    private static void RestoreFileSnapshot(string path, FileSnapshot snapshot)
    {
        if (!snapshot.Exists)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".isolation-restore-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporary, snapshot.Bytes);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static WindowEvidence DescribeWindow(string name, IntPtr handle, PixelRect display)
    {
        if (!GetWindowRect(handle, out var rect)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"无法读取{name}边界。");
        var bounds = new PixelRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        if (bounds.Width < 24 || bounds.Height < 24 || bounds.X < display.X || bounds.Y < display.Y || bounds.X + bounds.Width > display.X + display.Width || bounds.Y + bounds.Height > display.Y + display.Height)
            throw new InvalidOperationException($"{name}窗口没有完整位于主显示器内：{bounds}，显示器：{display}。");
        return new WindowEvidence(name, handle.ToInt64(), bounds, ReadAffinity(handle));
    }

    private static void SetAndVerifyAffinity(IntPtr handle, uint expected)
    {
        if (!NativeWindows.SetWindowDisplayAffinity(handle, expected))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"设置 HWND 0x{handle.ToInt64():X} affinity={expected} 失败。");
        var actual = ReadAffinity(handle);
        if (actual != expected) throw new InvalidOperationException($"HWND 0x{handle.ToInt64():X} affinity 读回为 {actual}，期望 {expected}。");
    }

    private static uint ReadAffinity(IntPtr handle)
    {
        if (!GetWindowDisplayAffinity(handle, out var affinity))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"读取 HWND 0x{handle.ToInt64():X} affinity 失败。");
        return affinity;
    }

    private static bool Intersects(PixelRect a, PixelRect b) =>
        a.X < b.X + b.Width && a.X + a.Width > b.X && a.Y < b.Y + b.Height && a.Y + a.Height > b.Y;

    private static PixelEvidence SampleWindowPixel(WindowEvidence window, PixelRect display, RecordingResult result, byte[] bgra)
    {
        var centerX = window.Bounds.X + window.Bounds.Width / 2;
        var centerY = window.Bounds.Y + window.Bounds.Height / 2;
        var frameX = (int)Math.Round((centerX - display.X) * (double)result.Width / display.Width);
        var frameY = (int)Math.Round((centerY - display.Y) * (double)result.Height / display.Height);
        frameX = Math.Clamp(frameX & ~1, 4, result.Width - 5);
        frameY = Math.Clamp(frameY & ~1, 4, result.Height - 5);
        long red = 0, green = 0, blue = 0;
        const int side = 8;
        for (var y = frameY - side / 2; y < frameY + side / 2; y++)
            for (var x = frameX - side / 2; x < frameX + side / 2; x++)
            {
                var index = checked((y * result.Width + x) * 4);
                blue += bgra[index]; green += bgra[index + 1]; red += bgra[index + 2];
            }
        var count = side * side;
        var pixel = new Rgb((byte)(red / count), (byte)(green / count), (byte)(blue / count));
        return new PixelEvidence(window.Name, centerX, centerY, frameX, frameY, pixel);
    }

    private static ScreenPixelEvidence SampleDesktopPixel(WindowEvidence window)
    {
        var centerX = window.Bounds.X + window.Bounds.Width / 2;
        var centerY = window.Bounds.Y + window.Bounds.Height / 2;
        var hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法取得桌面 GDI 设备上下文。");
        long red = 0, green = 0, blue = 0;
        const int side = 8;
        try
        {
            for (var y = centerY - side / 2; y < centerY + side / 2; y++)
                for (var x = centerX - side / 2; x < centerX + side / 2; x++)
                {
                    var color = GetPixel(hdc, x, y);
                    if (color == 0xFFFFFFFF) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"GDI 无法读取屏幕点 ({x},{y})。");
                    red += color & 0xFF;
                    green += (color >> 8) & 0xFF;
                    blue += (color >> 16) & 0xFF;
                }
        }
        finally { _ = ReleaseDC(IntPtr.Zero, hdc); }
        var count = side * side;
        return new ScreenPixelEvidence(window.Name, centerX, centerY, new Rgb((byte)(red / count), (byte)(green / count), (byte)(blue / count)));
    }

    private static object PlaceDiagnosticWindowsAboveBackdrop(Window backdrop, params IntPtr[] testWindows)
    {
        var backdropHandle = new WindowInteropHelper(backdrop).Handle;
        SetTopmostWithoutActivation(backdropHandle);
        foreach (var handle in testWindows) SetTopmostWithoutActivation(handle);
        var zOrder = ReadNativeZOrder();
        var backdropIndex = zOrder.IndexOf(backdropHandle.ToInt64());
        var windowIndexes = testWindows.Select(handle => zOrder.IndexOf(handle.ToInt64())).ToArray();
        if (backdropIndex < 0 || windowIndexes.Any(index => index < 0 || index >= backdropIndex))
            throw new InvalidOperationException($"诊断窗口未排在受控背景上方：zorder={string.Join(',', zOrder.Take(24).Select(x => $"0x{x:X}"))}。");
        return new
        {
            BackdropHandle = backdropHandle.ToInt64(),
            BackdropIndex = backdropIndex,
            TestWindowHandles = testWindows.Select(handle => handle.ToInt64()).ToArray(),
            TestWindowIndexes = windowIndexes,
            TopHandles = zOrder.Take(24).ToArray()
        };
    }

    private static void SetTopmostWithoutActivation(IntPtr handle)
    {
        if (!SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"无法把诊断 HWND 0x{handle.ToInt64():X} 放到 Topmost 区域。");
    }

    private static List<long> ReadNativeZOrder()
    {
        var handles = new List<long>();
        var current = GetTopWindow(IntPtr.Zero);
        var seen = new HashSet<long>();
        while (current != IntPtr.Zero && handles.Count < 4096 && seen.Add(current.ToInt64()))
        {
            handles.Add(current.ToInt64());
            current = GetWindow(current, 2); // GW_HWNDNEXT
        }
        return handles;
    }

    private static WindowOwnerEvidence[] SampleWindowOwners(params WindowEvidence[] windows) => windows.Select(window =>
    {
        var x = window.Bounds.X + window.Bounds.Width / 2;
        var y = window.Bounds.Y + window.Bounds.Height / 2;
        var hit = WindowFromPoint(new NativePoint(x, y));
        var root = hit == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hit, 2); // GA_ROOT
        _ = GetWindowThreadProcessId(root, out var processId);
        return new WindowOwnerEvidence(window.Name, window.Handle, x, y, hit.ToInt64(), root.ToInt64(), processId, root.ToInt64() == window.Handle && processId == Environment.ProcessId);
    }).ToArray();

    private static void RequireOwnWindowsAtSamplePoints(object pointEvidence)
    {
        var points = (WindowOwnerEvidence[])pointEvidence;
        var missed = points.Where(point => !point.IsExpectedWindow).ToArray();
        if (missed.Length > 0)
            throw new InvalidOperationException("测试窗口的取样点被其他 HWND 遮挡：" + string.Join("；", missed.Select(point => $"{point.Name}@({point.X},{point.Y}) hit=0x{point.HitHandle:X} root=0x{point.RootHandle:X} pid={point.ProcessId}, expected=0x{point.ExpectedHandle:X}")));
    }

    private static void FlushWindowPaint(params IntPtr[] handles)
    {
        foreach (var handle in handles)
        {
            _ = RedrawWindow(handle, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0080 | 0x0100);
            _ = UpdateWindow(handle);
        }
        _ = DwmFlush();
    }

    private static async Task WaitForDiagnosticForegroundAsync(IntPtr expectedWindow, string progressPath, TimeSpan timeout)
    {
        var started = Stopwatch.GetTimestamp();
        var lastProgress = TimeSpan.FromSeconds(-5);
        while (Stopwatch.GetElapsedTime(started) < timeout)
        {
            var foreground = GetForegroundWindow();
            if (foreground == expectedWindow)
            {
                await File.AppendAllTextAsync(progressPath, $"{DateTimeOffset.UtcNow:O} foreground-ready hwnd=0x{expectedWindow.ToInt64():X} pid={Environment.ProcessId}{Environment.NewLine}");
                return;
            }
            var elapsed = Stopwatch.GetElapsedTime(started);
            if (elapsed - lastProgress >= TimeSpan.FromSeconds(5))
            {
                var line = $"{DateTimeOffset.UtcNow:O} waiting-for-main-foreground elapsed={elapsed.TotalSeconds:0}s targetHwnd=0x{expectedWindow.ToInt64():X} targetPid={Environment.ProcessId} foregroundHwnd=0x{foreground.ToInt64():X}{Environment.NewLine}";
                await File.AppendAllTextAsync(progressPath, line);
                lastProgress = elapsed;
            }
            await Task.Delay(250);
        }
        throw new TimeoutException($"等待本测试主窗口成为前台超时。进度记录：{progressPath}");
    }

    private static bool Near(Rgb a, Rgb b, int tolerance) =>
        Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out NativeWindowRect rect);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetTopWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern uint GetPixel(IntPtr hdc, int x, int y);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RedrawWindow(IntPtr hwnd, IntPtr updateRect, IntPtr updateRegion, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeWindowRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
        public NativePoint(int x, int y) { X = x; Y = y; }
    }
    private sealed record WindowEvidence(string Name, long Handle, PixelRect Bounds, uint AffinityBeforeSample);
    private sealed record Rgb(byte R, byte G, byte B);
    private sealed record PixelEvidence(string Window, int ScreenX, int ScreenY, int VideoX, int VideoY, Rgb Pixel);
    private sealed record ScreenPixelEvidence(string Window, int ScreenX, int ScreenY, Rgb Pixel);
    private sealed record WindowOwnerEvidence(string Name, long ExpectedHandle, int X, int Y, long HitHandle, long RootHandle, uint ProcessId, bool IsExpectedWindow);
    private sealed record FileSnapshot(bool Exists, byte[] Bytes);

    private sealed class IsolationBackdropWindow : Window
    {
        public IsolationBackdropWindow(Color color, Rect workArea)
        {
            Title = "FrameTrail recording isolation backdrop";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            WindowState = WindowState.Normal;
            Left = workArea.Left;
            Top = workArea.Top;
            Width = workArea.Width;
            Height = workArea.Height;
            Background = new SolidColorBrush(color);
        }
    }
}
