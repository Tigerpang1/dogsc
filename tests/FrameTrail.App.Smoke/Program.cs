using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

// 面向交互桌面会话的 UIA 冒烟验收。运行必须显式传入 --interactive-desktop。
if (args.Contains("--guard-self-test", StringComparer.Ordinal))
{
    var passed = InteractiveDesktopGuard.RunPureSelfTest();
    Console.WriteLine($"INTERACTIVE_DESKTOP_GUARD_SELF_TEST={(passed ? "PASS" : "FAIL")}");
    return passed ? 0 : 1;
}
if (args.Length == 2 && string.Equals(args[0], "--restore-user-data", StringComparison.Ordinal))
{
    var restored = RestoreUserDataSnapshot(Path.GetFullPath(args[1]), out var recoveryMessage);
    Console.WriteLine($"USER_DATA_RECOVERY={(restored ? "RESTORED" : "FAILED")}: {recoveryMessage}");
    return restored ? 0 : 1;
}
if (!InteractiveDesktopGuard.HasExplicitConsent(args))
{
    Console.Error.WriteLine("已拒绝运行：UI Smoke 会操作真实桌面。请在明确授权后传入 --interactive-desktop；本次未启动应用或发送输入。");
    return 3;
}
if (args.Length < 2)
{
    Console.Error.WriteLine("用法：FrameTrail.App.Smoke.exe <FrameTrail.exe> <project.frametrail> [report.json] --interactive-desktop [--reopen-only|--transient-surfaces-only]");
    return 2;
}

var appPath = Path.GetFullPath(args[0]);
var sourceProject = Path.GetFullPath(args[1]);
var reportPath = Path.GetFullPath(args.Length > 2 ? args[2] : Path.Combine(Environment.CurrentDirectory, "artifacts", "validation", "ui-smoke.json"));
var progressPath = Path.ChangeExtension(reportPath, ".progress.log");
File.WriteAllText(progressPath, $"UIA 开始 {DateTimeOffset.Now:O}{Environment.NewLine}");
SmokeProgress.Path = progressPath;
InteractiveDesktopGuard.Initialize(consented: true, progressPath);
var testRoot = args.Contains("--relink-only")
    ? Path.Combine(Environment.CurrentDirectory, "artifacts", "validation", "ui-relink-fixture-audit-" + Guid.NewGuid().ToString("N"))
    : args.Contains("--effects-only")
    ? Path.Combine(Environment.CurrentDirectory, "artifacts", "validation", "ui-effect-fixture-audit-" + Guid.NewGuid().ToString("N"))
    : Path.Combine(Path.GetTempPath(), "frametrail-ui-smoke-" + Guid.NewGuid().ToString("N"));
