using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class PngOverlayUndoSmoke
{
    private sealed record Overlay(string Id, string MediaPath);
    public sealed record Result(bool Passed, string Detail);

    public static async Task<Result> RunAsync(
        string appPath,
        string projectPath,
        string pngPath,
        string reportPath,
        Func<Process, TimeSpan, AutomationElement?> waitForWindow,
        Func<AutomationElement, string, AutomationElement?> findButtonByHint,
        Func<AutomationElement, bool> invoke,
        Action<AutomationElement> focusWindow,
        Func<AutomationElement, bool> activateAtStartup,
        Action<string> progress)
    {
        var checks = new List<object>();
        Process? app = null;
        Process? reopened = null;
        var sourceHash = "";
        string? copiedAssetPath = null;
        var assetHash = "";
        var newOverlay = default(Overlay);
        Overlay[] baseline = [];
        var baselineJson = "[]";
        void Check(bool passed, string name, string detail)
        {
            checks.Add(new { Name = name, Passed = passed, Detail = detail });
            if (!passed) throw new InvalidOperationException(name + "：" + detail);
        }
        bool GuardedKey(AutomationElement window, byte key, bool control, string operation)
        {
            if (!TargetForeground(window, operation)) return false;
            focusWindow(window);
            if (!HasShortcutFocus(window) || !InteractiveDesktopGuard.BeforeInput(operation)) return false;
            return SendGuardedKey(key, control) && !InteractiveDesktopGuard.IsAborted;
        }

        try
        {
            var root = Path.GetFullPath(Path.GetDirectoryName(reportPath)!);
            var projectRoot = Path.GetFullPath(Path.GetDirectoryName(projectPath)!);
            if (!IsUnderRoot(projectRoot, root))
                throw new InvalidOperationException("测试工程副本必须位于本轮新建的 artifacts 验收目录内。");
            if (!File.Exists(projectPath)) throw new FileNotFoundException("隔离工程副本不存在。", projectPath);
            baseline = ReadOverlays(projectPath);
            baselineJson = ReadOverlayCollectionText(projectPath);
            CreateOwnedPng(pngPath);
            sourceHash = Hash(pngPath);
            var decodedSource = DecodePng(pngPath);
            Check(decodedSource.Width == 64 && decodedSource.Height == 48, "源 PNG 可解码", $"尺寸={decodedSource.Width}x{decodedSource.Height}，SHA256={sourceHash}");
            var productVersion = FileVersionInfo.GetVersionInfo(appPath).ProductVersion ?? "";
            var packageBuildInfo = ReadBuildInfo(appPath);
            Check(productVersion.StartsWith("0.2.5", StringComparison.Ordinal)
                && packageBuildInfo.Contains("版本：0.2.5", StringComparison.Ordinal),
                "运行目标确为 0.2.5 冻结发行包", $"ProductVersion={productVersion}；BUILD-INFO={packageBuildInfo}");

            app = Start(appPath, "--open", projectPath);
            var window = waitForWindow(app, TimeSpan.FromSeconds(35));
            Check(window is not null, "隔离工程主窗口启动", window is null ? $"PID={app.Id} 未找到主窗口。" : $"PID={app.Id}，HWND=0x{window.Current.NativeWindowHandle:X}");
            if (window is null) throw new InvalidOperationException("主窗口未出现。");
            Check(activateAtStartup(window), "启动时前台守卫完成一次激活", "仅在启动阶段使用一次测试应用窗口激活。");
            if (!TargetForeground(window, "PNG 导入开始")) throw new InvalidOperationException("测试主窗口没有通过前台守卫。");
            var title = WaitForLoadedTitle(window, projectPath, TimeSpan.FromSeconds(20));
            Check(title, "隔离工程标题与项目匹配", $"已加载={title}；期望标题={ExpectedProjectTitle(projectPath)}。");
            var addElements = findButtonByHint(window, "添加元素");
            var openedAddMenu = addElements is not null && invoke(addElements);
            Check(openedAddMenu, "打开添加元素菜单", openedAddMenu ? "通过主窗口中的真实工具栏按钮打开菜单。" : "未找到或无法调用“添加元素”按钮。");
            var addSticker = WaitForOwnedElement(app.Id, "添加贴图…", ControlType.MenuItem, TimeSpan.FromSeconds(5));
            var openedDialog = addSticker is not null && invoke(addSticker);
            Check(openedDialog, "调用真实贴图导入命令", openedDialog ? "菜单项为当前 DogSC 进程拥有。" : "未找到“添加贴图…”菜单项。");
            if (!openedDialog) throw new InvalidOperationException("贴图菜单命令未执行。");

            var dialog = await WaitForNativeDialogAsync(app.Id, (IntPtr)window.Current.NativeWindowHandle, TimeSpan.FromSeconds(30));
            Check(dialog is not null, "原生图片选择对话框出现", dialog is null ? "没有发现属于测试进程且位于前台的模态窗口。" : $"标题={SafeName(dialog)}，HWND=0x{dialog.Current.NativeWindowHandle:X}");
            if (dialog is null) throw new TimeoutException("未发现 PNG 选择对话框。");
            var dialogHandle = (IntPtr)dialog.Current.NativeWindowHandle;
            if (GetForegroundWindow() != dialogHandle || !TargetForeground(dialog, "选择测试 PNG"))
                throw new InvalidOperationException("原生文件对话框不是当前前台窗口，未发送文件路径。");
            var selectionPassed = AuxiliaryRelinkSmoke.SelectPath(dialog, dialogHandle, pngPath,
                (key, control) => { _ = SendGuardedKey(key, control); }, out var selectionDetail);
            var selection = (Passed: selectionPassed, Detail: selectionDetail);
            Check(selection.Passed, "原生对话框选择并确认 PNG", selection.Detail);
            if (!selection.Passed) throw new InvalidOperationException("PNG 文件选择失败。");

            var imported = await WaitForImportedOverlayAsync(projectPath, baseline, TimeSpan.FromSeconds(12));
            Check(imported is not null, "真实导入产生贴图引用", imported is null ? "等待自动保存后仍没有新的 Overlay 记录。" : $"id={imported.Id}，mediaPath={imported.MediaPath}");
            if (imported is null) throw new InvalidDataException("导入后没有新增贴图记录。");
            newOverlay = imported;
            copiedAssetPath = ResolveProjectAsset(projectRoot, newOverlay.MediaPath);
            var pathInsideProject = IsUnderRoot(copiedAssetPath, projectRoot);
            var copiedExists = File.Exists(copiedAssetPath);
            assetHash = copiedExists ? Hash(copiedAssetPath) : "";
            var decodedCopy = copiedExists ? DecodePng(copiedAssetPath) : default;
            Check(pathInsideProject && copiedExists && assetHash == sourceHash && decodedCopy.Width == 64 && decodedCopy.Height == 48,
                "贴图副本在工程内且哈希和解码一致",
                $"pathInsideProject={pathInsideProject}，exists={copiedExists}，sourceSHA256={sourceHash}，copySHA256={assetHash}，尺寸={decodedCopy.Width}x{decodedCopy.Height}。");
            if (!pathInsideProject || !copiedExists || assetHash != sourceHash) throw new InvalidDataException("导入贴图副本路径或字节校验失败。");

            var importedOverlays = ReadOverlays(projectPath);
            var importedJson = ReadOverlayCollectionText(projectPath);
            var importedIds = importedOverlays;
            var undoSent = GuardedKey(window, 0x5A, true, "导入后 Ctrl+Z 撤销");
            var undoState = await WaitForOverlayJsonAsync(projectPath, baselineJson, TimeSpan.FromSeconds(8));
            var assetAfterUndo = File.Exists(copiedAssetPath) && Hash(copiedAssetPath) == sourceHash;
            Check(undoSent && undoState && assetAfterUndo, "同会话撤销只移除引用并保留图片副本",
                $"keySent={undoSent}，overlayIdsRestored={undoState}，assetExistsAndHashMatches={assetAfterUndo}，撤销后引用数={ReadOverlays(projectPath).Length}，副本={copiedAssetPath}");
            if (!undoSent || !undoState || !assetAfterUndo) throw new InvalidDataException("撤销没有同时满足引用移除和副本保留。");

            var redoSent = GuardedKey(window, 0x59, true, "撤销后 Ctrl+Y 重做");
            var redoState = await WaitForOverlayJsonAsync(projectPath, importedJson, TimeSpan.FromSeconds(8));
            var assetAfterRedo = File.Exists(copiedAssetPath) && Hash(copiedAssetPath) == sourceHash;
            Check(redoSent && redoState && assetAfterRedo, "同会话重做恢复相同贴图引用",
                $"keySent={redoSent}，sameOverlayIdAndPath={redoState}，assetHashMatches={assetAfterRedo}，引用={newOverlay.Id}/{newOverlay.MediaPath}");
            if (!redoSent || !redoState || !assetAfterRedo) throw new InvalidDataException("重做没有恢复原贴图记录。");

            var undoAgainSent = GuardedKey(window, 0x5A, true, "保存前再次 Ctrl+Z");
            var undoAgain = await WaitForOverlayJsonAsync(projectPath, baselineJson, TimeSpan.FromSeconds(8));
            var saveSent = undoAgainSent && GuardedKey(window, 0x53, true, "保存已撤销状态 Ctrl+S");
            var savedUndoState = saveSent && await WaitForOverlayJsonAsync(projectPath, baselineJson, TimeSpan.FromSeconds(8));
            var noHistorySerialized = savedUndoState && !ContainsHistoryProperty(projectPath);
            Check(undoAgainSent && undoAgain && savedUndoState && noHistorySerialized,
                "撤销后保存工程且文件不序列化历史栈",
                $"undo={undoAgain}，saveKey={saveSent}，savedOverlayIdsRestored={savedUndoState}，historyPropertyAbsent={noHistorySerialized}，副本仍在={File.Exists(copiedAssetPath)}。");
            if (!undoAgainSent || !savedUndoState || !noHistorySerialized) throw new InvalidDataException("撤销后的工程状态未可靠保存。");
            var expectedSavedOverlays = ReadOverlays(projectPath);
            var firstClosed = CloseOwnedProcess(app);
            app = null;
            Check(firstClosed, "保存撤销状态后首实例正常关闭", "仅调用测试进程 CloseMainWindow 并等待退出。");
            if (!firstClosed) throw new TimeoutException("首实例没有正常关闭。");

            reopened = Start(appPath, "--open", projectPath);
            var reopenedWindow = waitForWindow(reopened, TimeSpan.FromSeconds(35));
            Check(reopenedWindow is not null, "保存后重开工程", reopenedWindow is null ? $"PID={reopened.Id} 未找到主窗口。" : $"PID={reopened.Id}，HWND=0x{reopenedWindow.Current.NativeWindowHandle:X}");
            if (reopenedWindow is null) throw new InvalidOperationException("重开主窗口未出现。");
            Check(activateAtStartup(reopenedWindow), "重开实例启动激活通过守卫", "新自有 PID 启动阶段单次激活。");
            if (!TargetForeground(reopenedWindow, "重开后检查工程")) throw new InvalidOperationException("重开实例没有通过前台守卫。");
            var reopenedTitle = WaitForLoadedTitle(reopenedWindow, projectPath, TimeSpan.FromSeconds(20));
            var reopenedState = await WaitForOverlayJsonAsync(projectPath, baselineJson, TimeSpan.FromSeconds(8));
            var reopenedAsset = copiedAssetPath is not null && File.Exists(copiedAssetPath) && Hash(copiedAssetPath) == sourceHash;
            Check(reopenedTitle && reopenedState && reopenedAsset,
                "重开后撤销状态及未引用图片副本仍存在",
                $"titleLoaded={reopenedTitle}，overlayIdsStayUndone={reopenedState}，imageCopyPreserved={reopenedAsset}，asset={copiedAssetPath}");
            if (!reopenedTitle || !reopenedState || !reopenedAsset) throw new InvalidDataException("重开后工程引用或图片副本状态不符。");

            var redoAfterReopenSent = GuardedKey(reopenedWindow, 0x59, true, "重开后 Ctrl+Y 检查历史不持久化");
            var redoAfterReopenNoEffect = redoAfterReopenSent && await ObserveOverlayJsonUnchangedAsync(projectPath, baselineJson, TimeSpan.FromSeconds(2))
                && ReadOverlays(projectPath).SequenceEqual(expectedSavedOverlays)
                && !ReadOverlays(projectPath).Any(x => x.Id == newOverlay.Id);
            var assetAfterReopenRedo = File.Exists(copiedAssetPath) && Hash(copiedAssetPath) == sourceHash;
            Check(redoAfterReopenSent && redoAfterReopenNoEffect && assetAfterReopenRedo,
                "新会话 Ctrl+Y 不恢复旧会话撤销记录",
                $"keySent={redoAfterReopenSent}，overlayStateUnchanged={redoAfterReopenNoEffect}，副本SHA256未变={assetAfterReopenRedo}。");
            if (!redoAfterReopenSent || !redoAfterReopenNoEffect || !assetAfterReopenRedo)
                throw new InvalidDataException("新会话中的 Ctrl+Y 改变了已保存工程或图片副本。");

            var closed = CloseOwnedProcess(reopened);
            reopened = null;
            Check(closed, "重开实例正常关闭", "只关闭本测试启动的 DogSC 进程。");
            if (!closed) throw new TimeoutException("重开实例没有正常关闭。");
            var closedStateUnchanged = ReadOverlayCollectionText(projectPath) == baselineJson
                && File.Exists(copiedAssetPath) && Hash(copiedAssetPath) == sourceHash;
            Check(closedStateUnchanged, "关闭后撤销状态与素材副本仍一致", $"overlayJson原样={ReadOverlayCollectionText(projectPath) == baselineJson}，副本SHA256一致={File.Exists(copiedAssetPath) && Hash(copiedAssetPath) == sourceHash}。");
            if (!closedStateUnchanged) throw new InvalidDataException("关闭后工程或素材状态发生变化。");
            var sourceUnchanged = Hash(pngPath) == sourceHash;
            Check(sourceUnchanged, "验收后源 PNG 字节未改变", $"初始={sourceHash}，结束={Hash(pngPath)}。");
            if (!sourceUnchanged) throw new InvalidDataException("源 PNG 在验收期间被修改。");
            var detail = $"project={projectPath}; sourcePNG={pngPath}; sourceSHA256={sourceHash}; copiedAsset={copiedAssetPath}; copiedSHA256={assetHash}; overlayId={newOverlay.Id}; baselineOverlayCount={baseline.Length}; importedOverlayCount={importedIds.Length}; appVersion={productVersion}; appSHA256={Hash(appPath)}; packageBuildInfo={packageBuildInfo}。";
            await WriteReportAsync(reportPath, true, checks, detail, sourceHash, assetHash, appPath, projectPath, pngPath, copiedAssetPath, newOverlay, baseline);
            return new(true, detail);
        }
        catch (Exception error)
        {
            if (app is not null) CloseOwnedProcess(app);
            if (reopened is not null) CloseOwnedProcess(reopened);
            var detail = error.ToString();
            try { await WriteReportAsync(reportPath, false, checks, detail, sourceHash, assetHash, appPath, projectPath, pngPath, copiedAssetPath, newOverlay, baseline); }
            catch (Exception writeError) { detail += "；诊断报告写入失败：" + writeError; }
            return new(false, detail);
        }
        finally
        {
            if (app is not null) CloseOwnedProcess(app);
            if (reopened is not null) CloseOwnedProcess(reopened);
        }
    }

    private static Process Start(string appPath, params string[] args)
    {
        var start = new ProcessStartInfo(appPath) { UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new InvalidOperationException("无法启动 DogSC 测试实例。");
    }

    private static bool CloseOwnedProcess(Process process)
    {
        try
        {
            if (process.HasExited) return true;
            _ = process.CloseMainWindow();
            if (process.WaitForExit(10000)) return true;
            process.Kill(entireProcessTree: true);
            return process.WaitForExit(5000) && false;
        }
        catch { return false; }
        finally { process.Dispose(); }
    }

    private static bool TargetForeground(AutomationElement element, string operation)
    {
        try
        {
            var processId = (int)element.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty);
            return InteractiveDesktopGuard.IsOwnedProcessId(processId)
                && InteractiveDesktopGuard.BindInputTargetIfForeground(processId, operation)
                && InteractiveDesktopGuard.BeforeInput(operation);
        }
        catch { return false; }
    }

    private static bool HasShortcutFocus(AutomationElement window)
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is null) return false;
            var processId = (int)window.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty);
            if ((int)focused.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty) != processId) return false;
            var type = focused.Current.ControlType;
            return type != ControlType.Edit && type != ControlType.ComboBox && type != ControlType.Slider;
        }
        catch { return false; }
    }

    private static bool WaitForLoadedTitle(AutomationElement window, string projectPath, TimeSpan timeout)
    {
        var expected = ExpectedProjectTitle(projectPath);
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until && !InteractiveDesktopGuard.IsAborted)
        {
            var title = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "ProjectTitle"));
            if (title is not null && string.Equals(SafeName(title), expected, StringComparison.Ordinal)) return true;
            Thread.Sleep(150);
        }
        return false;
    }

    private static string ExpectedProjectTitle(string projectPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(projectPath));
            if (doc.RootElement.TryGetProperty("displayName", out var title)
                && title.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(title.GetString())) return title.GetString()!;
        }
        catch { }
        return Path.GetFileNameWithoutExtension(projectPath);
    }

    private static async Task<AutomationElement?> WaitForNativeDialogAsync(int processId, IntPtr mainHandle, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until && !InteractiveDesktopGuard.IsAborted)
        {
            var foreground = GetForegroundWindow();
            if (foreground != IntPtr.Zero && foreground != mainHandle && IsWindowVisible(foreground))
            {
                GetWindowThreadProcessId(foreground, out var owner);
                if (owner == (uint)processId)
                {
                    try
                    {
                        var item = AutomationElement.FromHandle(foreground);
                        if ((int)item.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty) == processId
                            && item.Current.ControlType == ControlType.Window)
                            return item;
                    }
                    catch (ElementNotAvailableException) { }
                }
            }
            await Task.Delay(100);
        }
        return null;
    }

    private static AutomationElement? WaitForOwnedElement(int processId, string name, ControlType type, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until && !InteractiveDesktopGuard.IsAborted)
        {
            foreach (var root in OwnedWindows(processId))
            {
                try
                {
                    if (root.Current.ControlType == type && SafeName(root) == name) return root;
                    foreach (AutomationElement element in root.FindAll(TreeScope.Descendants, new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, type),
                        new PropertyCondition(AutomationElement.NameProperty, name)))
                        .Cast<AutomationElement>())
                        if ((int)element.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty) == processId) return element;
                }
                catch (ElementNotAvailableException) { }
            }
            Thread.Sleep(100);
        }
        return null;
    }

    private static AutomationElement[] OwnedWindows(int processId)
    {
        if (!InteractiveDesktopGuard.IsOwnedProcessId(processId)) return [];
        var result = new List<AutomationElement>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle)) return true;
            GetWindowThreadProcessId(handle, out var owner);
            if (owner != (uint)processId) return true;
            try
            {
                var element = AutomationElement.FromHandle(handle);
                if ((int)element.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty) == processId
                    && element.Current.ControlType == ControlType.Window) result.Add(element);
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return result.ToArray();
    }

    private static async Task<Overlay?> WaitForImportedOverlayAsync(string projectPath, Overlay[] baseline, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until && !InteractiveDesktopGuard.IsAborted)
        {
            try
            {
                var current = ReadOverlays(projectPath);
                if (current.Length == baseline.Length + 1
                    && current.Take(baseline.Length).SequenceEqual(baseline)
                    && !string.IsNullOrWhiteSpace(current[^1].MediaPath)) return current[^1];
            }
            catch { }
            await Task.Delay(100);
        }
        return null;
    }

    private static Overlay[] ReadOverlays(string projectPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(projectPath));
        if (!doc.RootElement.TryGetProperty("overlays", out var list) || list.ValueKind == JsonValueKind.Null) return [];
        return list.EnumerateArray().Select(x => new Overlay(
            x.GetProperty("id").GetString() ?? throw new InvalidDataException("贴图缺少 id。"),
            x.GetProperty("mediaPath").GetString() ?? throw new InvalidDataException("贴图缺少 mediaPath。"))).ToArray();
    }

    private static bool ContainsHistoryProperty(string projectPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(projectPath));
        return doc.RootElement.EnumerateObject().Any(x => x.Name.Contains("history", StringComparison.OrdinalIgnoreCase)
            || x.Name.Contains("undo", StringComparison.OrdinalIgnoreCase) || x.Name.Contains("redo", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<bool> WaitForOverlayJsonAsync(string projectPath, string expectedJson, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until && !InteractiveDesktopGuard.IsAborted)
        {
            try { if (string.Equals(ReadOverlayCollectionText(projectPath), expectedJson, StringComparison.Ordinal)) return true; }
            catch { }
            await Task.Delay(100);
        }
        return false;
    }

    private static async Task<bool> ObserveOverlayJsonUnchangedAsync(string projectPath, string expectedJson, TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration;
        do
        {
            if (InteractiveDesktopGuard.IsAborted) return false;
            try { if (!string.Equals(ReadOverlayCollectionText(projectPath), expectedJson, StringComparison.Ordinal)) return false; }
            catch { return false; }
            await Task.Delay(100);
        } while (DateTime.UtcNow < until);
        return true;
    }

    private static string ReadOverlayCollectionText(string projectPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(projectPath));
        return doc.RootElement.TryGetProperty("overlays", out var overlays) && overlays.ValueKind == JsonValueKind.Array
            ? overlays.GetRawText() : "[]";
    }

    private static string ResolveProjectAsset(string projectRoot, string relative)
    {
        if (Path.IsPathRooted(relative)) return Path.GetFullPath(relative);
        var candidate = Path.GetFullPath(Path.Combine(projectRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        return candidate;
    }

    private static bool IsUnderRoot(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static (int Width, int Height) DecodePng(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1 || decoder.Frames[0].PixelWidth <= 0 || decoder.Frames[0].PixelHeight <= 0)
            throw new InvalidDataException("PNG 解码结果无效：" + path);
        return (decoder.Frames[0].PixelWidth, decoder.Frames[0].PixelHeight);
    }

    private static void CreateOwnedPng(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const int width = 64, height = 48;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            var cell = ((x / 8) + (y / 8)) % 2 == 0;
            pixels[i] = cell ? (byte)0xE0 : (byte)0x18;
            pixels[i + 1] = cell ? (byte)0x38 : (byte)0xD8;
            pixels[i + 2] = cell ? (byte)0xA4 : (byte)0x42;
            pixels[i + 3] = 255;
        }
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoder.Save(output); output.Flush(true);
    }

    private static async Task WriteReportAsync(string path, bool passed, IReadOnlyList<object> checks, string detail,
        string sourceHash, string assetHash, string appPath, string projectPath, string pngPath, string? copiedAsset,
        Overlay? overlay, Overlay[] baseline)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var info = FileVersionInfo.GetVersionInfo(appPath);
        var buildInfoPath = Path.Combine(Path.GetDirectoryName(appPath)!, "BUILD-INFO.txt");
        var report = new
        {
            Passed = passed,
            Mode = "real-ui-png-overlay-undo-redo-reopen",
            Application = Path.GetFullPath(appPath),
            ProductVersion = info.ProductVersion,
            ApplicationSha256 = File.Exists(appPath) ? Hash(appPath) : "",
            FrameTrailDllSha256 = File.Exists(Path.Combine(Path.GetDirectoryName(appPath)!, "DogSC.dll")) ? Hash(Path.Combine(Path.GetDirectoryName(appPath)!, "DogSC.dll")) : "",
            BuildInfo = File.Exists(buildInfoPath) ? File.ReadAllText(buildInfoPath) : "missing",
            Project = Path.GetFullPath(projectPath),
            SourcePng = Path.GetFullPath(pngPath),
            SourcePngSha256 = sourceHash,
            CopiedAsset = copiedAsset,
            CopiedAssetSha256 = assetHash,
            ImportedOverlay = overlay,
            BaselineOverlays = baseline,
            Checks = checks,
            Detail = detail,
            GeneratedUtc = DateTimeOffset.UtcNow
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string ReadBuildInfo(string appPath)
    {
        var appDirectory = Path.GetDirectoryName(appPath)!;
        var candidates = new[] { Path.Combine(appDirectory, "BUILD-INFO.txt"), Path.Combine(Directory.GetParent(appDirectory)?.FullName ?? appDirectory, "BUILD-INFO.txt") };
        var path = candidates.FirstOrDefault(File.Exists);
        return path is not null ? File.ReadAllText(path).Trim() : "missing";
    }

    private static bool SendGuardedKey(byte key, bool control)
    {
        const byte controlKey = 0xA2;
        const byte keyUp = 2;
        var controlDown = false;
        var keyDown = false;
        var sent = true;
        try
        {
            if (control)
            {
                if (!InteractiveDesktopGuard.SendKeyEvent(controlKey, 0x1D, 0, UIntPtr.Zero)) return false;
                controlDown = true;
                Thread.Sleep(35);
            }
            var scan = (byte)MapVirtualKey(key, 0);
            if (!InteractiveDesktopGuard.SendKeyEvent(key, scan, 0, UIntPtr.Zero)) return false;
            keyDown = true;
            Thread.Sleep(35);
            if (!InteractiveDesktopGuard.SendKeyEvent(key, scan, keyUp, UIntPtr.Zero)) { sent = false; return false; }
            keyDown = false;
        }
        finally
        {
            if (keyDown) sent &= InteractiveDesktopGuard.SendKeyEvent(key, (byte)MapVirtualKey(key, 0), keyUp, UIntPtr.Zero);
            if (controlDown) sent &= InteractiveDesktopGuard.SendKeyEvent(controlKey, 0x1D, keyUp, UIntPtr.Zero);
        }
        return sent;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string SafeName(AutomationElement? element) { try { return element?.Current.Name ?? ""; } catch { return ""; } }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW")] private static extern uint MapVirtualKey(byte code, uint mapType);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
