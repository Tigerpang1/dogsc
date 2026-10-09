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

// 面向交互桌面会话的 UIA 冒烟验收。传入已存在项目，程序会先复制项目目录再启动，避免修改用户工程。
if (args.Length < 2)
{
    Console.Error.WriteLine("用法：FrameTrail.App.Smoke.exe <FrameTrail.exe> <project.frametrail> [report.json]");
    return 2;
}

var appPath = Path.GetFullPath(args[0]);
var sourceProject = Path.GetFullPath(args[1]);
var reportPath = Path.GetFullPath(args.Length > 2 ? args[2] : Path.Combine(Environment.CurrentDirectory, "artifacts", "validation", "ui-smoke.json"));
var progressPath = Path.ChangeExtension(reportPath, ".progress.log");
File.WriteAllText(progressPath, $"UIA 开始 {DateTimeOffset.Now:O}{Environment.NewLine}");
SmokeProgress.Path = progressPath;
var testRoot = args.Contains("--relink-only")
    ? Path.Combine(Environment.CurrentDirectory, "artifacts", "validation", "ui-relink-fixture-audit-" + Guid.NewGuid().ToString("N"))
    : args.Contains("--effects-only")
    ? Path.Combine(Environment.CurrentDirectory, "artifacts", "validation", "ui-effect-fixture-audit-" + Guid.NewGuid().ToString("N"))
    : Path.Combine(Path.GetTempPath(), "frametrail-ui-smoke-" + Guid.NewGuid().ToString("N"));
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
    CopyDirectory(Path.GetDirectoryName(sourceProject)!, projectCopy);
    if (args.Contains("--relink-only"))
    {
        RunRelinkOnly(appPath, sourceProject, projectCopyFile, results);
        return results.All(x => x.Passed) ? 0 : 1;
    }
    PrepareFeatureFixture(projectCopyFile);
    if (args.Contains("--effects-only") || args.Contains("--full-uncut-effects"))
        PrepareUncutEffectTimeline(projectCopyFile);

    app = Process.Start(new ProcessStartInfo(appPath) { UseShellExecute = false, ArgumentList = { "--open", projectCopyFile } })
        ?? throw new InvalidOperationException("无法启动 FrameTrail。");
    var window = WaitForWindow(app, TimeSpan.FromSeconds(35));
    AddResult(new("应用启动并加载项目", window is not null, window is null ? "未找到该进程的主窗口。" : "已通过进程窗口句柄读取 UI Automation 树。"));
    if (window is null) throw new TimeoutException("FrameTrail 主窗口未出现。");
    NativeMethods.SetWindowPos((IntPtr)window.Current.NativeWindowHandle, IntPtr.Zero, 960, 200, 0, 0, 0x0001 | 0x0004);
    var projectTitle = WaitForProjectTitle(window, TimeSpan.FromSeconds(20));
    var projectLoaded = projectTitle is not null && projectTitle.Current.Name != "未命名项目";
    AddResult(new("命令行项目确实已打开", projectLoaded, projectLoaded ? projectTitle!.Current.Name : "标题仍为未命名项目；本轮后续交互结果只作界面诊断，不计功能通过。"));
    if (args.Contains("--effects-only"))
    {
        RunEffectUiTests(window, projectCopyFile, args, results);
        app.CloseMainWindow();
        if (!app.WaitForExit(5000)) app.Kill(entireProcessTree: true);
        app.Dispose(); app = null;
        return results.All(x => x.Passed) ? 0 : 1;
    }
    if (args.Contains("--new-panels-only"))
    {
        RunNewPanelUiTests(window, app, appPath, projectCopyFile, results);
        app.CloseMainWindow();
        if (!app.WaitForExit(5000)) app.Kill(entireProcessTree: true);
        app.Dispose(); app = null;
        return results.All(x => x.Passed) ? 0 : 1;
    }
    if (args.Contains("--memo-only"))
    {
        TestMemoPersistence(window, app.Id, results);
        return results.All(x => x.Passed) ? 0 : 1;
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
        return results.All(x => x.Passed) ? 0 : 1;
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
            try { scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement); Thread.Sleep(250); if (timelineProbe is not null) ancestry.Add($"after scroll bounds={timelineProbe.Current.BoundingRectangle} scroll={scroll.Current.VerticalScrollPercent:0.##}%"); }
            catch (Exception ex) { ancestry.Add("scroll test=" + ex.GetType().Name + ":" + ex.Message); }
        }
        Console.WriteLine("TIMELINE_PROBE=" + string.Join(Environment.NewLine, ancestry));
        return 0;
    }
    var screenshotPath = Path.ChangeExtension(reportPath, ".png");
    CaptureWindow(window, screenshotPath);
    AddResult(new("主窗口实际截图已保存", File.Exists(screenshotPath), screenshotPath));

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

    TestCustomPresetLifecycle(window, app.Id, projectCopyFile, results);
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
    var zoomOpeningCard = openingToggleChanged ? FindButtonByHint(window, "放大入场") : null;
    var openingPresetSelected = zoomOpeningCard is not null && TryClickPhysical(zoomOpeningCard);
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
    if (projectMenuOpened) SendKey(0x1B, false);

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
        if (!exportWindowClosed && exportWindow is not null) { FocusWindow(exportWindow); SendAltF4(); }
    }
    else SendKey(0x1B, false); // 关闭旧版系统“另存为”对话框，避免后续保存操作被模态窗口拦截。

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
        try { if (folderEdit is not null) { var value = (ValuePattern)folderEdit.GetCurrentPattern(ValuePattern.Pattern); value.SetValue(testRoot); safeFolderSet = value.Current.Value == testRoot; } } catch { }
        var savePreferences = FindButtonByHint(prefsWindow, "保存设置");
        if (safeFolderSet && excludeEditorPreference is not null && savePreferences is not null && TryInvoke(savePreferences))
        {
            var settingFile = Path.Combine(localData, "FrameTrail", "settings.json");
            settingsSaved = WaitForPreferenceValues(settingFile, 960, 60, TimeSpan.FromSeconds(4));
        }
    }
    if (prefsWindow is not null) { FocusWindow(prefsWindow); SendAltF4(); }
    AddResult(new("预览质量和排除偏好可保存", settingsSaved, settingsSaved ? "设置页将预览宽度写为960、帧率写为60；测试结束会恢复本机原设置文件。" : $"设置窗口={prefsWindow is not null}，预览分辨率控件={prefWidth is not null}，帧率控件={prefFps is not null}，选项={widthSet}/{fpsSet}。"));

    app.CloseMainWindow();
    if (!app.WaitForExit(5000)) { app.Kill(entireProcessTree: true); app.WaitForExit(); }
    app.Dispose(); app = null;
    app = Process.Start(new ProcessStartInfo(appPath) { UseShellExecute = false, ArgumentList = { "--open", projectCopyFile } });
    var memoWindow = app is null ? null : WaitForWindow(app, TimeSpan.FromSeconds(20));
    if (memoWindow is not null)
    {
        NativeMethods.SetWindowPos((IntPtr)memoWindow.Current.NativeWindowHandle, IntPtr.Zero, 960, 200, 0, 0, 0x0001 | 0x0004);
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
    if (app is { HasExited: false }) { app.CloseMainWindow(); if (!app.WaitForExit(3000)) app.Kill(entireProcessTree: true); }
    app?.Dispose();
    foreach (var snapshot in userDataSnapshots) RestoreUserFile(snapshot.Key, snapshot.Value);
    Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
    File.WriteAllText(reportPath, JsonSerializer.Serialize(new
    {
        application = appPath,
        sourceProject,
        isolatedProjectCopy = projectCopyFile,
        generatedUtc = DateTimeOffset.UtcNow,
        results,
        passed = results.Count > 0 && results.All(x => x.Passed)
    }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"UIA_REPORT={reportPath}");
    foreach (var result in results) Console.WriteLine($"{(result.Passed ? "通过" : "未通过")}：{result.Name} — {result.Detail}");
    if (!args.Contains("--effects-only") && !args.Contains("--keep-fixture") && !args.Contains("--relink-only")) try { Directory.Delete(testRoot, true); } catch { }
}
return results.Count > 0 && results.All(x => x.Passed) ? 0 : 1;

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
    var openingCard = toggleSet ? FindButtonByHint(window, "放大入场") : null;
    var openingSet = openingCard is not null && TryClickPhysical(openingCard)
        && WaitForJsonString(projectFile, ["settings", "opening", "preset"], "zoomUp", TimeSpan.FromSeconds(5));
    AddStep(results, new("开场启用与预设卡片提交", openingOpened && toggleSet && openingSet,
        $"面板={openingOpened}，复选框={enabled is not null}，原值={isEnabled}，点击/已启用={toggleSet}，预设={openingSet}，JSON={ReadJsonBoolean(projectFile, "settings", "opening", "enabled")}/{ReadJsonString(projectFile, "settings", "opening", "preset")}"));

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
        var value = (ValuePattern)fileName.GetCurrentPattern(ValuePattern.Pattern);
        value.SetValue(path);
        details.Add("ValueSet=" + (value.Current.Value == path));
        if (value.Current.Value != path) { diagnostic = string.Join(";", details); return false; }
        details.AddRange(dialog.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>().Select(x => $"Button[{SafeName(x)}|{SafeAutomationId(x)}]"));
        var open = dialog.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>()
            .FirstOrDefault(x => SafeAutomationId(x) == "1")
            ?? dialog.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>()
                .FirstOrDefault(x => SafeName(x).Contains("打开", StringComparison.OrdinalIgnoreCase) || SafeName(x).Equals("Open", StringComparison.OrdinalIgnoreCase));
        if (open is null) { diagnostic = string.Join(";", details) + ";未找到Open按钮"; return false; }
        var invocation = (InvokePattern)open.GetCurrentPattern(InvokePattern.Pattern);
        var invokeTask = Task.Run(invocation.Invoke);
        var invoked = invokeTask.Wait(TimeSpan.FromSeconds(5));
        if (invoked) invokeTask.GetAwaiter().GetResult();
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
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until && !process.HasExited)
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
static AutomationElement? WaitForMainWindow(int processId, TimeSpan timeout)
{
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
static void RestoreUserFile(string path, byte[]? original)
{
    try
    {
        if (original is null) { if (File.Exists(path)) File.Delete(path); }
        else { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, original); }
    }
    catch { }
}
static bool WaitForPreferenceValues(string path, int width, int fps, TimeSpan timeout)
{
    var until=DateTime.UtcNow+timeout;
    while(DateTime.UtcNow<until)
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
    while (DateTime.UtcNow < until) { var combo = FindComboByName(root, name); if (combo is not null) return combo; Thread.Sleep(100); }
    return null;
}
static AutomationElement? WaitForProjectTitle(AutomationElement root, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until)
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
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until)
    {
        var matches = AutomationElement.RootElement.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name));
        foreach (AutomationElement item in matches) if ((int)item.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty) == processId) return item;
        Thread.Sleep(150);
    }
    return null;
}
static AutomationElement? WaitForWindowName(int processId, string namePart, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until)
    {
        var matches = AutomationElement.RootElement.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));
        foreach (AutomationElement item in matches)
            if ((int)item.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty) == processId && item.Current.ControlType == ControlType.Window && item.Current.Name.Contains(namePart, StringComparison.OrdinalIgnoreCase)) return item;
        Thread.Sleep(200);
    }
    return null;
}
static AutomationElement? WaitForNameById(AutomationElement root, string id, Func<string, bool> predicate, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until)
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
    while (DateTime.UtcNow < until)
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
        return AutomationElement.RootElement.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition).Cast<AutomationElement>()
            .Where(x => (int)x.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty) == processId)
            .Select(x => { try { return x.Current.Name; } catch { return ""; } }).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
    }
    catch { return []; }
}
static IEnumerable<string> FindProcessWindowNames(int processId)
{
    return AutomationElement.RootElement.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window)).Cast<AutomationElement>()
        .Where(x => (int)x.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty) == processId && x.Current.ControlType == ControlType.Window)
        .Select(x => x.Current.Name).ToArray();
}
static bool TryInvoke(AutomationElement element)
{
    try
    {
        var invocation = Task.Run(() =>
            ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke());
        if (!invocation.Wait(TimeSpan.FromSeconds(5))) return false;
        invocation.GetAwaiter().GetResult();
        var processId = (int)element.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty);
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
        var expansion = Task.Run(() => ((ExpandCollapsePattern)element.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand());
        if (!expansion.Wait(TimeSpan.FromSeconds(5))) return false;
        expansion.GetAwaiter().GetResult();
        return true;
    }
    catch { return false; }
}
static bool SetRangeValue(AutomationElement element, double value)
{
    try
    {
        var setting = Task.Run(() =>
        {
            var range = (RangeValuePattern)element.GetCurrentPattern(RangeValuePattern.Pattern);
            range.SetValue(Math.Clamp(value, range.Current.Minimum, range.Current.Maximum));
        });
        if (!setting.Wait(TimeSpan.FromSeconds(5))) return false;
        setting.GetAwaiter().GetResult();
        return true;
    }
    catch { return false; }
}
static bool TrySelect(AutomationElement element)
{
    try { ((SelectionItemPattern)element.GetCurrentPattern(SelectionItemPattern.Pattern)).Select(); return true; }
    catch { return TryInvoke(element); }
}
static bool SelectComboOption(AutomationElement combo, string option, int processId, int fallbackDown)
{
    if (TryExpand(combo))
    {
        Thread.Sleep(150);
        var item = FindNamed(combo, option) ?? WaitForName(option, processId, TimeSpan.FromSeconds(1));
        if (item is not null && (TrySelect(item) || TryClickPhysical(item)))
        { Thread.Sleep(250); try { ((ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse(); } catch { } return true; }
        try { ((ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse(); } catch { }
    }
    try
    {
        combo.SetFocus();
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
    var previousDpiContext = NativeMethods.SetThreadDpiAwarenessContext(new IntPtr(-4));
    try
    {
        var processId = (int)element.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty);
        if (preferAutomationInvoke && element.Current.ControlType == ControlType.Button && TryInvoke(element))
        {
            Thread.Sleep(220);
            ThrowIfApplicationErrorDialog(processId);
            return true;
        }
        IntPtr mainHandle;
        using (var owner = Process.GetProcessById(processId))
        {
            mainHandle = owner.MainWindowHandle;
            if (mainHandle != IntPtr.Zero)
            {
                NativeMethods.SetWindowPos(mainHandle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002);
                NativeMethods.SetForegroundWindow(mainHandle);
            }
        }
        Thread.Sleep(180);
        var rect = element.Current.BoundingRectangle;
        if (rect.IsEmpty || rect.Width < 2 || rect.Height < 2) return false;
        var x = (int)(rect.Left + rect.Width / 2); var y = (int)(rect.Top + rect.Height / 2);
        NativeMethods.SetCursorPos(x, y); Thread.Sleep(120);
        var cursor = new PointNative { X = x, Y = y };
        var pointWindow = NativeMethods.WindowFromPoint(cursor);
        NativeMethods.GetWindowThreadProcessId(pointWindow, out var pointProcessId);
        if (pointProcessId != (uint)processId && mainHandle != IntPtr.Zero)
        {
            NativeMethods.SetForegroundWindow(mainHandle);
            Thread.Sleep(150);
            NativeMethods.SetCursorPos(x, y); Thread.Sleep(120);
            pointWindow = NativeMethods.WindowFromPoint(cursor);
            NativeMethods.GetWindowThreadProcessId(pointWindow, out pointProcessId);
        }
        if (pointProcessId != (uint)processId) return false;
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
        if (recorderWindow is not null) { FocusWindow(recorderWindow); SendKey(0x1B, false); }
        return;
    }

    var edits = memo.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)).Cast<AutomationElement>().ToArray();
    var sliders = memo.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Slider)).Cast<AutomationElement>().ToArray();
    const string marker = "FrameTrail UIA memo persistence fixture";
    var textSet = false; var sizeSet = false; var opacitySet = false;
    try { ((ValuePattern)edits[0].GetCurrentPattern(ValuePattern.Pattern)).SetValue(marker); textSet = true; } catch { }
    try { ((RangeValuePattern)sliders[0].GetCurrentPattern(RangeValuePattern.Pattern)).SetValue(32); sizeSet = true; } catch { }
    try { ((RangeValuePattern)sliders[1].GetCurrentPattern(RangeValuePattern.Pattern)).SetValue(.61); opacitySet = true; } catch { }
    var initial = memo.Current.BoundingRectangle;
    var moved = new Rect(initial.Left + 90, initial.Top + 70, initial.Width, initial.Height);
    var movedWindow = initial.IsEmpty == false && NativeMethods.SetWindowPos((IntPtr)memo.Current.NativeWindowHandle, IntPtr.Zero, (int)moved.Left, (int)moved.Top, 0, 0, 0x0001 | 0x0004);
    Thread.Sleep(400);
    var afterMove = memo.Current.BoundingRectangle;
    FocusWindow(memo); SendAltF4(); Thread.Sleep(500);
    recorderWindow = WaitForWindowName(processId, "FrameTrail · 录制", TimeSpan.FromSeconds(3));
    var reopenedRecorder = recorderWindow is not null;
    memoButton = recorderWindow is null ? null : FindButtonByHint(recorderWindow, "备忘录");
    var reopened = memoButton is not null && TryClickPhysical(memoButton) ? WaitForWindowName(processId, "本机备忘录", TimeSpan.FromSeconds(4)) : null;
    if (reopened is null)
    {
        AddStep(results, new("备忘录位置、字号与透明度重开", false, $"文本={textSet}，字号={sizeSet}，透明度={opacitySet}，移动={movedWindow}；重开窗口未出现。"));
        if (recorderWindow is not null) { FocusWindow(recorderWindow); SendKey(0x1B, false); }
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
    AddStep(results, new("备忘录移动、字号与透明度保存重启", all, $"文本={textOkay}，字号={sizeOkay}，透明度={opacityOkay}，位置={positionOkay}；首次移动={movedWindow}，重开坐标差=({reopenedRect.Left-afterMove.Left:0},{reopenedRect.Top-afterMove.Top:0})。"));
    FocusWindow(reopened); SendAltF4();
    if (recorderWindow is not null) { FocusWindow(recorderWindow); SendKey(0x1B, false); }
}
static void TestCustomPresetLifecycle(AutomationElement mainWindow, int processId, string projectFile, List<StepResult> results)
{
    var name = "UIA-" + Guid.NewGuid().ToString("N")[..8];
    string? ownedPresetFile = null;
    var stage = "打开预设菜单";
    try
    {
        if (!OpenPresetMenu(mainWindow)) throw new InvalidOperationException("无法打开场景预设菜单。");
        var save = WaitForMenuItem(processId, "将当前样式保存为本机预设", TimeSpan.FromSeconds(3));
        if (save is null || !TryInvoke(save) || !CompleteTextPrompt(processId, "保存场景预设", name))
            throw new InvalidOperationException("新建预设的名称对话框未完成。");
        ownedPresetFile = WaitForPresetFile(name, TimeSpan.FromSeconds(5));
        var saved = ownedPresetFile is not null;
        AddStep(results, new("本机场景预设可新建", saved, saved ? "本机预设目录中出现独立 manifest。" : $"未发现新建预设 manifest；输入名称={name}；现有名称=[{string.Join(", ", EnumeratePresetNames())}]。"));
        if (!saved) return;

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
                while (clicked && DateTime.UtcNow < until)
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
                if (crossWindow is not null) { FocusWindow(crossWindow); SendAltF4(); }
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

        stage = "重命名预设";
        if (!OpenPresetMenu(mainWindow)) throw new InvalidOperationException("重命名前无法打开场景预设菜单。");
        var entry = WaitForMenuItem(processId, name, TimeSpan.FromSeconds(3));
        var submenuOpened = entry is not null && (TryExpand(entry) || TryClickPhysical(entry));
        var rename = submenuOpened ? WaitForMenuItem(processId, "重命名本机预设", TimeSpan.FromSeconds(2)) : null;
        var renamedName = name + "-renamed";
        var renamed = rename is not null && TryInvoke(rename) && CompleteTextPrompt(processId, "重命名本机预设", renamedName);
        var renamedFile = renamed ? WaitForPresetFile(renamedName, TimeSpan.FromSeconds(5)) : null;
        if (renamedFile is not null) ownedPresetFile = renamedFile;
        AddStep(results, new("本机场景预设可改名", renamed && renamedFile is not null, renamed && renamedFile is not null ? "manifest 名称已更新，预设资产目录保持原位。" : $"子菜单={submenuOpened}，重命名命令={rename is not null}。"));
        if (!renamed || renamedFile is null) return;

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
        if (!applied) { FocusWindow(mainWindow); SendKey(0x1B, false); }
        AddStep(results, new("本机预设可应用到项目", applied, applied ? "项目先切换到深色，再由本机预设恢复为浅色，写入行为已验证。" : $"先切换深色={darkApplied}，应用命令={apply is not null}，项目背景={ReadBackground(projectFile)}。"));
    }
    catch (ApplicationErrorDialogException) { throw; }
    catch (Exception ex) { AddStep(results, new("本机预设生命周期 UIA", false, $"阶段={stage}；{ex.Message}")); }
    finally
    {
        DismissOwnedDialog(processId, "覆盖本机预设");
        DismissOwnedDialog(processId, "保存场景预设");
        DismissOwnedDialog(processId, "重命名本机预设");
        if (ownedPresetFile is not null && File.Exists(ownedPresetFile))
        {
            try { Directory.Delete(Path.GetDirectoryName(ownedPresetFile)!, true); } catch { }
        }
    }
}

static bool WaitForWindowClosed(int processId, string namePart, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until)
    {
        if (WaitForWindowName(processId, namePart, TimeSpan.Zero) is null) return true;
        Thread.Sleep(100);
    }
    return WaitForWindowName(processId, namePart, TimeSpan.Zero) is null;
}