var userDataSnapshotPath = Path.Combine(testRoot, "user-data-snapshot.json");
var projectCopy = Path.Combine(testRoot, Path.GetFileName(Path.GetDirectoryName(sourceProject)) ?? "project");
var projectCopyFile = Path.Combine(projectCopy, Path.GetFileName(sourceProject));
var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
var userDataSnapshots = new Dictionary<string, byte[]?>
{
    [Path.Combine(localData, "FrameTrail", "settings.json")] = ReadIfPresent(Path.Combine(localData, "FrameTrail", "settings.json")),
    [Path.Combine(localData, "FrameTrail", "recent-projects.json")] = ReadIfPresent(Path.Combine(localData, "FrameTrail", "recent-projects.json")),
    [Path.Combine(localData, "FrameTrail", "memo.txt")] = ReadIfPresent(Path.Combine(localData, "FrameTrail", "memo.txt")),
    [Path.Combine(localData, "FrameTrail", "memo.txt.settings.json")] = ReadIfPresent(Path.Combine(localData, "FrameTrail", "memo.txt.settings.json"))
};
var results = new List<StepResult>();
Process? app = null;
void AddResult(StepResult result)
{
    results.Add(result);
    SmokeProgress.Append(result);
}
try
{
    if (!File.Exists(appPath)) throw new FileNotFoundException("找不到 FrameTrail 应用程序。", appPath);
    if (!File.Exists(sourceProject)) throw new FileNotFoundException("找不到测试项目。", sourceProject);
    Directory.CreateDirectory(testRoot);
    WriteUserDataSnapshot(userDataSnapshotPath, userDataSnapshots);
    SmokeProgress.AppendMessage($"用户数据恢复快照已保存：{userDataSnapshotPath}");
    CopyDirectory(Path.GetDirectoryName(sourceProject)!, projectCopy);
    if (args.Contains("--relink-only"))
    {
        RunRelinkOnly(appPath, sourceProject, projectCopyFile, results);
        throw new SmokeEarlyExitException();
    }
    PrepareFeatureFixture(projectCopyFile);
    if ((args.Contains("--effects-only") && !args.Contains("--preserve-reordered-timeline")) || args.Contains("--full-uncut-effects") || args.Contains("--timeline-shortcuts-only"))
        PrepareUncutEffectTimeline(projectCopyFile);
    if (args.Contains("--motion-curves-only"))
    {
        PrepareUncutEffectTimeline(projectCopyFile);
        PrepareMotionCurveFixture(projectCopyFile);
    }

    app = Process.Start(new ProcessStartInfo(appPath) { UseShellExecute = false, ArgumentList = { "--open", projectCopyFile } })
        ?? throw new InvalidOperationException("无法启动 FrameTrail。");
    var window = WaitForWindow(app, TimeSpan.FromSeconds(35));
    AddResult(new("应用启动并加载项目", window is not null, window is null ? "未找到该进程的主窗口。" : "已通过进程窗口句柄读取 UI Automation 树。"));
    if (window is null) throw new TimeoutException("FrameTrail 主窗口未出现。");
    var startupReady = args.Contains("--wait-for-foreground", StringComparer.Ordinal)
        ? InteractiveDesktopGuard.WaitForTargetForegroundAtStartup((IntPtr)window.Current.NativeWindowHandle, TimeSpan.FromSeconds(180))
        : InteractiveDesktopGuard.ActivateTargetOnceAtStartup((IntPtr)window.Current.NativeWindowHandle);
    if (!startupReady)
        throw new InvalidOperationException(args.Contains("--wait-for-foreground", StringComparer.Ordinal)
            ? "等待测试应用自然成为前台超时；未发送输入，项目偏好已按恢复快照处理。"
            : "启动时无法将焦点交给测试应用；守卫已停止本轮输入。");
    SmokeProgress.AppendMessage(args.Contains("--wait-for-foreground", StringComparer.Ordinal)
        ? "测试应用已自然成为前台；后续失焦会立即中止输入。"
        : "测试应用已在启动阶段完成唯一一次前台激活；后续失焦会立即中止输入。");
    NativeMethods.SetWindowPos((IntPtr)window.Current.NativeWindowHandle, IntPtr.Zero, 960, 200, 0, 0, 0x0001 | 0x0004 | 0x0010);
    var projectTitle = WaitForProjectTitle(window, TimeSpan.FromSeconds(20));
    var projectLoaded = projectTitle is not null && projectTitle.Current.Name != "未命名项目";
    AddResult(new("命令行项目确实已打开", projectLoaded, projectLoaded ? projectTitle!.Current.Name : "标题仍为未命名项目；本轮后续交互结果只作界面诊断，不计功能通过。"));
    if (args.Contains("--transient-surfaces-only", StringComparer.Ordinal))
    {
        if (!WaitForFrameTrailVisualReady(window, TimeSpan.FromSeconds(90), out var readyDetail))
        {
            SmokeProgress.MarkIncomplete("预览或时间线尚未报告ready；未生成浮层视觉截图。" + readyDetail);
            throw new SmokeEarlyExitException();
        }
        var captureDirectory = Path.Combine(Path.GetDirectoryName(reportPath)!, Path.GetFileNameWithoutExtension(reportPath) + ".surfaces-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(captureDirectory);
        RunTransientSurfacesUiTests(window, app, projectCopyFile, captureDirectory, results);
        if (!InteractiveDesktopGuard.IsAborted && !app.HasExited)
        {
            app.CloseMainWindow();
            if (app.WaitForExit(10000)) { app.Dispose(); app = null; }
        }
        throw new SmokeEarlyExitException();
    }
    if (args.Contains("--reopen-only", StringComparer.Ordinal))
    {
        FocusWindow(window);
        SendKey(0x53, true); // Ctrl+S
        var saveCompleted = !InteractiveDesktopGuard.IsAborted
            && SpinWaitUntil(() => FrameTrailProjectStoreProbe(projectCopyFile), TimeSpan.FromSeconds(5));
        var savedState = saveCompleted ? JsonNode.Parse(File.ReadAllText(projectCopyFile))?.AsObject() : null;
        var reopenOnlyExpectedSettings = savedState?["settings"]?.ToJsonString();
        var reopenOnlyExpectedCamera = savedState?["camera"]?.ToJsonString();
        var reopenOnlyExpectedMouseClock = savedState?["mouseClock"]?.ToJsonString();
        var expectedScreenMotions = ReadArrayCount(projectCopyFile, "screenMotions");

        var closeRequested = app.CloseMainWindow();
        var firstExited = closeRequested && app.WaitForExit(10000);
        AddResult(new("保存后请求关闭并等待首实例退出", saveCompleted && firstExited,
            $"保存可读={saveCompleted}，CloseMainWindow={closeRequested}，正常退出={firstExited}，PID={app.Id}。"));
        if (!firstExited || InteractiveDesktopGuard.IsAborted) throw new SmokeEarlyExitException();
        app.Dispose(); app = null;

        app = Process.Start(new ProcessStartInfo(appPath) { UseShellExecute = false, ArgumentList = { "--open", projectCopyFile } });
        var reopenOnlyWindow = app is null ? null : WaitForWindow(app, TimeSpan.FromSeconds(25));
        var reopenOnlyStartupReady = reopenOnlyWindow is not null && (args.Contains("--wait-for-foreground", StringComparer.Ordinal)
            ? InteractiveDesktopGuard.WaitForTargetForegroundAtStartup((IntPtr)reopenOnlyWindow.Current.NativeWindowHandle, TimeSpan.FromSeconds(180))
            : InteractiveDesktopGuard.ActivateTargetOnceAtStartup((IntPtr)reopenOnlyWindow.Current.NativeWindowHandle));
        var reopenOnlyTitle = reopenOnlyStartupReady && reopenOnlyWindow is not null
            ? WaitForProjectTitle(reopenOnlyWindow, TimeSpan.FromSeconds(15)) : null;
        var actualState = JsonNode.Parse(File.ReadAllText(projectCopyFile))?.AsObject();
        var reopenOnlyValues = actualState?["settings"]?.ToJsonString() == reopenOnlyExpectedSettings
            && actualState?["camera"]?.ToJsonString() == reopenOnlyExpectedCamera
            && actualState?["mouseClock"]?.ToJsonString() == reopenOnlyExpectedMouseClock
            && ReadArrayCount(projectCopyFile, "screenMotions") == expectedScreenMotions;
        var reopenOnlyOkay = saveCompleted && firstExited && reopenOnlyStartupReady
            && reopenOnlyWindow is not null && reopenOnlyTitle is not null && reopenOnlyValues;
        AddResult(new("保存项目重开并保留面板更改", reopenOnlyOkay,
            $"重开窗口={reopenOnlyWindow is not null}，启动守卫={reopenOnlyStartupReady}，标题={reopenOnlyTitle?.Current.Name ?? "未加载"}，配置/摄像头/鼠标时钟一致={reopenOnlyValues}，屏幕运镜数={ReadArrayCount(projectCopyFile, "screenMotions")}。"));
        if (app is not null && !app.HasExited)
        {
            var finalCloseRequested = app.CloseMainWindow();
            var finalExited = finalCloseRequested && app.WaitForExit(10000);
            AddResult(new("重开实例正常退出", finalExited,
                $"CloseMainWindow={finalCloseRequested}，正常退出={finalExited}，PID={app.Id}。"));
            if (finalExited) { app.Dispose(); app = null; }
        }
        throw new SmokeEarlyExitException();
    }
    if (args.Contains("--timeline-shortcuts-only"))
    {
        RunTimelineShortcutUiTests(window, projectCopyFile, results);
        var closeSent = app.CloseMainWindow();
        var exitedNormally = closeSent && app.WaitForExit(10000);
        AddResult(new("编辑与逐帧操作后正常关闭", exitedNormally,
            exitedNormally ? "应用完成预览取消与资源释放后自行退出，未强制终止。" : "关闭请求后应用未在10秒内自行退出。"));
        if (exitedNormally) { app.Dispose(); app = null; }
        throw new SmokeEarlyExitException();
    }
    if (args.Contains("--presets-only", StringComparer.Ordinal))
    {
        var presetLifecyclePassed = TestCustomPresetLifecycle(window, app.Id, projectCopyFile, results);
        if (presetLifecyclePassed)
        {
            app.CloseMainWindow();
            if (!app.WaitForExit(5000)) app.Kill(entireProcessTree: true);
            app.Dispose(); app = null;
        }
        throw new SmokeEarlyExitException();
    }
    if (args.Contains("--effects-only"))
    {
        RunEffectUiTests(window, projectCopyFile, args, results);
        app.CloseMainWindow();
        if (!app.WaitForExit(5000)) app.Kill(entireProcessTree: true);
        app.Dispose(); app = null;
        throw new SmokeEarlyExitException();
    }
    if (args.Contains("--new-panels-only"))
    {
        RunNewPanelUiTests(window, app, appPath, projectCopyFile, results);
        app.CloseMainWindow();
        if (!app.WaitForExit(5000)) app.Kill(entireProcessTree: true);
        app.Dispose(); app = null;
        throw new SmokeEarlyExitException();
    }
    if (args.Contains("--motion-curves-only"))
    {
        RunMotionCurveUiTests(window, app, projectCopyFile, results);
        app.CloseMainWindow();
        if (!app.WaitForExit(5000)) app.Kill(entireProcessTree: true);
        app.Dispose(); app = null;
        using var reopened = Process.Start(new ProcessStartInfo(appPath) { UseShellExecute = false, ArgumentList = { "--open", projectCopyFile } });
        var curveReopenedWindow = reopened is null ? null : WaitForWindow(reopened, TimeSpan.FromSeconds(20));
        var curveReopenedTitle = curveReopenedWindow is null ? null : WaitForProjectTitle(curveReopenedWindow, TimeSpan.FromSeconds(10));
        var persisted = curveReopenedWindow is not null && curveReopenedTitle is not null
            && Math.Abs(ReadJsonArrayNumber(projectCopyFile, "zoomClips", 0, "customCurve", "x1") - .42) < .015
            && Math.Abs(ReadJsonArrayNumber(projectCopyFile, "screenMotions", 0, "customCurve", "y2") - .63) < .015
            && Math.Abs(ReadJsonArrayNumber(projectCopyFile, "cameraMotions", ReadArrayCount(projectCopyFile, "cameraMotions") - 1, "customCurve", "x2") - .58) < .015
            && Math.Abs(ReadJsonNumber(projectCopyFile, "settings", "spring", "mass") - 2.7) < .015
            && ReadJsonArrayString(projectCopyFile, "zoomClips", 0, "easing") == "spring"
            && ReadJsonArrayString(projectCopyFile, "screenMotions", 0, "easing") == "custom"
            && ReadJsonArrayString(projectCopyFile, "cameraMotions", ReadArrayCount(projectCopyFile, "cameraMotions") - 1, "easing") == "custom"
            && ReadJsonString(projectCopyFile, "settings", "opening", "preset") == "light3D"
            && ReadJsonString(projectCopyFile, "settings", "opening", "elementCurve") == "gentle"
            && ReadJsonString(projectCopyFile, "settings", "cursor", "clickEffect") == "ripple"
            && Math.Abs(ReadJsonNumber(projectCopyFile, "settings", "cursor", "clickDuration") - .84) < .015
            && Math.Abs(ReadJsonNumber(projectCopyFile, "settings", "cursor", "clickScale") - 1.7) < .015
            && Math.Abs(ReadJsonNumber(projectCopyFile, "settings", "cursor", "clickOpacity") - .68) < .015
            && ReadJsonBoolean(projectCopyFile, "settings", "cursor", "motionTiltEnabled") == true
            && ReadTimelineSegmentRate(projectCopyFile, 0) == .5
            && ReadJsonString(projectCopyFile, "settings", "cursor", "style") == "followRecorded";
        AddResult(new("曲线与开场/光标参数保存后重开", persisted,
            $"重开窗口={curveReopenedWindow is not null}，缩放X1={ReadJsonArrayNumber(projectCopyFile, "zoomClips", 0, "customCurve", "x1"):0.###}，屏幕3D Y2={ReadJsonArrayNumber(projectCopyFile, "screenMotions", 0, "customCurve", "y2"):0.###}，摄像运动X2={ReadJsonArrayNumber(projectCopyFile, "cameraMotions", ReadArrayCount(projectCopyFile, "cameraMotions") - 1, "customCurve", "x2"):0.###}，弹簧质量={ReadJsonNumber(projectCopyFile, "settings", "spring", "mass"):0.###}，慢放={ReadTimelineSegmentRate(projectCopyFile, 0):0.##}×，光标={ReadJsonString(projectCopyFile, "settings", "cursor", "style")}，开场={ReadJsonString(projectCopyFile, "settings", "opening", "preset")}/{ReadJsonString(projectCopyFile, "settings", "opening", "elementCurve")}，点击效果={ReadJsonString(projectCopyFile, "settings", "cursor", "clickEffect")}。"));
        if (reopened is not null) { reopened.CloseMainWindow(); if (!reopened.WaitForExit(5000)) reopened.Kill(entireProcessTree: true); }
        throw new SmokeEarlyExitException();
    }
    if (args.Contains("--advanced-styles-only"))
    {
        RunAdvancedStyleUiTests(window, app.Id, projectCopyFile, results);
        app.CloseMainWindow();
        if (!app.WaitForExit(5000)) app.Kill(entireProcessTree: true);
        app.Dispose(); app = null;
        using var reopened = Process.Start(new ProcessStartInfo(appPath) { UseShellExecute = false, ArgumentList = { "--open", projectCopyFile } });
        var advancedReopenedWindow = reopened is null ? null : WaitForWindow(reopened, TimeSpan.FromSeconds(20));
        var advancedReopenedTitle = advancedReopenedWindow is null ? null : WaitForProjectTitle(advancedReopenedWindow, TimeSpan.FromSeconds(10));
        var focusSaved = ReadJsonArrayString(projectCopyFile, "zoomClips", ReadArrayCount(projectCopyFile, "zoomClips") - 1, "focusEffect", "shape") == "linear"
            && ReadJsonArrayString(projectCopyFile, "zoomClips", ReadArrayCount(projectCopyFile, "zoomClips") - 1, "focusEffect", "target") == "fixed"
            && Math.Abs(ReadJsonArrayNumber(projectCopyFile, "zoomClips", ReadArrayCount(projectCopyFile, "zoomClips") - 1, "focusEffect", "angleDegrees") - 27) < .2;
        var cameraIndex = ReadArrayCount(projectCopyFile, "cameraMotions") - 1;
        var cameraMotionSaved = ReadJsonArrayString(projectCopyFile, "cameraMotions", cameraIndex, "targetLayout") == "horizontal"
            && Math.Abs(ReadJsonArrayNumber(projectCopyFile, "cameraMotions", cameraIndex, "targetRoundness") - .63) < .015
            && Math.Abs(ReadJsonArrayNumber(projectCopyFile, "cameraMotions", cameraIndex, "targetOpacity") - .47) < .015;
        var cameraStyleSaved = Math.Abs(ReadJsonNumber(projectCopyFile, "camera", "scaleDuringZoom") - 1.4) < .025
            && Math.Abs(ReadJsonNumber(projectCopyFile, "camera", "shadowStrength") - .56) < .015;
        var overlaySaved = ReadJsonArrayNumber(projectCopyFile, "overlays", 0, "borderWidth") >= 4.9
            && Math.Abs(ReadJsonArrayNumber(projectCopyFile, "overlays", 0, "shadowOpacity") - .42) < .015
            && Math.Abs(ReadJsonArrayNumber(projectCopyFile, "overlays", 0, "shadowRadius") - 31) < .2
            && ReadJsonArrayString(projectCopyFile, "overlays", 0, "animationCurve") == "gentle"
            && Math.Abs(ReadJsonArrayNumber(projectCopyFile, "overlays", 0, "backdropBlur") - 37) < .2
            && ReadJsonArrayBoolean(projectCopyFile, "overlays", 0, "backdropBlurIncludesCamera")
            && ReadJsonArrayBoolean(projectCopyFile, "overlays", 0, "hidesScreen")
            && ReadJsonArrayBoolean(projectCopyFile, "overlays", 0, "hidesCamera");
        var reopenedSaved = advancedReopenedWindow is not null && advancedReopenedTitle is not null && focusSaved && cameraMotionSaved && cameraStyleSaved && overlaySaved;
        AddResult(new("ZoomFocus、摄像头目标与贴图样式保存后重开", reopenedSaved,
            $"窗口={advancedReopenedWindow is not null}，Focus={ReadJsonArrayString(projectCopyFile, "zoomClips", ReadArrayCount(projectCopyFile, "zoomClips") - 1, "focusEffect", "shape")}/{ReadJsonArrayString(projectCopyFile, "zoomClips", ReadArrayCount(projectCopyFile, "zoomClips") - 1, "focusEffect", "target")}/角度{ReadJsonArrayNumber(projectCopyFile, "zoomClips", ReadArrayCount(projectCopyFile, "zoomClips") - 1, "focusEffect", "angleDegrees"):0.##}，摄像头={ReadJsonArrayString(projectCopyFile, "cameraMotions", cameraIndex, "targetLayout")}/圆角{ReadJsonArrayNumber(projectCopyFile, "cameraMotions", cameraIndex, "targetRoundness"):0.##}/透明度{ReadJsonArrayNumber(projectCopyFile, "cameraMotions", cameraIndex, "targetOpacity"):0.##}，贴图描边{ReadJsonArrayNumber(projectCopyFile, "overlays", 0, "borderWidth"):0.##}/阴影{ReadJsonArrayNumber(projectCopyFile, "overlays", 0, "shadowOpacity"):0.##},{ReadJsonArrayNumber(projectCopyFile, "overlays", 0, "shadowRadius"):0.##}/曲线{ReadJsonArrayString(projectCopyFile, "overlays", 0, "animationCurve")}。"));
        if (reopened is not null) { reopened.CloseMainWindow(); if (!reopened.WaitForExit(5000)) reopened.Kill(entireProcessTree: true); }
        throw new SmokeEarlyExitException();
    }
    if (args.Contains("--spotlight-audio-only"))
    {
        RunSpotlightAndSegmentAudioUiTests(window, app.Id, projectCopyFile, results);
        app.CloseMainWindow();
        if (!app.WaitForExit(5000)) app.Kill(entireProcessTree: true);
        app.Dispose(); app = null;
        using var reopened = Process.Start(new ProcessStartInfo(appPath) { UseShellExecute = false, ArgumentList = { "--open", projectCopyFile } });
        var spotlightReopenedWindow = reopened is null ? null : WaitForWindow(reopened, TimeSpan.FromSeconds(20));
        var spotlightReopenedTitle = spotlightReopenedWindow is null ? null : WaitForProjectTitle(spotlightReopenedWindow, TimeSpan.FromSeconds(10));
        var segmentVolume = ReadTimelineSegmentSystemVolume(projectCopyFile, 0);
        var persisted = spotlightReopenedWindow is not null && spotlightReopenedTitle is not null
            && ReadJsonArrayString(projectCopyFile, "blurClips", ReadArrayCount(projectCopyFile, "blurClips") - 1, "kind") == "spotlight"
            && Math.Abs(ReadJsonArrayNumber(projectCopyFile, "blurClips", ReadArrayCount(projectCopyFile, "blurClips") - 1, "spotlightDimming") - .37) < .015
            && ReadJsonArrayString(projectCopyFile, "blurClips", ReadArrayCount(projectCopyFile, "blurClips") - 1, "transitionStyle") == "smooth"
            && Math.Abs(ReadJsonArrayNumber(projectCopyFile, "blurClips", ReadArrayCount(projectCopyFile, "blurClips") - 1, "enterDuration") - .4) < .015
            && Math.Abs(ReadJsonArrayNumber(projectCopyFile, "blurClips", ReadArrayCount(projectCopyFile, "blurClips") - 1, "exitDuration") - .25) < .015
            && Math.Abs(segmentVolume - .32) < .015;
        AddResult(new("聚光与屏幕片段音量覆盖保存后重开", persisted,
            $"窗口={spotlightReopenedWindow is not null}，聚光={ReadJsonArrayString(projectCopyFile, "blurClips", ReadArrayCount(projectCopyFile, "blurClips") - 1, "kind")}，区域外压暗={ReadJsonArrayNumber(projectCopyFile, "blurClips", ReadArrayCount(projectCopyFile, "blurClips") - 1, "spotlightDimming"):0.##}，转场={ReadJsonArrayString(projectCopyFile, "blurClips", ReadArrayCount(projectCopyFile, "blurClips") - 1, "transitionStyle")}，进/出={ReadJsonArrayNumber(projectCopyFile, "blurClips", ReadArrayCount(projectCopyFile, "blurClips") - 1, "enterDuration"):0.##}/{ReadJsonArrayNumber(projectCopyFile, "blurClips", ReadArrayCount(projectCopyFile, "blurClips") - 1, "exitDuration"):0.##}，第 1 屏幕片段系统声音={segmentVolume:0.##}。"));
        if (reopened is not null) { reopened.CloseMainWindow(); if (!reopened.WaitForExit(5000)) reopened.Kill(entireProcessTree: true); }
        throw new SmokeEarlyExitException();
    }
    if (args.Contains("--memo-only"))
    {
        TestMemoPersistence(window, app.Id, results);
        throw new SmokeEarlyExitException();
    }
    if (args.Contains("--motion-screenshot"))
    {
        var motionButton = FindRailButton(window, "运镜");
        var openedMotion = motionButton is not null && TryClickPhysical(motionButton);
        var screenButton = openedMotion ? FindButtonByHint(window, "屏幕 3D") : null;
        var openedScreen = screenButton is not null && TryClickPhysical(screenButton);
        var shot = Path.ChangeExtension(reportPath, ".motion.png");
        if (openedScreen) CaptureWindow(window, shot);
        AddResult(new("运镜 3D 面板视觉截图", openedScreen && File.Exists(shot), $"运镜={openedMotion}，屏幕3D={openedScreen}，截图={shot}。"));
        app.CloseMainWindow();
        if (!app.WaitForExit(5000)) app.Kill(entireProcessTree: true);
        app.Dispose(); app = null;
        throw new SmokeEarlyExitException();
    }
    if (args.Contains("--timeline-probe"))
    {
        var timelineProbe = FindById(window, "TimelineView");
        var ancestry = new List<string>();
        var node = timelineProbe;
        ScrollPattern? scroll = null;
        for (var depth = 0; node is not null && depth < 12; depth++)
        {
            var rect = node.Current.BoundingRectangle;
            var item = $"{node.Current.ControlType.ProgrammaticName} name='{node.Current.Name}' id='{node.Current.AutomationId}' bounds=({rect.Left:0},{rect.Top:0},{rect.Width:0},{rect.Height:0})";
            try
            {
                if (node.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern) && pattern is ScrollPattern found)
                {
                    scroll = found; item += $" scroll=({scroll.Current.VerticalScrollPercent:0.##}%,view={scroll.Current.VerticalViewSize:0.##}%)";
                }
            }
            catch { }
            ancestry.Add(item);
            node = TreeWalker.ControlViewWalker.GetParent(node);
        }
        if (scroll is not null)
        {
            try
            {
                if (InteractiveDesktopGuard.BeforeInput("UIA Scroll"))
                    scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement);
                Thread.Sleep(250);
                if (timelineProbe is not null) ancestry.Add($"after scroll bounds={timelineProbe.Current.BoundingRectangle} scroll={scroll.Current.VerticalScrollPercent:0.##}%");
            }
            catch (Exception ex) { ancestry.Add("scroll test=" + ex.GetType().Name + ":" + ex.Message); }
        }
        Console.WriteLine("TIMELINE_PROBE=" + string.Join(Environment.NewLine, ancestry));
        throw new SmokeEarlyExitException();
    }
    if (WaitForFrameTrailVisualReady(window, TimeSpan.FromSeconds(90), out var screenshotReadyDetail))
    {
        var screenshotPath = Path.ChangeExtension(reportPath, ".png");
        CaptureWindow(window, screenshotPath);
        AddResult(new("主窗口实际截图已保存", File.Exists(screenshotPath), screenshotPath + "；" + screenshotReadyDetail));
    }
    else
    {
        SmokeProgress.MarkIncomplete("预览或时间线尚未报告ready；跳过主窗口截图。" + screenshotReadyDetail);
    }

    var cameraTab = FindRailButton(window, "摄像头");
    var cameraOpened = cameraTab is not null && TryClickPhysical(cameraTab);
    var circleCard = cameraOpened ? FindButtonByHint(window, "圆形") : null;
    var cameraCombos = cameraOpened ? FindInspectorCombos(window) : [];
    var circleSelected = circleCard is not null && TryClickPhysical(circleCard);
    var cameraSaved = circleSelected && WaitForJsonString(projectCopyFile, ["camera", "shape"], "circle", TimeSpan.FromSeconds(5));
    AddResult(new("摄像头面板可编辑并保存形状", cameraSaved, cameraSaved ? "通过圆形卡片将摄像头形状写入项目副本。" : $"面板={cameraOpened}，旧式下拉数={cameraCombos.Length}，圆形卡片={circleCard is not null}，点击={circleSelected}，已保存形状={ReadJsonString(projectCopyFile, "camera", "shape")}，控件=[{DescribeInspector(window)}]"));
    var cameraDragDetail = "摄像头设置未完成，跳过画布拖动。";
    var cameraDrag = cameraSaved && TryDragCameraOnCanvas(window, projectCopyFile, out cameraDragDetail);
    AddResult(new("预览画布拖动摄像头并持久化位置", cameraDrag, cameraSaved ? cameraDragDetail : "摄像头设置未完成，跳过画布拖动。"));

    if (!TestCustomPresetLifecycle(window, app.Id, projectCopyFile, results))
    {
        if (InteractiveDesktopGuard.IsAborted) throw new SmokeEarlyExitException();
        throw new InvalidOperationException("场景预设交互未完整结束；已停止后续 UIA 步骤，避免在未关闭的模态窗口上级联误报。");
    }
    window = WaitForMainWindow(app.Id, TimeSpan.FromSeconds(3)) ?? window;

    var frameTab = FindRailButton(window, "画面");
    var frameOpened = frameTab is not null && TryClickPhysical(frameTab);
    var backgroundTab = frameOpened ? FindRightPanelButton(window, "背景") : null;
    var backgroundClicked = backgroundTab is not null && TryClickPhysical(backgroundTab);
    var backgroundOpened = backgroundTab is not null && backgroundClicked;
    var backgroundCombos = backgroundOpened ? FindInspectorCombos(window) : [];
    var patternCard = backgroundOpened ? FindButtonByHint(window, "图案") : null;
    var patternSourceSelected = patternCard is not null && TryClickPhysical(patternCard);
    var gridCard = patternSourceSelected ? FindButtonByHint(window, "细网格") : null;
    var patternSelected = gridCard is not null && TryClickPhysical(gridCard);
    var backgroundSaved = patternSelected && WaitForJsonString(projectCopyFile, ["settings", "backgroundStyle", "kind"], "pattern", TimeSpan.FromSeconds(5));
    AddResult(new("背景面板可编辑并保存图案", backgroundSaved, backgroundSaved ? "依次选择图案与细网格卡片，项目已保存。" : $"画面页={frameOpened}，背景按钮存在={backgroundTab is not null}，名称={backgroundTab?.Current.Name}，点击={backgroundClicked}，图案卡片={patternCard is not null}，细网格卡片={gridCard is not null}，旧式下拉数={backgroundCombos.Length}，类型={ReadJsonString(projectCopyFile, "settings", "backgroundStyle", "kind")}，控件=[{DescribeInspector(window)}]"));

    var mockupTab = FindRightPanelButton(window, "样机");
    var mockupTabOpened = mockupTab is not null && TryClickPhysical(mockupTab);
    var windowFrameCard = mockupTabOpened ? FindButtonByHint(window, "浅色窗口") : null;
    var windowFrameSelected = windowFrameCard is not null && TryClickPhysical(windowFrameCard)
        && WaitForJsonString(projectCopyFile, ["settings", "canvas", "frame"], "windowLight", TimeSpan.FromSeconds(5));
    var frameScaleSlider = windowFrameSelected ? WaitForControlName(window, ControlType.Slider, "框体比例", TimeSpan.FromSeconds(3)) : null;
    var frameScaleSet = frameScaleSlider is not null && SetRangeValue(frameScaleSlider, 1.12)
        && WaitForJsonNumber(projectCopyFile, ["settings", "canvas", "frameScale"], 1.12, TimeSpan.FromSeconds(5));
    var toolbarSlider = frameScaleSet ? WaitForControlName(window, ControlType.Slider, "顶栏高度", TimeSpan.FromSeconds(3)) : null;
    var toolbarSet = toolbarSlider is not null && SetRangeValue(toolbarSlider, 1.08)
        && WaitForJsonNumber(projectCopyFile, ["settings", "canvas", "frameToolbarScale"], 1.08, TimeSpan.FromSeconds(5));
    var radiusSlider = toolbarSet ? WaitForControlName(window, ControlType.Slider, "框体外圆角", TimeSpan.FromSeconds(3)) : null;
    var radiusSet = radiusSlider is not null && SetRangeValue(radiusSlider, 26)
        && WaitForJsonNumber(projectCopyFile, ["settings", "canvas", "frameOuterRadius"], 26, TimeSpan.FromSeconds(5));
    var mockupSaved = mockupTabOpened && windowFrameSelected && frameScaleSet && toolbarSet && radiusSet;
    AddResult(new("样机卡片与框体参数可保存", mockupSaved, mockupSaved ? "选择浅色窗口卡片并设置框体比例1.12、顶栏高度1.08、外圆角26，三项均写入项目。" : $"样机页={mockupTabOpened}，卡片={windowFrameSelected}，比例={frameScaleSet}，顶栏={toolbarSet}，外圆角={radiusSet}，值=({ReadJsonNumber(projectCopyFile, ["settings", "canvas", "frameScale"])},{ReadJsonNumber(projectCopyFile, ["settings", "canvas", "frameToolbarScale"])},{ReadJsonNumber(projectCopyFile, ["settings", "canvas", "frameOuterRadius"])})。"));

    var cursorTab = FindRailButton(window, "光标");
    var cursorOpened = cursorTab is not null && TryClickPhysical(cursorTab);
    var cursorCombos = cursorOpened ? FindInspectorCombos(window) : [];
    var ringCard = cursorOpened ? FindButtonByHint(window, "圆环") : null;
    var ringSelected = ringCard is not null && TryClickPhysical(ringCard);
    var cursorSaved = ringSelected && WaitForJsonString(projectCopyFile, ["settings", "cursor", "style"], "ring", TimeSpan.FromSeconds(5));
    AddResult(new("光标面板可编辑并保存样式", cursorSaved, cursorSaved ? "通过圆环卡片将光标样式写入项目副本。" : $"面板={cursorOpened}，旧式下拉数={cursorCombos.Length}，圆环卡片={ringCard is not null}，点击={ringSelected}，样式={ReadJsonString(projectCopyFile, "settings", "cursor", "style")}，控件=[{DescribeInspector(window)}]"));
    var smoothingCombo = cursorSaved ? WaitForComboByName(window, "移动平滑", TimeSpan.FromSeconds(3)) : null;
    var smoothingSelected = smoothingCombo is not null && SelectComboOption(smoothingCombo, "灵敏", app.Id, 2);
    var smoothingSaved = smoothingSelected && WaitForJsonString(projectCopyFile, ["settings", "pointerSmoothing"], "rapid", TimeSpan.FromSeconds(5));
    AddResult(new("光标移动平滑设置持久化", smoothingSaved, smoothingSaved ? "在光标面板将移动平滑设为灵敏，项目 JSON 保存为 rapid。" : $"控件={smoothingCombo is not null}，选择={smoothingSelected}，项目值={ReadJsonString(projectCopyFile, "settings", "pointerSmoothing")}。"));
    var alignmentDisclosure = cursorSaved ? FindNamed(window, "时间对齐") : null;
    var alignmentExpanded = alignmentDisclosure is not null && TryExpand(alignmentDisclosure);
    var offsetControl = alignmentExpanded ? WaitForControlName(window, ControlType.Slider, "光标时间偏移", TimeSpan.FromSeconds(3)) : null;
    var offsetChanged = offsetControl is not null && SetRangeValue(offsetControl, 10);
    var offsetSaved = offsetChanged && WaitForJsonNumber(projectCopyFile, ["mouseClock", "offsetSeconds"], .01, TimeSpan.FromSeconds(5));
    var offsetControlRemains = offsetSaved && WaitForControlName(window, ControlType.Slider, "光标时间偏移", TimeSpan.FromSeconds(2)) is not null;
    AddResult(new("展开光标时间对齐并保存偏移", offsetSaved && offsetControlRemains, offsetSaved && offsetControlRemains ? "展开折叠分组后将光标时间偏移设为 10 ms；Inspector 重建后分组仍展开，项目 JSON 保存为 0.01 秒。" : $"分组={alignmentExpanded}，控件={Describe(offsetControl)}，重建后仍可见={offsetControlRemains}，JSON偏移={ReadJsonNumber(projectCopyFile, ["mouseClock", "offsetSeconds"])}"));

    var openingTab = FindRailButton(window, "开场");
    var openingOpened = openingTab is not null && TryClickPhysical(openingTab);
    var openingCombos = openingOpened ? FindInspectorCombos(window) : [];
    var openingEnabled = openingOpened ? FindCheckboxContaining(window, "启用全局开场") : null;
    var openingWasEnabled = ReadJsonBoolean(projectCopyFile, "settings", "opening", "enabled") == true;
    var openingToggleClicked = openingEnabled is not null
        && (openingWasEnabled || TryClickPhysical(openingEnabled, preferAutomationInvoke: false));
    var openingToggleChanged = openingToggleClicked
        && WaitForJsonBoolean(projectCopyFile, ["settings", "opening", "enabled"], true, TimeSpan.FromSeconds(5));
    window = WaitForMainWindow(app.Id, TimeSpan.FromSeconds(3)) ?? window;
    var openingCustomDisclosure = openingToggleChanged ? FindNamed(window, "自定开场与起始姿态") : null;
    var openingCustomExpanded = openingCustomDisclosure is not null && TryExpand(openingCustomDisclosure);
    window = WaitForMainWindow(app.Id, TimeSpan.FromSeconds(3)) ?? window;
    var zoomOpeningCard = openingCustomExpanded ? FindButtonByHint(window, "放大入场") : null;
    var openingPresetSelected = openingCustomExpanded && zoomOpeningCard is not null && TryClickPhysical(zoomOpeningCard);
    var openingSaved = openingPresetSelected && openingToggleChanged && WaitForJsonBoolean(projectCopyFile, ["settings", "opening", "enabled"], true, TimeSpan.FromSeconds(5)) && WaitForJsonString(projectCopyFile, ["settings", "opening", "preset"], "zoomUp", TimeSpan.FromSeconds(5));
    AddResult(new("开场面板可编辑并保存动画", openingSaved, openingSaved ? "启用全局开场后选择放大入场卡片并写入项目副本。" : $"面板={openingOpened}，下拉数={openingCombos.Length}，预设={openingPresetSelected}，开关={openingToggleChanged}，enabled={ReadJsonBoolean(projectCopyFile, "settings", "opening", "enabled")}, preset={ReadJsonString(projectCopyFile, "settings", "opening", "preset")}，控件=[{DescribeInspector(window)}]"));

    var motionTabButton = FindRailButton(window, "运镜");
    var motionPanel = motionTabButton is not null && TryClickPhysical(motionTabButton);
    var screen3DTab = motionPanel ? FindButtonByHint(window, "屏幕 3D") : null;
    var screen3DOpened = screen3DTab is not null && TryClickPhysical(screen3DTab);
    var oldMotionCount = ReadArrayCount(projectCopyFile, "screenMotions");
    var addElement = FindButtonByHint(window, "添加元素");
    var addElementOpened = screen3DOpened && addElement is not null && TryInvoke(addElement);
    var addMotionMenuItem = addElementOpened ? WaitForName("添加运镜片段", app.Id, TimeSpan.FromSeconds(3)) : null;
    var motionAdded = addMotionMenuItem is not null && TryInvoke(addMotionMenuItem) && WaitForArrayCount(projectCopyFile, "screenMotions", Math.Max(0, oldMotionCount) + 1, TimeSpan.FromSeconds(6));
    var motionUndo = false; var motionRedo = false; var motionCleanup = false;
    if (motionAdded)
    {
        FocusWindow(window); SendKey(0x5A, true);
        motionUndo = WaitForArrayCount(projectCopyFile, "screenMotions", oldMotionCount, TimeSpan.FromSeconds(5));
        SendKey(0x59, true);
        motionRedo = WaitForArrayCount(projectCopyFile, "screenMotions", oldMotionCount + 1, TimeSpan.FromSeconds(5));
        SendKey(0x5A, true);
        motionCleanup = WaitForArrayCount(projectCopyFile, "screenMotions", oldMotionCount, TimeSpan.FromSeconds(5));
    }
    AddResult(new("屏幕 3D 片段创建、撤销、重做", motionAdded && motionUndo && motionRedo && motionCleanup,
        $"创建={motionAdded}，撤销={motionUndo}，重做={motionRedo}，清理后数量={ReadArrayCount(projectCopyFile, "screenMotions")}；原数量={oldMotionCount}"));

    var projectMenuButton = FindButtonByHint(window, "项目");
    var projectMenuOpened = projectMenuButton is not null && TryInvoke(projectMenuButton);
    var dogScMenuItem = projectMenuOpened ? WaitForName("导入 DogSC 项目…", app.Id, TimeSpan.FromSeconds(3)) : null;
    AddResult(new("项目菜单及 DogSC 导入入口可访问", dogScMenuItem is not null, dogScMenuItem is null ? "未能从 UIA 菜单找到 DogSC 导入入口。" : "打开项目菜单后发现 DogSC 导入命令。"));
    if (projectMenuOpened) _ = CloseOwnedTransientPopups(app.Id, window);

    var aspectLabel = FindById(window, "AspectLabel");
    var aspectButton = aspectLabel is null ? null : AncestorOfType(aspectLabel, ControlType.Button);
    AddResult(new("画布比例菜单可访问", aspectButton is not null, Describe(aspectLabel)));
    var before = ReadDimensions(projectCopyFile);
    if (aspectButton is not null && TryInvoke(aspectButton))
    {
        var ratio = WaitForName("9:16", app.Id, TimeSpan.FromSeconds(4));
        var selected = ratio is not null && TryInvoke(ratio);
        var changed = selected && WaitForDimensions(projectCopyFile, 9, 16, TimeSpan.FromSeconds(8));
        AddResult(new("比例菜单调整并自动保存", changed, changed ? "画布设置已写入项目副本。" : "未能在 UIA 菜单中选择 9:16 或检测到项目文件更新。"));

        if (selected)
        {
            FocusWindow(window);
            SendKey(0x5A, true); // Ctrl+Z
            var undo = WaitForDimensions(projectCopyFile, before.Width, before.Height, TimeSpan.FromSeconds(6));
            AddResult(new("撤销比例设置", undo, undo ? "Ctrl+Z 恢复了原画布尺寸。" : "撤销后项目尺寸未恢复。"));
            SendKey(0x59, true); // Ctrl+Y
            var redo = WaitForDimensions(projectCopyFile, 9, 16, TimeSpan.FromSeconds(6));
            AddResult(new("重做比例设置", redo, redo ? "Ctrl+Y 重新应用了画布比例。" : "重做后项目尺寸未更新。"));
        }
    }
    else AddResult(new("比例菜单调整并自动保存", false, "无法通过 UI Automation 打开画布比例菜单。"));

    var presetButton = FindButtonByHint(window, "场景预设");
    var presetOpened = presetButton is not null && TryClickPhysical(presetButton);
    var darkPreset = presetOpened ? WaitForName("深色窗口", app.Id, TimeSpan.FromSeconds(3)) : null;
    var initialBackground = ReadBackground(projectCopyFile);
    var presetInvoked = darkPreset is not null && TryInvoke(darkPreset);
    var presetApplied = presetInvoked && WaitForBackground(projectCopyFile, "#202124", TimeSpan.FromSeconds(6));
    var finalBackground = ReadBackground(projectCopyFile);
    AddResult(new("场景预设菜单可应用", presetApplied, presetApplied ? "已应用内建深色窗口样式。" : $"按钮名={presetButton?.Current.Name}，物理点击={presetOpened}，找到菜单项={darkPreset is not null}，触发={presetInvoked}，原背景={initialBackground}，当前背景={finalBackground}；进程节点=[{string.Join(" | ", FindProcessNames(app.Id).Where(x => x.Contains("深色") || x.Contains("预设") || x.Contains("简洁") || x.Contains("浏览器")).Take(12))}]"));
    if (presetApplied)
    {
        FocusWindow(window); SendKey(0x5A, true);
        var presetUndo = WaitForBackground(projectCopyFile, initialBackground, TimeSpan.FromSeconds(5));
        AddResult(new("预设操作支持撤销", presetUndo, presetUndo ? "撤销恢复了预设前的背景颜色。" : "撤销没有恢复原背景颜色。"));
        SendKey(0x59, true);
    }

    var addElementButton = FindButtonByHint(window, "添加元素");
    var addMenuOpened = addElementButton is not null && TryInvoke(addElementButton);
    var blurItem = addMenuOpened ? WaitForName("添加模糊遮罩", app.Id, TimeSpan.FromSeconds(3)) : null;
    var oldBlurCount = ReadArrayCount(projectCopyFile, "blurClips");
    var blurAdded = blurItem is not null && TryInvoke(blurItem) && WaitForArrayCount(projectCopyFile, "blurClips", oldBlurCount + 1, TimeSpan.FromSeconds(6));
    AddResult(new("添加元素菜单创建遮罩轨道", blurAdded, blurAdded ? "已从菜单创建模糊遮罩并写入项目。" : "遮罩命令未创建可保存的遮罩片段。"));
    if (blurAdded)
    {
        FocusWindow(window); SendKey(0x5A, true);
        var blurUndo = WaitForArrayCount(projectCopyFile, "blurClips", oldBlurCount, TimeSpan.FromSeconds(5));
        AddResult(new("遮罩轨道支持撤销", blurUndo, blurUndo ? "撤销移除了刚创建的遮罩片段。" : "撤销没有移除新遮罩片段。"));
        SendKey(0x59, true);
        var blurRedo = WaitForArrayCount(projectCopyFile, "blurClips", oldBlurCount + 1, TimeSpan.FromSeconds(5));
        SendKey(0x5A, true);
        var blurCleanup = WaitForArrayCount(projectCopyFile, "blurClips", oldBlurCount, TimeSpan.FromSeconds(5));
        AddStep(results, new("遮罩片段重做后恢复测试夹具", blurRedo && blurCleanup,
            $"重做={blurRedo}，最终数量={ReadArrayCount(projectCopyFile, "blurClips")}，原数量={oldBlurCount}"));
    }

    var timeline = FindById(window, "TimelineView");
    var timelineDetail = "未找到 TimelineView。";
    var timelineGesture = timeline is not null && TryMoveFirstZoom(timeline, window, projectCopyFile, app.Id, out timelineDetail);
    AddResult(new("时间线多轨片段拖动", timelineGesture, timeline is null ? "未找到 TimelineView。" : timelineGesture ? "拖动缩放片段后，片段源时间已更新。" : timelineDetail));
    var effectPasteDetail = "未找到 TimelineView。";
    var effectPastePassed = timeline is not null && TryPasteZoomAttributes(timeline, window, projectCopyFile, out effectPasteDetail);
    AddResult(new("效果属性复制粘贴保留目标时间", effectPastePassed, effectPasteDetail));
    var groupDeleteDetail = "未找到 TimelineView。";
    var groupDeletePassed = timeline is not null && TryDeleteZoomGroup(timeline, window, projectCopyFile, out groupDeleteDetail);
    AddResult(new("时间线多选后成组删除并撤销", groupDeletePassed, groupDeleteDetail));

    foreach (var effect in new[]
    {
        ("screenMotions", "屏幕 3D", "target.position.x"),
        ("cameraMotions", "摄像运动", "targetPosition.x"),
        ("overlays", "贴图", "opacity"),
        ("blurClips", "柔化", "strength")
    })
    {
        var detail = timeline is null ? "未找到时间线。" : "";
        var passed = timeline is not null && TryCopyPasteEffectType(timeline, window, projectCopyFile, effect.Item1, effect.Item3, out detail);
        AddResult(new($"{effect.Item2}完整复制与属性粘贴", passed, detail));
    }
    var rangeDetail = "未找到 TimelineView。";
    var rangePassed = timeline is not null && TryExportRangeFromTimeline(timeline, window, projectCopyFile, app.Id, out rangeDetail);
    AddResult(new("I/O 导出范围影响导出时长", rangePassed, rangeDetail));

    var exportButton = FindById(window, "ExportButton");
    var exportInvoked = exportButton is not null && TryClickPhysical(exportButton);
    var foundExportWindow = exportInvoked ? WaitForWindowName(app.Id, "导出作品", TimeSpan.FromSeconds(5)) : null;
    var exportOpened = foundExportWindow is not null;
    var statusAfterExport = FindById(window, "Status")?.Current.Name ?? "<无状态文本>";
    AddResult(new("导出窗口可访问", exportOpened, exportOpened ? $"已发现导出窗口：{foundExportWindow!.Current.Name}" : $"找到按钮={exportButton is not null}，物理点击={exportInvoked}，进程窗口=[{string.Join(" | ", FindProcessWindowNames(app.Id))}]，状态栏={statusAfterExport}"));
    if (exportOpened)
    {
        var exportWindow = WaitForWindowName(app.Id, "导出作品", TimeSpan.FromSeconds(1));
        if (exportWindow is not null) CaptureWindow(exportWindow, Path.ChangeExtension(reportPath, ".export.png"));
        var controls = exportWindow?.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition).Cast<AutomationElement>().ToArray() ?? [];
        var buttonNames = controls.Where(x => x.Current.ControlType == ControlType.Button).Select(SafeName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allResolutionButtons = new[] { "原始素材", "1080p", "1440p", "4K" }.All(buttonNames.Contains);
        var allRateButtons = new[] { "30 FPS", "60 FPS" }.All(buttonNames.Contains);
        var fourKButton = controls.FirstOrDefault(x => x.Current.ControlType == ControlType.Button && SafeName(x) == "4K");
        var fourKSelected = fourKButton is not null && TryInvoke(fourKButton);
        var fourKSummary = exportWindow is null ? null : WaitForNameById(exportWindow, "ExportSummaryResolution", name => name.Contains("2160", StringComparison.Ordinal), TimeSpan.FromSeconds(2));
        var fourKSummaryText = SafeName(fourKSummary);
        var hdButton = controls.FirstOrDefault(x => x.Current.ControlType == ControlType.Button && SafeName(x) == "1080p");
        var hdSelected = hdButton is not null && TryInvoke(hdButton);
        var hdSummary = exportWindow is null ? null : WaitForNameById(exportWindow, "ExportSummaryResolution", name => name.Contains("1920", StringComparison.Ordinal), TimeSpan.FromSeconds(2));
        var hdSummaryText = SafeName(hdSummary);
        var sixtyOption = controls.FirstOrDefault(x => x.Current.ControlType == ControlType.Button && SafeName(x) == "60 FPS");
        var selected60 = sixtyOption is not null && TryInvoke(sixtyOption);
        var sixtySummary = exportWindow is null ? null : WaitForNameById(exportWindow, "ExportSummaryFrameRate", name => name == "60 FPS", TimeSpan.FromSeconds(2));
        var exportChoices = allResolutionButtons && allRateButtons && fourKSelected && fourKSummary is not null && hdSelected && hdSummary is not null && selected60 && sixtySummary is not null;
        AddResult(new("导出选项可访问并更新摘要", exportChoices, $"四种分辨率按钮={allResolutionButtons}，两种帧率按钮={allRateButtons}，4K摘要={fourKSummaryText}，1080p摘要={hdSummaryText}，60FPS摘要={SafeName(sixtySummary)}；控件名称=[{string.Join(" | ", controls.Select(SafeName).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Take(80))}]"));
        var idleCancel = exportWindow?.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition)
            .Cast<AutomationElement>().FirstOrDefault(x => x.Current.ControlType == ControlType.Button && SafeName(x) == "取消");
        var idleCancelEnabled = idleCancel is not null && idleCancel.Current.IsEnabled;
        var idleCancelClicked = idleCancelEnabled && TryClickPhysical(idleCancel!);
        var exportWindowClosed = idleCancelClicked && WaitForWindowName(app.Id, "导出作品", TimeSpan.FromSeconds(2)) is null;
        AddResult(new("导出窗口空闲时可关闭", idleCancelEnabled && exportWindowClosed,
            $"取消按钮启用={idleCancelEnabled}，点击={idleCancelClicked}，窗口关闭={exportWindowClosed}"));
        if (!exportWindowClosed && exportWindow is not null) _ = CloseWindowWithoutKeyboard(exportWindow);
    }
    else _ = CloseOwnedTransientPopups(app.Id, window); // 关闭旧版系统“另存为”对话框，不向关闭后的窗口发送按键释放。

    FocusWindow(window);
    SendKey(0x53, true); // Ctrl+S
    Thread.Sleep(800);
    var saved = File.Exists(projectCopyFile) && FrameTrailProjectStoreProbe(projectCopyFile);
    AddResult(new("保存后项目可重新读取", saved, saved ? "项目 JSON 可解析且媒体相对路径有效。" : "保存后的项目文件无法读取。"));

    var prefsButton = FindButtonByHint(window, "设置"); var prefsMenuOpened = prefsButton is not null && TryInvoke(prefsButton);
    var prefsMenuItem = prefsMenuOpened ? WaitForName("录制与界面设置…", app.Id, TimeSpan.FromSeconds(3)) : null;
    if (prefsMenuItem is not null) TryInvoke(prefsMenuItem);
    var prefsWindow = prefsMenuItem is null ? null : WaitForWindowName(app.Id, "录制与界面设置", TimeSpan.FromSeconds(4));
    var prefWidth = prefsWindow is null ? null : WaitForComboByName(prefsWindow, "预览分辨率", TimeSpan.FromSeconds(2));
    var prefFps = prefsWindow is null ? null : WaitForComboByName(prefsWindow, "预览帧率", TimeSpan.FromSeconds(2));
    var prefControlsVisible = prefWidth is not null && prefFps is not null;
    var excludeEditorPreference = prefsWindow is null ? null : FindCheckboxContaining(prefsWindow, "录制时排除编辑器窗口");
    var widthSet = prefWidth is not null && SelectComboOption(prefWidth, "960", app.Id, 0);
    var fpsSet = prefFps is not null && SelectComboOption(prefFps, "60", app.Id, 1);
    var settingsSaved = false;
    if (prefsWindow is not null && prefControlsVisible && widthSet && fpsSet)
    {
        var folderEdit = prefsWindow.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        var safeFolderSet = false;
        try { if (folderEdit is not null) safeFolderSet = TrySetValue(folderEdit, testRoot) && ((ValuePattern)folderEdit.GetCurrentPattern(ValuePattern.Pattern)).Current.Value == testRoot; } catch { }
        var savePreferences = FindButtonByHint(prefsWindow, "保存设置");
        if (safeFolderSet && excludeEditorPreference is not null && savePreferences is not null && TryInvoke(savePreferences))
        {
            var settingFile = Path.Combine(localData, "FrameTrail", "settings.json");
            settingsSaved = WaitForPreferenceValues(settingFile, 960, 60, TimeSpan.FromSeconds(4));
        }
    }
    var prefsClosed = prefsWindow is null || CloseWindowWithoutKeyboard(prefsWindow);
    AddResult(new("预览质量和排除偏好可保存", settingsSaved && prefsClosed, settingsSaved && prefsClosed ? "设置页将预览宽度写为960、帧率写为60，并通过窗口关闭请求退出；测试结束会恢复本机原设置文件。" : $"设置窗口={prefsWindow is not null}，预览分辨率控件={prefWidth is not null}，帧率控件={prefFps is not null}，选项={widthSet}/{fpsSet}，窗口关闭={prefsClosed}。"));

    app.CloseMainWindow();
    if (!app.WaitForExit(5000)) { app.Kill(entireProcessTree: true); app.WaitForExit(); }
    app.Dispose(); app = null;
    app = Process.Start(new ProcessStartInfo(appPath) { UseShellExecute = false, ArgumentList = { "--open", projectCopyFile } });
    var memoWindow = app is null ? null : WaitForWindow(app, TimeSpan.FromSeconds(20));
    if (memoWindow is not null)
    {
        var memoStartupReady = InteractiveDesktopGuard.ActivateTargetOnceAtStartup((IntPtr)memoWindow.Current.NativeWindowHandle);
        AddResult(new("备忘录重启实例完成一次性前台交接", memoStartupReady,
            $"新 PID={app!.Id}，启动激活={memoStartupReady}；未成功时不执行备忘录输入。"));
        if (!memoStartupReady) throw new SmokeEarlyExitException();
        NativeMethods.SetWindowPos((IntPtr)memoWindow.Current.NativeWindowHandle, IntPtr.Zero, 960, 200, 0, 0, 0x0001 | 0x0004 | 0x0010);
        _ = WaitForProjectTitle(memoWindow, TimeSpan.FromSeconds(10));
        TestMemoPersistence(memoWindow, app!.Id, results);
    }
    else AddResult(new("备忘录移动、字号与透明度保存重启", false, "重新启动测试项目后无法定位主窗口。"));

    if (app is not null)
    {
        app.CloseMainWindow();
        if (!app.WaitForExit(8000)) { app.Kill(entireProcessTree: true); app.WaitForExit(); }
        app.Dispose(); app = null;
    }
    var expectedReopenState = JsonNode.Parse(File.ReadAllText(projectCopyFile))?.AsObject();
    var expectedSettings = expectedReopenState?["settings"]?.ToJsonString();
    var expectedCamera = expectedReopenState?["camera"]?.ToJsonString();
    var expectedMouseClock = expectedReopenState?["mouseClock"]?.ToJsonString();
    var reopen = Process.Start(new ProcessStartInfo(appPath) { UseShellExecute = false, ArgumentList = { "--open", projectCopyFile } });
    var reopenedWindow = reopen is null ? null : WaitForWindow(reopen, TimeSpan.FromSeconds(20));
    var reopenedTitle = reopenedWindow is null ? null : WaitForProjectTitle(reopenedWindow, TimeSpan.FromSeconds(10));
    var reopenedState = JsonNode.Parse(File.ReadAllText(projectCopyFile))?.AsObject();
    var reopenedValues = reopenedState?["settings"]?.ToJsonString() == expectedSettings
        && reopenedState?["camera"]?.ToJsonString() == expectedCamera
        && reopenedState?["mouseClock"]?.ToJsonString() == expectedMouseClock
        && ReadArrayCount(projectCopyFile, "screenMotions") > 0;
    var reopenOkay = reopenedWindow is not null && reopenedTitle is not null && reopenedValues;
    AddResult(new("保存项目重开并保留面板更改", reopenOkay, reopenedWindow is null ? "关闭后未能重新打开保存的项目。" : $"标题={reopenedTitle?.Current.Name ?? "未加载"}；关闭前设置与应用场景预设后的最终 JSON 快照一致，3D片段数={ReadArrayCount(projectCopyFile,"screenMotions")}。"));
    if (reopen is not null) { reopen.CloseMainWindow(); if (!reopen.WaitForExit(5000)) reopen.Kill(entireProcessTree: true); reopen.Dispose(); }
}
catch (SmokeEarlyExitException) { }
catch (ApplicationErrorDialogException ex)
{
    AddResult(new("FrameTrail 错误对话框已中止测试", false, ex.Message));
}
catch (Exception ex)
{
    AddResult(new("UI 冒烟验收执行", false, ex.ToString()));
}
finally
{
    var cleanupErrors = new List<string>();
    try { InteractiveDesktopGuard.Complete(); }
    catch (Exception ex) { cleanupErrors.Add($"守卫释放输入：{ex.GetType().Name}: {ex.Message}"); }
    try
    {
        if (app is not null)
        {
            try
            {
                if (!app.HasExited)
                {
                    try { app.CloseMainWindow(); }
                    catch (Exception ex) { cleanupErrors.Add($"请求应用正常关闭：{ex.GetType().Name}: {ex.Message}"); }
                    try
                    {
                        if (!app.HasExited && !app.WaitForExit(3000))
                        {
                            try { app.Kill(entireProcessTree: true); }
                            catch (Exception ex) { cleanupErrors.Add($"终止测试应用：{ex.GetType().Name}: {ex.Message}"); }
                            try { if (!app.HasExited) app.WaitForExit(3000); }
                            catch (Exception ex) { cleanupErrors.Add($"等待测试应用退出：{ex.GetType().Name}: {ex.Message}"); }
                        }
                    }
                    catch (Exception ex) { cleanupErrors.Add($"等待应用关闭：{ex.GetType().Name}: {ex.Message}"); }
                }
            }
            catch (Exception ex) { cleanupErrors.Add($"检查测试应用状态：{ex.GetType().Name}: {ex.Message}"); }
        }
    }
    finally
    {
        try { app?.Dispose(); }
        catch (Exception ex) { cleanupErrors.Add($"释放应用进程句柄：{ex.GetType().Name}: {ex.Message}"); }
    }
    if (cleanupErrors.Count > 0)
        AddResult(new("Smoke 应用清理", false, string.Join("；", cleanupErrors)));
    if (InteractiveDesktopGuard.IsAborted)
        AddResult(new("交互中止（本轮结果不完整，需复核）", false, InteractiveDesktopGuard.AbortReason));
    var userDataRestored = true;
    var failedRestorePaths = new List<string>();
    foreach (var snapshot in userDataSnapshots)
    {
        var restored = RestoreUserFile(snapshot.Key, snapshot.Value);
        userDataRestored &= restored;
        if (!restored) failedRestorePaths.Add(snapshot.Key);
    }
    if (userDataRestored)
    {
        try { if (File.Exists(userDataSnapshotPath)) File.Delete(userDataSnapshotPath); }
        catch { userDataRestored = false; failedRestorePaths.Add(userDataSnapshotPath); }
    }
    if (!userDataRestored)
    {
        AddResult(new("恢复 Smoke 前的用户偏好", false,
            $"文件恢复或校验失败=[{string.Join(", ", failedRestorePaths)}]；恢复快照保留于 {userDataSnapshotPath}。"));
        SmokeProgress.AppendMessage($"用户数据恢复未能完整确认；请使用 FrameTrail.App.Smoke.exe --restore-user-data \"{testRoot}\"，快照保留于 {userDataSnapshotPath}");
    }
    Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
    File.WriteAllText(reportPath, JsonSerializer.Serialize(new
    {
        application = appPath,
        sourceProject,
        isolatedProjectCopy = projectCopyFile,
        generatedUtc = DateTimeOffset.UtcNow,
        results,
        productStepsPassed = results.Count > 0 && !results.Any(x => !x.Passed && x.Name != "交互中止（本轮结果不完整，需复核）"),
        complete = SmokeProgress.IsComplete,
        status = results.Any(x => !x.Passed && x.Name != "交互中止（本轮结果不完整，需复核）") ? "failed" : InteractiveDesktopGuard.IsAborted || !SmokeProgress.IsComplete ? "incomplete" : "passed",
        passed = results.Count > 0 && !results.Any(x => !x.Passed && x.Name != "交互中止（本轮结果不完整，需复核）") && SmokeProgress.IsComplete && !InteractiveDesktopGuard.IsAborted
    }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"UIA_REPORT={reportPath}");
    foreach (var result in results) Console.WriteLine($"{(result.Passed ? "通过" : "未通过")}：{result.Name} — {result.Detail}");
    if (userDataRestored && cleanupErrors.Count == 0 && !args.Contains("--effects-only") && !args.Contains("--keep-fixture") && !args.Contains("--relink-only")) try { Directory.Delete(testRoot, true); } catch { }
}
return results.Any(x => !x.Passed && x.Name != "交互中止（本轮结果不完整，需复核）") ? 1
    : results.Count == 0 || !SmokeProgress.IsComplete || InteractiveDesktopGuard.IsAborted ? 2 : 0;

static void RunNewPanelUiTests(AutomationElement window, Process app, string appPath, string projectFile, List<StepResult> results)
{
    var mockupTab = FindRightPanelButton(window, "样机");
    var openedMockup = mockupTab is not null && TryClickPhysical(mockupTab);
    var frameCard = openedMockup ? FindButtonByHint(window, "浅色窗口") : null;
    var frameSelected = frameCard is not null && TryClickPhysical(frameCard);
    var scale = frameSelected ? WaitForControlName(window, ControlType.Slider, "框体比例", TimeSpan.FromSeconds(3)) : null;
    var scaleSet = scale is not null && SetRangeValue(scale, 1.12)
        && WaitForJsonNumber(projectFile, ["settings", "canvas", "frameScale"], 1.12, TimeSpan.FromSeconds(5));
    var toolbar = scaleSet ? WaitForControlName(window, ControlType.Slider, "顶栏高度", TimeSpan.FromSeconds(3)) : null;
    var toolbarSet = toolbar is not null && SetRangeValue(toolbar, 1.08)
        && WaitForJsonNumber(projectFile, ["settings", "canvas", "frameToolbarScale"], 1.08, TimeSpan.FromSeconds(5));
    var radius = toolbarSet ? WaitForControlName(window, ControlType.Slider, "框体外圆角", TimeSpan.FromSeconds(3)) : null;
    var radiusSet = radius is not null && SetRangeValue(radius, 26)
        && WaitForJsonNumber(projectFile, ["settings", "canvas", "frameOuterRadius"], 26, TimeSpan.FromSeconds(5));
    AddStep(results, new("样机卡片与框体滑杆提交", openedMockup && frameSelected && scaleSet && toolbarSet && radiusSet,
        $"样机={openedMockup}，卡片={frameSelected}，比例={scaleSet}，顶栏={toolbarSet}，圆角={radiusSet}；JSON=({ReadJsonNumber(projectFile, ["settings", "canvas", "frameScale"])},{ReadJsonNumber(projectFile, ["settings", "canvas", "frameToolbarScale"])},{ReadJsonNumber(projectFile, ["settings", "canvas", "frameOuterRadius"])})"));

    window = WaitForMainWindow(app.Id, TimeSpan.FromSeconds(3)) ?? window;
    var cursor = FindRailButton(window, "光标");
    var cursorOpened = cursor is not null && TryClickPhysical(cursor);
    var smoothing = cursorOpened ? WaitForComboByName(window, "移动平滑", TimeSpan.FromSeconds(3)) : null;
    var smoothingSet = smoothing is not null && SelectComboOption(smoothing, "灵敏", app.Id, 2)
        && WaitForJsonString(projectFile, ["settings", "pointerSmoothing"], "rapid", TimeSpan.FromSeconds(5));
    var disclosure = cursorOpened ? FindNamed(window, "时间对齐") : null;
    var expanded = disclosure is not null && TryExpand(disclosure);
    var offset = expanded ? WaitForControlName(window, ControlType.Slider, "光标时间偏移", TimeSpan.FromSeconds(3)) : null;
    var offsetSet = offset is not null && SetRangeValue(offset, 10)
        && WaitForJsonNumber(projectFile, ["mouseClock", "offsetSeconds"], .01, TimeSpan.FromSeconds(5));
    var offsetPersists = offsetSet && WaitForControlName(WaitForMainWindow(app.Id, TimeSpan.FromSeconds(3)) ?? window,
        ControlType.Slider, "光标时间偏移", TimeSpan.FromSeconds(2)) is not null;
    AddStep(results, new("光标平滑与时间对齐持久化", smoothingSet && offsetPersists,
        $"平滑={smoothingSet}，分组展开={expanded}，偏移={offsetSet}，重建后可见={offsetPersists}，JSON={ReadJsonString(projectFile, "settings", "pointerSmoothing")}/{ReadJsonNumber(projectFile, ["mouseClock", "offsetSeconds"])}"));

    window = WaitForMainWindow(app.Id, TimeSpan.FromSeconds(3)) ?? window;
    var openingRail = FindRailButton(window, "开场");
    var openingOpened = openingRail is not null && TryClickPhysical(openingRail);
    var enabled = openingOpened ? FindCheckboxContaining(window, "启用全局开场") : null;
    var isEnabled = false;
    try { isEnabled = enabled is not null && ((ToggleState)enabled.GetCurrentPropertyValue(TogglePattern.ToggleStateProperty)) == ToggleState.On; } catch { }
    var projectAlreadyEnabled = ReadJsonBoolean(projectFile, "settings", "opening", "enabled") == true;
    var toggleClicked = enabled is not null && (projectAlreadyEnabled || TryClickPhysical(enabled, preferAutomationInvoke: false));
    var toggleSet = toggleClicked
        && WaitForJsonBoolean(projectFile, ["settings", "opening", "enabled"], true, TimeSpan.FromSeconds(5));
    window = WaitForMainWindow(app.Id, TimeSpan.FromSeconds(3)) ?? window;
    var customDisclosure = toggleSet ? FindNamed(window, "自定开场与起始姿态") : null;
    var customExpanded = customDisclosure is not null && TryExpand(customDisclosure);
    window = WaitForMainWindow(app.Id, TimeSpan.FromSeconds(3)) ?? window;
    var openingCard = customExpanded ? FindButtonByHint(window, "放大入场") : null;
    var openingSet = openingCard is not null && TryClickPhysical(openingCard)
        && WaitForJsonString(projectFile, ["settings", "opening", "preset"], "zoomUp", TimeSpan.FromSeconds(5));
    AddStep(results, new("开场启用与折叠自定预设提交", openingOpened && toggleSet && customExpanded && openingSet,
        $"面板={openingOpened}，复选框={enabled is not null}，原值={isEnabled}，启用={toggleSet}，自定折叠展开={customExpanded}，预设={openingSet}，JSON={ReadJsonBoolean(projectFile, "settings", "opening", "enabled")}/{ReadJsonString(projectFile, "settings", "opening", "preset")}"));

    app.CloseMainWindow();
    if (!app.WaitForExit(5000)) app.Kill(entireProcessTree: true);
    using var reopened = Process.Start(new ProcessStartInfo(appPath) { UseShellExecute = false, ArgumentList = { "--open", projectFile } });
    var reopenedWindow = reopened is null ? null : WaitForWindow(reopened, TimeSpan.FromSeconds(20));
    var reopenedTitle = reopenedWindow is null ? null : WaitForProjectTitle(reopenedWindow, TimeSpan.FromSeconds(10));
    var persisted = reopenedWindow is not null && reopenedTitle is not null
        && Math.Abs(ReadJsonNumber(projectFile, "settings", "canvas", "frameScale") - 1.12) < .001
        && Math.Abs(ReadJsonNumber(projectFile, "settings", "canvas", "frameToolbarScale") - 1.08) < .001
        && Math.Abs(ReadJsonNumber(projectFile, "settings", "canvas", "frameOuterRadius") - 26) < .001
        && ReadJsonString(projectFile, "settings", "pointerSmoothing") == "rapid"
        && Math.Abs(ReadJsonNumber(projectFile, "mouseClock", "offsetSeconds") - .01) < .001
        && ReadJsonBoolean(projectFile, "settings", "opening", "enabled") == true
        && ReadJsonString(projectFile, "settings", "opening", "preset") == "zoomUp";
    AddStep(results, new("新面板设置保存并重开", persisted,
        $"窗口={reopenedWindow is not null}，标题={reopenedTitle?.Current.Name}，样机/指针/开场值已按 JSON 复核。"));
    if (reopened is not null) { reopened.CloseMainWindow(); if (!reopened.WaitForExit(5000)) reopened.Kill(entireProcessTree: true); }
}

static void RunAdvancedStyleUiTests(AutomationElement window, int processId, string projectFile, List<StepResult> results)
{
    var handle = (IntPtr)window.Current.NativeWindowHandle;
    if (handle != IntPtr.Zero && InteractiveDesktopGuard.BeforeInput("恢复测试窗口"))
        try { if (window.TryGetCurrentPattern(WindowPattern.Pattern, out var pattern) && pattern is WindowPattern windowPattern) windowPattern.SetWindowVisualState(WindowVisualState.Normal); } catch { }
    Thread.Sleep(300);
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(4)) ?? window;

    // 聚焦虚化：从真实界面新建缩放片段，启用并改形状、目标与数值。
    var zoomRail = FindRailButton(window, "运镜");
    var zoomOpened = zoomRail is not null && TryClickPhysical(zoomRail);
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(4)) ?? window;
    var addZoom = zoomOpened ? FindButtonByHint(window, "添加手动缩放") : null;
    var oldZoomCount = ReadArrayCount(projectFile, "zoomClips");
    var zoomAdded = addZoom is not null && TryInvoke(addZoom)
        && WaitForArrayCount(projectFile, "zoomClips", oldZoomCount + 1, TimeSpan.FromSeconds(5));
    var zoomIndex = ReadArrayCount(projectFile, "zoomClips") - 1;
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(4)) ?? window;
    var focusToggle = zoomAdded ? FindCheckboxContaining(window, "启用聚焦") : null;
    var focusEnabled = focusToggle is not null && TrySetToggleOn(focusToggle)
        && WaitForArrayObject(projectFile, "zoomClips", zoomIndex, "focusEffect", TimeSpan.FromSeconds(5));
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(4)) ?? window;
    var focusShape = focusEnabled ? WaitForComboByName(window, "形状", TimeSpan.FromSeconds(3)) : null;
    var focusLinear = focusShape is not null && SelectComboOption(focusShape, "线性", processId, 1)
        && WaitForJsonArrayStringPath(projectFile, "zoomClips", zoomIndex, ["focusEffect", "shape"], "linear", TimeSpan.FromSeconds(5));
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(4)) ?? window;
    var focusTarget = focusLinear ? WaitForComboByName(window, "目标", TimeSpan.FromSeconds(3)) : null;
    var targetFixed = focusTarget is not null && SelectComboOption(focusTarget, "固定", processId, 2)
        && WaitForJsonArrayStringPath(projectFile, "zoomClips", zoomIndex, ["focusEffect", "target"], "fixed", TimeSpan.FromSeconds(5));
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(4)) ?? window;
    var focusSize = targetFixed ? WaitForControlName(window, ControlType.Slider, "范围", TimeSpan.FromSeconds(3)) : null;
    var sizeSet = focusSize is not null && SetRangeValue(focusSize, .62)
        && WaitForJsonArrayNumber(projectFile, "zoomClips", zoomIndex, ["focusEffect", "size"], .62, TimeSpan.FromSeconds(5));
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(4)) ?? window;
    var focusBlur = sizeSet ? WaitForControlName(window, ControlType.Slider, "虚化", TimeSpan.FromSeconds(3)) : null;
    var blurSet = focusBlur is not null && SetRangeValue(focusBlur, .41)
        && WaitForJsonArrayNumber(projectFile, "zoomClips", zoomIndex, ["focusEffect", "blur"], .41, TimeSpan.FromSeconds(5));
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(4)) ?? window;
    var focusAngle = blurSet ? WaitForControlName(window, ControlType.Slider, "焦面角度", TimeSpan.FromSeconds(3)) : null;
    var angleSet = focusAngle is not null && SetRangeValue(focusAngle, 27)
        && WaitForJsonArrayNumber(projectFile, "zoomClips", zoomIndex, ["focusEffect", "angleDegrees"], 27, TimeSpan.FromSeconds(5));
    AddStep(results, new("ZoomFocus 线性固定目标与参数写入项目", zoomOpened && zoomAdded && focusEnabled && focusLinear && targetFixed && sizeSet && blurSet && angleSet,
        $"运镜页={zoomOpened}，新增缩放={zoomAdded}，聚焦={focusEnabled}，线性={focusLinear}，固定目标={targetFixed}，范围/虚化/角度={sizeSet}/{blurSet}/{angleSet}；Focus={ReadJsonArrayString(projectFile, "zoomClips", zoomIndex, "focusEffect", "shape")}/{ReadJsonArrayString(projectFile, "zoomClips", zoomIndex, "focusEffect", "target")}/{ReadJsonArrayNumber(projectFile, "zoomClips", zoomIndex, "focusEffect", "size"):0.##}/{ReadJsonArrayNumber(projectFile, "zoomClips", zoomIndex, "focusEffect", "blur"):0.##}/{ReadJsonArrayNumber(projectFile, "zoomClips", zoomIndex, "focusEffect", "angleDegrees"):0.##}。"));

    // 摄像头动画：选择横向目标形状并实际修改目标圆角和透明度。
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(4)) ?? window;
    var cameraRail = FindRailButton(window, "摄像头");
    var cameraOpened = cameraRail is not null && TryClickPhysical(cameraRail);
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(4)) ?? window;
    var cameraScale = cameraOpened ? WaitForControlName(window, ControlType.Slider, "缩放联动", TimeSpan.FromSeconds(3)) : null;
    var cameraScaleSet = cameraScale is not null && SetRangeValue(cameraScale, 1.4)
        && WaitForJsonNumber(projectFile, ["camera", "scaleDuringZoom"], 1.4, TimeSpan.FromSeconds(5));
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(4)) ?? window;
    var cameraShadow = cameraScaleSet ? WaitForControlName(window, ControlType.Slider, "阴影", TimeSpan.FromSeconds(3)) : null;
    var cameraShadowSet = cameraShadow is not null && SetRangeValue(cameraShadow, 56)
        && WaitForJsonNumber(projectFile, ["camera", "shadowStrength"], .56, TimeSpan.FromSeconds(5));
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(4)) ?? window;
    var addCamera = cameraOpened ? FindButtonByHint(window, "添加摄像头运动") : null;
    var oldCameraCount = ReadArrayCount(projectFile, "cameraMotions");
    var cameraAdded = addCamera is not null && TryInvoke(addCamera)
        && WaitForArrayCount(projectFile, "cameraMotions", oldCameraCount + 1, TimeSpan.FromSeconds(5));
    var cameraIndex = ReadArrayCount(projectFile, "cameraMotions") - 1;
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(4)) ?? window;
    var horizontal = cameraAdded ? FindButtonByHint(window, "横向") : null;
    var horizontalSet = horizontal is not null && TryInvoke(horizontal)
        && WaitForJsonArrayString(projectFile, "cameraMotions", cameraIndex, "targetLayout", "horizontal", TimeSpan.FromSeconds(5));
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(4)) ?? window;
    var roundness = horizontalSet ? WaitForControlName(window, ControlType.Slider, "目标圆角", TimeSpan.FromSeconds(3)) : null;
    var roundnessSet = roundness is not null && SetRangeValue(roundness, 63)
        && WaitForJsonArrayNumber(projectFile, "cameraMotions", cameraIndex, ["targetRoundness"], .63, TimeSpan.FromSeconds(5));
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(4)) ?? window;
    var opacity = roundnessSet ? WaitForControlName(window, ControlType.Slider, "目标不透明度", TimeSpan.FromSeconds(3)) : null;
    var opacitySet = opacity is not null && SetRangeValue(opacity, 47)
        && WaitForJsonArrayNumber(projectFile, "cameraMotions", cameraIndex, ["targetOpacity"], .47, TimeSpan.FromSeconds(5));
    AddStep(results, new("摄像头动画、缩放联动与阴影写入项目", cameraOpened && cameraAdded && horizontalSet && roundnessSet && opacitySet && cameraScaleSet && cameraShadowSet,
        $"摄像头页={cameraOpened}，新增动画={cameraAdded}，横向/圆角/透明度={horizontalSet}/{roundnessSet}/{opacitySet}，联动缩放/阴影={cameraScaleSet}/{cameraShadowSet}；数值={ReadJsonNumber(projectFile, "camera", "scaleDuringZoom"):0.##}/{ReadJsonNumber(projectFile, "camera", "shadowStrength"):0.##}。"));

    // 贴图样式：按时间线点击首个既有贴图，并验证边框、阴影及曲线。
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(4)) ?? window;
    var timeline = FindById(window, "TimelineView");
    var overlayIndex = 0;
    var overlaySelected = false;
    var overlayHitDiagnostic = timeline is null ? "未找到 TimelineView。" : "点击尚未执行。";
    if (timeline is not null)
    {
        using var doc = JsonDocument.Parse(ReadSharedBytes(projectFile));
        overlaySelected = ClickEffectClip(timeline, window, doc.RootElement, laneIndex: 4, sourceTime: .9, out overlayHitDiagnostic);
    }
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(4)) ?? window;
    var border = overlaySelected ? WaitForControlName(window, ControlType.Slider, "贴图描边", TimeSpan.FromSeconds(3)) : null;
    var borderSet = border is not null && SetRangeValue(border, 5)
        && WaitForJsonArrayNumber(projectFile, "overlays", overlayIndex, ["borderWidth"], 5, TimeSpan.FromSeconds(5));
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(4)) ?? window;
    var shadowOpacity = borderSet ? WaitForControlName(window, ControlType.Slider, "阴影不透明度", TimeSpan.FromSeconds(3)) : null;
    var shadowSet = shadowOpacity is not null && SetRangeValue(shadowOpacity, 42)
        && WaitForJsonArrayNumber(projectFile, "overlays", overlayIndex, ["shadowOpacity"], .42, TimeSpan.FromSeconds(5));
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(4)) ?? window;
    var shadowRadius = shadowSet ? WaitForControlName(window, ControlType.Slider, "阴影柔和度", TimeSpan.FromSeconds(3)) : null;
    var radiusSet = shadowRadius is not null && SetRangeValue(shadowRadius, 31)
        && WaitForJsonArrayNumber(projectFile, "overlays", overlayIndex, ["shadowRadius"], 31, TimeSpan.FromSeconds(5));
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(4)) ?? window;
    var curve = radiusSet ? WaitForComboByName(window, "动画节奏", TimeSpan.FromSeconds(3)) : null;
    var curveSet = curve is not null && SelectComboOption(curve, "柔和", processId, 3)
        && WaitForJsonArrayString(projectFile, "overlays", overlayIndex, "animationCurve", "gentle", TimeSpan.FromSeconds(5));
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(4)) ?? window;
    var backdropBlur = curveSet ? WaitForControlName(window, ControlType.Slider, "背景柔化", TimeSpan.FromSeconds(3)) : null;
    var backdropSet = backdropBlur is not null && SetRangeValue(backdropBlur, 37)
        && WaitForJsonArrayNumber(projectFile, "overlays", overlayIndex, ["backdropBlur"], 37, TimeSpan.FromSeconds(5));
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(4)) ?? window;
    var includesCamera = backdropSet ? FindCheckboxContaining(window, "同时柔化摄像头") : null;
    var includesCameraSet = includesCamera is not null && TrySetToggleOn(includesCamera)
        && WaitForJsonArrayBoolean(projectFile, "overlays", overlayIndex, "backdropBlurIncludesCamera", true, TimeSpan.FromSeconds(5));
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(4)) ?? window;
    var hidesScreen = includesCameraSet ? FindCheckboxContaining(window, "贴图显示时隐藏屏幕") : null;
    var hidesScreenSet = hidesScreen is not null && TrySetToggleOn(hidesScreen)
        && WaitForJsonArrayBoolean(projectFile, "overlays", overlayIndex, "hidesScreen", true, TimeSpan.FromSeconds(5));
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(4)) ?? window;
    var hidesCamera = hidesScreenSet ? FindCheckboxContaining(window, "贴图显示时隐藏摄像头") : null;
    var hidesCameraSet = hidesCamera is not null && TrySetToggleOn(hidesCamera)
        && WaitForJsonArrayBoolean(projectFile, "overlays", overlayIndex, "hidesCamera", true, TimeSpan.FromSeconds(5));
    AddStep(results, new("贴图边框、阴影、背景柔化与覆盖开关写入项目", overlaySelected && borderSet && shadowSet && radiusSet && curveSet && backdropSet && includesCameraSet && hidesScreenSet && hidesCameraSet,
        $"选中贴图={overlaySelected}，命中={overlayHitDiagnostic}，边框/阴影/圆角/曲线/柔化={borderSet}/{shadowSet}/{radiusSet}/{curveSet}/{backdropSet}，开关={includesCameraSet}/{hidesScreenSet}/{hidesCameraSet}。"));
}

static void RunSpotlightAndSegmentAudioUiTests(AutomationElement window, int processId, string projectFile, List<StepResult> results)
{
    var addButton = FindButtonByHint(window, "添加元素");
    var menuOpened = addButton is not null && TryInvoke(addButton);
    var addBlurItem = menuOpened ? WaitForName("添加模糊遮罩", processId, TimeSpan.FromSeconds(4)) : null;
    var oldCount = ReadArrayCount(projectFile, "blurClips");
    var blurAdded = addBlurItem is not null && TryInvoke(addBlurItem)
        && WaitForArrayCount(projectFile, "blurClips", oldCount + 1, TimeSpan.FromSeconds(6));
    var blurIndex = ReadArrayCount(projectFile, "blurClips") - 1;
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(5)) ?? window;
    var method = blurAdded ? WaitForComboByName(window, "处理方式", TimeSpan.FromSeconds(4)) : null;
    var spotlightSet = method is not null && SelectComboOption(method, "聚光", processId, 2)
        && WaitForJsonArrayString(projectFile, "blurClips", blurIndex, "kind", "spotlight", TimeSpan.FromSeconds(5));
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(5)) ?? window;
    var dimming = spotlightSet ? WaitForControlName(window, ControlType.Slider, "区域外压暗", TimeSpan.FromSeconds(4)) : null;
    var dimmingSet = dimming is not null && SetRangeValue(dimming, .37)
        && WaitForJsonArrayNumber(projectFile, "blurClips", blurIndex, ["spotlightDimming"], .37, TimeSpan.FromSeconds(5));
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(5)) ?? window;
    var transition = dimmingSet ? WaitForComboByName(window, "转场", TimeSpan.FromSeconds(4)) : null;
    var transitionSet = transition is not null && SelectComboOption(transition, "顺滑", processId, 3)
        && WaitForJsonArrayString(projectFile, "blurClips", blurIndex, "transitionStyle", "smooth", TimeSpan.FromSeconds(5));
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(5)) ?? window;
    var enter = transitionSet ? WaitForControlName(window, ControlType.Slider, "进入", TimeSpan.FromSeconds(4)) : null;
    var enterSet = enter is not null && SetRangeValue(enter, .4)
        && WaitForJsonArrayNumber(projectFile, "blurClips", blurIndex, ["enterDuration"], .4, TimeSpan.FromSeconds(5));
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(5)) ?? window;
    var exit = enterSet ? WaitForControlName(window, ControlType.Slider, "退出", TimeSpan.FromSeconds(4)) : null;
    var exitSet = exit is not null && SetRangeValue(exit, .25)
        && WaitForJsonArrayNumber(projectFile, "blurClips", blurIndex, ["exitDuration"], .25, TimeSpan.FromSeconds(5));
    AddStep(results, new("聚光遮罩压暗与转场写入项目", menuOpened && blurAdded && spotlightSet && dimmingSet && transitionSet && enterSet && exitSet,
        $"菜单={menuOpened}，新增遮罩={blurAdded}，聚光={spotlightSet}，区域外压暗={dimmingSet}，顺滑转场={transitionSet}，进/出时长={enterSet}/{exitSet}；JSON={ReadJsonArrayString(projectFile, "blurClips", blurIndex, "kind")}/{ReadJsonArrayNumber(projectFile, "blurClips", blurIndex, "spotlightDimming"):0.##}/{ReadJsonArrayString(projectFile, "blurClips", blurIndex, "transitionStyle")}/{ReadJsonArrayNumber(projectFile, "blurClips", blurIndex, "enterDuration"):0.##}/{ReadJsonArrayNumber(projectFile, "blurClips", blurIndex, "exitDuration"):0.##}。"));

    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(5)) ?? window;
    var timeline = FindById(window, "TimelineView");
    var segmentClicked = false;
    var clickDiagnostic = "未找到 TimelineView。";
    if (timeline is not null)
    {
        using var doc = JsonDocument.Parse(ReadSharedBytes(projectFile));
        var first = doc.RootElement.GetProperty("timeline").GetProperty("segments")[0];
        var sourceTime = first.GetProperty("sourceStart").GetDouble() + Math.Min(.2, first.GetProperty("sourceDuration").GetDouble() * .35);
        segmentClicked = ClickPrimarySegment(timeline, window, doc.RootElement, sourceTime, out clickDiagnostic);
    }
    var audioTitle = segmentClicked ? WaitForInspectorTitle(window, "声音", TimeSpan.FromSeconds(4)) == "声音" : false;
    var segmentSection = audioTitle && FindNamed(window, "所选片段音量") is not null;
    var systemVolume = segmentSection ? WaitForControlName(window, ControlType.Slider, "系统声音", TimeSpan.FromSeconds(4)) : null;
    var volumeSet = systemVolume is not null && SetRangeValue(systemVolume, 32)
        && WaitForTimelineSegmentSystemVolume(projectFile, 0, .32, TimeSpan.FromSeconds(6));
    AddStep(results, new("点选屏幕片段并单独调整系统声音", segmentClicked && audioTitle && segmentSection && volumeSet,
        $"点击={segmentClicked}，命中={clickDiagnostic}，声音 Inspector={audioTitle}，片段音量分组={segmentSection}，系统声音滑块={systemVolume is not null}，写入={volumeSet}；片段覆写={ReadTimelineSegmentSystemVolume(projectFile, 0):0.##}。"));
}

static bool ClickPrimarySegment(AutomationElement timeline, AutomationElement mainWindow, JsonElement project, double sourceTime, out string diagnostic)
{
    diagnostic = "";
    var outputTime = double.NaN;
    var outputStart = 0d;
    foreach (var segment in project.GetProperty("timeline").GetProperty("segments").EnumerateArray())
    {
        var start = segment.GetProperty("sourceStart").GetDouble();
        var duration = segment.GetProperty("sourceDuration").GetDouble();
        var rate = segment.GetProperty("playbackRate").GetDouble();
        if (sourceTime >= start && sourceTime < start + duration) { outputTime = outputStart + (sourceTime - start) / rate; break; }
        outputStart += duration / rate;
    }
    if (!double.IsFinite(outputTime)) { diagnostic = $"源时间 {sourceTime:0.###} 秒不在时间线上。"; return false; }
    var handle = (IntPtr)mainWindow.Current.NativeWindowHandle;
    var dpi = NativeMethods.GetDpiForWindow(handle) / 96d; if (dpi < .5 || dpi > 4) dpi = 1;
    if (!PrepareTimelinePoint(timeline, project, 85, dpi, out var viewport, out var scrollOffset, out var scrollDescription, out _))
    { diagnostic = "无法将屏幕录制轨道滚入可见视口：" + scrollDescription; return false; }
    var bounds = timeline.Current.BoundingRectangle;
    var scale = (bounds.Width - 152 * dpi) / project.GetProperty("timeline").GetProperty("duration").GetDouble();
    var x = (int)(bounds.Left + 152 * dpi + outputTime * scale);
    var y = (int)(viewport.Top + (85 - scrollOffset) * dpi);
    var priorContext = NativeMethods.SetThreadDpiAwarenessContext(new IntPtr(-4));
    try
    {
        if (!InteractiveDesktopGuard.BeforeInput("录制轨道时间线点击")) { diagnostic = InteractiveDesktopGuard.AbortReason; return false; }
        NativeMethods.SetCursorPos(x, y);
        Thread.Sleep(150);
        NativeMethods.GetCursorPos(out var cursor);
        var point = NativeMethods.WindowFromPoint(cursor);
        var root = NativeMethods.GetAncestor(point, 2);
        NativeMethods.GetWindowThreadProcessId(root, out var rootPid);
        var processId = (int)mainWindow.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty);
        if (root != handle)
        {
            diagnostic = $"点击被拦截：指针位于其他窗口。xy={x},{y},point={point.ToInt64():X},root={root.ToInt64():X}/pid{rootPid},app={handle.ToInt64():X}/pid{processId},timeline={bounds.Left:0.#},{bounds.Top:0.#},{bounds.Width:0.#},{bounds.Height:0.#},viewport={viewport},scroll={scrollDescription}";
            return false;
        }
        NativeMethods.MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(65);
        NativeMethods.MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(250);
        diagnostic = $"输出时间={outputTime:0.###} 秒，坐标={x},{y}，DPI={dpi:0.##}，窗口={handle.ToInt64():X}，前台={root.ToInt64():X}，视口={viewport}，滚动={scrollDescription}。";
        return true;
    }
    finally { if (priorContext != IntPtr.Zero) NativeMethods.SetThreadDpiAwarenessContext(priorContext); }
}

static void RunMotionCurveUiTests(AutomationElement window, Process app, string projectFile, List<StepResult> results)
{
    var processId = app.Id;

    // 缩放：在真实曲线选项中选“自定义”，改控制点，再切换弹簧并改全项目弹簧质量。
    var rail = FindRailButton(window, "运镜");
    var opened = rail is not null && TryClickPhysical(rail);
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(3)) ?? window;
    var zoomTab = FindButtonByHint(window, "缩放");
    var zoomSelected = zoomTab is not null && TryClickPhysical(zoomTab);
    var zoomCurve = zoomSelected ? WaitForComboByName(window, "曲线", TimeSpan.FromSeconds(3)) : null;
    var zoomCustom = zoomCurve is not null && SelectComboOption(zoomCurve, "自定义", processId, 5);
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(3)) ?? window;
    var zoomDisclosure = zoomCustom ? FindNamed(window, "曲线控制点") : null;
    var zoomExpanded = zoomDisclosure is not null && TryExpand(zoomDisclosure);
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(3)) ?? window;
    var zoomX1 = zoomExpanded ? WaitForControlName(window, ControlType.Slider, "起点控制 X", TimeSpan.FromSeconds(3)) : null;
    var zoomChanged = zoomX1 is not null && SetRangeValue(zoomX1, .42)
        && WaitForJsonArrayNumber(projectFile, "zoomClips", 0, ["customCurve", "x1"], .42, TimeSpan.FromSeconds(5));
    AddStep(results, new("缩放自定贝塞尔控制点实际写入项目", opened && zoomSelected && zoomCustom && zoomChanged,
        $"运镜={opened}，缩放页={zoomSelected}，自定曲线={zoomCustom}，控制点展开={zoomExpanded}，X1={ReadJsonArrayNumber(projectFile, "zoomClips", 0, "customCurve", "x1"):0.###}。"));

    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(3)) ?? window;
    var springCombo = WaitForComboByName(window, "曲线", TimeSpan.FromSeconds(3));
    var springSelected = springCombo is not null && SelectComboOption(springCombo, "弹簧", processId, 4);
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(3)) ?? window;
    var springDisclosure = springSelected ? FindNamed(window, "弹簧参数") : null;
    var springExpanded = springDisclosure is not null && TryExpand(springDisclosure);
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(3)) ?? window;
    var mass = springExpanded ? WaitForControlName(window, ControlType.Slider, "弹簧质量", TimeSpan.FromSeconds(3)) : null;
    var massSet = mass is not null && SetRangeValue(mass, 2.7)
        && WaitForJsonNumber(projectFile, ["settings", "spring", "mass"], 2.7, TimeSpan.FromSeconds(5));
    AddStep(results, new("弹簧动画参数实际写入项目", springSelected && springExpanded && massSet,
        $"曲线弹簧={springSelected}，参数展开={springExpanded}，质量={ReadJsonNumber(projectFile, "settings", "spring", "mass"):0.###}。"));

    // 屏幕 3D：在自定义曲线的第二个控制点上写入不同数值。
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(3)) ?? window;
    rail = FindRailButton(window, "运镜"); if (rail is not null) TryClickPhysical(rail);
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(3)) ?? window;
    var screenTab = FindButtonByHint(window, "屏幕 3D");
    var screenSelected = screenTab is not null && TryClickPhysical(screenTab);
    var screenCurve = screenSelected ? WaitForComboByName(window, "曲线", TimeSpan.FromSeconds(3)) : null;
    var screenCustom = screenCurve is not null && SelectComboOption(screenCurve, "自定义", processId, 5);
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(3)) ?? window;
    var screenDisclosure = screenCustom ? FindNamed(window, "曲线控制点") : null;
    var screenExpanded = screenDisclosure is not null && TryExpand(screenDisclosure);
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(3)) ?? window;
    var screenY2 = screenExpanded ? WaitForControlName(window, ControlType.Slider, "终点控制 Y", TimeSpan.FromSeconds(3)) : null;
    var screenChanged = screenY2 is not null && SetRangeValue(screenY2, .63)
        && WaitForJsonArrayNumber(projectFile, "screenMotions", 0, ["customCurve", "y2"], .63, TimeSpan.FromSeconds(5));
    AddStep(results, new("屏幕 3D 自定曲线实际写入项目", screenSelected && screenCustom && screenChanged,
        $"屏幕3D={screenSelected}，自定曲线={screenCustom}，控制点展开={screenExpanded}，Y2={ReadJsonArrayNumber(projectFile, "screenMotions", 0, "customCurve", "y2"):0.###}。"));

    // 摄像头动画需从真实时间线选中片段，避免只验证空态面板。
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(3)) ?? window;
    var cameraRail = FindRailButton(window, "摄像头");
    var cameraOpened = cameraRail is not null && TryClickPhysical(cameraRail);
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(3)) ?? window;
    var cameraMotionCount = ReadArrayCount(projectFile, "cameraMotions");
    var addCameraMotion = cameraOpened ? FindButtonByHint(window, "添加摄像头运动") : null;
    var cameraClipCreated = addCameraMotion is not null && TryClickPhysical(addCameraMotion)
        && WaitForArrayCount(projectFile, "cameraMotions", cameraMotionCount + 1, TimeSpan.FromSeconds(5));
    var cameraClipIndex = ReadArrayCount(projectFile, "cameraMotions") - 1;
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(3)) ?? window;
    var cameraCurve = cameraClipCreated ? FindButtonByHint(window, "自定义") : null;
    var cameraCustom = cameraCurve is not null && TryClickPhysical(cameraCurve)
        && WaitForJsonArrayString(projectFile, "cameraMotions", cameraClipIndex, "easing", "custom", TimeSpan.FromSeconds(5));
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(3)) ?? window;
    var cameraDisclosure = cameraCustom ? FindNamed(window, "曲线控制点") : null;
    var cameraExpanded = cameraDisclosure is not null && TryExpand(cameraDisclosure);
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(3)) ?? window;
    var cameraX2 = cameraExpanded ? WaitForControlName(window, ControlType.Slider, "终点控制 X", TimeSpan.FromSeconds(3)) : null;
    var cameraChanged = cameraX2 is not null && SetRangeValue(cameraX2, .58)
        && WaitForJsonArrayNumber(projectFile, "cameraMotions", cameraClipIndex, ["customCurve", "x2"], .58, TimeSpan.FromSeconds(5));
    AddStep(results, new("新建摄像头动画并保存自定曲线", cameraOpened && cameraClipCreated && cameraCustom && cameraChanged,
        $"摄像头页={cameraOpened}，新片段={cameraClipCreated}，自定曲线={cameraCustom}，按钮={SafeName(cameraCurve)}，控制点展开={cameraExpanded}，X2={ReadJsonArrayNumber(projectFile, "cameraMotions", cameraClipIndex, "customCurve", "x2"):0.###}；控件={DescribeInspector(window)}。"));

    // 开场：顶层三种原预设与三种节奏逐项验证 JSON，确保卡片交互更新模型。
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(3)) ?? window;
    var openingRail = FindRailButton(window, "开场");
    var openingOpened = openingRail is not null && TryClickPhysical(openingRail);
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(3)) ?? window;
    var enabled = openingOpened ? FindCheckboxContaining(window, "启用全局开场") : null;
    var alreadyEnabled = ReadJsonBoolean(projectFile, "settings", "opening", "enabled") == true;
    var enabledSet = enabled is not null && (alreadyEnabled || TryClickPhysical(enabled, preferAutomationInvoke: false))
        && WaitForJsonBoolean(projectFile, ["settings", "opening", "enabled"], true, TimeSpan.FromSeconds(5));
    var presetChecks = new List<bool>();
    foreach (var (label, value) in new[] { ("聚拢", "converge"), ("侧滑", "sideSlide"), ("轻 3D", "light3D") })
    {
        window = WaitForMainWindow(processId, TimeSpan.FromSeconds(3)) ?? window;
        var card = FindButtonByHint(window, label);
        var selected = card is not null && TryClickPhysical(card)
            && WaitForJsonString(projectFile, ["settings", "opening", "preset"], value, TimeSpan.FromSeconds(5));
        presetChecks.Add(selected);
    }
    var curveChecks = new List<bool>();
    foreach (var (label, value) in new[] { ("顺滑", "smooth"), ("利落", "swift"), ("柔和", "gentle") })
    {
        window = WaitForMainWindow(processId, TimeSpan.FromSeconds(3)) ?? window;
        var card = FindButtonByHint(window, label);
        var selected = card is not null && TryClickPhysical(card)
            && WaitForJsonString(projectFile, ["settings", "opening", "elementCurve"], value, TimeSpan.FromSeconds(5));
        curveChecks.Add(selected);
    }
    AddStep(results, new("开场三种原预设与三种节奏实际写入项目", enabledSet && presetChecks.All(x => x) && curveChecks.All(x => x),
        $"启用={enabledSet}，预设={string.Join(",", presetChecks)}，节奏={string.Join(",", curveChecks)}，最终={ReadJsonString(projectFile, "settings", "opening", "preset")}/{ReadJsonString(projectFile, "settings", "opening", "elementCurve")}。"));

    // 光标：测倾斜开关和点击效果种类及三个数值的真实项目变更。
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(3)) ?? window;
    var cursorRail = FindRailButton(window, "光标");
    var cursorOpened = cursorRail is not null && TryClickPhysical(cursorRail);
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(3)) ?? window;
    var tilt = cursorOpened ? FindCheckboxContaining(window, "移动时倾斜") : null;
    var tiltSet = tilt is not null && (ReadJsonBoolean(projectFile, "settings", "cursor", "motionTiltEnabled") == true || TryClickPhysical(tilt, preferAutomationInvoke: false))
        && WaitForJsonBoolean(projectFile, ["settings", "cursor", "motionTiltEnabled"], true, TimeSpan.FromSeconds(5));
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(3)) ?? window;
    var tiltStrength = WaitForComboByName(window, "倾斜强度", TimeSpan.FromSeconds(3));
    var tiltStrengthSet = tiltStrength is not null && SelectComboOption(tiltStrength, "明显", processId, 1)
        && WaitForJsonNumber(projectFile, ["settings", "cursor", "motionTiltStrength"], 3, TimeSpan.FromSeconds(5));
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(3)) ?? window;
    var clickEnabled = FindCheckboxContaining(window, "显示点击效果");
    var clickEnabledSet = clickEnabled is not null && (ReadJsonBoolean(projectFile, "settings", "cursor", "clickEffectsEnabled") == true || TrySetToggleOn(clickEnabled))
        && WaitForJsonBoolean(projectFile, ["settings", "cursor", "clickEffectsEnabled"], true, TimeSpan.FromSeconds(5));
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(3)) ?? window;
    var effectCombo = WaitForComboByName(window, "效果", TimeSpan.FromSeconds(3));
    var effectSet = effectCombo is not null && SelectComboOption(effectCombo, "涟漪", processId, 2)
        && WaitForJsonString(projectFile, ["settings", "cursor", "clickEffect"], "ripple", TimeSpan.FromSeconds(5));
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(3)) ?? window;
    var duration = WaitForControlName(window, ControlType.Slider, "持续时间", TimeSpan.FromSeconds(3));
    var durationSet = duration is not null && SetRangeValue(duration, .84)
        && WaitForJsonNumber(projectFile, ["settings", "cursor", "clickDuration"], .84, TimeSpan.FromSeconds(5));
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(3)) ?? window;
    var scale = WaitForControlName(window, ControlType.Slider, "点击范围", TimeSpan.FromSeconds(3));
    var scaleSet = scale is not null && SetRangeValue(scale, 1.7)
        && WaitForJsonNumber(projectFile, ["settings", "cursor", "clickScale"], 1.7, TimeSpan.FromSeconds(5));
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(3)) ?? window;
    var opacity = WaitForControlName(window, ControlType.Slider, "点击不透明度", TimeSpan.FromSeconds(3));
    var opacitySet = opacity is not null && SetRangeValue(opacity, 68)
        && WaitForJsonNumber(projectFile, ["settings", "cursor", "clickOpacity"], .68, TimeSpan.FromSeconds(5));
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(3)) ?? window;
    var recordedCursor = FindButtonByHint(window, "跟随录制");
    var recordedCursorSet = recordedCursor is not null && TryClickPhysical(recordedCursor)
        && WaitForJsonString(projectFile, ["settings", "cursor", "style"], "followRecorded", TimeSpan.FromSeconds(5));
    AddStep(results, new("光标倾斜、点击反馈与指针样式实际写入项目", cursorOpened && tiltSet && tiltStrengthSet && clickEnabledSet && effectSet && durationSet && scaleSet && opacitySet && recordedCursorSet,
        $"倾斜={tiltSet}/{tiltStrengthSet}，点击={clickEnabledSet}/{effectSet}，时长/范围/不透明度={durationSet}/{scaleSet}/{opacitySet}，指针样式={recordedCursorSet}；JSON={ReadJsonString(projectFile, "settings", "cursor", "style")}/{ReadJsonString(projectFile, "settings", "cursor", "clickEffect")}/{ReadJsonNumber(projectFile, "settings", "cursor", "clickDuration")}/{ReadJsonNumber(projectFile, "settings", "cursor", "clickScale")}/{ReadJsonNumber(projectFile, "settings", "cursor", "clickOpacity")}。"));

    // 时间线右键菜单的慢放是一次真实编辑；验证项目模型写入 0.5 倍。
    window = WaitForMainWindow(processId, TimeSpan.FromSeconds(3)) ?? window;
    var timelineForRate = FindById(window, "TimelineView");
    var slowMenuDiagnostic = timelineForRate is null ? "未找到 TimelineView 自动化元素。" : "";
    var slowMenuOpened = timelineForRate is not null && OpenScreenSegmentContext(timelineForRate, window, projectFile, 1.0, out slowMenuDiagnostic);
    var slowItem = slowMenuOpened ? WaitForNameContaining("速度 0.5×", processId, TimeSpan.FromSeconds(3)) : null;
    var slowSet = slowItem is not null && TryInvoke(slowItem)
        && WaitForTimelineSegmentRate(projectFile, 0, .5, TimeSpan.FromSeconds(5));
    AddStep(results, new("时间线菜单慢放写入项目", slowSet,
        $"右键菜单={slowMenuOpened}，0.5×选项={slowItem is not null}，片段速率={ReadTimelineSegmentRate(projectFile, 0):0.##}；定位={slowMenuDiagnostic}。"));
}