static void DismissOwnedDialog(int processId, string namePart)
{
    var dialog = WaitForWindowName(processId, namePart, TimeSpan.FromMilliseconds(50));
    if (dialog is null) return;
    var cancel = dialog.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
        .Cast<AutomationElement>().FirstOrDefault(x => SafeName(x) is "否" or "取消" or "No" or "Cancel");
    if (cancel is not null) TryClickPhysical(cancel, preferAutomationInvoke: false);
    else { FocusWindow(dialog); SendKey(0x1B, false); }
    WaitForWindowClosed(processId, namePart, TimeSpan.FromSeconds(2));
}

static bool OpenPresetMenu(AutomationElement mainWindow)
{
    var button = FindButtonByHint(mainWindow, "场景预设");
    FocusWindow(mainWindow); SendKey(0x1B, false);
    return button is not null && TryClickPhysical(button);
}

static AutomationElement? WaitForMenuItem(int processId, string namePart, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until)
    {
        var items = AutomationElement.RootElement.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem));
        foreach (AutomationElement item in items)
            try { if ((int)item.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty) == processId && SafeName(item).Contains(namePart, StringComparison.OrdinalIgnoreCase)) return item; } catch { }
        Thread.Sleep(100);
    }
    return null;
}

static bool CompleteTextPrompt(int processId, string title, string value)
{
    var dialog = WaitForWindowName(processId, title, TimeSpan.FromSeconds(3));
    if (dialog is null) return false;
    var edits = dialog.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)).Cast<AutomationElement>().ToArray();
    var okay = dialog.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>().FirstOrDefault(x => SafeName(x) == "确定");
    try { if (edits.Length == 0) return false; ((ValuePattern)edits[0].GetCurrentPattern(ValuePattern.Pattern)).SetValue(value); }
    catch { return false; }
    return okay is not null && TryInvoke(okay);
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
    } while (DateTime.UtcNow < until);
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
    try
    {
        window.SetFocus();
        var handle = (IntPtr)window.Current.NativeWindowHandle;
        if (handle != IntPtr.Zero) NativeMethods.SetForegroundWindow(handle);
    }
    catch { }
}
static void SendKey(byte key, bool control, bool shift = false)
{
    if (shift) NativeMethods.KeybdEvent(0x10, 0, 0, UIntPtr.Zero);
    if (control) NativeMethods.KeybdEvent(0x11, 0, 0, UIntPtr.Zero);
    NativeMethods.KeybdEvent(key, 0, 0, UIntPtr.Zero); NativeMethods.KeybdEvent(key, 0, 2, UIntPtr.Zero);
    if (control) NativeMethods.KeybdEvent(0x11, 0, 2, UIntPtr.Zero);
    if (shift) NativeMethods.KeybdEvent(0x10, 0, 2, UIntPtr.Zero);
    Thread.Sleep(300);
}
static void SendAltF4()
{
    NativeMethods.KeybdEvent(0x12, 0, 0, UIntPtr.Zero);
    NativeMethods.KeybdEvent(0x73, 0, 0, UIntPtr.Zero);
    NativeMethods.KeybdEvent(0x73, 0, 2, UIntPtr.Zero);
    NativeMethods.KeybdEvent(0x12, 0, 2, UIntPtr.Zero);
    Thread.Sleep(500);
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
static double ReadJsonNumber(string project, params string[] path)
{
    try { using var doc = JsonDocument.Parse(ReadSharedBytes(project)); return Navigate(doc.RootElement, path).GetDouble(); }
    catch { return double.NaN; }
}
static bool WaitForJsonNumber(string project, string[] path, double value, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until)
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
    while (DateTime.UtcNow < until) { if (string.Equals(ReadJsonString(project, path), value, StringComparison.OrdinalIgnoreCase)) return true; Thread.Sleep(120); }
    return false;
}
static bool WaitForJsonBoolean(string project, string[] path, bool value, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until) { if (ReadJsonBoolean(project, path) == value) return true; Thread.Sleep(120); }
    return false;
}
static int ReadArrayCount(string project, string property)
{
    try { using var doc = JsonDocument.Parse(ReadSharedBytes(project)); return doc.RootElement.GetProperty(property).GetArrayLength(); } catch { return -1; }
}
static bool WaitForArrayCount(string project, string property, int count, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until) { if (ReadArrayCount(project, property) == count) return true; Thread.Sleep(150); }
    return false;
}
static bool WaitForBackground(string project, string color, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until) { try { if (ReadBackground(project).Equals(color, StringComparison.OrdinalIgnoreCase)) return true; } catch { } Thread.Sleep(150); }
    return false;
}
static bool WaitForDimensions(string project, int width, int height, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until)
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
        NativeMethods.SetForegroundWindow((IntPtr)timeline.Current.NativeWindowHandle);
        NativeMethods.SetCursorPos(x, y); NativeMethods.MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero);
        NativeMethods.SetCursorPos(Math.Min((int)bounds.Right - 20, x + 30), y); NativeMethods.MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < until)
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
        while (DateTime.UtcNow < until)
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
        var laneIndex = arrayName switch { "screenMotions" => 1, "cameraMotions" => 3, "overlays" => 4, "blurClips" => 5, _ => -1 };
        if (laneIndex < 0) { diagnostic = "未知的时间线轨道。"; return false; }
        var expectedInspector = arrayName switch { "screenMotions" => "运镜", "cameraMotions" => "摄像头", "overlays" => "贴图", "blurClips" => "柔化", _ => "" };
        var sourceInspector = "";
        var targetInspector = "";
        timelineScroll = FindTimelineScrollPattern(timeline);
        if (laneIndex >= 2 && timelineScroll is not null)
        {
            timelineScroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement);
            Thread.Sleep(300);
            timeline = FindById(mainWindow, "TimelineView") ?? timeline;
            timelineScroll = FindTimelineScrollPattern(timeline);
        }
        if (!ClickEffectClip(timeline, mainWindow, before.RootElement, laneIndex, sourceStart + Math.Min(.2, sourceDuration * .35), out var sourceClick)) { diagnostic = "无法选择来源片段。"; return false; }
        sourceInspector = WaitForInspectorTitle(mainWindow, expectedInspector, TimeSpan.FromSeconds(2));
        if (sourceInspector != expectedInspector)
        {
            var sourceShot = Path.GetFullPath(Path.Combine("artifacts", "validation", $"ui-effect-source-debug-{arrayName}.png"));
            try { CaptureWindow(mainWindow, sourceShot); } catch { sourceShot = "截图失败"; }
            diagnostic = $"来源片段选择尚未在面板确认：期望={expectedInspector}，实际={sourceInspector}；点击={sourceClick}；截图={sourceShot}。";
            return false;
        }
        FocusWindow(mainWindow); TryFocusTimeline(timeline); SendKey(0x43, true);
        if (!ClickEffectClip(timeline, mainWindow, before.RootElement, laneIndex, targetStart + Math.Min(.2, sourceDuration * .35), out var targetClick)) { diagnostic = "无法选择目标片段。"; return false; }
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
        while (DateTime.UtcNow < wait)
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

        if (!ClickEffectClip(timeline, mainWindow, before.RootElement, laneIndex, sourceStart + Math.Min(.2, sourceDuration * .35), out _)) { diagnostic = "无法重新选择来源片段。"; return false; }
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
        if (timelineScroll is not null)
        {
            try { timelineScroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeDecrement); Thread.Sleep(200); } catch { }
        }
    }
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