static void RunRelinkOnly(string appPath, string sourceProject, string isolatedProject, List<StepResult> results)
{
    var projectDirectory = Path.GetDirectoryName(isolatedProject)!;
    var originalDirectory = Path.GetDirectoryName(sourceProject)!;
    var document = JsonNode.Parse(File.ReadAllBytes(isolatedProject))?.AsObject()
        ?? throw new InvalidDataException("测试项目 JSON 无效。");
    var originalRelative = document["mediaPath"]?.GetValue<string>()
        ?? throw new InvalidDataException("测试项目缺少主素材路径。");
    var missingCopy = ResolveProjectAsset(projectDirectory, originalRelative);
    var originalMedia = ResolveProjectAsset(originalDirectory, originalRelative);
    if (!File.Exists(originalMedia) || !File.Exists(missingCopy)) throw new FileNotFoundException("重定位 fixture 的原始或复制素材不存在。", originalMedia);
    var originalHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(originalMedia)));
    File.Delete(missingCopy);
    var app = Process.Start(new ProcessStartInfo(appPath) { UseShellExecute = false, ArgumentList = { "--open", isolatedProject } })
        ?? throw new InvalidOperationException("无法启动 FrameTrail。");
    try
    {
        var dialog = WaitForWindowName(app.Id, "素材缺失：请选择原视频", TimeSpan.FromSeconds(20));
        var dialogControls = "";
        var selected = dialog is not null && SetNativeOpenDialogPath(dialog, originalMedia, out dialogControls);
        if (dialog is null) dialogControls = "对话框未出现";
        var dialogClosed = selected && WaitForWindowClosed(app.Id, "素材缺失：请选择原视频", TimeSpan.FromSeconds(15));
        var window = dialogClosed ? WaitForMainWindow(app.Id, TimeSpan.FromSeconds(20)) : null;
        var title = window is null ? null : WaitForProjectTitle(window, TimeSpan.FromSeconds(15));
        var updatedRelative = ReadJsonString(isolatedProject, "mediaPath");
        var updatedAsset = updatedRelative is null ? null : ResolveProjectAsset(projectDirectory, updatedRelative);
        var storedInsideProject = updatedRelative is not null
            && updatedRelative.StartsWith("media/relocated-", StringComparison.OrdinalIgnoreCase)
            && updatedAsset is not null && File.Exists(updatedAsset)
            && Path.GetFullPath(updatedAsset).StartsWith(Path.GetFullPath(Path.Combine(projectDirectory, "media")) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        var copiedHash = storedInsideProject ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(updatedAsset!))) : null;
        var originalUnchanged = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(originalMedia))) == originalHash;
        var previewControl = window is null ? null : FindById(window, "PreviewImage");
        var previewPresent = previewControl is not null && !previewControl.Current.IsOffscreen;
        var decoded = storedInsideProject && DecodeFrameWithBundledFfmpeg(appPath, updatedAsset!);
        AddStep(results, new("主素材缺失后可重定位到工程内并解码", dialog is not null && selected && dialogClosed && title is not null && storedInsideProject && copiedHash == originalHash && originalUnchanged && previewPresent && decoded,
            $"缺失对话框={dialog is not null}，选回原素材={selected}，对话框关闭={dialogClosed}，项目已加载={title is not null}，mediaPath={updatedRelative}，副本哈希匹配={copiedHash == originalHash}，原素材未改={originalUnchanged}，预览控件可见={previewPresent}，FFmpeg解码={decoded}；对话框控件={dialogControls}；fixture保留于 {projectDirectory}"));

        if (window is not null)
        {
            app.CloseMainWindow();
            if (!app.WaitForExit(5000)) { app.Kill(entireProcessTree: true); app.WaitForExit(); }
            app.Dispose();
            app = Process.Start(new ProcessStartInfo(appPath) { UseShellExecute = false, ArgumentList = { "--open", isolatedProject } })
                ?? throw new InvalidOperationException("无法重新启动 FrameTrail 验证重开。");
            var reopenedDialog = WaitForWindowName(app.Id, "素材缺失：请选择原视频", TimeSpan.FromSeconds(3));
            var reopened = reopenedDialog is null ? WaitForMainWindow(app.Id, TimeSpan.FromSeconds(20)) : null;
            var reopenedTitle = reopened is null ? null : WaitForProjectTitle(reopened, TimeSpan.FromSeconds(15));
            var reopenedPreview = reopened is not null && FindById(reopened, "PreviewImage") is not null;
            var relinkPersists = reopenedTitle is not null && reopenedPreview && reopenedDialog is null
                && ReadJsonString(isolatedProject, "mediaPath") == updatedRelative
                && updatedAsset is not null && File.Exists(updatedAsset);
            AddStep(results, new("重新打开重定位项目仍使用工程内素材", relinkPersists,
                $"重新打开={reopenedTitle is not null}，再次弹缺失框={reopenedDialog is not null}，预览控件={reopenedPreview}，相对路径={ReadJsonString(isolatedProject, "mediaPath")}。"));
        }
    }
    finally
    {
        if (!app.HasExited) { app.CloseMainWindow(); if (!app.WaitForExit(3000)) app.Kill(entireProcessTree: true); }
        app.Dispose();
    }
}

static string ResolveProjectAsset(string projectDirectory, string relative)
{
    var normalized = relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
    return Path.GetFullPath(Path.Combine(projectDirectory, normalized));
}

static bool SetNativeOpenDialogPath(AutomationElement dialog, string path, out string diagnostic)
{
    var details = new List<string>();
    try
    {
        var edits = dialog.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)).Cast<AutomationElement>().ToArray();
        details.AddRange(edits.Select(x => $"Edit[{SafeName(x)}|{SafeAutomationId(x)}]"));
        var fileName = edits.FirstOrDefault(x => SafeAutomationId(x) == "1148")
            ?? edits.FirstOrDefault(x => SafeName(x).Contains("文件名", StringComparison.OrdinalIgnoreCase) || SafeName(x).Contains("File name", StringComparison.OrdinalIgnoreCase));
        if (fileName is null) { diagnostic = string.Join(";", details); return false; }
        if (!TrySetValue(fileName, path)) { diagnostic = "前台已切离测试应用或 UIA 写入失败。"; return false; }
        var value = (ValuePattern)fileName.GetCurrentPattern(ValuePattern.Pattern);
        details.Add("ValueSet=" + (value.Current.Value == path));
        if (value.Current.Value != path) { diagnostic = string.Join(";", details); return false; }
        details.AddRange(dialog.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>().Select(x => $"Button[{SafeName(x)}|{SafeAutomationId(x)}]"));
        var open = dialog.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>()
            .FirstOrDefault(x => SafeAutomationId(x) == "1")
            ?? dialog.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>()
                .FirstOrDefault(x => SafeName(x).Contains("打开", StringComparison.OrdinalIgnoreCase) || SafeName(x).Equals("Open", StringComparison.OrdinalIgnoreCase));
        if (open is null) { diagnostic = string.Join(";", details) + ";未找到Open按钮"; return false; }
        if (!InteractiveDesktopGuard.BeforeInput("文件选择对话框打开")) { diagnostic = "前台已切离测试应用；已停止后续输入。"; return false; }
        var invocation = (InvokePattern)open.GetCurrentPattern(InvokePattern.Pattern);
        var invokeTask = Task.Run(() =>
        {
            if (!InteractiveDesktopGuard.BeforeInput("文件选择对话框打开")) return false;
            invocation.Invoke();
            return true;
        });
        var invoked = invokeTask.Wait(TimeSpan.FromSeconds(5));
        if (invoked) invoked = invokeTask.GetAwaiter().GetResult();
        diagnostic = string.Join(";", details) + ";FileNameId=" + SafeAutomationId(fileName) + ";OpenId=" + SafeAutomationId(open) + ";InvokeCompleted=" + invoked;
        return invoked;
    }
    catch (Exception ex) { diagnostic = string.Join(";", details) + ";exception=" + ex.GetType().Name + ":" + ex.Message; return false; }
}

static bool DecodeFrameWithBundledFfmpeg(string appPath, string mediaPath)
{
    var directory = Path.GetDirectoryName(appPath)!;
    var candidates = new[] { Path.Combine(directory, "tools", "ffmpeg", "ffmpeg.exe"), Path.Combine(directory, "ffmpeg.exe"), Path.Combine(Environment.CurrentDirectory, "artifacts", "validation", "ffmpeg-bundle-probe", "tools", "ffmpeg", "ffmpeg.exe") };
    var ffmpeg = candidates.FirstOrDefault(File.Exists);
    if (ffmpeg is null) return false;
    try
    {
        var process = Process.Start(new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            ArgumentList = { "-v", "error", "-i", mediaPath, "-frames:v", "1", "-f", "null", "NUL" }
        });
        if (process is null) return false;
        if (!process.WaitForExit(15000)) { process.Kill(entireProcessTree: true); return false; }
        return process.ExitCode == 0;
    }
    catch { return false; }
}

static AutomationElement? WaitForWindow(Process process, TimeSpan timeout)
{
    InteractiveDesktopGuard.BindTargetProcess(process.Id);
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until && !process.HasExited && !InteractiveDesktopGuard.IsAborted)
    {
        process.Refresh();
        try
        {
            var candidate = AutomationElement.RootElement.FindAll(TreeScope.Children, System.Windows.Automation.Condition.TrueCondition).Cast<AutomationElement>()
                .Where(x => (int)x.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty) == process.Id && x.Current.ControlType == ControlType.Window)
                .OrderByDescending(x => x.Current.IsEnabled).FirstOrDefault();
            if (candidate is not null) return candidate;
        }
        catch { }
        if (process.MainWindowHandle != IntPtr.Zero) try { return AutomationElement.FromHandle(process.MainWindowHandle); } catch (ElementNotAvailableException) { }
        Thread.Sleep(250);
    }
    return null;
}
static bool WaitForFrameTrailVisualReady(AutomationElement mainWindow, TimeSpan timeout, out string detail)
{
    var until = DateTime.UtcNow + timeout;
    var preview = FindById(mainWindow, "PreviewImage");
    var timeline = FindById(mainWindow, "TimelineView");
    string previewState = "missing";
    string timelineState = "missing";
    while (DateTime.UtcNow < until && !InteractiveDesktopGuard.IsAborted)
    {
        try { previewState = preview?.Current.ItemStatus ?? "missing"; } catch (ElementNotAvailableException) { previewState = "unavailable"; }
        try { timelineState = timeline?.Current.ItemStatus ?? "missing"; } catch (ElementNotAvailableException) { timelineState = "unavailable"; }
        if (previewState.Equals("error", StringComparison.OrdinalIgnoreCase)
            || timelineState.Equals("error", StringComparison.OrdinalIgnoreCase))
        {
            detail = $"PreviewImage={previewState}，TimelineView={timelineState}。";
            return false;
        }
        if (previewState.Equals("ready", StringComparison.OrdinalIgnoreCase)
            && timelineState.Equals("ready", StringComparison.OrdinalIgnoreCase))
        {
            var windowBounds = mainWindow.Current.BoundingRectangle;
            var previewBounds = preview?.Current.BoundingRectangle ?? Rect.Empty;
            var timelineBounds = timeline?.Current.BoundingRectangle ?? Rect.Empty;
            var viewportDetail = "TimelineView UIA 元素缺失。";
            var timelineViewport = timeline is null ? Rect.Empty : FindTimelineViewportBounds(mainWindow, timeline, out viewportDetail);
            var visibleTimelineBounds = IntersectRects(timelineBounds, timelineViewport);
            var boundsValid = IsUsableRect(windowBounds) && IsUsableRect(previewBounds) && IsUsableRect(timelineBounds)
                && RectContains(windowBounds, previewBounds) && IsUsableRect(visibleTimelineBounds);
            detail = $"PreviewImage={previewState} bounds=({previewBounds.Left:0},{previewBounds.Top:0},{previewBounds.Width:0},{previewBounds.Height:0})，TimelineView={timelineState} full=({timelineBounds.Left:0},{timelineBounds.Top:0},{timelineBounds.Width:0},{timelineBounds.Height:0}) visible=({visibleTimelineBounds.Left:0},{visibleTimelineBounds.Top:0},{visibleTimelineBounds.Width:0},{visibleTimelineBounds.Height:0})，viewport={viewportDetail}，位于窗口可见区={boundsValid}。";
            return boundsValid;
        }
        Thread.Sleep(150);
    }
    detail = $"等待超时或守卫中止：PreviewImage={previewState}，TimelineView={timelineState}。";
    return false;
}
static bool IsUsableRect(Rect rect) => !rect.IsEmpty && double.IsFinite(rect.Left) && double.IsFinite(rect.Top)
    && double.IsFinite(rect.Width) && double.IsFinite(rect.Height) && rect.Width >= 2 && rect.Height >= 2;
static bool RectContains(Rect container, Rect child) => IsUsableRect(container) && IsUsableRect(child)
    && child.Left >= container.Left - 2 && child.Top >= container.Top - 2
    && child.Right <= container.Right + 2 && child.Bottom <= container.Bottom + 2;
static Rect IntersectRects(Rect first, Rect second)
{
    if (!IsUsableRect(first) || !IsUsableRect(second)) return Rect.Empty;
    var left = Math.Max(first.Left, second.Left);
    var top = Math.Max(first.Top, second.Top);
    var right = Math.Min(first.Right, second.Right);
    var bottom = Math.Min(first.Bottom, second.Bottom);
    return right > left && bottom > top ? new Rect(left, top, right - left, bottom - top) : Rect.Empty;
}
static Rect FindTimelineViewportBounds(AutomationElement mainWindow, AutomationElement timeline, out string detail)
{
    var mainBounds = mainWindow.Current.BoundingRectangle;
    var parent = TreeWalker.ControlViewWalker.GetParent(timeline);
    for (var depth = 0; parent is not null && depth < 24; depth++)
    {
        try
        {
            if (parent.Current.ControlType == ControlType.Pane)
            {
                var pane = parent.Current.BoundingRectangle;
                var clippedPane = IntersectRects(pane, mainBounds);
                if (IsUsableRect(clippedPane))
                {
                    detail = $"最近Pane视口({clippedPane.Left:0},{clippedPane.Top:0},{clippedPane.Width:0},{clippedPane.Height:0})";
                    return clippedPane;
                }
            }
            if ((IntPtr)parent.Current.NativeWindowHandle == (IntPtr)mainWindow.Current.NativeWindowHandle) break;
            parent = TreeWalker.ControlViewWalker.GetParent(parent);
        }
        catch (ElementNotAvailableException) { break; }
    }
    detail = $"未发现ScrollViewer Pane，退回主窗可视区({mainBounds.Left:0},{mainBounds.Top:0},{mainBounds.Width:0},{mainBounds.Height:0})";
    return mainBounds;
}
static bool CaptureElementRegion(AutomationElement element, string path, out string detail, Rect? clipBounds = null)
{
    detail = "";
    if (!InteractiveDesktopGuard.BeforeInput("读取测试界面区域截图")) { detail = "前台守卫拒绝截图。"; return false; }
    Rect bounds;
    try { bounds = element.Current.BoundingRectangle; }
    catch (ElementNotAvailableException) { detail = "UIA元素已失效。"; return false; }
    if (!IsUsableRect(bounds)) { detail = $"区域无效：{bounds}。"; return false; }
    if (clipBounds is { } clip) bounds = IntersectRects(bounds, clip);
    if (!IsUsableRect(bounds) || (clipBounds is null && element.Current.IsOffscreen)) { detail = $"区域无效或不可见：{bounds}。"; return false; }
    var x = (int)Math.Floor(bounds.Left); var y = (int)Math.Floor(bounds.Top);
    var width = (int)Math.Ceiling(bounds.Right) - x; var height = (int)Math.Ceiling(bounds.Bottom) - y;
    var screenLeft = NativeMethods.GetSystemMetrics(76); var screenTop = NativeMethods.GetSystemMetrics(77);
    var screenWidth = NativeMethods.GetSystemMetrics(78); var screenHeight = NativeMethods.GetSystemMetrics(79);
    var withinVirtualDesktop = x >= screenLeft && y >= screenTop && x + width <= screenLeft + screenWidth && y + height <= screenTop + screenHeight;
    if (width < 2 || height < 2 || !withinVirtualDesktop) { detail = $"区域越出虚拟桌面或尺寸无效：({x},{y},{width},{height})，桌面=({screenLeft},{screenTop},{screenWidth},{screenHeight})。"; return false; }
    var source = NativeMethods.GetDC(IntPtr.Zero); var memory = NativeMethods.CreateCompatibleDC(source);
    var bitmap = NativeMethods.CreateCompatibleBitmap(source, width, height); var previous = NativeMethods.SelectObject(memory, bitmap);
    try
    {
        if (!NativeMethods.BitBlt(memory, 0, 0, width, height, source, x, y, 0x00CC0020)) { detail = "BitBlt失败。"; return false; }
        var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        image.Freeze(); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var file = File.Create(path)) encoder.Save(file);
        bool validPng;
        using (var verify = File.OpenRead(path))
        {
            var frame = BitmapFrame.Create(verify, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            validPng = verify.Length > 256 && frame.PixelWidth == width && frame.PixelHeight == height;
        }
        detail = $"路径={path}，bounds=({x},{y},{width},{height})，桌面范围内={withinVirtualDesktop}，PNG={validPng}。";
        return validPng;
    }
    finally { NativeMethods.SelectObject(memory, previous); NativeMethods.DeleteObject(bitmap); NativeMethods.DeleteDC(memory); NativeMethods.ReleaseWindowDC(IntPtr.Zero, source); }
}
static bool CaptureWindowToFile(AutomationElement window, string path)
{
    try { CaptureWindow(window, path); return File.Exists(path); }
    catch (InvalidOperationException) { return false; }
}
static bool CaptureVisibleOwnedMenus(int processId, string pathPrefix, out string details, out int capturedRegions, AutomationElement? ownerWindow = null)
{
    var menuCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu);
    var candidates = new List<(AutomationElement Element, Rect Bounds, string Kind)>();
    var seenBounds = new HashSet<string>(StringComparer.Ordinal);
    void Add(AutomationElement element, string kind)
    {
        try
        {
            if (!IsElementVisible(element)) return;
            var bounds = element.Current.BoundingRectangle;
            var key = $"{Math.Round(bounds.Left)}:{Math.Round(bounds.Top)}:{Math.Round(bounds.Width)}:{Math.Round(bounds.Height)}";
            if (seenBounds.Add(key)) candidates.Add((element, bounds, kind));
        }
        catch (ElementNotAvailableException) { }
    }
    foreach (var menu in FindOwnedProcessDescendants(processId, menuCondition)) Add(menu, "Menu UIA");

    var ownerHandle = IntPtr.Zero;
    try { if (ownerWindow is not null) ownerHandle = (IntPtr)ownerWindow.Current.NativeWindowHandle; } catch { }
    var popupWindows = 0;
    foreach (var window in FindOwnedProcessWindows(processId))
    {
        try
        {
            var handle = (IntPtr)window.Current.NativeWindowHandle;
            if (handle == IntPtr.Zero || handle == ownerHandle || !IsOwnedWindowVisible(window, processId)) continue;
            var menuItems = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem));
            if (!menuItems.Cast<AutomationElement>().Any(IsElementVisible)) continue;
            popupWindows++;
            Add(window, "独立弹出窗口");
        }
        catch (ElementNotAvailableException) { }
    }

    if (candidates.Count == 0) { details = "未发现可见且归属测试进程的菜单区域。"; capturedRegions = 0; return false; }
    capturedRegions = 0; var lines = new List<string>();
    for (var i = 0; i < candidates.Count; i++)
    {
        var candidate = candidates[i];
        var path = $"{pathPrefix}-{i + 1}.png";
        if (CaptureElementRegion(candidate.Element, path, out var detail)) capturedRegions++;
        lines.Add($"{candidate.Kind} bounds=({candidate.Bounds.Left:0},{candidate.Bounds.Top:0},{candidate.Bounds.Width:0},{candidate.Bounds.Height:0}) {detail}");
    }
    details = $"去重可见菜单区域={candidates.Count}，独立弹出窗口={popupWindows}，截图={capturedRegions}，" + string.Join("；", lines);
    return capturedRegions == candidates.Count;
}

static void RunTransientSurfacesUiTests(AutomationElement initialWindow, Process app, string projectFile, string outputDirectory, List<StepResult> results)
{
    var processId = app.Id;
    var settingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameTrail", "settings.json");
    var currentDark = ReadUserDarkTheme(settingsFile);
    var originalDark = currentDark;
    var success = true;
    try
    {
    foreach (var targetDark in new[] { false, true })
    {
        if (InteractiveDesktopGuard.IsAborted) return;
        var main = WaitForMainWindow(processId, TimeSpan.FromSeconds(3)) ?? initialWindow;
        if (currentDark != targetDark)
        {
            var themeButton = FindButtonByHint(main, "明暗切换");
            var changed = themeButton is not null && TryClickPhysical(themeButton);
            currentDark = targetDark;
            AddStep(results, new(targetDark ? "切换深色浮层主题" : "切换浅色浮层主题", changed,
                $"主题按钮={themeButton is not null}，已点击={changed}。"));
            success &= changed;
            if (!changed || InteractiveDesktopGuard.IsAborted) return;
            Thread.Sleep(350);
            main = WaitForMainWindow(processId, TimeSpan.FromSeconds(3)) ?? main;
        }
        var theme = targetDark ? "dark" : "light";
        var mainShot = Path.Combine(outputDirectory, $"{theme}-main.png");
        var previewShot = Path.Combine(outputDirectory, $"{theme}-preview.png");
        var timelineShot = Path.Combine(outputDirectory, $"{theme}-timeline.png");
        CaptureWindow(main, mainShot);
        var previewElement = FindById(main, "PreviewImage");
        var timelineElement = FindById(main, "TimelineView");
        var previewDetail = "预览 UIA 元素缺失。"; var timelineDetail = "时间线 UIA 元素缺失。";
        var previewCaptured = previewElement is not null && CaptureElementRegion(previewElement, previewShot, out previewDetail);
        var timelineViewport = timelineElement is null ? Rect.Empty : FindTimelineViewportBounds(main, timelineElement, out timelineDetail);
        if (timelineElement is null) timelineDetail = "时间线 UIA 元素缺失。";
        var timelineCaptured = timelineElement is not null && CaptureElementRegion(timelineElement, timelineShot, out timelineDetail, timelineViewport);
        var regionsCaptured = File.Exists(mainShot) && previewCaptured && timelineCaptured;
        AddStep(results, new($"{(targetDark ? "深色" : "浅色")}主界面、预览和时间线截图", regionsCaptured,
            $"主窗={mainShot}，预览={previewDetail}，时间线={timelineDetail}。"));
        success &= regionsCaptured;

        // The Settings menu contains a nested settings/help surface and opens a real modal window.
        var settingsButton = FindButtonByHint(main, "设置");
        var menuOpened = settingsButton is not null && TryClickPhysical(settingsButton);
        var settingsItem = menuOpened ? WaitForMenuItem(processId, "录制与界面设置", TimeSpan.FromSeconds(3)) : null;
        var menuShot = Path.Combine(outputDirectory, $"{theme}-settings-menu.png");
        var settingsMenuDetail = "设置菜单未打开。";
        var settingsMenuCaptured = menuOpened && CaptureVisibleOwnedMenus(processId, Path.Combine(outputDirectory, $"{theme}-settings-menu"), out settingsMenuDetail, out _, main);
        if (menuOpened && !settingsMenuCaptured) _ = CaptureWindowToFile(main, menuShot);
        var settingsOpened = settingsItem is not null && TryInvoke(settingsItem);
        var dialog = settingsOpened ? WaitForWindowName(processId, "录制与界面设置", TimeSpan.FromSeconds(4)) : null;
        var combo = dialog is null ? null : WaitForComboByName(dialog, "预览分辨率", TimeSpan.FromSeconds(3));
        var comboExpanded = combo is not null && TryExpand(combo);
        var comboStable = comboExpanded && combo is not null && WaitForStableComboPopup(combo, TimeSpan.FromSeconds(3));
        var comboShot = Path.Combine(outputDirectory, $"{theme}-combo-open.png");
        if (comboStable && dialog is not null) CaptureWindow(dialog, comboShot);
        var comboPopupShot = Path.Combine(outputDirectory, $"{theme}-combo-popup.png");
        var comboPopupDetail = comboExpanded ? "未发现可见下拉选项区域。" : "下拉未展开。";
        var comboPopupCaptured = comboStable && combo is not null
            && CaptureVisibleComboPopup(combo, comboPopupShot, out comboPopupDetail);
        var comboFocus = comboExpanded && combo is not null && TrySetAutomationFocus(combo);
        if (comboFocus) SendEscapeToVisibleOwnedWindow(combo!, dialog!, processId, "ComboBox popup priority");
        var comboCollapsed = combo is not null && IsComboCollapsed(combo);
        var dialogRemains = dialog is not null && IsOwnedWindowVisible(dialog, processId);
        var comboEscPriority = settingsMenuCaptured && comboStable && File.Exists(comboShot) && comboPopupCaptured
            && comboFocus && comboCollapsed && dialogRemains && !InteractiveDesktopGuard.IsAborted;
        AddStep(results, new($"{(targetDark ? "深色" : "浅色")}下拉框 Escape 先关闭子弹层", comboEscPriority,
            $"设置菜单={menuOpened}，菜单截图={settingsMenuDetail}，设置窗={dialog is not null}，combo展开={comboExpanded}，弹层稳定250ms={comboStable}，焦点={comboFocus}，选项截图={comboPopupDetail}，下拉已收起={comboCollapsed}，设置窗仍开={dialogRemains}，截图={Path.Combine(outputDirectory, $"{theme}-combo-open.png")}。"));
        success &= comboEscPriority;
        if (dialog is not null && dialogRemains)
        {
            var secondEscape = SendEscapeToVisibleOwnedWindow(dialog, dialog, processId, "Settings dialog close");
            var dialogClosed = secondEscape && WaitForWindowClosed(processId, "录制与界面设置", TimeSpan.FromSeconds(3));
            AddStep(results, new($"{(targetDark ? "深色" : "浅色")}第二次 Escape 关闭设置对话框", dialogClosed,
                $"第二次Escape={secondEscape}，对话框关闭={dialogClosed}。"));
            success &= dialogClosed;
        }
        else if (dialog is not null) CloseWindowWithoutKeyboard(dialog);
        if (InteractiveDesktopGuard.IsAborted) return;

        // Exercise the nested Recent Projects submenu; opening the submenu must not dismiss its parent.
        main = WaitForMainWindow(processId, TimeSpan.FromSeconds(3)) ?? main;
        var projectMenu = FindButtonByHint(main, "项目");
        var projectMenuOpened = projectMenu is not null && TryClickPhysical(projectMenu);
        var recentItem = projectMenuOpened ? WaitForMenuItem(processId, "最近项目", TimeSpan.FromSeconds(3)) : null;
        var recentExpanded = recentItem is not null && (TryExpand(recentItem) || HoverOwnedElement(recentItem, processId));
        var recentChild = recentExpanded ? WaitForMenuItem(processId, Path.GetFileNameWithoutExtension(projectFile), TimeSpan.FromSeconds(3)) : null;
        if (recentChild is null && recentExpanded) recentChild = WaitForMenuItem(processId, Path.GetFileName(Path.GetDirectoryName(projectFile)!), TimeSpan.FromSeconds(2));
        var nestedShot = Path.Combine(outputDirectory, $"{theme}-nested-menu-open.png");
        var nestedMenuDetail = "最近项目子菜单未展开。";
        var nestedMenuCaptured = recentExpanded && CaptureVisibleOwnedMenus(processId, Path.Combine(outputDirectory, $"{theme}-nested-menu"), out nestedMenuDetail, out var nestedRegions, main) && nestedRegions >= 2;
        if (recentExpanded && !nestedMenuCaptured) _ = CaptureWindowToFile(main, nestedShot);
        var recentVisible = recentItem is not null && IsElementVisible(recentItem);
        var childVisible = recentChild is not null && IsElementVisible(recentChild);
        var nestedEscapeSent = recentItem is not null && SendEscapeToVisibleOwnedWindow(recentItem, main, processId, "Nested context menu child close");
        var parentRemains = recentItem is not null && IsElementVisible(recentItem);
        var childClosedFirst = recentChild is null || !IsElementVisible(recentChild);
        var nestedEscapePass = projectMenuOpened && recentItem is not null && recentExpanded && nestedMenuCaptured && recentVisible
            && nestedEscapeSent && parentRemains && childClosedFirst && !InteractiveDesktopGuard.IsAborted;
        AddStep(results, new($"{(targetDark ? "深色" : "浅色")}嵌套最近项目菜单 Escape 优先级", nestedEscapePass,
            $"项目菜单={projectMenuOpened}，最近项目={recentItem is not null}，子项目={recentChild is not null}，打开后可见={childVisible}，菜单截图={nestedMenuDetail}，Escape={nestedEscapeSent}，父菜单保留={parentRemains}，子菜单已收起={childClosedFirst}，截图={nestedShot}。"));
        success &= nestedEscapePass;
        if (InteractiveDesktopGuard.IsAborted) return;
        if (parentRemains)
        {
            var parentEscape = SendEscapeToVisibleOwnedWindow(recentItem!, main, processId, "Parent context menu close");
            var parentClosed = parentEscape && WaitForMenuItemClosed(processId, "最近项目", TimeSpan.FromSeconds(2));
            AddStep(results, new($"{(targetDark ? "深色" : "浅色")}第二次 Escape 关闭父菜单", parentClosed,
                $"第二次Escape={parentEscape}，父菜单关闭={parentClosed}。"));
            success &= parentClosed;
        }
    else _ = CloseOwnedTransientPopups(processId, main);
        if (InteractiveDesktopGuard.IsAborted) return;

        // ToolTip is shown by pointing at the theme control; the pointer move is guarded and uses no click.
        main = WaitForMainWindow(processId, TimeSpan.FromSeconds(3)) ?? main;
        var tooltipTarget = FindButtonByHint(main, "明暗切换");
        var hovered = tooltipTarget is not null && HoverOwnedElement(tooltipTarget, processId);
        var tooltip = hovered ? WaitForOwnedToolTip(processId, TimeSpan.FromSeconds(3)) : null;
        var tooltipShot = Path.Combine(outputDirectory, $"{theme}-tooltip.png");
        var tooltipDetail = "未发现 ToolTip。";
        var tooltipCaptured = tooltip is not null && CaptureElementRegion(tooltip, tooltipShot, out tooltipDetail);
        AddStep(results, new($"{(targetDark ? "深色" : "浅色")}悬停提示截图", hovered && tooltipCaptured,
            $"悬停={hovered}，ToolTip UIA={tooltip is not null}，{tooltipDetail}。"));
        success &= hovered && tooltipCaptured;
        if (InteractiveDesktopGuard.IsAborted) return;
        if (tooltip is not null) HoverOwnedElement(main, processId);

        // The rename prompt is cancelled with Escape after a screenshot, so project data stays unchanged.
        main = WaitForMainWindow(processId, TimeSpan.FromSeconds(3)) ?? main;
        var originalTitle = ReadProjectDisplayName(projectFile);
        var renameButton = FindButtonByHint(main, "重命名项目");
        var promptOpened = renameButton is not null && TryClickPhysical(renameButton);
        var prompt = promptOpened ? WaitForWindowName(processId, "重命名项目", TimeSpan.FromSeconds(3)) : null;
        var promptShot = Path.Combine(outputDirectory, $"{theme}-text-dialog.png");
        var promptCaptured = false;
        if (prompt is not null)
        {
            CaptureWindow(prompt, promptShot);
            promptCaptured = File.Exists(promptShot) && WindowIsWithinVirtualDesktop(prompt);
        }
        var promptEscape = prompt is not null && SendEscapeToVisibleOwnedWindow(prompt, prompt, processId, "Text prompt cancel");
        var promptClosed = promptEscape && WaitForWindowClosed(processId, "重命名项目", TimeSpan.FromSeconds(3));
        var titleUnchanged = string.Equals(ReadProjectDisplayName(projectFile), originalTitle, StringComparison.Ordinal);
        var promptPass = promptOpened && promptCaptured && promptEscape && promptClosed && titleUnchanged && !InteractiveDesktopGuard.IsAborted;
        AddStep(results, new($"{(targetDark ? "深色" : "浅色")}文字对话框截图与取消", promptPass,
            $"窗口={prompt is not null}，截图={promptShot}，Escape={promptEscape}，关闭={promptClosed}，项目名未变={titleUnchanged}。"));
        success &= promptPass;
        if (prompt is not null && !promptClosed) CloseWindowWithoutKeyboard(prompt);
    }

    // Create a disposable preset so the real Yes/No confirmation can be shown and
    // cancelled without touching any existing user preset.
    var disposableName = "UIA-Surface-" + Guid.NewGuid().ToString("N")[..10];
    string? disposablePreset = null;
    var confirmPass = false;
    var confirmDetail = "未能创建本轮专用临时预设。";
    try
    {
        var main = WaitForMainWindow(processId, TimeSpan.FromSeconds(3)) ?? initialWindow;
        var saveMenuOpened = OpenPresetMenu(main);
        var saveFound = false;
        var savePromptCompleted = false;
        if (saveMenuOpened)
        {
            var save = WaitForMenuItem(processId, "将当前样式保存为本机预设", TimeSpan.FromSeconds(3));
            saveFound = save is not null;
            if (save is not null && TryInvoke(save))
                savePromptCompleted = CompleteTextPrompt(processId, "保存场景预设", disposableName, out _);
            if (savePromptCompleted)
                disposablePreset = WaitForPresetFile(disposableName, TimeSpan.FromSeconds(5));
        }
        if (disposablePreset is not null)
        {
            main = WaitForMainWindow(processId, TimeSpan.FromSeconds(3)) ?? main;
            var deleteMenuOpened = OpenPresetMenu(main);
            var submenuOpen = false;
            AutomationElement? remove = null;
            var confirmationCommandInvoked = false;
            var confirmationWindowFound = false;
            if (deleteMenuOpened)
            {
                var entry = WaitForMenuItem(processId, disposableName, TimeSpan.FromSeconds(3));
                submenuOpen = entry is not null && (TryExpand(entry) || TryClickPhysical(entry));
                remove = submenuOpen ? WaitForMenuItem(processId, "删除本机预设", TimeSpan.FromSeconds(2)) : null;
                confirmationCommandInvoked = remove is not null && TryInvoke(remove);
                var confirmation = confirmationCommandInvoked ? WaitForWindowName(processId, "删除预设", TimeSpan.FromSeconds(3)) : null;
                confirmationWindowFound = confirmation is not null;
                var screenshot = Path.Combine(outputDirectory, "disposable-preset-confirmation.png");
                var captured = false;
                if (confirmation is not null)
                {
                    CaptureWindow(confirmation, screenshot);
                    captured = File.Exists(screenshot) && new FileInfo(screenshot).Length > 256
                        && WindowIsWithinVirtualDesktop(confirmation);
                }
                var cancelSent = confirmation is not null
                    && SendEscapeToVisibleOwnedWindow(confirmation, confirmation, processId, "Disposable preset confirmation cancel");
                var closed = cancelSent && WaitForWindowClosed(processId, "删除预设", TimeSpan.FromSeconds(3));
                var presetPreserved = File.Exists(disposablePreset);
                confirmPass = confirmationCommandInvoked && captured && cancelSent && closed && presetPreserved && !InteractiveDesktopGuard.IsAborted;
                confirmDetail = $"窗口={confirmation is not null}，截图={screenshot}，Escape={cancelSent}，关闭={closed}，临时预设仍在={presetPreserved}。";
            }
            if (!deleteMenuOpened || !submenuOpen || remove is null || !confirmationCommandInvoked || !confirmationWindowFound)
                confirmDetail = $"删除菜单={deleteMenuOpened}，预设子菜单={submenuOpen}，删除命令={remove is not null}，命令调用={confirmationCommandInvoked}，确认窗={confirmationWindowFound}，预设文件={disposablePreset}。";
        }
        else confirmDetail = $"保存菜单={saveMenuOpened}，命令={saveFound}，名称对话框已确认={savePromptCompleted}，预设文件={disposablePreset}。";
    }
    catch (ApplicationErrorDialogException) { throw; }
    catch (Exception ex)
    {
        confirmDetail = $"临时预设确认流程异常：{ex.GetType().Name}: {ex.Message}。";
        if (InteractiveDesktopGuard.IsAborted) SmokeProgress.MarkIncomplete(confirmDetail);
    }
    finally
    {
        if (disposablePreset is not null && File.Exists(disposablePreset))
        {
            try { Directory.Delete(Path.GetDirectoryName(disposablePreset)!, recursive: true); }
            catch (Exception ex) { SmokeProgress.AppendMessage($"无法清理本轮自建临时预设：{disposableName}；{ex.GetType().Name}: {ex.Message}"); }
        }
        _ = CloseOwnedTransientPopups(processId, WaitForMainWindow(processId, TimeSpan.FromSeconds(2)) ?? initialWindow);
    }
    AddStep(results, new("临时预设删除确认框截图与 Escape 取消", confirmPass, confirmDetail));
    success &= confirmPass;
    var files = Directory.EnumerateFiles(outputDirectory, "*.png").ToArray();
    AddStep(results, new("浮层视觉模式已归档截图", success && files.Length >= 10,
        $"目录={outputDirectory}，PNG数量={files.Length}，浅/深主题包含主界面、预览、时间线、设置菜单、下拉、嵌套菜单、提示框和对话框；另有临时预设确认框截图。"));
    }
    finally
    {
        var restored = ReadUserDarkTheme(settingsFile) == originalDark;
        if (!restored && !InteractiveDesktopGuard.IsAborted && !app.HasExited)
        {
            var main = WaitForMainWindow(processId, TimeSpan.FromSeconds(2)) ?? initialWindow;
            var themeButton = FindButtonByHint(main, "明暗切换");
            var clicked = themeButton is not null && TryClickPhysical(themeButton);
            restored = clicked && SpinWaitUntil(() => ReadUserDarkTheme(settingsFile) == originalDark, TimeSpan.FromSeconds(3));
            if (restored) currentDark = originalDark;
        }
        if (!restored)
            SmokeProgress.AppendMessage($"浮层测试主题恢复状态：成功={restored}，原值={originalDark}，当前={currentDark}，守卫中止={InteractiveDesktopGuard.IsAborted}。用户设置快照由Smoke结束清理处理。");
    }
}

static bool ReadUserDarkTheme(string settingsFile)
{
    try
    {
        using var doc = JsonDocument.Parse(ReadSharedBytes(settingsFile));
        return TryGetPropertyIgnoreCase(doc.RootElement, "DarkTheme", out var value) && value.ValueKind == JsonValueKind.True;
    }
    catch { return false; }
}
static bool IsComboCollapsed(AutomationElement combo)
{
    try { return ((ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Current.ExpandCollapseState == ExpandCollapseState.Collapsed; }
    catch { return false; }
}
static bool CaptureVisibleComboPopup(AutomationElement combo, string path, out string detail)
{
    var candidates = new List<AutomationElement>();
    foreach (var type in new[] { ControlType.List, ControlType.ListItem })
    {
        try
        {
            candidates.AddRange(combo.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, type))
                .Cast<AutomationElement>().Where(IsElementVisible));
        }
        catch (ElementNotAvailableException) { }
    }
    var captured = 0;
    var bounds = new List<string>();
    for (var i = 0; i < candidates.Count; i++)
    {
        var candidatePath = candidates.Count == 1 ? path : Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + $"-{i + 1}.png");
        if (CaptureElementRegion(candidates[i], candidatePath, out var captureDetail)) captured++;
        try
        {
            var rect = candidates[i].Current.BoundingRectangle;
            bounds.Add($"({rect.Left:0},{rect.Top:0},{rect.Width:0},{rect.Height:0})");
        }
        catch { }
    }
    detail = $"可见列表项={candidates.Count}，截图={captured}，bounds=[{string.Join(";", bounds)}]。";
    return candidates.Count > 0 && captured == candidates.Count;
}
static bool WaitForStableComboPopup(AutomationElement combo, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until && !InteractiveDesktopGuard.IsAborted)
    {
        var first = GetComboPopupBounds(combo);
        if (IsUsableRect(first))
        {
            Thread.Sleep(250);
            var second = GetComboPopupBounds(combo);
            if (IsUsableRect(second)
                && Math.Abs(first.Left - second.Left) <= 2
                && Math.Abs(first.Top - second.Top) <= 2
                && Math.Abs(first.Width - second.Width) <= 2
                && Math.Abs(first.Height - second.Height) <= 2)
                return true;
        }
        Thread.Sleep(60);
    }
    return false;
}
static Rect GetComboPopupBounds(AutomationElement combo)
{
    foreach (var type in new[] { ControlType.List, ControlType.ListItem })
    {
        try
        {
            var bounds = combo.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, type))
                .Cast<AutomationElement>().Where(IsElementVisible).Select(x => x.Current.BoundingRectangle).Where(IsUsableRect).ToArray();
            if (bounds.Length == 0) continue;
            return bounds.Aggregate(Rect.Union);
        }
        catch (ElementNotAvailableException) { }
    }
    return Rect.Empty;
}
static bool IsElementVisible(AutomationElement element)
{
    try { return !element.Current.IsOffscreen && IsUsableRect(element.Current.BoundingRectangle); }
    catch { return false; }
}
static bool IsOwnedWindowVisible(AutomationElement window, int processId)
{
    try
    {
        var handle = (IntPtr)window.Current.NativeWindowHandle;
        NativeMethods.GetWindowThreadProcessId(handle, out var ownerPid);
        return handle != IntPtr.Zero && ownerPid == (uint)processId && NativeMethods.IsWindow(handle) && NativeMethods.IsWindowVisible(handle);
    }
    catch { return false; }
}
static bool WindowIsWithinVirtualDesktop(AutomationElement window)
{
    try
    {
        var bounds = window.Current.BoundingRectangle;
        var left = NativeMethods.GetSystemMetrics(76); var top = NativeMethods.GetSystemMetrics(77);
        var width = NativeMethods.GetSystemMetrics(78); var height = NativeMethods.GetSystemMetrics(79);
        return IsUsableRect(bounds) && bounds.Left >= left && bounds.Top >= top && bounds.Right <= left + width && bounds.Bottom <= top + height;
    }
    catch { return false; }
}
static bool SendEscapeToVisibleOwnedWindow(AutomationElement target, AutomationElement owner, int processId, string purpose)
{
    if (!InteractiveDesktopGuard.IsOwnedProcessId(processId) || !IsOwnedWindowVisible(owner, processId)
        || !IsElementVisible(target)) return false;
    try
    {
        if ((int)target.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty) != processId) return false;
    }
    catch { return false; }
    if (!TrySetAutomationFocus(target) || InteractiveDesktopGuard.IsAborted) return false;
    SmokeProgress.AppendMessage($"在可见的测试窗口中发送Escape：{purpose}，PID={processId}。");
    SendKey(0x1B, false);
    return !InteractiveDesktopGuard.IsAborted;
}
static bool HoverOwnedElement(AutomationElement element, int processId)
{
    try
    {
        if (!InteractiveDesktopGuard.BindInputTargetIfForeground(processId, "悬停测试控件")
            || !IsElementVisible(element)) return false;
        var bounds = element.Current.BoundingRectangle;
        var x = (int)(bounds.Left + bounds.Width / 2); var y = (int)(bounds.Top + bounds.Height / 2);
        if (!NativeMethods.SetCursorPos(x, y)) return false;
        Thread.Sleep(650);
        var point = new PointNative { X = x, Y = y };
        var hwnd = NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(point), 2);
        NativeMethods.GetWindowThreadProcessId(hwnd, out var ownerPid);
        return ownerPid == (uint)processId && InteractiveDesktopGuard.IsOwnedProcessId((int)ownerPid);
    }
    catch { return false; }
}
static AutomationElement? WaitForOwnedToolTip(int processId, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until && !InteractiveDesktopGuard.IsAborted)
    {
        var tooltip = FindOwnedProcessDescendants(processId, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ToolTip))
            .FirstOrDefault(IsElementVisible);
        if (tooltip is not null) return tooltip;
        foreach (var window in FindOwnedProcessWindows(processId))
            if (SafeName(window).Length > 0 && SafeName(window).Contains("提示", StringComparison.Ordinal) && IsElementVisible(window)) return window;
        Thread.Sleep(100);
    }
    return null;
}
static bool WaitForMenuItemClosed(int processId, string namePart, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until && !InteractiveDesktopGuard.IsAborted)
    {
        if (!FindOwnedProcessDescendants(processId, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem))
            .Any(x => SafeName(x).Contains(namePart, StringComparison.OrdinalIgnoreCase) && IsElementVisible(x))) return true;
        Thread.Sleep(80);
    }
    return !FindOwnedProcessDescendants(processId, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem))
        .Any(x => SafeName(x).Contains(namePart, StringComparison.OrdinalIgnoreCase) && IsElementVisible(x));
}
static string ReadProjectDisplayName(string projectFile)
{
    try
    {
        using var doc = JsonDocument.Parse(ReadSharedBytes(projectFile));
        return TryGetPropertyIgnoreCase(doc.RootElement, "displayName", out var value) ? value.GetString() ?? "" : "";
    }
    catch { return ""; }
}
static bool SpinWaitUntil(Func<bool> condition, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until && !InteractiveDesktopGuard.IsAborted)
    {
        try { if (condition()) return true; }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        Thread.Sleep(50);
    }
    return false;
}
static AutomationElement? WaitForMainWindow(int processId, TimeSpan timeout)
{
    InteractiveDesktopGuard.BindTargetProcess(processId);
    return WaitForWindowName(processId, "FrameTrail", timeout);
}
static AutomationElement? FindById(AutomationElement root, string id) => root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
static AutomationElement? FindNamed(AutomationElement root, string name) => root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name));
static AutomationElement? FindRailButton(AutomationElement root, string text)
{
    var bounds = root.Current.BoundingRectangle;
    var namedButton = root.FindAll(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.NameProperty, text)))
        .Cast<AutomationElement>()
        .FirstOrDefault(x => { try { var r = x.Current.BoundingRectangle; return !r.IsEmpty && r.Left < bounds.Left + bounds.Width * .09; } catch { return false; } });
    if (namedButton is not null) return namedButton;
    var label = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, text)).Cast<AutomationElement>()
        .Where(x => { try { var r = x.Current.BoundingRectangle; return !r.IsEmpty && r.Left < bounds.Left + bounds.Width * .09 && r.Top > bounds.Top + bounds.Height * .12 && r.Top < bounds.Top + bounds.Height * .72; } catch { return false; } })
        .OrderBy(x => x.Current.BoundingRectangle.Top)
        .FirstOrDefault();
    // 点击图标按钮内部真实的文字标签，避免宽按钮容器的中心点落到相邻 rail 项。
    return label ?? root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, text)).Cast<AutomationElement>()
        .Select(x => AncestorOfType(x, ControlType.Button)).Where(x => x is not null).Cast<AutomationElement>()
        .Where(x => { try { var r = x.Current.BoundingRectangle; return !r.IsEmpty && r.Left < bounds.Left + bounds.Width * .09; } catch { return false; } })
        .FirstOrDefault();
}
static AutomationElement? FindRightPanelButton(AutomationElement root, string text)
{
    var bounds = root.Current.BoundingRectangle;
    return root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>()
        .Where(x => { try { return (x.Current.Name ?? "").Contains(text, StringComparison.OrdinalIgnoreCase); } catch { return false; } })
        .Where(x => { try { var r = x.Current.BoundingRectangle; return !r.IsEmpty && r.Left > bounds.Left + bounds.Width * .72 && r.Top < bounds.Top + bounds.Height * .35; } catch { return false; } })
        .OrderBy(x => x.Current.BoundingRectangle.Top).FirstOrDefault();
}
static AutomationElement? FindCheckboxContaining(AutomationElement root, string fragment) =>
    root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox)).Cast<AutomationElement>()
        .FirstOrDefault(x => { try { return (x.Current.Name ?? "").Contains(fragment, StringComparison.OrdinalIgnoreCase); } catch { return false; } });