static ScrollPattern? FindTimelineScrollPattern(AutomationElement timeline)
{
    var node = TreeWalker.ControlViewWalker.GetParent(timeline);
    for (var depth = 0; node is not null && depth < 12; depth++, node = TreeWalker.ControlViewWalker.GetParent(node))
    {
        try { if (node.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern) && pattern is ScrollPattern scroll) return scroll; } catch { }
    }
    return null;
}

static void TryFocusTimeline(AutomationElement timeline)
{
    try { timeline.SetFocus(); } catch { }
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
    } while (DateTime.UtcNow < until);
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
    var bounds = timeline.Current.BoundingRectangle; var windowBounds = mainWindow.Current.BoundingRectangle;
    var dpi = windowBounds.Width / 1920d; if (dpi < .5 || dpi > 4) dpi = 1;
    var scale = (bounds.Width - 152 * dpi) / project.GetProperty("timeline").GetProperty("duration").GetDouble();
    var x = (int)(bounds.Left + 152 * dpi + outputTime * scale);
    var y = (int)(bounds.Top + (156 + laneIndex * 52) * dpi);
    var mainHandle = (IntPtr)mainWindow.Current.NativeWindowHandle;
    var previousDpiContext = NativeMethods.SetThreadDpiAwarenessContext(new IntPtr(-4));
    try
    {
        NativeMethods.SetWindowPos(mainHandle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002);
        try { mainWindow.SetFocus(); } catch { }
        var foregroundSet = NativeMethods.SetForegroundWindow(mainHandle);
        NativeMethods.SetCursorPos(x, y); Thread.Sleep(100);
        NativeMethods.GetCursorPos(out var cursor);
        var pointWindow = NativeMethods.WindowFromPoint(cursor);
        var rootBefore = NativeMethods.GetAncestor(pointWindow, 2);
        var pointOwner = NativeMethods.GetWindowThreadProcessId(pointWindow, out var pointPid);
        var rootOwner = NativeMethods.GetWindowThreadProcessId(rootBefore, out var rootPid);
        var rootName = new System.Text.StringBuilder(256); var rootClass = new System.Text.StringBuilder(128);
        NativeMethods.GetWindowText(rootBefore, rootName, rootName.Capacity); NativeMethods.GetClassName(rootBefore, rootClass, rootClass.Capacity);
        NativeMethods.MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(65); NativeMethods.MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(180);
        var rootAfter = NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(cursor), 2);
        if (rootBefore != mainHandle && rootAfter == mainHandle)
        {
            NativeMethods.SetCursorPos(x, y); Thread.Sleep(60); NativeMethods.MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(65); NativeMethods.MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(220);
        }
        diagnostic = $"out={outputTime:0.###},xy={x},{y},cursor={cursor.X},{cursor.Y},hwnd={pointWindow.ToInt64():X}/pid{pointPid},thread{pointOwner},root={rootBefore.ToInt64():X}->{rootAfter.ToInt64():X}/pid{rootPid},thread{rootOwner},own={mainHandle.ToInt64():X},foregroundSet={foregroundSet},rootTitle={rootName},rootClass={rootClass},timeline={bounds.Left:0.#},{bounds.Top:0.#},{bounds.Width:0.#},{bounds.Height:0.#}";
    }
    finally { if (previousDpiContext != IntPtr.Zero) NativeMethods.SetThreadDpiAwarenessContext(previousDpiContext); }
    return true;
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
        var rect = image.Current.BoundingRectangle; if (rect.IsEmpty || rect.Width < 100 || rect.Height < 100) { diagnostic = $"预览画布尺寸无效：{rect.Width:0}×{rect.Height:0}。"; return false; }
        var x = (int)(rect.Left + oldX * rect.Width); var y = (int)(rect.Top + oldY * rect.Height);
        var dx = Math.Max(18, (int)(rect.Width * .025)); var dy = -Math.Max(10, (int)(rect.Height * .015));
        NativeMethods.SetCursorPos(x, y); Thread.Sleep(150); NativeMethods.MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(80);
        NativeMethods.SetCursorPos(x + dx, y + dy); Thread.Sleep(160); NativeMethods.MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < until)
        {
            try { using var after = JsonDocument.Parse(ReadSharedBytes(projectFile)); var p = after.RootElement.GetProperty("camera").GetProperty("position"); var nx = p.GetProperty("x").GetDouble(); var ny = p.GetProperty("y").GetDouble(); if (Math.Abs(nx-oldX)>.005 || Math.Abs(ny-oldY)>.005) { diagnostic=$"画布拖动已保存位置 ({oldX:0.###},{oldY:0.###})→({nx:0.###},{ny:0.###})。"; return true; } } catch { }
            Thread.Sleep(120);
        }
        diagnostic=$"鼠标手势未写入位置；起点=({oldX:0.###},{oldY:0.###})，输入点=({x},{y})，增量=({dx},{dy})。"; return false;
    }
    catch(Exception ex){diagnostic=ex.Message;return false;}
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
        var names=export.FindAll(TreeScope.Descendants,System.Windows.Automation.Condition.TrueCondition).Cast<AutomationElement>().Select(SafeName).ToArray();var rangeDuration=names.Contains("0:02");FocusWindow(export);SendAltF4();Thread.Sleep(250);
        if(!rangeDuration){diagnostic=$"导出摘要没有显示预期2秒范围；控件=[{string.Join(" | ",names.Where(x=>x.Contains(':')))}]。";return false;}
        if(!OpenTimelineContextAt(timeline,window,projectFile,.5)){diagnostic="无法打开清除范围菜单。";return false;}
        var clear=WaitForName("清除导出范围",processId,TimeSpan.FromSeconds(3));if(clear is null||!TryInvoke(clear)){diagnostic="2秒范围已显示，但右键菜单没有清除命令。";return false;}
        exp=FindById(window,"ExportButton");if(exp is null||!TryClickPhysical(exp)){diagnostic="清除后无法重新打开导出窗口。";return false;}export=WaitForWindowName(processId,"导出作品",TimeSpan.FromSeconds(4));if(export is null){diagnostic="清除后导出窗口未出现。";return false;}
        names=export.FindAll(TreeScope.Descendants,System.Windows.Automation.Condition.TrueCondition).Cast<AutomationElement>().Select(SafeName).ToArray();
        using var fullDoc=JsonDocument.Parse(ReadSharedBytes(projectFile));
        var fullSeconds=fullDoc.RootElement.GetProperty("timeline").GetProperty("duration").GetDouble();
        var expectedDuration=TimeSpan.FromSeconds(fullSeconds).ToString(@"m\:ss");
        var fullDuration=names.Contains(expectedDuration);FocusWindow(export);SendAltF4();
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
    var until=DateTime.UtcNow+timeout;while(DateTime.UtcNow<until){try{if((FindById(window,"Status")?.Current.Name??"").Contains(fragment,StringComparison.Ordinal))return true;}catch{}Thread.Sleep(100);}return false;
}
static AutomationElement? WaitForNameContaining(string fragment,int processId,TimeSpan timeout)
{
    var until=DateTime.UtcNow+timeout;while(DateTime.UtcNow<until){try{foreach(var item in AutomationElement.RootElement.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.MenuItem)).Cast<AutomationElement>())if((int)item.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty)==processId&&(item.Current.Name??"").Contains(fragment,StringComparison.OrdinalIgnoreCase))return item;}catch{}Thread.Sleep(100);}return null;
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
    var handle = (IntPtr)window.Current.NativeWindowHandle;
    if (handle == IntPtr.Zero) throw new InvalidOperationException("UIA 窗口没有原生句柄，无法截图。");
    NativeMethods.SetForegroundWindow(handle); Thread.Sleep(250);
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
    public static void Append(StepResult result)
    {
        if (string.IsNullOrWhiteSpace(Path)) return;
        var line = $"{DateTimeOffset.Now:O} {(result.Passed ? "通过" : "未通过")}：{result.Name} — {result.Detail}{Environment.NewLine}";
        File.AppendAllText(Path, line);
    }
}

record StepResult(string Name, bool Passed, string Detail);
sealed class ApplicationErrorDialogException(string message) : Exception(message);

static class NativeMethods
{
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out PointNative point);
    [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(PointNative point);
    [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll", EntryPoint = "mouse_event")] internal static extern void MouseEvent(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    [DllImport("user32.dll", EntryPoint = "keybd_event")] internal static extern void KeybdEvent(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
    [DllImport("user32.dll")] internal static extern IntPtr GetWindowDC(IntPtr hWnd);
    [DllImport("user32.dll", EntryPoint = "GetDC")] internal static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll", EntryPoint = "ReleaseDC")] internal static extern int ReleaseWindowDC(IntPtr hWnd, IntPtr hDc);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hWnd, out RectNative rect);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleDC(IntPtr hDc);
    [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr hDc);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleBitmap(IntPtr hDc, int width, int height);
    [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr hDc, IntPtr value);
    [DllImport("gdi32.dll")] internal static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr value);
}
struct PointNative { public int X, Y; }
struct RectNative { public int Left, Top, Right, Bottom; }