static byte[] ReadSharedBytes(string path)
{
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    using var memory = new MemoryStream();
    stream.CopyTo(memory);
    return memory.ToArray();
}
static byte[]? ReadIfPresent(string path) => File.Exists(path) ? ReadSharedBytes(path) : null;
static void WriteUserDataSnapshot(string path, IReadOnlyDictionary<string, byte[]?> snapshots)
{
    var document = new UserDataSnapshotDocument(1, snapshots
        .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
        .Select(item => new UserDataSnapshotEntry(Path.GetFullPath(item.Key), item.Value is not null, item.Value is null ? null : Convert.ToBase64String(item.Value)))
        .ToArray());
    var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    stream.Write(bytes);
    stream.Flush(flushToDisk: true);
}
static bool RestoreUserDataSnapshot(string snapshotDirectory, out string message)
{
    try
    {
        var path = Path.Combine(snapshotDirectory, "user-data-snapshot.json");
        if (!File.Exists(path)) { message = "快照清单不存在。"; return false; }
        var document = JsonSerializer.Deserialize<UserDataSnapshotDocument>(File.ReadAllText(path));
        if (document is null || document.Version != 1 || document.Files is null || document.Files.Length != 4)
        { message = "快照格式无效或文件清单数量不符。"; return false; }
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(Path.Combine(localData, "FrameTrail", "settings.json")),
            Path.GetFullPath(Path.Combine(localData, "FrameTrail", "recent-projects.json")),
            Path.GetFullPath(Path.Combine(localData, "FrameTrail", "memo.txt")),
            Path.GetFullPath(Path.Combine(localData, "FrameTrail", "memo.txt.settings.json"))
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in document.Files)
        {
            var target = Path.GetFullPath(entry.Path);
            if (!allowed.Contains(target) || !seen.Add(target))
            { message = $"快照包含不允许或重复的目标路径：{target}"; return false; }
            byte[]? content = entry.Exists ? Convert.FromBase64String(entry.ContentBase64 ?? throw new InvalidDataException("存在的文件缺少内容。")) : null;
            if (!RestoreUserFile(target, content))
            { message = $"恢复或校验失败：{target}"; return false; }
        }
        message = $"已从 {path} 恢复并核对四个 FrameTrail 用户数据文件。";
        return true;
    }
    catch (Exception ex) { message = $"恢复失败：{ex.GetType().Name}: {ex.Message}"; return false; }
}
static bool RestoreUserFile(string path, byte[]? original)
{
    try
    {
        if (original is null)
        {
            if (File.Exists(path)) File.Delete(path);
            return !File.Exists(path);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, original);
        return File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(original);
    }
    catch { return false; }
}
static bool WaitForPreferenceValues(string path, int width, int fps, TimeSpan timeout)
{
    var until=DateTime.UtcNow+timeout;
    while(DateTime.UtcNow<until && !InteractiveDesktopGuard.IsAborted)
    {
        try
        {
            using var doc=JsonDocument.Parse(ReadSharedBytes(path));var root=doc.RootElement;
            if(root.GetProperty("PreviewResolution").GetInt32()==width&&root.GetProperty("PreviewFrameRate").GetInt32()==fps&&root.GetProperty("ExcludeEditorDuringRecording").GetBoolean())return true;
        }
        catch{}
        Thread.Sleep(100);
    }
    return false;
}
static AutomationElement[] FindInspectorCombos(AutomationElement root)
{
    return root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox)).Cast<AutomationElement>().ToArray();
}
static AutomationElement? FindComboByName(AutomationElement root, string name) => root.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox), new PropertyCondition(AutomationElement.NameProperty, name)));
static AutomationElement? WaitForComboByName(AutomationElement root, string name, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until && !InteractiveDesktopGuard.IsAborted) { var combo = FindComboByName(root, name); if (combo is not null) return combo; Thread.Sleep(100); }
    return null;
}
static AutomationElement? WaitForProjectTitle(AutomationElement root, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until && !InteractiveDesktopGuard.IsAborted)
    {
        var title = FindById(root, "ProjectTitle");
        if (title is not null && !string.Equals(title.Current.Name, "未命名项目", StringComparison.Ordinal)) return title;
        Thread.Sleep(200);
    }
    return FindById(root, "ProjectTitle");
}
static AutomationElement? FindButtonByHint(AutomationElement root, string hint)
{
    foreach (var item in root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>())
    {
        try { if ((item.Current.Name ?? "").Contains(hint, StringComparison.OrdinalIgnoreCase)) return item; } catch (ElementNotAvailableException) { }
    }
    foreach (var item in root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, hint)).Cast<AutomationElement>())
    {
        var button = AncestorOfType(item, ControlType.Button);
        if (button is not null) return button;
    }
    foreach (var item in root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>())
    {
        try { if ((item.Current.HelpText ?? "").Contains(hint, StringComparison.OrdinalIgnoreCase)) return item; } catch (ElementNotAvailableException) { }
    }
    return null;
}
static AutomationElement? AncestorOfType(AutomationElement node, ControlType type)
{
    var parent = TreeWalker.ControlViewWalker.GetParent(node);
    while (parent is not null)
    {
        if (parent.Current.ControlType == type) return parent;
        parent = TreeWalker.ControlViewWalker.GetParent(parent);
    }
    return null;
}
static AutomationElement? WaitForName(string name, int processId, TimeSpan timeout)
{
    InteractiveDesktopGuard.BindTargetProcess(processId);
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until && !InteractiveDesktopGuard.IsAborted)
    {
        foreach (var item in FindOwnedProcessDescendants(processId, new PropertyCondition(AutomationElement.NameProperty, name)))
            if (string.Equals(SafeName(item), name, StringComparison.Ordinal)) return item;
        Thread.Sleep(150);
    }
    return null;
}
static AutomationElement? WaitForWindowName(int processId, string namePart, TimeSpan timeout)
{
    InteractiveDesktopGuard.BindTargetProcess(processId);
    var until = DateTime.UtcNow + timeout;
    while (!InteractiveDesktopGuard.IsAborted)
    {
        var found = FindOwnedWindowName(processId, namePart);
        if (found is not null) return found;
        if (DateTime.UtcNow >= until) break;
        Thread.Sleep(200);
    }
    return null;
}
static AutomationElement? FindOwnedWindowName(int processId, string namePart)
{
    foreach (var item in FindOwnedProcessWindows(processId))
        if (SafeName(item).Contains(namePart, StringComparison.OrdinalIgnoreCase)) return item;
    return null;
}
static AutomationElement[] FindOwnedProcessWindows(int processId)
{
    if (!InteractiveDesktopGuard.IsOwnedProcessId(processId)) return [];
    try
    {
        var roots = new List<AutomationElement>();
        foreach (AutomationElement candidate in AutomationElement.RootElement.FindAll(TreeScope.Children, System.Windows.Automation.Condition.TrueCondition))
        {
            try
            {
                if ((int)candidate.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty) == processId
                    && candidate.Current.ControlType == ControlType.Window) roots.Add(candidate);
            }
            catch { }
        }
        var windows = new List<AutomationElement>(roots);
        foreach (var root in roots)
        {
            try
            {
                windows.AddRange(root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window))
                    .Cast<AutomationElement>()
                    .Where(x => (int)x.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty) == processId));
            }
            catch { }
        }
        return windows
            .GroupBy(x => SafeWindowIdentity(x), StringComparer.Ordinal)
            .Select(g => g.First())
            .ToArray();
    }
    catch { return []; }
}
static string SafeWindowIdentity(AutomationElement element)
{
    try
    {
        var handle = element.Current.NativeWindowHandle;
        if (handle != 0) return "hwnd:" + handle.ToString("X");
        return "runtime:" + string.Join(".", element.GetRuntimeId());
    }
    catch { return "unavailable:" + element.GetHashCode(); }
}
static AutomationElement[] FindOwnedProcessDescendants(int processId, System.Windows.Automation.Condition condition)
{
    var elements = new List<AutomationElement>();
    foreach (var window in FindOwnedProcessWindows(processId))
    {
        try
        {
            elements.AddRange(window.FindAll(TreeScope.Descendants, condition).Cast<AutomationElement>()
                .Where(x => (int)x.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty) == processId));
        }
        catch { }
    }
    return elements.ToArray();
}
static AutomationElement? WaitForNameById(AutomationElement root, string id, Func<string, bool> predicate, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until && !InteractiveDesktopGuard.IsAborted)
    {
        var value = FindById(root, id);
        if (value is not null && predicate(SafeName(value))) return value;
        Thread.Sleep(120);
    }
    return null;
}
static AutomationElement? WaitForControlName(AutomationElement root, ControlType controlType, string name, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until && !InteractiveDesktopGuard.IsAborted)
    {
        var match = root.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, controlType),
            new PropertyCondition(AutomationElement.NameProperty, name)));
        if (match is not null) return match;
        Thread.Sleep(100);
    }
    return null;
}
static IEnumerable<string> FindProcessNames(int processId)
{
    try
    {
        return FindOwnedProcessWindows(processId).Concat(FindOwnedProcessDescendants(processId, System.Windows.Automation.Condition.TrueCondition))
            .Select(x => SafeName(x)).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
    }
    catch { return []; }
}
static IEnumerable<string> FindProcessWindowNames(int processId)
{
    return FindOwnedProcessWindows(processId).Select(SafeName).ToArray();
}
static bool TryInvoke(AutomationElement element)
{
    try
    {
        var processId = (int)element.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty);
        if (!InteractiveDesktopGuard.BindInputTargetIfForeground(processId, "UIA Invoke")) return false;
        if (!InteractiveDesktopGuard.BeforeInput("UIA Invoke")) return false;
        var invocation = Task.Run(() =>
        {
            if (!InteractiveDesktopGuard.BeforeInput("UIA Invoke")) return false;
            ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            return true;
        });
        if (!invocation.Wait(TimeSpan.FromSeconds(5))) return false;
        if (!invocation.GetAwaiter().GetResult()) return false;
        Thread.Sleep(220);
        ThrowIfApplicationErrorDialog(processId);
        return true;
    }
    catch (ApplicationErrorDialogException) { throw; }
    catch { return false; }
}
static string SafeName(AutomationElement? element) { try { return element?.Current.Name ?? ""; } catch { return ""; } }
static string SafeAutomationId(AutomationElement? element) { try { return element?.Current.AutomationId ?? ""; } catch { return ""; } }
static bool TryExpand(AutomationElement element)
{
    try
    {
        var processId = (int)element.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty);
        if (!InteractiveDesktopGuard.BindInputTargetIfForeground(processId, "UIA Expand")) return false;
        if (!InteractiveDesktopGuard.BeforeInput("UIA Expand")) return false;
        var expansion = Task.Run(() =>
        {
            if (!InteractiveDesktopGuard.BeforeInput("UIA Expand")) return false;
            ((ExpandCollapsePattern)element.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
            return true;
        });
        if (!expansion.Wait(TimeSpan.FromSeconds(5))) return false;
        return expansion.GetAwaiter().GetResult();
    }
    catch { return false; }
}
static bool TrySetToggleOn(AutomationElement element)
{
    try
    {
        var processId = (int)element.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty);
        if (!InteractiveDesktopGuard.BindInputTargetIfForeground(processId, "UIA Toggle")) return false;
        if (!InteractiveDesktopGuard.BeforeInput("UIA Toggle")) return false;
        var toggleTask = Task.Run(() =>
        {
            if (!InteractiveDesktopGuard.BeforeInput("UIA Toggle")) return false;
            var pattern = (TogglePattern)element.GetCurrentPattern(TogglePattern.Pattern);
            if (pattern.Current.ToggleState == ToggleState.Off) pattern.Toggle();
            return true;
        });
        if (!toggleTask.Wait(TimeSpan.FromSeconds(5))) return false;
        return toggleTask.GetAwaiter().GetResult();
    }
    catch { return false; }
}
static bool TrySetValue(AutomationElement element, string value)
{
    try
    {
        var processId = (int)element.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty);
        if (!InteractiveDesktopGuard.BindInputTargetIfForeground(processId, "UIA SetValue")) return false;
        if (!InteractiveDesktopGuard.BeforeInput("UIA SetValue")) return false;
        var pattern = (ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern);
        pattern.SetValue(value);
        return true;
    }
    catch { return false; }
}
static bool TryCollapse(AutomationElement element)
{
    try
    {
        var processId = (int)element.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty);
        if (!InteractiveDesktopGuard.BindInputTargetIfForeground(processId, "UIA Collapse")) return false;
        if (!InteractiveDesktopGuard.BeforeInput("UIA Collapse")) return false;
        ((ExpandCollapsePattern)element.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse();
        return true;
    }
    catch { return false; }
}
static bool TrySetAutomationFocus(AutomationElement element)
{
    try
    {
        var processId = (int)element.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty);
        if (!InteractiveDesktopGuard.BeforeOwnedFocusChange(processId)) return false;
        element.SetFocus();
        return InteractiveDesktopGuard.WaitForTargetForeground(processId, TimeSpan.FromSeconds(2));
    }
    catch { return false; }
}
static bool SetRangeValue(AutomationElement element, double value)
{
    try
    {
        var processId = (int)element.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty);
        if (!InteractiveDesktopGuard.BindInputTargetIfForeground(processId, "UIA SetRangeValue")) return false;
        if (!InteractiveDesktopGuard.BeforeInput("UIA SetRangeValue")) return false;
        var setting = Task.Run(() =>
        {
            if (!InteractiveDesktopGuard.BeforeInput("UIA SetRangeValue")) return false;
            var range = (RangeValuePattern)element.GetCurrentPattern(RangeValuePattern.Pattern);
            range.SetValue(Math.Clamp(value, range.Current.Minimum, range.Current.Maximum));
            return true;
        });
        if (!setting.Wait(TimeSpan.FromSeconds(5))) return false;
        return setting.GetAwaiter().GetResult();
    }
    catch { return false; }
}
static bool TrySelect(AutomationElement element)
{
    try
    {
        var processId = (int)element.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty);
        if (!InteractiveDesktopGuard.BindInputTargetIfForeground(processId, "UIA Select")) return false;
        if (!InteractiveDesktopGuard.BeforeInput("UIA Select")) return false;
        ((SelectionItemPattern)element.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        return true;
    }
    catch { return TryInvoke(element); }
}
static bool SelectComboOption(AutomationElement combo, string option, int processId, int fallbackDown)
{
    if (TryExpand(combo))
    {
        Thread.Sleep(150);
        var item = FindNamed(combo, option) ?? WaitForName(option, processId, TimeSpan.FromSeconds(1));
        if (item is not null && (TrySelect(item) || TryClickPhysical(item)))
        { Thread.Sleep(250); TryCollapse(combo); return true; }
        TryCollapse(combo);
    }
    try
    {
        if (!TrySetAutomationFocus(combo)) return false;
        if (!TryClickPhysical(combo)) return false;
        for (var i = 0; i < fallbackDown; i++) SendKey(0x28, false);
        SendKey(0x0D, false); Thread.Sleep(250); return true;
    }
    catch { return false; }
}
static string DescribeInspector(AutomationElement root)
{
    try
    {
        return string.Join(" | ", root.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition).Cast<AutomationElement>()
            .Select(x => { try { return x.Current.ControlType.ProgrammaticName + ":" + x.Current.AutomationId + ":" + x.Current.Name; } catch { return ""; } })
            .Where(x => !x.EndsWith(":", StringComparison.Ordinal)).Take(70));
    }
    catch (Exception ex) { return ex.GetType().Name; }
}
static bool TryClickPhysical(AutomationElement element, bool preferAutomationInvoke = true)
{
    int processId;
    try { processId = (int)element.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty); }
    catch { return false; }
    if (!InteractiveDesktopGuard.BindInputTargetIfForeground(processId, "UIA/鼠标点击")) return false;
    if (!InteractiveDesktopGuard.BeforeInput("UIA/鼠标点击")) return false;
    var previousDpiContext = NativeMethods.SetThreadDpiAwarenessContext(new IntPtr(-4));
    try
    {
        if (preferAutomationInvoke && element.Current.ControlType == ControlType.Button && TryInvoke(element))
        {
            Thread.Sleep(220);
            ThrowIfApplicationErrorDialog(processId);
            return true;
        }
        var rect = element.Current.BoundingRectangle;
        if (rect.IsEmpty || rect.Width < 2 || rect.Height < 2) return false;
        var x = (int)(rect.Left + rect.Width / 2); var y = (int)(rect.Top + rect.Height / 2);
        NativeMethods.SetCursorPos(x, y); Thread.Sleep(120);
        var cursor = new PointNative { X = x, Y = y };
        var pointWindow = NativeMethods.WindowFromPoint(cursor);
        var pointRoot = NativeMethods.GetAncestor(pointWindow, 2);
        NativeMethods.GetWindowThreadProcessId(pointRoot, out var pointProcessId);
        if (pointProcessId != (uint)processId || !InteractiveDesktopGuard.IsOwnedProcessId((int)pointProcessId)) return false;
        NativeMethods.MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(80);
        NativeMethods.MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(250);
        ThrowIfApplicationErrorDialog(processId);
        return true;
    }
    catch (ApplicationErrorDialogException) { throw; }
    catch { return false; }
    finally { if (previousDpiContext != IntPtr.Zero) NativeMethods.SetThreadDpiAwarenessContext(previousDpiContext); }
}
static void ThrowIfApplicationErrorDialog(int processId)
{
    var dialog = WaitForWindowName(processId, "操作未完成", TimeSpan.FromMilliseconds(80));
    if (dialog is null) return;
    var text = string.Join(" | ", dialog.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition)
        .Cast<AutomationElement>().Select(SafeName).Where(x => !string.IsNullOrWhiteSpace(x)).Take(8));
    throw new ApplicationErrorDialogException($"FrameTrail 弹出错误对话框：{text}");
}
static void TestMemoPersistence(AutomationElement mainWindow, int processId, List<StepResult> results)
{
    var refreshedMain = WaitForMainWindow(processId, TimeSpan.FromSeconds(3));
    if (refreshedMain is null)
    {
        AddStep(results, new("备忘录移动、字号与透明度保存重启", false, "主窗口在关闭设置对话框后不可重新定位。"));
        return;
    }
    mainWindow = refreshedMain;
    FocusWindow(mainWindow);
    Thread.Sleep(250);
    var launcher = FindButtonByHint(mainWindow, "打开录制工具");
    var openedRecorder = launcher is not null && TryClickPhysical(launcher);
    var recorderWindow = openedRecorder ? WaitForWindowName(processId, "FrameTrail · 录制", TimeSpan.FromSeconds(4)) : null;
    var memoButton = recorderWindow is null ? null : FindButtonByHint(recorderWindow, "备忘录");
    var openedMemo = memoButton is not null && TryClickPhysical(memoButton);
    var memo = openedMemo ? WaitForWindowName(processId, "本机备忘录", TimeSpan.FromSeconds(4)) : null;
    if (memo is null)
    {
        AddStep(results, new("备忘录位置、字号与透明度重开", false, $"启动按钮={launcher is not null}，启动点击={openedRecorder}，录制工具窗口={recorderWindow is not null}，备忘录按钮={memoButton is not null}，备忘录点击={openedMemo}，窗口={openedMemo}。"));
        if (recorderWindow is not null) _ = CloseWindowWithoutKeyboard(recorderWindow);
        return;
    }

    var edits = memo.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)).Cast<AutomationElement>().ToArray();
    var sliders = memo.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Slider)).Cast<AutomationElement>().ToArray();
    const string marker = "FrameTrail UIA memo persistence fixture";
    var textSet = false; var sizeSet = false; var opacitySet = false;
    try { textSet = edits.Length > 0 && TrySetValue(edits[0], marker); } catch { }
    try { sizeSet = sliders.Length > 0 && SetRangeValue(sliders[0], 32); } catch { }
    try { opacitySet = sliders.Length > 1 && SetRangeValue(sliders[1], .61); } catch { }
    var initial = memo.Current.BoundingRectangle;
    var moved = new Rect(initial.Left + 90, initial.Top + 70, initial.Width, initial.Height);
    var movedWindow = initial.IsEmpty == false && NativeMethods.SetWindowPos((IntPtr)memo.Current.NativeWindowHandle, IntPtr.Zero, (int)moved.Left, (int)moved.Top, 0, 0, 0x0001 | 0x0004 | 0x0010);
    Thread.Sleep(400);
    var afterMove = memo.Current.BoundingRectangle;
    var memoClosed = CloseWindowWithoutKeyboard(memo);
    if (!memoClosed)
    {
        AddStep(results, new("备忘录移动、字号与透明度保存重启", false, "备忘录未能通过窗口关闭请求正常关闭，停止重复打开步骤。"));
        return;
    }
    recorderWindow = WaitForWindowName(processId, "FrameTrail · 录制", TimeSpan.FromSeconds(3));
    var reopenedRecorder = recorderWindow is not null;
    memoButton = recorderWindow is null ? null : FindButtonByHint(recorderWindow, "备忘录");
    var reopened = memoButton is not null && TryClickPhysical(memoButton) ? WaitForWindowName(processId, "本机备忘录", TimeSpan.FromSeconds(4)) : null;
    if (reopened is null)
    {
        AddStep(results, new("备忘录位置、字号与透明度重开", false, $"文本={textSet}，字号={sizeSet}，透明度={opacitySet}，移动={movedWindow}；重开窗口未出现。"));
        if (recorderWindow is not null) _ = CloseWindowWithoutKeyboard(recorderWindow);
        return;
    }
    Thread.Sleep(350);
    var values = reopened.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)).Cast<AutomationElement>().ToArray();
    var ranges = reopened.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Slider)).Cast<AutomationElement>().ToArray();
    var reopenedRect = reopened.Current.BoundingRectangle;
    var textOkay = false;
    try { textOkay = values.Length > 0 && ((ValuePattern)values[0].GetCurrentPattern(ValuePattern.Pattern)).Current.Value.Contains(marker, StringComparison.Ordinal); } catch { }
    var sizeOkay = ranges.Length > 0 && Math.Abs(((RangeValuePattern)ranges[0].GetCurrentPattern(RangeValuePattern.Pattern)).Current.Value - 32) < 1;
    var opacityOkay = ranges.Length > 1 && Math.Abs(((RangeValuePattern)ranges[1].GetCurrentPattern(RangeValuePattern.Pattern)).Current.Value - .61) < .02;
    var positionOkay = !afterMove.IsEmpty && !reopenedRect.IsEmpty && Math.Abs(afterMove.Left - reopenedRect.Left) < 16 && Math.Abs(afterMove.Top - reopenedRect.Top) < 16;
    var all = textSet && sizeSet && opacitySet && movedWindow && textOkay && sizeOkay && opacityOkay && positionOkay;
    AddStep(results, new("备忘录移动、字号与透明度保存重启", all && memoClosed, $"文本={textOkay}，字号={sizeOkay}，透明度={opacityOkay}，位置={positionOkay}，首次窗口正常关闭={memoClosed}；首次移动={movedWindow}，重开坐标差=({reopenedRect.Left-afterMove.Left:0},{reopenedRect.Top-afterMove.Top:0})。"));
    _ = CloseWindowWithoutKeyboard(reopened);
    if (recorderWindow is not null) _ = CloseWindowWithoutKeyboard(recorderWindow);
}
static bool TestCustomPresetLifecycle(AutomationElement mainWindow, int processId, string projectFile, List<StepResult> results)
{
    var name = "UIA-" + Guid.NewGuid().ToString("N")[..8];
    string? ownedPresetFile = null;
    var stage = "打开预设菜单";
    var lifecyclePassed = false;
    try
    {
        if (!OpenPresetMenu(mainWindow)) throw new InvalidOperationException("无法打开场景预设菜单。");
        var save = WaitForMenuItem(processId, "将当前样式保存为本机预设", TimeSpan.FromSeconds(3));
        if (save is null || !TryInvoke(save)) throw new InvalidOperationException("未能打开保存预设命令。");
        if (!CompleteTextPrompt(processId, "保存场景预设", name, out var savePromptTrace))
            throw new InvalidOperationException("新建预设名称对话框未完成；" + savePromptTrace);
        ownedPresetFile = WaitForPresetFile(name, TimeSpan.FromSeconds(5));
        var saved = ownedPresetFile is not null;
        AddStep(results, new("本机场景预设可新建", saved, saved ? $"本机预设目录中出现独立 manifest；{savePromptTrace}。" : $"未发现新建预设 manifest；输入名称={name}；{savePromptTrace}；现有名称=[{string.Join(", ", EnumeratePresetNames())}]。"));
        if (!saved) return false;

        var crossProjectApplied = false;
        string crossProjectDetail;
        try
        {
            var originalImagePath = ReadJsonString(projectFile, "settings", "backgroundStyle", "imagePath");
            using var savedPreset = JsonDocument.Parse(ReadSharedBytes(ownedPresetFile!));
            var presetImagePath = ReadNumberOrString(savedPreset.RootElement, "Settings", "BackgroundStyle", "ImagePath");
            var presetFolder = Path.GetDirectoryName(ownedPresetFile)!;
            var presetAssetFile = Path.GetFullPath(Path.Combine(presetFolder, presetImagePath));
            var presetPrefix = Path.GetFullPath(presetFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var presetAssetCopied = !string.Equals(presetImagePath, originalImagePath, StringComparison.OrdinalIgnoreCase)
                && presetAssetFile.StartsWith(presetPrefix, StringComparison.OrdinalIgnoreCase) && File.Exists(presetAssetFile);
            var sourceFolder = Path.GetDirectoryName(projectFile)!;
            var testParent = Directory.GetParent(sourceFolder)!.FullName;
            var crossFolder = Path.Combine(testParent, "preset-cross-project-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(crossFolder);
            CopyDirectory(sourceFolder, crossFolder);
            var crossProjectFile = Path.Combine(crossFolder, Path.GetFileName(projectFile));
            var crossImageRelative = "media/uia-overlay-cross-" + Guid.NewGuid().ToString("N") + Path.GetExtension(originalImagePath);
            var sourceImage = Path.GetFullPath(Path.Combine(crossFolder, originalImagePath.Replace('/', Path.DirectorySeparatorChar)));
            var crossImage = Path.GetFullPath(Path.Combine(crossFolder, crossImageRelative.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(sourceImage)) throw new FileNotFoundException("跨项目测试素材缺失。", sourceImage);
            File.Copy(sourceImage, crossImage);
            var crossJson = JsonNode.Parse(ReadSharedBytes(crossProjectFile))?.AsObject() ?? throw new InvalidDataException("跨项目测试工程 JSON 无效。");
            var settingsNode = crossJson.First(x => x.Key.Equals("settings", StringComparison.OrdinalIgnoreCase)).Value?.AsObject() ?? throw new InvalidDataException("跨项目工程缺少 settings。");
            var backgroundNode = settingsNode.First(x => x.Key.Equals("backgroundStyle", StringComparison.OrdinalIgnoreCase)).Value?.AsObject() ?? throw new InvalidDataException("跨项目工程缺少 backgroundStyle。");
            var imageProperty = backgroundNode.First(x => x.Key.Equals("imagePath", StringComparison.OrdinalIgnoreCase)).Key;
            backgroundNode[imageProperty] = crossImageRelative;
            File.WriteAllText(crossProjectFile, crossJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            var appExecutable = Process.GetProcessById(processId).MainModule?.FileName ?? throw new InvalidOperationException("无法读取测试应用路径。");
            using var crossProcess = Process.Start(new ProcessStartInfo(appExecutable) { UseShellExecute = false, ArgumentList = { "--open", crossProjectFile } }) ?? throw new InvalidOperationException("无法启动第二个测试项目窗口。");
            try
            {
                var crossWindow = WaitForWindow(crossProcess, TimeSpan.FromSeconds(35));
                var crossTitle = crossWindow is null ? null : WaitForProjectTitle(crossWindow, TimeSpan.FromSeconds(15));
                var crossMenu = crossWindow is not null && crossTitle is not null && OpenPresetMenu(crossWindow);
                var crossEntry = crossMenu ? WaitForMenuItem(crossProcess.Id, name, TimeSpan.FromSeconds(4)) : null;
                var crossExpanded = crossEntry is not null && (TryExpand(crossEntry) || TryClickPhysical(crossEntry));
                var crossApply = crossExpanded ? WaitForMenuItem(crossProcess.Id, "应用此预设", TimeSpan.FromSeconds(3)) : null;
                var clicked = crossApply is not null && TryInvoke(crossApply);
                var relativeAsset = "";
                var validAsset = false;
                var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                while (clicked && DateTime.UtcNow < until && !InteractiveDesktopGuard.IsAborted)
                {
                    try
                    {
                        using var crossDoc = JsonDocument.Parse(ReadSharedBytes(crossProjectFile));
                        var style = Navigate(crossDoc.RootElement, ["settings", "backgroundStyle"]);
                        if (TryGetPropertyIgnoreCase(style, "kind", out var kind) && kind.ToString().Equals("image", StringComparison.OrdinalIgnoreCase))
                        {
                            relativeAsset = ReadJsonString(crossProjectFile, "settings", "backgroundStyle", "imagePath");
                            var resolved = Path.GetFullPath(Path.Combine(crossFolder, relativeAsset.Replace('/', Path.DirectorySeparatorChar)));
                            var prefix = Path.GetFullPath(crossFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                            validAsset = resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && File.Exists(resolved);
                            if (validAsset) break;
                        }
                    }
                    catch { }
                    Thread.Sleep(100);
                }
                crossProjectApplied = crossWindow is not null && crossTitle is not null && clicked && validAsset && presetAssetCopied && !string.Equals(relativeAsset, originalImagePath, StringComparison.OrdinalIgnoreCase);
                crossProjectDetail = $"预设内素材已独立复制={presetAssetCopied}，第二个项目打开={crossTitle is not null}，应用命令={crossApply is not null}，源路径={originalImagePath}，预设路径={presetImagePath}，目标项目路径={relativeAsset}，目标文件存在={validAsset}。";
                if (crossWindow is not null) _ = CloseWindowWithoutKeyboard(crossWindow);
                if (!crossProcess.WaitForExit(5000)) crossProcess.Kill(entireProcessTree: true);
            }
            finally
            {
                if (!crossProcess.HasExited) { crossProcess.CloseMainWindow(); if (!crossProcess.WaitForExit(3000)) crossProcess.Kill(entireProcessTree: true); }
            }
        }
        catch (ApplicationErrorDialogException) { throw; }
        catch (Exception ex) { crossProjectDetail = "跨项目素材检查异常：" + ex.Message; }
        FocusWindow(mainWindow);
        AddStep(results, new("本机预设跨项目复制背景素材", crossProjectApplied, crossProjectDetail));
        if (!crossProjectApplied) throw new InvalidOperationException("跨项目应用预设未完成；已停止后续生命周期步骤。");

        stage = "重命名预设";
        if (!OpenPresetMenu(mainWindow)) throw new InvalidOperationException("重命名前无法打开场景预设菜单。");
        var entry = WaitForMenuItem(processId, name, TimeSpan.FromSeconds(3));
        var submenuOpened = entry is not null && (TryExpand(entry) || TryClickPhysical(entry));
        var rename = submenuOpened ? WaitForMenuItem(processId, "重命名本机预设", TimeSpan.FromSeconds(2)) : null;
        var renamedName = name + "-renamed";
        var renamed = false;
        var renamePromptTrace = "重命名命令未打开名称对话框。";
        if (rename is not null && TryInvoke(rename))
            renamed = CompleteTextPrompt(processId, "重命名本机预设", renamedName, out renamePromptTrace);
        var renamedFile = renamed ? WaitForPresetFile(renamedName, TimeSpan.FromSeconds(5)) : null;
        if (renamedFile is not null) ownedPresetFile = renamedFile;
        AddStep(results, new("本机场景预设可改名", renamed && renamedFile is not null, renamed && renamedFile is not null ? $"manifest 名称已更新，预设资产目录保持原位；{renamePromptTrace}。" : $"子菜单={submenuOpened}，重命名命令={rename is not null}，对话框={renamePromptTrace}"));
        if (!renamed || renamedFile is null) throw new InvalidOperationException("预设重命名未完成；已停止后续生命周期步骤。" + renamePromptTrace);

        stage = "切换预设覆盖的对照样式";
        if (!OpenPresetMenu(mainWindow)) throw new InvalidOperationException("覆盖前无法打开场景预设菜单。");
        var builtin = WaitForMenuItem(processId, "简洁浅色", TimeSpan.FromSeconds(2));
        var changedStyle = builtin is not null && TryInvoke(builtin) && WaitForBackground(projectFile, "#F4F6F6", TimeSpan.FromSeconds(4));
        if (!OpenPresetMenu(mainWindow)) throw new InvalidOperationException("覆盖后无法重开场景预设菜单。");
        entry = WaitForMenuItem(processId, renamedName, TimeSpan.FromSeconds(3));
        submenuOpened = entry is not null && (TryExpand(entry) || TryClickPhysical(entry));
        var overwrite = submenuOpened ? WaitForMenuItem(processId, "用当前样式覆盖", TimeSpan.FromSeconds(2)) : null;
        var overwritePrompt = overwrite is not null && TryInvoke(overwrite) ? WaitForWindowName(processId, "覆盖本机预设", TimeSpan.FromSeconds(3)) : null;
        var yes = overwritePrompt is null ? null : overwritePrompt.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>().FirstOrDefault(x => SafeName(x) is "是" or "Yes");
        stage = "确认安全覆盖";
        var confirmed = yes is not null && TryClickPhysical(yes, preferAutomationInvoke: false)
            && WaitForWindowClosed(processId, "覆盖本机预设", TimeSpan.FromSeconds(5));
        if (!confirmed) throw new InvalidOperationException($"覆盖确认未完成，确认按钮={yes is not null}。");
        using var manifest = JsonDocument.Parse(ReadSharedBytes(renamedFile));
        var overwritten = changedStyle && confirmed && ReadNumberOrString(manifest.RootElement, "settings", "background") == "#F4F6F6";
        AddStep(results, new("本机场景预设可安全覆盖", overwritten, $"当前样式已变更={changedStyle}，确认={confirmed}，新背景={ReadNumberOrString(manifest.RootElement, "settings", "background")}；原目录在新版本提交前保留用于回滚。"));

        stage = "应用已覆盖预设";
        if (!OpenPresetMenu(mainWindow)) throw new InvalidOperationException("应用预设前无法打开场景预设菜单。");
        var dark = WaitForMenuItem(processId, "深色窗口", TimeSpan.FromSeconds(2));
        var darkApplied = dark is not null && TryInvoke(dark) && WaitForBackground(projectFile, "#202124", TimeSpan.FromSeconds(4));
        if (!darkApplied) throw new InvalidOperationException("无法在应用本机预设前切换到可区分的深色样式。");
        if (!OpenPresetMenu(mainWindow)) throw new InvalidOperationException("深色样式后无法重开场景预设菜单。");
        entry = WaitForMenuItem(processId, renamedName, TimeSpan.FromSeconds(3));
        submenuOpened = entry is not null && (TryExpand(entry) || TryClickPhysical(entry));
        var apply = submenuOpened ? WaitForMenuItem(processId, "应用此预设", TimeSpan.FromSeconds(2)) : null;
        var applied = apply is not null && TryInvoke(apply) && WaitForBackground(projectFile, "#F4F6F6", TimeSpan.FromSeconds(4));
        if (!applied) _ = CloseOwnedTransientPopups(processId, mainWindow);
        AddStep(results, new("本机预设可应用到项目", applied, applied ? "项目先切换到深色，再由本机预设恢复为浅色，写入行为已验证。" : $"先切换深色={darkApplied}，应用命令={apply is not null}，项目背景={ReadBackground(projectFile)}。"));
        lifecyclePassed = applied;
    }
    catch (ApplicationErrorDialogException) { throw; }
    catch (Exception ex)
    {
        if (InteractiveDesktopGuard.IsAborted) SmokeProgress.MarkIncomplete($"守卫中止本机预设生命周期；阶段={stage}；{ex.Message}");
        else AddStep(results, new("本机预设生命周期 UIA", false, $"阶段={stage}；{ex.Message}"));
    }
    finally
    {
        var overwriteClosed = DismissOwnedDialog(processId, "覆盖本机预设");
        var saveClosed = DismissOwnedDialog(processId, "保存场景预设");
        var renameClosed = DismissOwnedDialog(processId, "重命名本机预设");
        var dialogsClosed = overwriteClosed && saveClosed && renameClosed;
        if (!dialogsClosed)
        {
            lifecyclePassed = false;
            SmokeProgress.AppendMessage("场景预设模态窗口未确认关闭；保留相关预设文件并停止后续测试。");
        }
        else if (ownedPresetFile is not null && File.Exists(ownedPresetFile))
        {
            try { Directory.Delete(Path.GetDirectoryName(ownedPresetFile)!, true); } catch { }
        }
    }
    return lifecyclePassed;
}

static bool WaitForWindowClosed(int processId, string namePart, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (!InteractiveDesktopGuard.IsAborted)
    {
        if (FindOwnedWindowName(processId, namePart) is null) return true;
        if (DateTime.UtcNow >= until) break;
        Thread.Sleep(100);
    }
    return FindOwnedWindowName(processId, namePart) is null;
}

static bool DismissOwnedDialog(int processId, string namePart)
{
    var dialog = FindOwnedWindowName(processId, namePart);
    if (dialog is null) return true;
    if (InteractiveDesktopGuard.IsAborted) return false;
    AutomationElement? cancel = null;
    var cancelInvoked = false;
    try
    {
        cancel = dialog.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
            .Cast<AutomationElement>().FirstOrDefault(x => SafeName(x) is "否" or "取消" or "No" or "Cancel");
        if (cancel is not null) cancelInvoked = TryClickPhysical(cancel, preferAutomationInvoke: false);
    }
    catch (ElementNotAvailableException) { }
    var closed = WaitForWindowClosed(processId, namePart, TimeSpan.FromSeconds(1));
    if (!closed && !InteractiveDesktopGuard.IsAborted)
    {
        _ = CloseWindowWithoutKeyboard(dialog);
        closed = WaitForWindowClosed(processId, namePart, TimeSpan.FromSeconds(2));
    }
    SmokeProgress.AppendMessage($"模态窗口清理：{namePart}，找到=true，取消按钮={cancel is not null}，取消输入={cancelInvoked}，程序关闭请求={closed}，已关闭={closed}。");
    return closed;
}

static bool OpenPresetMenu(AutomationElement mainWindow)
{
    var button = FindButtonByHint(mainWindow, "场景预设");
    var processId = (int)mainWindow.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty);
    _ = CloseOwnedTransientPopups(processId, mainWindow);
    FocusWindow(mainWindow);
    return button is not null && TryClickPhysical(button);
}

static AutomationElement? WaitForMenuItem(int processId, string namePart, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until && !InteractiveDesktopGuard.IsAborted)
    {
        foreach (var item in FindOwnedProcessDescendants(processId, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem)))
            if (SafeName(item).Contains(namePart, StringComparison.OrdinalIgnoreCase)) return item;
        Thread.Sleep(100);
    }
    return null;
}

static bool CompleteTextPrompt(int processId, string title, string value, out string diagnostic)
{
    var dialog = WaitForWindowName(processId, title, TimeSpan.FromSeconds(3));
    var found = dialog is not null;
    if (dialog is null)
    {
        diagnostic = "found=false, editFound=false, set=false, readback=false, confirm=false, closed=false";
        SmokeProgress.AppendMessage($"文字对话框结果：{title}；{diagnostic}");
        return false;
    }
    var edits = dialog.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)).Cast<AutomationElement>().ToArray();
    var okay = dialog.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>().FirstOrDefault(x => SafeName(x) == "确定");
    var set = false;
    var readback = false;
    if (edits.Length > 0)
    {
        try
        {
            set = TrySetValue(edits[0], value);
            if (set && edits[0].TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && pattern is ValuePattern valuePattern)
                readback = string.Equals(valuePattern.Current.Value, value, StringComparison.Ordinal);
        }
        catch (Exception ex) { SmokeProgress.AppendMessage($"文字对话框写入异常：{title}；{ex.GetType().Name}: {ex.Message}"); }
    }
    var snapshotSaved = false;
    var layoutDiagnostic = "layout=unavailable";
    if (set && readback)
        layoutDiagnostic = CaptureTextPromptSnapshot(dialog, edits[0], title, out snapshotSaved);
    if (!snapshotSaved) layoutDiagnostic += ", screenshotEvidence=incomplete";
    var confirm = set && readback && okay is not null && !InteractiveDesktopGuard.IsAborted && TryInvoke(okay);
    var closed = confirm && WaitForWindowClosed(processId, title, TimeSpan.FromSeconds(4));
    var dismissed = false;
    if (!closed)
    {
        dismissed = DismissOwnedDialog(processId, title);
        closed = dismissed && WaitForWindowClosed(processId, title, TimeSpan.FromSeconds(1));
    }
    diagnostic = $"found={found}, editFound={edits.Length > 0}, set={set}, readback={readback}, {layoutDiagnostic}, confirm={confirm}, closed={closed}, dismissFallback={dismissed}";
    SmokeProgress.AppendMessage($"文字对话框结果：{title}；{diagnostic}");
    return confirm && closed;
}

static string CaptureTextPromptSnapshot(AutomationElement dialog, AutomationElement input, string title, out bool saved)
{
    saved = false;
    var dialogBounds = Rect.Empty;
    var inputBounds = Rect.Empty;
    uint dpi = 0;
    var hwnd = IntPtr.Zero;
    try
    {
        dialogBounds = dialog.Current.BoundingRectangle;
        inputBounds = input.Current.BoundingRectangle;
        hwnd = (IntPtr)dialog.Current.NativeWindowHandle;
        if (hwnd != IntPtr.Zero) dpi = NativeMethods.GetDpiForWindow(hwnd);
        var titleToken = new string(title.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray());
        var prefix = Path.GetFileNameWithoutExtension(SmokeProgress.Path);
        var screenshot = Path.Combine(Path.GetDirectoryName(SmokeProgress.Path)!, $"{prefix}.prompt-{titleToken}-{Guid.NewGuid():N}.png");
        CaptureWindow(dialog, screenshot);
        saved = File.Exists(screenshot);
        if (!saved) SmokeProgress.MarkIncomplete($"提示框截图未生成：{screenshot}");
        var dpiScale = dpi > 0 ? dpi / 96d : double.NaN;
        var inputHeightDip = double.IsFinite(dpiScale) && dpiScale > 0 && !inputBounds.IsEmpty ? inputBounds.Height / dpiScale : double.NaN;
        var message = $"截图={screenshot}, saved={saved}, HWND=0x{hwnd.ToInt64():X}, dialogPx=({dialogBounds.Left:0.#},{dialogBounds.Top:0.#},{dialogBounds.Width:0.#},{dialogBounds.Height:0.#}), inputPx=({inputBounds.Left:0.#},{inputBounds.Top:0.#},{inputBounds.Width:0.#},{inputBounds.Height:0.#}), inputHeightPx={inputBounds.Height:0.##}, dpi={dpi}, dpiScale≈{dpiScale:0.###}, inputHeightDip≈{inputHeightDip:0.##}";
        SmokeProgress.AppendMessage("提示框布局诊断：" + message);
        return message;
    }
    catch (Exception ex)
    {
        SmokeProgress.MarkIncomplete($"提示框布局截图失败；截图不是产品交互断言：{ex.GetType().Name}: {ex.Message}");
        var message = $"截图失败={ex.GetType().Name}: {ex.Message}, HWND=0x{hwnd.ToInt64():X}, dialogPx=({dialogBounds.Left:0.#},{dialogBounds.Top:0.#},{dialogBounds.Width:0.#},{dialogBounds.Height:0.#}), inputPx=({inputBounds.Left:0.#},{inputBounds.Top:0.#},{inputBounds.Width:0.#},{inputBounds.Height:0.##}), dpi={dpi}";
        SmokeProgress.AppendMessage("提示框布局诊断：" + message);
        return message;
    }
}

static string? FindPresetFile(string name)
{
    var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameTrail", "presets");
    if (!Directory.Exists(root)) return null;
    foreach (var path in Directory.EnumerateFiles(root, "preset.json", SearchOption.AllDirectories))
        try { using var doc = JsonDocument.Parse(ReadSharedBytes(path)); if (TryGetPropertyIgnoreCase(doc.RootElement, "name", out var presetName) && presetName.GetString() == name) return path; } catch { }
    return null;
}

static string[] EnumeratePresetNames()
{
    var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameTrail", "presets");
    if (!Directory.Exists(root)) return [];
    var names = new List<string>();
    foreach (var path in Directory.EnumerateFiles(root, "preset.json", SearchOption.AllDirectories))
        try { using var doc = JsonDocument.Parse(ReadSharedBytes(path)); if (TryGetPropertyIgnoreCase(doc.RootElement, "name", out var presetName)) names.Add(presetName.GetString() ?? ""); } catch { }
    return names.ToArray();
}

static string? WaitForPresetFile(string name, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    do
    {
        var path = FindPresetFile(name);
        if (path is not null) return path;
        Thread.Sleep(100);
    } while (DateTime.UtcNow < until && !InteractiveDesktopGuard.IsAborted);
    return null;
}

static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
{
    if (element.ValueKind == JsonValueKind.Object)
    {
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
    }
    value = default;
    return false;
}

static string ReadNumberOrString(JsonElement root, params string[] path)
{
    var value = Navigate(root, path);
    return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
}
static string Describe(AutomationElement? element) => element is null ? "未发现 AspectLabel 的 UIA automation id。" : $"AutomationId={element.Current.AutomationId}; Name={element.Current.Name}";
static void FocusWindow(AutomationElement window)
{
    _ = TrySetAutomationFocus(window);
}
static void SendKey(byte key, bool control, bool shift = false)
{
    if (shift) NativeMethods.KeybdEvent(0xA0, 0x2A, 0, UIntPtr.Zero);
    if (control) NativeMethods.KeybdEvent(0xA2, 0x1D, 0, UIntPtr.Zero);
    if (shift || control) Thread.Sleep(35);
    var scan = (byte)NativeMethods.MapVirtualKey(key, 0);
    var flags = key is >= 0x21 and <= 0x28 or 0x2D or 0x2E ? 1u : 0u;
    NativeMethods.KeybdEvent(key, scan, flags, UIntPtr.Zero); Thread.Sleep(35);
    NativeMethods.KeybdEvent(key, scan, flags | 2, UIntPtr.Zero);
    if (control) NativeMethods.KeybdEvent(0xA2, 0x1D, 2, UIntPtr.Zero);
    if (shift) NativeMethods.KeybdEvent(0xA0, 0x2A, 2, UIntPtr.Zero);
    Thread.Sleep(300);
}
static bool CloseWindowWithoutKeyboard(AutomationElement window)
{
    try
    {
        var handle = (IntPtr)window.Current.NativeWindowHandle;
        var processId = (int)window.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty);
        if (!InteractiveDesktopGuard.IsOwnedProcessId(processId)) return false;
        if (handle == IntPtr.Zero || !NativeMethods.IsWindow(handle)) return true;
        var isVisible = NativeMethods.IsWindowVisible(handle);
        NativeMethods.GetWindowThreadProcessId(handle, out var ownerPid);
        if (ownerPid != (uint)processId) return false;
        if (!isVisible) return true;
        if (window.TryGetCurrentPattern(WindowPattern.Pattern, out var pattern) && pattern is WindowPattern windowPattern)
            windowPattern.Close();
        else if (!NativeMethods.PostMessage(handle, 0x0010, IntPtr.Zero, IntPtr.Zero))
            return false;
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < until)
        {
            try { if (!NativeMethods.IsWindow(handle) || !NativeMethods.IsWindowVisible(handle)) return true; }
            catch (ElementNotAvailableException) { return true; }
            Thread.Sleep(50);
        }
        return false;
    }
    catch (ElementNotAvailableException) { return true; }
    catch (InvalidOperationException) { return false; }
}
static int CloseOwnedTransientPopups(int processId, AutomationElement ownerWindow)
{
    if (!InteractiveDesktopGuard.IsOwnedProcessId(processId)) return 0;
    var ownerHandle = (IntPtr)ownerWindow.Current.NativeWindowHandle;
    var closed = 0;
    foreach (var candidate in FindOwnedProcessWindows(processId))
    {
        try
        {
            if ((IntPtr)candidate.Current.NativeWindowHandle == ownerHandle || !IsOwnedWindowVisible(candidate, processId)) continue;
            if (CloseWindowWithoutKeyboard(candidate)) closed++;
        }
        catch (ElementNotAvailableException) { closed++; }
    }
    return closed;
}
static (int Width, int Height) ReadDimensions(string project)
{
    using var doc = JsonDocument.Parse(ReadSharedBytes(project));
    var settings = doc.RootElement.GetProperty("settings");
    return (settings.GetProperty("canvasWidth").GetInt32(), settings.GetProperty("canvasHeight").GetInt32());
}
static string ReadBackground(string project)
{
    using var doc = JsonDocument.Parse(ReadSharedBytes(project)); return doc.RootElement.GetProperty("settings").GetProperty("background").GetString() ?? "";
}
static string ReadJsonString(string project, params string[] path)
{
    try { using var doc = JsonDocument.Parse(ReadSharedBytes(project)); var node = Navigate(doc.RootElement, path); return node.ValueKind == JsonValueKind.String ? node.GetString() ?? "" : node.ToString(); }
    catch { return ""; }
}
static string ReadJsonArrayString(string project, string arrayName, int index, params string[] path)
{
    try { using var doc = JsonDocument.Parse(ReadSharedBytes(project)); var node = doc.RootElement.GetProperty(arrayName)[index]; foreach (var part in path) node = node.GetProperty(part); return node.ValueKind == JsonValueKind.String ? node.GetString() ?? "" : node.ToString(); }
    catch { return ""; }
}
static bool WaitForJsonArrayString(string project, string arrayName, int index, string path, string value, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until && !InteractiveDesktopGuard.IsAborted)
    {
        if (string.Equals(ReadJsonArrayString(project, arrayName, index, path), value, StringComparison.OrdinalIgnoreCase)) return true;
        Thread.Sleep(120);
    }
    return false;
}
static bool WaitForJsonArrayStringPath(string project, string arrayName, int index, string[] path, string value, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until && !InteractiveDesktopGuard.IsAborted)
    {
        if (string.Equals(ReadJsonArrayString(project, arrayName, index, path), value, StringComparison.OrdinalIgnoreCase)) return true;
        Thread.Sleep(120);
    }
    return false;
}
static bool WaitForArrayObject(string project, string arrayName, int index, string property, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until && !InteractiveDesktopGuard.IsAborted)
    {
        try
        {
            using var doc = JsonDocument.Parse(ReadSharedBytes(project));
            if (doc.RootElement.GetProperty(arrayName)[index].TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object) return true;
        }
        catch { }
        Thread.Sleep(120);
    }
    return false;
}
static double ReadJsonArrayNumber(string project, string arrayName, int index, params string[] path)
{
    try { using var doc = JsonDocument.Parse(ReadSharedBytes(project)); var node = doc.RootElement.GetProperty(arrayName)[index]; foreach (var part in path) node = node.GetProperty(part); return node.GetDouble(); }
    catch { return double.NaN; }
}
static double ReadTimelineSegmentRate(string project, int index)
{
    try { using var doc = JsonDocument.Parse(ReadSharedBytes(project)); return doc.RootElement.GetProperty("timeline").GetProperty("segments")[index].GetProperty("playbackRate").GetDouble(); }
    catch { return double.NaN; }
}
static double ReadTimelineSegmentSystemVolume(string project, int index)
{
    try { using var doc = JsonDocument.Parse(ReadSharedBytes(project)); return doc.RootElement.GetProperty("timeline").GetProperty("segments")[index].GetProperty("audioOverrides").GetProperty("systemVolume").GetDouble(); }
    catch { return double.NaN; }
}
static bool WaitForTimelineSegmentSystemVolume(string project, int index, double value, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until && !InteractiveDesktopGuard.IsAborted)
    {
        if (Math.Abs(ReadTimelineSegmentSystemVolume(project, index) - value) < .001) return true;
        Thread.Sleep(120);
    }
    return false;
}
static bool WaitForTimelineSegmentRate(string project, int index, double value, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until && !InteractiveDesktopGuard.IsAborted)
    {
        if (Math.Abs(ReadTimelineSegmentRate(project, index) - value) < .001) return true;
        Thread.Sleep(120);
    }
    return false;
}
static bool WaitForJsonArrayNumber(string project, string arrayName, int index, string[] path, double value, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until && !InteractiveDesktopGuard.IsAborted)
    {
        if (Math.Abs(ReadJsonArrayNumber(project, arrayName, index, path) - value) < .001) return true;
        Thread.Sleep(120);
    }
    return false;
}
static bool ReadJsonArrayBoolean(string project, string arrayName, int index, params string[] path)
{
    try
    {
        using var doc = JsonDocument.Parse(ReadSharedBytes(project));
        return Navigate(doc.RootElement.GetProperty(arrayName)[index], path).GetBoolean();
    }
    catch { return false; }
}
static bool WaitForJsonArrayBoolean(string project, string arrayName, int index, string property, bool value, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until && !InteractiveDesktopGuard.IsAborted)
    {
        if (ReadJsonArrayBoolean(project, arrayName, index, property) == value) return true;
        Thread.Sleep(120);
    }
    return false;
}
static double ReadJsonNumber(string project, params string[] path)
{
    try { using var doc = JsonDocument.Parse(ReadSharedBytes(project)); return Navigate(doc.RootElement, path).GetDouble(); }
    catch { return double.NaN; }
}
static bool WaitForJsonNumber(string project, string[] path, double value, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until && !InteractiveDesktopGuard.IsAborted)
    {
        if (Math.Abs(ReadJsonNumber(project, path) - value) < 1e-6) return true;
        Thread.Sleep(120);
    }
    return false;
}
static bool ReadJsonBoolean(string project, params string[] path)
{
    try { using var doc = JsonDocument.Parse(ReadSharedBytes(project)); return Navigate(doc.RootElement, path).GetBoolean(); } catch { return false; }
}
static JsonElement Navigate(JsonElement root, string[] path)
{
    foreach (var part in path)
    {
        if (!TryGetPropertyIgnoreCase(root, part, out var child)) throw new KeyNotFoundException($"JSON 中缺少字段 {part}。");
        root = child;
    }
    return root;
}
static bool WaitForJsonString(string project, string[] path, string value, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until && !InteractiveDesktopGuard.IsAborted) { if (string.Equals(ReadJsonString(project, path), value, StringComparison.OrdinalIgnoreCase)) return true; Thread.Sleep(120); }
    return false;
}
static bool WaitForJsonBoolean(string project, string[] path, bool value, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until && !InteractiveDesktopGuard.IsAborted) { if (ReadJsonBoolean(project, path) == value) return true; Thread.Sleep(120); }
    return false;
}
static int ReadArrayCount(string project, string property)
{
    try { using var doc = JsonDocument.Parse(ReadSharedBytes(project)); return doc.RootElement.GetProperty(property).GetArrayLength(); } catch { return -1; }
}
static bool WaitForArrayCount(string project, string property, int count, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until && !InteractiveDesktopGuard.IsAborted) { if (ReadArrayCount(project, property) == count) return true; Thread.Sleep(150); }
    return false;
}
static bool WaitForBackground(string project, string color, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until && !InteractiveDesktopGuard.IsAborted) { try { if (ReadBackground(project).Equals(color, StringComparison.OrdinalIgnoreCase)) return true; } catch { } Thread.Sleep(150); }
    return false;
}
static bool WaitForDimensions(string project, int width, int height, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until && !InteractiveDesktopGuard.IsAborted)
    {
        try { if (ReadDimensions(project) == (width, height)) return true; } catch { }
        Thread.Sleep(150);
    }
    return false;
}
static bool TryMoveFirstZoom(AutomationElement timeline, AutomationElement mainWindow, string projectFile, int processId, out string diagnostic)
{
    diagnostic = "";
    JsonDocument doc;
    try { doc = JsonDocument.Parse(ReadSharedBytes(projectFile)); } catch { diagnostic = "项目JSON读取失败。"; return false; }
    using (doc)
    {
        var root = doc.RootElement;
        if (!root.TryGetProperty("zoomClips", out var clips) || clips.GetArrayLength() == 0) { diagnostic = "项目中没有缩放片段。"; return false; }
        var first = clips[0]; var start = first.GetProperty("start").GetDouble(); var duration = first.GetProperty("end").GetDouble() - start;
        if (duration <= 0) duration = first.GetProperty("duration").GetDouble();
        var bounds = timeline.Current.BoundingRectangle;
        var timelineDuration = root.GetProperty("timeline").GetProperty("duration").GetDouble();
        if (timelineDuration <= 0) return false;
        var outputStart = 0d;
        var outputClipTime = double.NaN;
        foreach (var segment in root.GetProperty("timeline").GetProperty("segments").EnumerateArray())
        {
            var sourceStart = segment.GetProperty("sourceStart").GetDouble();
            var sourceDuration = segment.GetProperty("sourceDuration").GetDouble();
            var rate = segment.GetProperty("playbackRate").GetDouble();
            if (start >= sourceStart && start <= sourceStart + sourceDuration) { outputClipTime = outputStart + (start - sourceStart) / rate; break; }
            outputStart += sourceDuration / rate;
        }
        if (!double.IsFinite(outputClipTime)) { diagnostic = $"无法将缩放素材时间 {start:0.###} 映射到输出时间。"; return false; }
        var dpiScale = mainWindow.Current.BoundingRectangle.Width / 1920d;
        if (dpiScale < .5 || dpiScale > 4) dpiScale = 1;
        var scale = (bounds.Width - 152 * dpiScale) / timelineDuration;
        var x = (int)(bounds.Left + 152 * dpiScale + (outputClipTime + Math.Min(duration * .5, .2)) * scale);
        var y = (int)(bounds.Top + (40 + 90 + 26) * dpiScale);
        var before = start;
        if (!InteractiveDesktopGuard.BeforeInput("缩放片段时间线拖动")) { diagnostic = InteractiveDesktopGuard.AbortReason; return false; }
        NativeMethods.SetCursorPos(x, y); NativeMethods.MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero);
        NativeMethods.SetCursorPos(Math.Min((int)bounds.Right - 20, x + 30), y); NativeMethods.MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < until && !InteractiveDesktopGuard.IsAborted)
        {
            try
            {
                using var changed = JsonDocument.Parse(ReadSharedBytes(projectFile));
                var current = changed.RootElement.GetProperty("zoomClips")[0].GetProperty("start").GetDouble();
                if (Math.Abs(current - before) > .001) { diagnostic = $"已更新 start={current:0.###}。"; return true; }
            }
            catch { }
            Thread.Sleep(150);
        }
        using var final = JsonDocument.Parse(ReadSharedBytes(projectFile));
        var after = final.RootElement.GetProperty("zoomClips")[0].GetProperty("start").GetDouble();
        NativeMethods.GetWindowRect((IntPtr)mainWindow.Current.NativeWindowHandle, out var mainRect);
        diagnostic = $"拖动未改变 start：{before:0.###}→{after:0.###}；timeline={bounds.Left},{bounds.Top} {bounds.Width:0}×{bounds.Height:0}，窗口={mainRect.Left},{mainRect.Top} {mainRect.Right-mainRect.Left}×{mainRect.Bottom-mainRect.Top}，DPI比例={dpiScale:0.##}，输入点=({x},{y})，输出时间={outputClipTime:0.###}，duration={timelineDuration:0.###}，进程ID={processId}。";
        return false;
    }
}
static bool FrameTrailProjectStoreProbe(string path)
{
    try
    {
        using var doc = JsonDocument.Parse(ReadSharedBytes(path));
        return doc.RootElement.GetProperty("format").GetString() == "FrameTrailProject" && doc.RootElement.GetProperty("timeline").GetProperty("segments").GetArrayLength() > 0;
    }
    catch { return false; }
}
static bool TryPasteZoomAttributes(AutomationElement timeline, AutomationElement mainWindow, string projectFile, out string diagnostic)
{
    diagnostic = "";
    try
    {
        using var doc = JsonDocument.Parse(ReadSharedBytes(projectFile));
        var clips = doc.RootElement.GetProperty("zoomClips");
        if (clips.GetArrayLength() < 2) { diagnostic = "项目需要至少两个缩放片段。"; return false; }
        var first = clips[0]; var second = clips[1];
        var targetStart = second.GetProperty("start").GetDouble(); var targetEnd = second.GetProperty("end").GetDouble();
        var expectedScale = first.GetProperty("scale").GetDouble(); var expectedFocusX = first.GetProperty("focus").GetProperty("x").GetDouble();
        if (!ClickZoomClip(timeline, mainWindow, doc.RootElement, 0)) { diagnostic = "无法选中属性来源缩放片段。"; return false; }
        FocusWindow(mainWindow); SendKey(0x43, true); Thread.Sleep(180);
        if (!ClickZoomClip(timeline, mainWindow, doc.RootElement, 1)) { diagnostic = "无法选中属性目标缩放片段。"; return false; }
        FocusWindow(mainWindow); SendKey(0x56, true, shift: true);
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < until && !InteractiveDesktopGuard.IsAborted)
        {
            try
            {
                using var updated = JsonDocument.Parse(ReadSharedBytes(projectFile)); var target = updated.RootElement.GetProperty("zoomClips")[1];
                var scale = target.GetProperty("scale").GetDouble(); var focusX = target.GetProperty("focus").GetProperty("x").GetDouble();
                var start = target.GetProperty("start").GetDouble(); var end = target.GetProperty("end").GetDouble();
                if (Math.Abs(scale - expectedScale) < .001 && Math.Abs(focusX - expectedFocusX) < .001 && Math.Abs(start - targetStart) < .001 && Math.Abs(end - targetEnd) < .001)
                { diagnostic = $"已粘贴缩放属性；目标时间保持 {start:0.###}–{end:0.###} 秒，倍率={scale:0.##}，焦点X={focusX:0.##}。"; return true; }
            }
            catch { }
            Thread.Sleep(120);
        }
        diagnostic = $"目标属性或时间未符合预期；原时间 {targetStart:0.###}–{targetEnd:0.###} 秒，期望倍率={expectedScale:0.##}、焦点X={expectedFocusX:0.##}。"; return false;
    }
    catch (Exception ex) { diagnostic = $"{ex.GetType().Name}: {ex.Message}; {ex.StackTrace}"; return false; }
}
static bool ClickZoomClip(AutomationElement timeline, AutomationElement mainWindow, JsonElement root, int index, bool control = false)
{
    var clips = root.GetProperty("zoomClips"); if (index >= clips.GetArrayLength()) return false;
    var clip = clips[index]; var sourceStart = clip.GetProperty("start").GetDouble(); var sourceDuration = clip.GetProperty("end").GetDouble() - sourceStart;
    var outputStart = 0d; var outputTime = double.NaN;
    foreach (var segment in root.GetProperty("timeline").GetProperty("segments").EnumerateArray())
    {
        var start = segment.GetProperty("sourceStart").GetDouble(); var duration = segment.GetProperty("sourceDuration").GetDouble(); var rate = segment.GetProperty("playbackRate").GetDouble();
        if (sourceStart >= start && sourceStart <= start + duration) { outputTime = outputStart + (sourceStart - start) / rate + Math.Min(sourceDuration * .5, .2); break; }
        outputStart += duration / rate;
    }
    if (!double.IsFinite(outputTime)) return false;
    var bounds = timeline.Current.BoundingRectangle; var dpi = mainWindow.Current.BoundingRectangle.Width / 1920d; if (dpi < .5 || dpi > 4) dpi = 1;
    var scale = (bounds.Width - 152 * dpi) / root.GetProperty("timeline").GetProperty("duration").GetDouble();
    var x = (int)(bounds.Left + 152 * dpi + outputTime * scale); var y = (int)(bounds.Top + 156 * dpi);
    if (control) NativeMethods.KeybdEvent(0x11, 0, 0, UIntPtr.Zero);
    NativeMethods.SetCursorPos(x, y); NativeMethods.MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(80); NativeMethods.MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
    if (control) NativeMethods.KeybdEvent(0x11, 0, 2, UIntPtr.Zero);
    Thread.Sleep(250); return true;
}
static bool TryCopyPasteEffectType(AutomationElement timeline, AutomationElement mainWindow, string projectFile, string arrayName, string valuePath, out string diagnostic)
{
    diagnostic = "";
    ScrollPattern? timelineScroll = null;
    double? originalScrollPercent = null;
    try
    {
        using var before = JsonDocument.Parse(ReadSharedBytes(projectFile));
        var clips = before.RootElement.GetProperty(arrayName);
        if (clips.GetArrayLength() < 2) { diagnostic = $"fixture 中 {arrayName} 少于两段。"; return false; }
        var source = clips[0]; var targetClip = clips[1];
        var sourceValue = ReadNumberPath(source, valuePath); var targetValue = ReadNumberPath(targetClip, valuePath);
        var sourceStart = source.GetProperty("start").GetDouble(); var targetStart = targetClip.GetProperty("start").GetDouble();
        var sourceDuration = source.GetProperty("duration").GetDouble();
        if (!double.IsFinite(sourceValue) || !double.IsFinite(targetValue) || Math.Abs(sourceValue - targetValue) < .001) { diagnostic = $"fixture 值没有区别：{valuePath}={sourceValue}/{targetValue}。"; return false; }
        var sourcePoint = FindVisibleClipInteriorPoint(before.RootElement, timeline, mainWindow, sourceStart, sourceDuration);
        var targetPoint = FindVisibleClipInteriorPoint(before.RootElement, timeline, mainWindow, targetStart, targetClip.GetProperty("duration").GetDouble());
        if (!double.IsFinite(sourcePoint) || !double.IsFinite(targetPoint)) { diagnostic = $"时间线上找不到有足够可见宽度的来源或目标片段：source={sourceStart:0.###}+{sourceDuration:0.###}，target={targetStart:0.###}+{targetClip.GetProperty("duration").GetDouble():0.###}。"; return false; }
        var laneIndex = arrayName switch { "screenMotions" => 1, "cameraMotions" => 3, "overlays" => 4, "blurClips" => 5, _ => -1 };
        if (laneIndex < 0) { diagnostic = "未知的时间线轨道。"; return false; }
        var expectedInspector = arrayName switch { "screenMotions" => "运镜", "cameraMotions" => "摄像头", "overlays" => "贴图", "blurClips" => "柔化", _ => "" };
        var sourceInspector = "";
        var targetInspector = "";
        timelineScroll = FindTimelineScrollPattern(timeline);
        if (timelineScroll is not null) originalScrollPercent = timelineScroll.Current.VerticalScrollPercent;
        if (!ClickEffectClip(timeline, mainWindow, before.RootElement, laneIndex, sourcePoint, out var sourceClick)) { diagnostic = "无法选择来源片段。"; return false; }
        sourceInspector = WaitForInspectorTitle(mainWindow, expectedInspector, TimeSpan.FromSeconds(2));
        if (sourceInspector != expectedInspector)
        {
            var sourceShot = Path.GetFullPath(Path.Combine("artifacts", "validation", $"ui-effect-source-debug-{arrayName}.png"));
            try { CaptureWindow(mainWindow, sourceShot); } catch { sourceShot = "截图失败"; }
            diagnostic = $"来源片段选择尚未在面板确认：期望={expectedInspector}，实际={sourceInspector}；点击={sourceClick}；截图={sourceShot}。";
            return false;
        }
        FocusWindow(mainWindow); TryFocusTimeline(timeline); SendKey(0x43, true);
        if (!ClickEffectClip(timeline, mainWindow, before.RootElement, laneIndex, targetPoint, out var targetClick)) { diagnostic = "无法选择目标片段。"; return false; }
        targetInspector = WaitForInspectorTitle(mainWindow, expectedInspector, TimeSpan.FromSeconds(2));
        if (targetInspector != expectedInspector)
        {
            diagnostic = $"目标片段选择尚未在面板确认：期望={expectedInspector}，实际={targetInspector}；来源点击={sourceClick}，目标点击={targetClick}。";
            return false;
        }
        var debugShot = Path.GetFullPath(Path.Combine("artifacts", "validation", $"ui-effect-debug-{arrayName}.png"));
        try { CaptureWindow(mainWindow, debugShot); } catch { debugShot = "截图失败"; }
        FocusWindow(mainWindow); TryFocusTimeline(timeline); SendKey(0x56, true, shift: true);
        var wait = DateTime.UtcNow + TimeSpan.FromSeconds(4);
        var attributesCopied = false;
        while (DateTime.UtcNow < wait && !InteractiveDesktopGuard.IsAborted)
        {
            using var after = JsonDocument.Parse(ReadSharedBytes(projectFile));
            var list = after.RootElement.GetProperty(arrayName);
            var currentTarget = list.EnumerateArray().OrderBy(x => Math.Abs(x.GetProperty("start").GetDouble() - targetStart)).First();
            attributesCopied = Math.Abs(ReadNumberPath(currentTarget, valuePath) - sourceValue) < .001
                && Math.Abs(currentTarget.GetProperty("start").GetDouble() - targetStart) < .03;
            if (attributesCopied) break;
            Thread.Sleep(120);
        }
        if (!attributesCopied)
        {
            var actual = ReadCurrentClipValue(projectFile, arrayName, targetStart, valuePath);
            var focused = "";
            try { focused = AutomationElement.FocusedElement?.Current.Name ?? ""; } catch { }
            diagnostic = $"属性粘贴后目标值/时间错误；源={sourceValue:0.###}，目标原值={targetValue:0.###}，当前值={actual:0.###}，时间={targetStart:0.###}，来源/目标检查器={sourceInspector}/{targetInspector}，键盘焦点={focused}；来源点击={sourceClick}；目标点击={targetClick}；截图={debugShot}。";
            return false;
        }

        if (!ClickEffectClip(timeline, mainWindow, before.RootElement, laneIndex, sourcePoint, out _)) { diagnostic = "无法重新选择来源片段。"; return false; }
        FocusWindow(mainWindow); SendKey(0x43, true);
        if (!ClickEffectClip(timeline, mainWindow, before.RootElement, laneIndex, 5.55, out _)) { diagnostic = "无法将播放头移到空白轨道位置。"; return false; }
        FocusWindow(mainWindow);
        var oldCount = ReadArrayCount(projectFile, arrayName); SendKey(0x56, true);
        if (!WaitForArrayCount(projectFile, arrayName, oldCount + 1, TimeSpan.FromSeconds(5))) { diagnostic = $"完整粘贴未新增片段：{oldCount}→{ReadArrayCount(projectFile, arrayName)}。"; return false; }
        using var final = JsonDocument.Parse(ReadSharedBytes(projectFile));
        var cloned = final.RootElement.GetProperty(arrayName).EnumerateArray().OrderByDescending(x => x.GetProperty("start").GetDouble()).First();
        var cloneMatches = Math.Abs(ReadNumberPath(cloned, valuePath) - sourceValue) < .001;
        diagnostic = cloneMatches ? $"完整复制新增一段；属性粘贴把目标值 {targetValue:0.###} 更新为 {sourceValue:0.###} 并保留时间 {targetStart:0.###}。" : "完整复制的片段属性与来源不一致。";
        return cloneMatches;
    }
    catch (Exception ex) { diagnostic = $"{ex.GetType().Name}: {ex.Message}; {ex.StackTrace}"; return false; }
    finally
    {
        if (timelineScroll is not null && originalScrollPercent is double original && double.IsFinite(original) && original >= 0)
        {
            try { timelineScroll.SetScrollPercent(ScrollPattern.NoScroll, original); Thread.Sleep(200); } catch { }
        }
    }
}

static void RunTimelineShortcutUiTests(AutomationElement window, string projectFile, List<StepResult> results)
{
    var timeline = FindById(window, "TimelineView") ?? throw new InvalidOperationException("时间线不可见。");
    using var original = JsonDocument.Parse(ReadSharedBytes(projectFile));
    var initial = original.RootElement;
    var motion = initial.GetProperty("screenMotions")[0];
    var start = motion.GetProperty("start").GetDouble();
    var duration = motion.GetProperty("duration").GetDouble();
    var midpoint = start + duration / 2;
    var motionCount = initial.GetProperty("screenMotions").GetArrayLength();
    var screenCount = initial.GetProperty("timeline").GetProperty("segments").GetArrayLength();
    void SelectMotion()
    {
        using var current = JsonDocument.Parse(ReadSharedBytes(projectFile));
        if (!ClickEffectClip(timeline, window, current.RootElement, 1, midpoint, out var detail))
            throw new InvalidOperationException("无法选中屏幕3D内部：" + detail);
        // Effect selection deliberately preserves the playhead. Seek through the ruler
        // as a user would before issuing the edit command.
        var handle = (IntPtr)window.Current.NativeWindowHandle;
        var dpi = NativeMethods.GetDpiForWindow(handle) / 96d;
        if (!PrepareTimelinePoint(timeline, current.RootElement, 18, dpi, out var viewport, out var scrollOffset, out var scrollDetail, out _))
            throw new InvalidOperationException("时间标尺不在可见区域：" + scrollDetail);
        var bounds = timeline.Current.BoundingRectangle;
        var total = current.RootElement.GetProperty("timeline").GetProperty("duration").GetDouble();
        var previousDpi = NativeMethods.SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            var x = (int)(bounds.Left + 152 * dpi + midpoint * (bounds.Width - 152 * dpi) / total);
            var y = (int)(viewport.Top + (18 - scrollOffset) * dpi);
            NativeMethods.SetCursorPos(x, y); Thread.Sleep(80);
            NativeMethods.MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(50);
            NativeMethods.MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(150);
        }
        finally { if (previousDpi != IntPtr.Zero) NativeMethods.SetThreadDpiAwarenessContext(previousDpi); }
        FocusWindow(window); TryFocusTimeline(timeline);
        if (Math.Abs(Time() - midpoint) > .06)
            throw new InvalidOperationException($"播放头未到效果内部：实际 {Time():0.###}，期望 {midpoint:0.###}。");
    }
    JsonDocument Read() => JsonDocument.Parse(ReadSharedBytes(projectFile));
    void Check(bool success, string title, string detail)
    {
        AddStep(results, new(title, success, detail));
        if (!success) throw new InvalidOperationException(title + "：" + detail);
    }
    bool WaitFor(Func<bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        do { if (predicate()) return true; Thread.Sleep(80); } while (timer.Elapsed < TimeSpan.FromSeconds(5) && !InteractiveDesktopGuard.IsAborted);
        return false;
    }
    double MotionValue(string name) { using var state = Read(); return state.RootElement.GetProperty("screenMotions")[0].GetProperty(name).GetDouble(); }
    double Time()
    {
        var text = SafeName(FindById(window, "TimeLabel")).Split(" / ")[0];
        return TimeSpan.ParseExact(text, new[] { @"mm\:ss\.ff", @"h\:mm\:ss\.ff", @"hh\:mm\:ss\.ff" }, System.Globalization.CultureInfo.InvariantCulture).TotalSeconds;
    }

    SelectMotion(); SendKey(0x53, false);
    var split = WaitForArrayCount(projectFile, "screenMotions", motionCount + 1, TimeSpan.FromSeconds(5));
    using (var current = Read())
        Check(split && current.RootElement.GetProperty("timeline").GetProperty("segments").GetArrayLength() == screenCount,
            "S拆分当前屏幕3D，不误切主视频", $"效果数量{motionCount}→{ReadArrayCount(projectFile, "screenMotions")}；主视频仍{screenCount}段；time={Time():0.###}；inspector={SafeName(FindById(window, "InspectorTitle"))}；status={SafeName(FindById(window, "Status"))}；focus={AutomationElement.FocusedElement?.Current.AutomationId}/{AutomationElement.FocusedElement?.Current.ControlType.ProgrammaticName}。");
    SendKey(0x5A, true);
    Check(WaitForArrayCount(projectFile, "screenMotions", motionCount, TimeSpan.FromSeconds(5)), "效果分割可撤销", "Ctrl+Z恢复原片段数。");
    SendKey(0x53, false);
    using (var current = Read())
    {
        var repeated = WaitForArrayCount(projectFile, "screenMotions", motionCount + 1, TimeSpan.FromSeconds(5));
        using var afterRepeat = Read();
        Check(repeated && afterRepeat.RootElement.GetProperty("timeline").GetProperty("segments").GetArrayLength() == screenCount,
            "撤销后再次S保持效果选择", "没有重新点击效果，仍只分割3D轨道且主视频段数不变。");
    }
    SendKey(0x5A, true);
    WaitForArrayCount(projectFile, "screenMotions", motionCount, TimeSpan.FromSeconds(5));
    SendKey(0x59, true);
    Check(WaitForArrayCount(projectFile, "screenMotions", motionCount + 1, TimeSpan.FromSeconds(5)), "效果分割可重做", "Ctrl+Y恢复两段。");
    SendKey(0x5A, true); WaitForArrayCount(projectFile, "screenMotions", motionCount, TimeSpan.FromSeconds(5));

    SelectMotion(); SendKey(0x51, false);
    Check(WaitFor(() => Math.Abs(MotionValue("start") - midpoint) < .06), "Q裁去所选效果左侧", "实际键盘输入后检查自动保存工程的开始时间。");
    SendKey(0x5A, true); WaitFor(() => Math.Abs(MotionValue("start") - start) < .001);
    SelectMotion(); SendKey(0x57, false);
    Check(WaitFor(() => Math.Abs(MotionValue("duration") - duration / 2) < .06), "W裁去所选效果右侧", "实际键盘输入后检查自动保存工程的持续时间。");
    SendKey(0x5A, true); WaitFor(() => Math.Abs(MotionValue("duration") - duration) < .001);
    SelectMotion(); SendKey(0x44, false);
    Check(WaitForArrayCount(projectFile, "screenMotions", motionCount - 1, TimeSpan.FromSeconds(5)), "D删除当前选择", "所选3D片段被移除。");
    SendKey(0x5A, true); WaitForArrayCount(projectFile, "screenMotions", motionCount, TimeSpan.FromSeconds(5));

    SelectMotion(); var before = Time(); var expectedDelta = 5d / initial.GetProperty("media").GetProperty("frameRate").GetDouble();
    SendKey(0x27, false, shift: true);
    Check(WaitFor(() => Math.Abs(Time() - before - expectedDelta) < .021), "Shift+右方向键前进5帧", $"前进量应为{expectedDelta:0.######}秒，实际{before:0.###}→{Time():0.###}，显示时码精度0.01秒。");
    SendKey(0x25, false, shift: true);
    Check(WaitFor(() => Math.Abs(Time() - before) < .021), "Shift+左方向键后退5帧", "时码回到操作前。");
}

static void RunEffectUiTests(AutomationElement window, string projectFile, string[] args, List<StepResult> results)
{
    var filter = args.FirstOrDefault(a => a.StartsWith("--effect=", StringComparison.Ordinal));
    foreach (var effect in new[] { ("screenMotions", "屏幕 3D", "target.position.x"), ("cameraMotions", "摄像运动", "targetPosition.x"), ("overlays", "贴图", "opacity"), ("blurClips", "柔化", "strength") }.Where(x => filter is null || filter == "--effect=" + x.Item1))
    {
        var timeline = FindById(window, "TimelineView");
        var detail = "未找到时间线。";
        var passed = timeline is not null && TryCopyPasteEffectType(timeline, window, projectFile, effect.Item1, effect.Item3, out detail);
        AddStep(results, new($"{effect.Item2}完整复制与属性粘贴", passed, detail));
    }
}

static double ReadCurrentClipValue(string projectFile, string arrayName, double start, string valuePath)
{
    try
    {
        using var document = JsonDocument.Parse(ReadSharedBytes(projectFile));
        var clip = document.RootElement.GetProperty(arrayName).EnumerateArray().MinBy(x => Math.Abs(x.GetProperty("start").GetDouble() - start));
        return clip.ValueKind == JsonValueKind.Undefined ? double.NaN : ReadNumberPath(clip, valuePath);
    }
    catch { return double.NaN; }
}

static double FindVisibleClipInteriorPoint(JsonElement project, AutomationElement timeline, AutomationElement mainWindow, double clipStart, double clipDuration)
{
    var duration = project.GetProperty("timeline").GetProperty("duration").GetDouble();
    var handle = (IntPtr)mainWindow.Current.NativeWindowHandle;
    var dpi = NativeMethods.GetDpiForWindow(handle) / 96d; if (dpi < .5 || dpi > 4) dpi = 1;
    var bounds = timeline.Current.BoundingRectangle;
    var pixelsPerOutputSecond = (bounds.Width - 152 * dpi) / duration;
    if (!double.IsFinite(pixelsPerOutputSecond) || pixelsPerOutputSecond <= 0) return double.NaN;
    var candidates = new List<(double Start, double End, double OutputWidth)>();
    foreach (var segment in project.GetProperty("timeline").GetProperty("segments").EnumerateArray())
    {
        var start = segment.GetProperty("sourceStart").GetDouble();
        var end = start + segment.GetProperty("sourceDuration").GetDouble();
        var rate = segment.GetProperty("playbackRate").GetDouble();
        var overlapStart = Math.Max(clipStart, start);
        var overlapEnd = Math.Min(clipStart + clipDuration, end);
        if (overlapEnd - overlapStart <= 0 || rate <= 0) continue;
        var outputWidth = ((overlapEnd - overlapStart) / rate) * pixelsPerOutputSecond;
        candidates.Add((overlapStart, overlapEnd, outputWidth));
    }
    var selected = candidates.FirstOrDefault(x => x.OutputWidth >= 16);
    if (selected.OutputWidth < 16) selected = candidates.OrderByDescending(x => x.OutputWidth).FirstOrDefault();
    return selected.OutputWidth <= 0 ? double.NaN : (selected.Start + selected.End) / 2;
}

static (ScrollPattern? Pattern, AutomationElement? Viewport) FindTimelineScroll(AutomationElement timeline)
{
    var node = TreeWalker.ControlViewWalker.GetParent(timeline);
    for (var depth = 0; node is not null && depth < 12; depth++, node = TreeWalker.ControlViewWalker.GetParent(node))
    {
        try { if (node.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern) && pattern is ScrollPattern scroll) return (scroll, node); } catch { }
    }
    return (null, null);
}

static ScrollPattern? FindTimelineScrollPattern(AutomationElement timeline) => FindTimelineScroll(timeline).Pattern;

static bool PrepareTimelinePoint(AutomationElement timeline, JsonElement project, double contentY, double dpi,
    out System.Windows.Rect viewport, out double scrollOffset, out string diagnostic, out ScrollPattern? scroll)
{
    viewport = default; scrollOffset = 0; diagnostic = ""; scroll = null;
    var ancestor = FindTimelineScroll(timeline);
    scroll = ancestor.Pattern;
    if (ancestor.Viewport is null || scroll is null) { diagnostic = "未找到包裹时间线的可滚动视口。"; return false; }
    try { viewport = ancestor.Viewport.Current.BoundingRectangle; }
    catch (Exception ex) { diagnostic = "无法读取滚动视口边界：" + ex.Message; return false; }
    if (viewport.IsEmpty || viewport.Height <= 0) { diagnostic = $"视口边界无效：{viewport}"; return false; }

    var laneCount = 2;
    if (project.TryGetProperty("camera", out var camera) && camera.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) laneCount++;
    if (project.TryGetProperty("cameraMotions", out var cameraMotions) && cameraMotions.ValueKind == JsonValueKind.Array && cameraMotions.GetArrayLength() > 0) laneCount++;
    if (project.TryGetProperty("overlays", out var overlays) && overlays.ValueKind == JsonValueKind.Array && overlays.GetArrayLength() > 0) laneCount++;
    if (project.TryGetProperty("blurClips", out var blurClips) && blurClips.ValueKind == JsonValueKind.Array && blurClips.GetArrayLength() > 0) laneCount++;
    if (project.TryGetProperty("audioTracks", out var audioTracks) && audioTracks.ValueKind == JsonValueKind.Array && audioTracks.GetArrayLength() > 0) laneCount++;
    var contentHeight = 130d + laneCount * 52d;
    var viewportHeightDip = viewport.Height / dpi;
    var rangeDip = Math.Max(0, contentHeight - viewportHeightDip);
    var maxOffset = Math.Max(0, rangeDip);
    var wantedOffset = Math.Clamp(contentY - viewportHeightDip * .52, 0, maxOffset);
    var targetPercent = maxOffset <= 0 ? 0 : wantedOffset / maxOffset * 100;
    try
    {
        if (targetPercent > .05 && scroll.Current.VerticallyScrollable)
            scroll.SetScrollPercent(ScrollPattern.NoScroll, targetPercent);
        else if (scroll.Current.VerticallyScrollable)
            scroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
        Thread.Sleep(220);
        var actualPercent = scroll.Current.VerticalScrollPercent;
        if (actualPercent < 0 || !double.IsFinite(actualPercent)) actualPercent = 0;
        scrollOffset = maxOffset * actualPercent / 100;
        viewport = ancestor.Viewport.Current.BoundingRectangle;
        var y = viewport.Top + (contentY - scrollOffset) * dpi;
        var within = y >= viewport.Top + 2 && y <= viewport.Bottom - 2;
        diagnostic = $"contentY={contentY:0.#} DIP, offset={scrollOffset:0.#}/{maxOffset:0.#} DIP, scroll={actualPercent:0.#}%, viewport={viewport}, pointY={y:0.#}, laneCount={laneCount}";
        return within;
    }
    catch (Exception ex) { diagnostic = $"设置滚动位置失败：{ex.GetType().Name}: {ex.Message}；viewport={viewport}"; return false; }
}

static int GetContentLaneIndex(JsonElement project, int requestedLane)
{
    var targetKey = requestedLane switch { 1 => "motion", 3 => "cameraMotion", 4 => "overlay", 5 => "blur", _ => "" };
    if (targetKey.Length == 0) return -1;
    var lanes = new List<string> { "zoom", "motion" };
    if (project.TryGetProperty("camera", out var camera) && camera.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) lanes.Add("camera");
    if (project.TryGetProperty("cameraMotions", out var cameraMotions) && cameraMotions.ValueKind == JsonValueKind.Array && cameraMotions.GetArrayLength() > 0) lanes.Add("cameraMotion");
    if (project.TryGetProperty("overlays", out var overlays) && overlays.ValueKind == JsonValueKind.Array && overlays.GetArrayLength() > 0) lanes.Add("overlay");
    if (project.TryGetProperty("blurClips", out var blurClips) && blurClips.ValueKind == JsonValueKind.Array && blurClips.GetArrayLength() > 0) lanes.Add("blur");
    if (project.TryGetProperty("audioTracks", out var audioTracks) && audioTracks.ValueKind == JsonValueKind.Array && audioTracks.GetArrayLength() > 0) lanes.Add("audio");
    return lanes.IndexOf(targetKey);
}

static void TryFocusTimeline(AutomationElement timeline)
{
    _ = TrySetAutomationFocus(timeline);
}

static string WaitForInspectorTitle(AutomationElement window, string expected, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    var actual = "";
    do
    {
        actual = SafeName(FindById(window, "InspectorTitle"));
        if (actual == expected) return actual;
        Thread.Sleep(50);
    } while (DateTime.UtcNow < until && !InteractiveDesktopGuard.IsAborted);
    return actual;
}

static bool ClickEffectClip(AutomationElement timeline, AutomationElement mainWindow, JsonElement project, int laneIndex, double sourceTime, out string diagnostic)
{
    diagnostic = "";
    var elapsed = 0d; var outputTime = double.NaN;
    foreach (var segment in project.GetProperty("timeline").GetProperty("segments").EnumerateArray())
    {
        var start = segment.GetProperty("sourceStart").GetDouble(); var duration = segment.GetProperty("sourceDuration").GetDouble(); var rate = segment.GetProperty("playbackRate").GetDouble();
        if (sourceTime >= start && sourceTime < start + duration) { outputTime = elapsed + (sourceTime - start) / rate; break; }
        elapsed += duration / rate;
    }
    if (!double.IsFinite(outputTime)) return false;
    var mainHandle = (IntPtr)mainWindow.Current.NativeWindowHandle;
    var dpi = NativeMethods.GetDpiForWindow(mainHandle) / 96d; if (dpi < .5 || dpi > 4) dpi = 1;
    var contentLaneIndex = GetContentLaneIndex(project, laneIndex);
    if (contentLaneIndex < 0) { diagnostic = $"无法匹配时间线轨道 laneIndex={laneIndex}；项目轨道布局已改变。"; return false; }
    var contentY = 156 + contentLaneIndex * 52;
    if (!PrepareTimelinePoint(timeline, project, contentY, dpi, out var viewport, out var scrollOffset, out var scrollDescription, out _))
    { diagnostic = "时间线轨道当前不在可见视口：" + scrollDescription; return false; }
    var bounds = timeline.Current.BoundingRectangle;
    var scale = (bounds.Width - 152 * dpi) / project.GetProperty("timeline").GetProperty("duration").GetDouble();
    var x = (int)(bounds.Left + 152 * dpi + outputTime * scale);
    var y = (int)(viewport.Top + (contentY - scrollOffset) * dpi);
    var previousDpiContext = NativeMethods.SetThreadDpiAwarenessContext(new IntPtr(-4));
    var rootAfter = IntPtr.Zero;
    try
    {
        if (!InteractiveDesktopGuard.BeforeInput("效果片段时间线点击")) { diagnostic = InteractiveDesktopGuard.AbortReason; return false; }
        NativeMethods.SetCursorPos(x, y); Thread.Sleep(100);
        NativeMethods.GetCursorPos(out var cursor);
        var pointWindow = NativeMethods.WindowFromPoint(cursor);
        var rootBefore = NativeMethods.GetAncestor(pointWindow, 2);
        var pointOwner = NativeMethods.GetWindowThreadProcessId(pointWindow, out var pointPid);
        var rootOwner = NativeMethods.GetWindowThreadProcessId(rootBefore, out var rootPid);
        var rootName = new System.Text.StringBuilder(256); var rootClass = new System.Text.StringBuilder(128);
        NativeMethods.GetWindowText(rootBefore, rootName, rootName.Capacity); NativeMethods.GetClassName(rootBefore, rootClass, rootClass.Capacity);
        if (rootBefore != mainHandle)
        {
            diagnostic = $"点击被拦截：点位属于其他窗口。xy={x},{y},cursor={cursor.X},{cursor.Y},hwnd={pointWindow.ToInt64():X}/pid{pointPid},root={rootBefore.ToInt64():X}/pid{rootPid},own={mainHandle.ToInt64():X},rootTitle={rootName},rootClass={rootClass},timeline={bounds.Left:0.#},{bounds.Top:0.#},{bounds.Width:0.#},{bounds.Height:0.#},viewport={viewport},scroll={scrollDescription}";
            return false;
        }
        NativeMethods.MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(65); NativeMethods.MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(180);
        rootAfter = NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(cursor), 2);
    diagnostic = $"out={outputTime:0.###},xy={x},{y},cursor={cursor.X},{cursor.Y},hwnd={pointWindow.ToInt64():X}/pid{pointPid},thread{pointOwner},root={rootBefore.ToInt64():X}->{rootAfter.ToInt64():X}/pid{rootPid},thread{rootOwner},own={mainHandle.ToInt64():X},rootTitle={rootName},rootClass={rootClass},timeline={bounds.Left:0.#},{bounds.Top:0.#},{bounds.Width:0.#},{bounds.Height:0.#},viewport={viewport},scroll={scrollDescription}";
    }
    finally { if (previousDpiContext != IntPtr.Zero) NativeMethods.SetThreadDpiAwarenessContext(previousDpiContext); }
    return rootAfter == mainHandle;
}
static double ReadNumberPath(JsonElement root, string path)
{
    foreach (var part in path.Split('.')) root = root.GetProperty(part);
    return root.GetDouble();
}
static bool TryDragCameraOnCanvas(AutomationElement window, string projectFile, out string diagnostic)
{
    diagnostic = "";
    try
    {
        var image = FindById(window, "PreviewImage"); if (image is null) { diagnostic = "未找到预览画布。"; return false; }
        using var beforeDoc = JsonDocument.Parse(ReadSharedBytes(projectFile)); var camera = beforeDoc.RootElement.GetProperty("camera");
        var oldX = camera.GetProperty("position").GetProperty("x").GetDouble(); var oldY = camera.GetProperty("position").GetProperty("y").GetDouble();
        if (!double.IsFinite(oldX) || !double.IsFinite(oldY)) { diagnostic = $"摄像头位置不是有限值：({oldX},{oldY})。"; return false; }
        if (!WaitForFinitePreviewBounds(image, TimeSpan.FromSeconds(5), out var rect))
        { diagnostic = "预览画布边界在5秒内未稳定为有限且可用的尺寸。"; return false; }
        var x = (int)(rect.Left + oldX * rect.Width); var y = (int)(rect.Top + oldY * rect.Height);
        var dx = Math.Max(18, (int)(rect.Width * .025)); var dy = -Math.Max(10, (int)(rect.Height * .015));
        var processId = window.Current.ProcessId;
        if (!InteractiveDesktopGuard.BeforeInput("摄像头拖动定位")) { diagnostic = InteractiveDesktopGuard.AbortReason; return false; }
        NativeMethods.SetCursorPos(x, y); Thread.Sleep(120);
        NativeMethods.GetCursorPos(out var startPoint);
        var rootWindow = NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(startPoint), 2);
        NativeMethods.GetWindowThreadProcessId(rootWindow, out var rootProcessId);
        if (rootProcessId != processId) { diagnostic = $"摄像头拖动起点不属于测试应用：pid={rootProcessId}，期望={processId}。"; return false; }
        if (!InteractiveDesktopGuard.BeforeInput("摄像头拖动按下")) { diagnostic = InteractiveDesktopGuard.AbortReason; return false; }
        NativeMethods.MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero);
        try
        {
            Thread.Sleep(80);
            var destinationX = Math.Clamp(x + dx, (int)Math.Ceiling(rect.Left + 2), (int)Math.Floor(rect.Right - 2));
            var destinationY = Math.Clamp(y + dy, (int)Math.Ceiling(rect.Top + 2), (int)Math.Floor(rect.Bottom - 2));
            if (InteractiveDesktopGuard.BeforeInput("摄像头拖动移动"))
            {
                NativeMethods.SetCursorPos(destinationX, destinationY);
                NativeMethods.GetCursorPos(out var destinationPoint);
                var destinationRoot = NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(destinationPoint), 2);
                NativeMethods.GetWindowThreadProcessId(destinationRoot, out var destinationProcessId);
                if (destinationProcessId != processId) diagnostic = $"摄像头拖动终点不属于测试应用：pid={destinationProcessId}，期望={processId}。";
            }
            else diagnostic = InteractiveDesktopGuard.AbortReason;
            Thread.Sleep(160);
        }
        finally
        {
            // Always release the button this gesture pressed, including guard aborts.
            NativeMethods.MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
        }
        if (!string.IsNullOrEmpty(diagnostic)) return false;
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < until && !InteractiveDesktopGuard.IsAborted)
        {
            try { using var after = JsonDocument.Parse(ReadSharedBytes(projectFile)); var p = after.RootElement.GetProperty("camera").GetProperty("position"); var nx = p.GetProperty("x").GetDouble(); var ny = p.GetProperty("y").GetDouble(); if (Math.Abs(nx-oldX)>.005 || Math.Abs(ny-oldY)>.005) { diagnostic=$"画布拖动已保存位置 ({oldX:0.###},{oldY:0.###})→({nx:0.###},{ny:0.###})。"; return true; } } catch { }
            Thread.Sleep(120);
        }
        diagnostic=$"鼠标手势未写入位置；起点=({oldX:0.###},{oldY:0.###})，输入点=({x},{y})，增量=({dx},{dy})。"; return false;
    }
    catch(Exception ex){diagnostic=ex.Message;return false;}
}
static bool WaitForFinitePreviewBounds(AutomationElement image, TimeSpan timeout, out Rect rect)
{
    rect = Rect.Empty;
    var until = DateTime.UtcNow + timeout;
    Rect? previous = null;
    while (DateTime.UtcNow < until && !InteractiveDesktopGuard.IsAborted)
    {
        try
        {
            var current = image.Current.BoundingRectangle;
            var valid = !current.IsEmpty && double.IsFinite(current.Left) && double.IsFinite(current.Top)
                && double.IsFinite(current.Width) && double.IsFinite(current.Height)
                && double.IsFinite(current.Right) && double.IsFinite(current.Bottom)
                && current.Left >= int.MinValue && current.Top >= int.MinValue && current.Right <= int.MaxValue && current.Bottom <= int.MaxValue
                && current.Width is >= 100 and <= 10000 && current.Height is >= 100 and <= 10000;
            if (valid)
            {
                rect = current;
                if (previous is { } last && Math.Abs(last.Left - current.Left) < 2 && Math.Abs(last.Top - current.Top) < 2
                    && Math.Abs(last.Width - current.Width) < 2 && Math.Abs(last.Height - current.Height) < 2) return true;
                previous = current;
            }
            else previous = null;
        }
        catch (ElementNotAvailableException) { previous = null; }
        Thread.Sleep(120);
    }
    return false;
}
static bool TryDeleteZoomGroup(AutomationElement timeline, AutomationElement mainWindow, string projectFile, out string diagnostic)
{
    diagnostic="";
    try
    {
        using var doc=JsonDocument.Parse(ReadSharedBytes(projectFile)); var root=doc.RootElement; var count=root.GetProperty("zoomClips").GetArrayLength(); if(count<2){diagnostic="少于两个缩放片段，无法构成多选。";return false;}
        if(!ClickZoomClip(timeline,mainWindow,root,0)||!ClickZoomClip(timeline,mainWindow,root,1,control:true)){diagnostic="未能点击两个缩放片段。";return false;}
        FocusWindow(mainWindow); NativeMethods.KeybdEvent(0x2E,0,0,UIntPtr.Zero);NativeMethods.KeybdEvent(0x2E,0,2,UIntPtr.Zero);
        var removed=WaitForArrayCount(projectFile,"zoomClips",count-2,TimeSpan.FromSeconds(5));
        if(!removed){diagnostic=$"Delete后缩放数量未从{count}降到{count-2}，当前={ReadArrayCount(projectFile,"zoomClips")}。";return false;}
        FocusWindow(mainWindow);SendKey(0x5A,true);var restored=WaitForArrayCount(projectFile,"zoomClips",count,TimeSpan.FromSeconds(5));
        diagnostic=restored?$"Ctrl多选并Delete移除两段；Ctrl+Z恢复为{count}段。":$"两段已删除，但撤销后未恢复到{count}段。当前={ReadArrayCount(projectFile,"zoomClips")}。";return restored;
    }
    catch(Exception ex){diagnostic=ex.Message;return false;}
}
static bool TryExportRangeFromTimeline(AutomationElement timeline,AutomationElement window,string projectFile,int processId,out string diagnostic)
{
    diagnostic="";
    try
    {
        if(!ClickTimelineAt(timeline,window,projectFile,.5)||!OpenTimelineContextAt(timeline,window,projectFile,.5)){diagnostic="无法打开导出范围起点菜单。";return false;}
        var start=WaitForNameContaining("设置导出起点",processId,TimeSpan.FromSeconds(3));if(start is null||!TryInvoke(start)){diagnostic="右键菜单未提供导出起点命令。";return false;}
        if(!ClickTimelineAt(timeline,window,projectFile,2.5)||!OpenTimelineContextAt(timeline,window,projectFile,2.5)){diagnostic="无法打开导出范围终点菜单。";return false;}
        var end=WaitForNameContaining("设置导出终点",processId,TimeSpan.FromSeconds(3));if(end is null||!TryInvoke(end)){diagnostic="右键菜单未提供导出终点命令。";return false;}
        var status=FindById(window,"Status");if(status is null||!WaitForStatusContains(window,"导出范围",TimeSpan.FromSeconds(3))){diagnostic=$"I/O快捷键未设置范围：{status?.Current.Name}";return false;}
        var exp=FindById(window,"ExportButton");if(exp is null||!TryClickPhysical(exp)){diagnostic="无法打开导出窗口。";return false;}var export=WaitForWindowName(processId,"导出作品",TimeSpan.FromSeconds(4));if(export is null){diagnostic="导出窗口未出现。";return false;}
        var names=export.FindAll(TreeScope.Descendants,System.Windows.Automation.Condition.TrueCondition).Cast<AutomationElement>().Select(SafeName).ToArray();var rangeDuration=names.Contains("0:02");_ = CloseWindowWithoutKeyboard(export);Thread.Sleep(250);
        if(!rangeDuration){diagnostic=$"导出摘要没有显示预期2秒范围；控件=[{string.Join(" | ",names.Where(x=>x.Contains(':')))}]。";return false;}
        if(!OpenTimelineContextAt(timeline,window,projectFile,.5)){diagnostic="无法打开清除范围菜单。";return false;}
        var clear=WaitForName("清除导出范围",processId,TimeSpan.FromSeconds(3));if(clear is null||!TryInvoke(clear)){diagnostic="2秒范围已显示，但右键菜单没有清除命令。";return false;}
        exp=FindById(window,"ExportButton");if(exp is null||!TryClickPhysical(exp)){diagnostic="清除后无法重新打开导出窗口。";return false;}export=WaitForWindowName(processId,"导出作品",TimeSpan.FromSeconds(4));if(export is null){diagnostic="清除后导出窗口未出现。";return false;}
        names=export.FindAll(TreeScope.Descendants,System.Windows.Automation.Condition.TrueCondition).Cast<AutomationElement>().Select(SafeName).ToArray();
        using var fullDoc=JsonDocument.Parse(ReadSharedBytes(projectFile));
        var fullSeconds=fullDoc.RootElement.GetProperty("timeline").GetProperty("duration").GetDouble();
        var expectedDuration=TimeSpan.FromSeconds(fullSeconds).ToString(@"m\:ss");
        var fullDuration=names.Contains(expectedDuration);_ = CloseWindowWithoutKeyboard(export);
        diagnostic=fullDuration?$"I/O选择2秒区间后导出摘要显示0:02；清除区间后显示完整{expectedDuration}。":$"范围清除后预期时长{expectedDuration}，摘要文本=[{string.Join(" | ",names.Where(x=>x.Contains(':')))}]。";return fullDuration;
    }
    catch(Exception ex){diagnostic=ex.Message;return false;}
}
static bool OpenTimelineContextAt(AutomationElement timeline,AutomationElement window,string projectFile,double time)
{
    try
    {
        using var doc=JsonDocument.Parse(ReadSharedBytes(projectFile));var bounds=timeline.Current.BoundingRectangle;var dpi=window.Current.BoundingRectangle.Width/1920d;if(dpi<.5||dpi>4)dpi=1;
        var duration=doc.RootElement.GetProperty("timeline").GetProperty("duration").GetDouble();var scale=(bounds.Width-142*dpi)/duration;
        var x=(int)(bounds.Left+142*dpi+time*scale);var y=(int)(bounds.Top+20*dpi);NativeMethods.SetCursorPos(x,y);Thread.Sleep(100);NativeMethods.MouseEvent(0x0008,0,0,0,UIntPtr.Zero);Thread.Sleep(80);NativeMethods.MouseEvent(0x0010,0,0,0,UIntPtr.Zero);return true;
    }
    catch{return false;}
}
static bool OpenScreenSegmentContext(AutomationElement timeline, AutomationElement window, string projectFile, double sourceTime, out string diagnostic)
{
    diagnostic = "";
    var previousDpiContext = NativeMethods.SetThreadDpiAwarenessContext(new IntPtr(-4));
    try
    {
        using var doc = JsonDocument.Parse(ReadSharedBytes(projectFile));
        var segments = doc.RootElement.GetProperty("timeline").GetProperty("segments");
        if (segments.GetArrayLength() == 0) return false;
        var segment = segments[0];
        var sourceStart = segment.GetProperty("sourceStart").GetDouble();
        var sourceDuration = segment.GetProperty("sourceDuration").GetDouble();
        var rate = segment.GetProperty("playbackRate").GetDouble();
        if (sourceTime < sourceStart || sourceTime >= sourceStart + sourceDuration) sourceTime = sourceStart + sourceDuration * .5;
        var outputStart = 0d;
        foreach (var item in segments.EnumerateArray())
        {
            if (item.GetProperty("id").GetString() == segment.GetProperty("id").GetString()) break;
            outputStart += item.GetProperty("sourceDuration").GetDouble() / item.GetProperty("playbackRate").GetDouble();
        }
        var outputTime = outputStart + (sourceTime - sourceStart) / rate;
        var bounds = timeline.Current.BoundingRectangle;
        var windowBounds = window.Current.BoundingRectangle;
        var dpi = windowBounds.Width / 1920d; if (dpi < .5 || dpi > 4) dpi = 1;
        var scale = (bounds.Width - 152 * dpi) / doc.RootElement.GetProperty("timeline").GetProperty("duration").GetDouble();
        var x = (int)(bounds.Left + 152 * dpi + outputTime * scale);
        var y = (int)(bounds.Top + 40 * dpi + 35 * dpi);
        var handle = (IntPtr)window.Current.NativeWindowHandle;
        var processId = (int)window.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty);
        if (handle == IntPtr.Zero) { diagnostic = "主窗口句柄为空。"; return false; }
        if (!InteractiveDesktopGuard.BeforeInput("速度菜单时间线点击")) { diagnostic = InteractiveDesktopGuard.AbortReason; return false; }
        NativeMethods.SetCursorPos(x, y); Thread.Sleep(100);
        var point = NativeMethods.WindowFromPoint(new PointNative { X = x, Y = y });
        NativeMethods.GetWindowThreadProcessId(point, out var pointProcessId);
        if (pointProcessId != (uint)processId)
        {
            diagnostic = $"点击点不属于测试进程：xy={x},{y}，hwnd={point.ToInt64():X}，pid={pointProcessId}，期望pid={processId}，窗口={bounds.Left:0.#},{bounds.Top:0.#},{bounds.Width:0.#},{bounds.Height:0.#}。";
            return false;
        }
        NativeMethods.MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(60);
        NativeMethods.MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(180);
        NativeMethods.MouseEvent(0x0008, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(70);
        NativeMethods.MouseEvent(0x0010, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(180);
        var menu = WaitForNameContaining("速度 0.5×", processId, TimeSpan.FromSeconds(2));
        diagnostic = $"点击测试进程窗口：xy={x},{y}，窗口={bounds.Left:0.#},{bounds.Top:0.#},{bounds.Width:0.#},{bounds.Height:0.#}，菜单={menu is not null}。";
        return true;
    }
    catch (Exception ex) { diagnostic = ex.GetType().Name + ": " + ex.Message; return false; }
    finally { if (previousDpiContext != IntPtr.Zero) NativeMethods.SetThreadDpiAwarenessContext(previousDpiContext); }
}
static bool ClickTimelineAt(AutomationElement timeline,AutomationElement window,string projectFile,double time)
{
    try
    {
        using var doc=JsonDocument.Parse(ReadSharedBytes(projectFile));var bounds=timeline.Current.BoundingRectangle;var dpi=window.Current.BoundingRectangle.Width/1920d;if(dpi<.5||dpi>4)dpi=1;
        var duration=doc.RootElement.GetProperty("timeline").GetProperty("duration").GetDouble();var scale=(bounds.Width-142*dpi)/duration;
        var x=(int)(bounds.Left+142*dpi+time*scale);var y=(int)(bounds.Top+20*dpi);NativeMethods.SetCursorPos(x,y);Thread.Sleep(150);NativeMethods.MouseEvent(0x0001,0,0,0,UIntPtr.Zero);NativeMethods.MouseEvent(0x0002,0,0,0,UIntPtr.Zero);Thread.Sleep(60);NativeMethods.MouseEvent(0x0004,0,0,0,UIntPtr.Zero);Thread.Sleep(120);return true;
    }
    catch{return false;}
}
static bool WaitForStatusContains(AutomationElement window,string fragment,TimeSpan timeout)
{
    var until=DateTime.UtcNow+timeout;while(DateTime.UtcNow<until && !InteractiveDesktopGuard.IsAborted){try{if((FindById(window,"Status")?.Current.Name??"").Contains(fragment,StringComparison.Ordinal))return true;}catch{}Thread.Sleep(100);}return false;
}
static AutomationElement? WaitForNameContaining(string fragment,int processId,TimeSpan timeout)
{
    var until=DateTime.UtcNow+timeout;while(DateTime.UtcNow<until && !InteractiveDesktopGuard.IsAborted){foreach(var item in FindOwnedProcessDescendants(processId,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.MenuItem)))if((item.Current.Name??"").Contains(fragment,StringComparison.OrdinalIgnoreCase))return item;Thread.Sleep(100);}return null;
}
static void CopyDirectory(string source, string destination)
{
    Directory.CreateDirectory(destination);
    foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
    foreach (var directory in Directory.EnumerateDirectories(source)) CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
}
static void PrepareFeatureFixture(string projectFile)
{
    var project = JsonNode.Parse(File.ReadAllText(projectFile))?.AsObject() ?? throw new InvalidDataException("测试项目JSON无效。");
    var mediaPath = project["mediaPath"]?.GetValue<string>() ?? throw new InvalidDataException("测试项目缺少主素材路径。");
    var projectDirectory = Path.GetDirectoryName(projectFile)!;
    var overlayRelative = "media/uia-overlay.png";
    var overlayPath = Path.Combine(projectDirectory, overlayRelative.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(overlayPath)!);
    var sampleImage = Path.Combine(projectDirectory, "export-frame-0001.png");
    if (!File.Exists(sampleImage)) throw new FileNotFoundException("合成测试项目缺少 PNG 帧图。", sampleImage);
    File.Copy(sampleImage, overlayPath, true);
    project["camera"] = new JsonObject
    {
        ["mediaPath"] = mediaPath, ["sourceStart"] = 0, ["duration"] = 6, ["timelineStart"] = 0,
        ["position"] = new JsonObject { ["x"] = .8, ["y"] = .8 }, ["size"] = .25, ["cornerRadius"] = .12,
        ["borderWidth"] = 1, ["opacity"] = 1, ["volume"] = 1, ["muted"] = true,
        ["mirrored"] = false, ["shape"] = "roundedRectangle",
        ["crop"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = 1, ["height"] = 1 }
    };
    var settings = project["settings"]?.AsObject() ?? new JsonObject();
    settings["backgroundStyle"] = new JsonObject { ["kind"] = "image", ["imagePath"] = overlayRelative, ["imageFit"] = "cover", ["color"] = "#23313D" };
    project["settings"] = settings;
    project["screenMotions"] = new JsonArray
    {
        new JsonObject { ["start"] = 1.3, ["duration"] = .65, ["target"] = new JsonObject { ["position"] = new JsonObject { ["x"] = .22, ["y"] = .36 }, ["scale"] = 1.4 } },
        new JsonObject { ["start"] = 3.0, ["duration"] = .8, ["target"] = new JsonObject { ["position"] = new JsonObject { ["x"] = .72, ["y"] = .58 }, ["scale"] = 1.1 } }
    };
    project["cameraMotions"] = new JsonArray
    {
        new JsonObject { ["start"] = .7, ["duration"] = .8, ["targetPosition"] = new JsonObject { ["x"] = .24, ["y"] = .72 }, ["targetSize"] = .2 },
        new JsonObject { ["start"] = 2.6, ["duration"] = .8, ["targetPosition"] = new JsonObject { ["x"] = .72, ["y"] = .75 }, ["targetSize"] = .3 }
    };
    project["overlays"] = new JsonArray
    {
        new JsonObject { ["mediaPath"] = overlayRelative, ["start"] = .8, ["duration"] = .8, ["opacity"] = .35 },
        new JsonObject { ["mediaPath"] = overlayRelative, ["start"] = 2.8, ["duration"] = .8, ["opacity"] = .92 }
    };
    project["blurClips"] = new JsonArray
    {
        new JsonObject { ["start"] = .9, ["duration"] = .8, ["strength"] = .22 },
        new JsonObject { ["start"] = 3.0, ["duration"] = .8, ["strength"] = .78 }
    };
    if (project["zoomClips"] is JsonArray zooms && zooms.Count >= 2)
    {
        zooms[0]!["scale"] = 2.2; zooms[0]!["automatic"] = false; zooms[0]!["focus"]!["x"] = .32;
        zooms[1]!["scale"] = 1.1; zooms[1]!["automatic"] = false; zooms[1]!["focus"]!["x"] = .76;
    }
    File.WriteAllText(projectFile, project.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
}
static void PrepareMotionCurveFixture(string projectFile)
{
    var project = JsonNode.Parse(File.ReadAllBytes(projectFile))?.AsObject() ?? throw new InvalidDataException("测试项目 JSON 无效。");
    static JsonObject Curve(double x1 = .65, double y1 = .15, double x2 = .35, double y2 = .85) => new() { ["x1"] = x1, ["y1"] = y1, ["x2"] = x2, ["y2"] = y2 };
    var zooms = project["zoomClips"]?.AsArray() ?? new JsonArray();
    if (zooms.Count == 0) zooms.Add(new JsonObject { ["start"] = 0, ["end"] = 1.8, ["automatic"] = false });
    var zoom = zooms[0]!.AsObject(); zoom["start"] = 0; zoom["end"] = 1.8; zoom["automatic"] = false; zoom["easing"] = "cubic"; zoom["customCurve"] = Curve(); project["zoomClips"] = zooms;
    var screens = project["screenMotions"]?.AsArray() ?? new JsonArray();
    if (screens.Count == 0) screens.Add(new JsonObject { ["start"] = 0, ["duration"] = 1.8, ["target"] = new JsonObject { ["position"] = new JsonObject { ["x"] = .5, ["y"] = .5 }, ["scale"] = 1.2 } });
    var screen = screens[0]!.AsObject(); screen["start"] = 0; screen["duration"] = 1.8; screen["easing"] = "cubic"; screen["customCurve"] = Curve(); project["screenMotions"] = screens;
    var cameras = project["cameraMotions"]?.AsArray() ?? new JsonArray();
    if (cameras.Count == 0) cameras.Add(new JsonObject { ["start"] = 0, ["duration"] = 1.8, ["targetPosition"] = new JsonObject { ["x"] = .5, ["y"] = .5 }, ["targetSize"] = .3 });
    var camera = cameras[0]!.AsObject(); camera["start"] = 0; camera["duration"] = 1.8; camera["easing"] = "cubic"; camera["customCurve"] = Curve(); project["cameraMotions"] = cameras;
    var settings = project["settings"]?.AsObject() ?? new JsonObject();
    settings["opening"] = new JsonObject { ["enabled"] = false, ["preset"] = "converge", ["elementCurve"] = "swift", ["duration"] = 2.2 };
    settings["spring"] = new JsonObject { ["mass"] = 2.4, ["stiffness"] = 210, ["damping"] = 42 };
    settings["cursor"] = new JsonObject { ["visible"] = true, ["scale"] = 1, ["style"] = "systemArrow", ["motionTiltEnabled"] = false, ["motionTiltStrength"] = 2, ["clickEffectsEnabled"] = false, ["clickEffect"] = "pulse", ["clickDuration"] = .55, ["clickScale"] = 1.2, ["clickOpacity"] = .55 };
    project["settings"] = settings;
    File.WriteAllText(projectFile, project.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
}
static void PrepareUncutEffectTimeline(string projectFile)
{
    var project = JsonNode.Parse(File.ReadAllText(projectFile))?.AsObject() ?? throw new InvalidDataException("测试项目JSON无效。");
    var duration = project["media"]?["duration"]?.GetValue<double>() ?? 6;
    project["timeline"] = new JsonObject
    {
        ["segments"] = new JsonArray(new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(), ["sourceStart"] = 0, ["sourceDuration"] = duration, ["playbackRate"] = 1
        }),
        ["duration"] = duration
    };
    File.WriteAllText(projectFile, project.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
}
static void CaptureWindow(AutomationElement window, string path)
{
    if (!InteractiveDesktopGuard.BeforeInput("读取测试窗口截图"))
        throw new InvalidOperationException("前台已切离测试应用；已停止截图和后续输入。");
    var handle = (IntPtr)window.Current.NativeWindowHandle;
    if (handle == IntPtr.Zero) throw new InvalidOperationException("UIA 窗口没有原生句柄，无法截图。");
    Thread.Sleep(100);
    NativeMethods.GetWindowRect(handle, out var rect);
    var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
    if (width <= 0 || height <= 0) throw new InvalidOperationException("主窗口截图尺寸无效。");
    var source = NativeMethods.GetDC(IntPtr.Zero); var memory = NativeMethods.CreateCompatibleDC(source);
    var bitmap = NativeMethods.CreateCompatibleBitmap(source, width, height); var previous = NativeMethods.SelectObject(memory, bitmap);
    try
    {
        if (!NativeMethods.BitBlt(memory, 0, 0, width, height, source, rect.Left, rect.Top, 0x00CC0020)) throw new InvalidOperationException("BitBlt 无法读取屏幕窗口像素。");
        var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        image.Freeze(); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(path); encoder.Save(file);
    }
    finally { NativeMethods.SelectObject(memory, previous); NativeMethods.DeleteObject(bitmap); NativeMethods.DeleteDC(memory); NativeMethods.ReleaseWindowDC(IntPtr.Zero, source); }
}

static void AddStep(List<StepResult> results, StepResult result)
{
    results.Add(result);
    SmokeProgress.Append(result);
}

static class SmokeProgress
{
    public static string Path = string.Empty;
    public static bool IsComplete { get; private set; } = true;
    public static void MarkIncomplete(string message)
    {
        IsComplete = false;
        AppendMessage("验证证据不完整：" + message);
    }
    public static void AppendMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(Path)) return;
        try { File.AppendAllText(Path, $"{DateTimeOffset.Now:O} 信息：{message}{Environment.NewLine}"); } catch { }
    }
    public static void Append(StepResult result)
    {
        if (string.IsNullOrWhiteSpace(Path)) return;
        var line = $"{DateTimeOffset.Now:O} {(result.Passed ? "通过" : "未通过")}：{result.Name} — {result.Detail}{Environment.NewLine}";
        try { File.AppendAllText(Path, line); } catch { }
    }
}

record StepResult(string Name, bool Passed, string Detail);
sealed record UserDataSnapshotEntry(string Path, bool Exists, string? ContentBase64);
sealed record UserDataSnapshotDocument(int Version, UserDataSnapshotEntry[] Files);
sealed class ApplicationErrorDialogException(string message) : Exception(message);
sealed class SmokeEarlyExitException : Exception { }

static class NativeMethods
{
    internal static bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags)
    {
        return InteractiveDesktopGuard.RunGuardedInput("SetWindowPos", () => SetWindowPosNative(hWnd, insertAfter, x, y, width, height, flags | 0x0010)); // SWP_NOACTIVATE
    }
    [DllImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)] private static extern bool SetWindowPosNative(IntPtr hWnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr hWnd);
    internal static bool SetCursorPos(int x, int y) => InteractiveDesktopGuard.RunGuardedInput("SetCursorPos", () => SetCursorPosNative(x, y));
    [DllImport("user32.dll", EntryPoint = "SetCursorPos")] private static extern bool SetCursorPosNative(int x, int y);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out PointNative point);
    [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(PointNative point);
    [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);
    internal static void MouseEvent(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo)
    {
        _ = InteractiveDesktopGuard.SendMouseEvent(flags, dx, dy, data, extraInfo);
    }
    internal static void KeybdEvent(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo)
    {
        _ = InteractiveDesktopGuard.SendKeyEvent(virtualKey, scanCode, flags, extraInfo);
    }
    [DllImport("user32.dll")] internal static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] internal static extern IntPtr GetWindowDC(IntPtr hWnd);
    [DllImport("user32.dll", EntryPoint = "GetDC")] internal static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll", EntryPoint = "ReleaseDC")] internal static extern int ReleaseWindowDC(IntPtr hWnd, IntPtr hDc);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hWnd, out RectNative rect);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)] internal static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleDC(IntPtr hDc);
    [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr hDc);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleBitmap(IntPtr hDc, int width, int height);
    [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr hDc, IntPtr value);
    [DllImport("gdi32.dll")] internal static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr value);
}
struct PointNative { public int X, Y; }
struct RectNative { public int Left, Top, Right, Bottom; }
