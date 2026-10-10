using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Automation;

internal static class AuxiliaryRelinkSmoke
{
    private sealed record Asset(string Key, string MissingRelative, string CandidatePath, string Type, string ExpectedHash);

    public static async Task<IReadOnlyList<StepResult>> RunAsync(string appPath, string sourceProject, string isolatedProject, string reportPath,
        bool waitForForeground, bool externalInput, Action<byte, bool> sendKey, Action<string> progress)
    {
        var checks = new List<StepResult>();
        var fixtureRoot = Path.Combine(Path.GetDirectoryName(reportPath)!, "auxiliary-relink-inputs");
        Directory.CreateDirectory(fixtureRoot);
        var ffmpeg = FindFfmpeg(appPath);
        var background = await CreateColorImageAsync("background.png", "0x18E7B5");
        var overlay = await CreateColorImageAsync("overlay.png", "0xF12A91");
        var systemAudio = await CreateToneAsync("system.wav", 440);
        var microphoneAudio = await CreateToneAsync("microphone.wav", 880);
        var assets = new[]
        {
            MakeAsset("background", "media/relink-background.png", background, "image"),
            MakeAsset("overlay", "media/relink-overlay.png", overlay, "image"),
            MakeAsset("systemAudio", "media/relink-system.wav", systemAudio, "audio"),
            MakeAsset("microphoneAudio", "media/relink-microphone.wav", microphoneAudio, "audio")
        };
        foreach (var asset in assets)
        {
            var validCandidate = asset.Type == "audio"
                ? await RunFfmpegAsync(ffmpeg, ["-v", "error", "-i", asset.CandidatePath, "-map", "0:a:0", "-f", "null", "NUL"])
                : await RunFfmpegAsync(ffmpeg, ["-v", "error", "-i", asset.CandidatePath, "-frames:v", "1", "-f", "null", "NUL"]);
            if (!validCandidate) throw new InvalidDataException($"候选素材类型/解码不符合预期：{asset.Key}，type={asset.Type}，path={asset.CandidatePath}，SHA256={asset.ExpectedHash}");
        }
        PrepareProject(assets);

        Process? relinked = null;
        try
        {
            relinked = Start(appPath, "--open", isolatedProject);
            InteractiveDesktopGuard.BindTargetProcess(relinked.Id);
            var foregroundReady = false;
            foreach (var asset in new[] { assets[2], assets[3], assets[1], assets[0] })
            {
                var titlePart = "素材缺失：重新定位 " + Path.GetFileName(asset.MissingRelative);
                if (externalInput)
                {
                    progress($"请在桌面中定位当前缺失素材对话框并选择：title包含“{titlePart}”；唯一候选={asset.CandidatePath}；预期SHA256={asset.ExpectedHash}。本模式不读取UIA对话框，只等待工程 media/ 中出现此哈希的 relocated 副本。");
                    var copiedPath = await WaitForRelocatedCopyAsync(Path.GetDirectoryName(isolatedProject)!, asset, ffmpeg, TimeSpan.FromSeconds(180));
                    if (copiedPath is null)
                        throw new TimeoutException($"外部选择后未发现可解码的副本：key={asset.Key}，候选={asset.CandidatePath}，预期SHA256={asset.ExpectedHash}，当前媒体文件={DescribeRelocatedFiles(Path.GetDirectoryName(isolatedProject)!)}");
                    checks.Add(new("外部选择素材并核对副本 " + asset.Key, true,
                        $"candidate={asset.CandidatePath}，expectedType={asset.Type}，expectedSHA256={asset.ExpectedHash}，copied={copiedPath}，actualSHA256={Hash(copiedPath)}，FFmpeg解码通过。"));
                    progress($"素材副本通过：key={asset.Key}；candidate={asset.CandidatePath}；copied={copiedPath}；SHA256={asset.ExpectedHash}；FFmpeg解码通过。");
                    continue;
                }
                progress($"等待前台文件对话框：title包含“{titlePart}”；PID={relinked.Id}；候选={asset.CandidatePath}；SHA256={asset.ExpectedHash}。");
                var dialog = await WaitForForegroundDialogAsync(relinked.Id, titlePart,
                    TimeSpan.FromSeconds(waitForForeground ? 180 : 60));
                if (dialog is null) throw new TimeoutException("未在目标进程的前台 HWND 找到文件对话框：" + titlePart + "；当前前台=" + DescribeForegroundWindow());
                var dialogHandle = (IntPtr)dialog.Current.NativeWindowHandle;
                if (!foregroundReady)
                {
                    foregroundReady = dialogHandle != IntPtr.Zero && (waitForForeground
                        ? InteractiveDesktopGuard.WaitForTargetForegroundAtStartup(dialogHandle, TimeSpan.FromSeconds(180), requireExactForeground: true)
                        : InteractiveDesktopGuard.ActivateTargetOnceAtStartup(dialogHandle, requireExactForeground: true));
                    foregroundReady = foregroundReady && GetForegroundWindow() == dialogHandle;
                    if (!foregroundReady) throw new InvalidOperationException(waitForForeground
                        ? "未等待到文件模态 HWND 自然成为前台；没有发送文件选择输入。"
                        : "首次文件模态 HWND 未通过守卫的一次性前台激活；没有发送文件选择输入。");
                }
                var selected = SelectPath(dialog, dialogHandle, asset.CandidatePath, sendKey, out var dialogDetails);
                if (!selected) throw new InvalidOperationException("无法在缺失辅助素材对话框中选择候选文件：" + titlePart + "; " + dialogDetails);
                var copiedPathForAsset = await WaitForRelocatedCopyAsync(Path.GetDirectoryName(isolatedProject)!, asset, ffmpeg, TimeSpan.FromSeconds(60));
                if (copiedPathForAsset is null)
                    throw new TimeoutException($"选择操作后没有生成与候选一致且可解码的工程副本：key={asset.Key}，candidate={asset.CandidatePath}，SHA256={asset.ExpectedHash}，当前媒体文件={DescribeRelocatedFiles(Path.GetDirectoryName(isolatedProject)!)}；选择诊断={dialogDetails}；当前前台={DescribeForegroundWindow()}");
                checks.Add(new("选择缺失辅助素材 " + asset.Key, true,
                    $"dialogTitle={Safe(() => dialog.Current.Name)}，candidate={asset.CandidatePath}，expectedType={asset.Type}，SHA256={asset.ExpectedHash}，copied={copiedPathForAsset}，selection={dialogDetails}。"));
                progress($"素材副本通过：key={asset.Key}；candidate={asset.CandidatePath}；copied={copiedPathForAsset}；SHA256={asset.ExpectedHash}；FFmpeg解码通过。");
            }
            var main = await WaitForWindowAsync(relinked.Id, TimeSpan.FromSeconds(30));
            if (main is null) throw new TimeoutException("重链接后主窗口未加载。");
            var initialVisual = await WaitForFrameTrailVisualReadyAsync(main, TimeSpan.FromSeconds(90));
            checks.Add(new("重链接后预览与时间线完成加载", initialVisual.Passed, initialVisual.Detail));
            if (!initialVisual.Passed) throw new InvalidOperationException("重链接后预览或时间线未就绪。");
            var copyChecks = await VerifyCopiedProjectAsync(isolatedProject, assets, ffmpeg);
            checks.Add(new("辅助素材重链接复制、哈希和解码", copyChecks.Passed, copyChecks.Detail));
            if (!copyChecks.Passed) throw new InvalidDataException(copyChecks.Detail);

            Close(relinked); relinked = null;
            using var reopened = Start(appPath, "--open", isolatedProject);
            InteractiveDesktopGuard.BindTargetProcess(reopened.Id);
            var reopenedMain = await WaitForWindowAsync(reopened.Id, TimeSpan.FromSeconds(30));
            if (reopenedMain is null) throw new TimeoutException("重开重链接工程后主窗口未加载。");
            var reopenedVisual = await WaitForFrameTrailVisualReadyAsync(reopenedMain, TimeSpan.FromSeconds(90));
            checks.Add(new("重开后预览与时间线完成加载", reopenedVisual.Passed, reopenedVisual.Detail));
            if (!reopenedVisual.Passed) throw new InvalidOperationException("重开后预览或时间线未就绪。");
            var referencesResolve = ProjectReferencesResolve(isolatedProject, assets);
            var reopenPassed = reopenedVisual.Passed && referencesResolve;
            checks.Add(new("辅助素材相对路径保存并可无提示重开", reopenPassed,
                $"主窗口={reopenedMain is not null}，预览/时间线ready={reopenedVisual.Passed}，四个引用均指向工程内已存在副本={referencesResolve}；本模式不依赖UIA对话框关闭状态。"));
            if (!reopenPassed) throw new InvalidDataException("重开时仍有辅助素材缺失或工程路径未保存。");
            Close(reopened);

            var exportReport = Path.Combine(Path.GetDirectoryName(reportPath)!, "auxiliary-relink-export-panel.json");
            using var exporter = Start(appPath, "--open", isolatedProject, "--export-panel-smoke", exportReport);
            if (!exporter.WaitForExit(180_000)) { exporter.Kill(entireProcessTree: true); throw new TimeoutException("辅助素材工程的真实导出回归超时。"); }
            if (exporter.ExitCode != 0 || !File.Exists(exportReport)) throw new InvalidOperationException($"真实导出面板回归失败，exit={exporter.ExitCode}，report={exportReport}");
            using var exportJson = JsonDocument.Parse(await File.ReadAllTextAsync(exportReport));
            var exportRoot = exportJson.RootElement;
            var exportPassed = exportRoot.GetProperty("Passed").GetBoolean();
            var exportDirectory = exportRoot.GetProperty("Output").GetString()!;
            var videoPath = Path.Combine(exportDirectory, "panel-video.mp4");
            var audioPath = Path.Combine(exportDirectory, "panel-audio.m4a");
            var videoDecoded = exportPassed && File.Exists(videoPath) && await RunFfmpegAsync(ffmpeg, ["-v", "error", "-i", videoPath, "-f", "null", "NUL"]);
            var audioDecoded = exportPassed && File.Exists(audioPath) && await RunFfmpegAsync(ffmpeg, ["-v", "error", "-i", audioPath, "-map", "0:a:0", "-f", "null", "NUL"]);
            var exportedTone = audioDecoded ? await ReadToneAmplitudesAsync(ffmpeg, audioPath) : (440d, 880d);
            var tonePassed = exportedTone.Item1 > .005 && exportedTone.Item2 > .005;
            var backgroundPixel = videoDecoded ? await ReadExportPixelAsync(ffmpeg, videoPath, 1.0, .02, .02) : null;
            var overlayPixel = videoDecoded ? await ReadExportPixelAsync(ffmpeg, videoPath, 1.0, .5, .5) : null;
            var backgroundPixelPassed = IsNearColor(backgroundPixel, 0x18, 0xE7, 0xB5, 36);
            var overlayPixelPassed = IsNearColor(overlayPixel, 0xF1, 0x2A, 0x91, 36);
            var exportChecksPassed = exportPassed && videoDecoded && audioDecoded && tonePassed && backgroundPixelPassed && overlayPixelPassed;
            checks.Add(new("重链接工程通过实际视频与音频导出", exportChecksPassed,
                $"ExportPanelPassed={exportPassed}，视频可完整解码={videoDecoded}，M4A可解码={audioDecoded}，导出音频440Hz幅值={exportedTone.Item1:0.0000}、880Hz幅值={exportedTone.Item2:0.0000}，背景角像素={FormatPixel(backgroundPixel)}命中={backgroundPixelPassed}，贴图中心像素={FormatPixel(overlayPixel)}命中={overlayPixelPassed}（RGB每通道容差36），report={exportReport}。"));
            if (!exportChecksPassed) throw new InvalidDataException("重链接后的图像和两条独立音轨未通过实际导出检查。");
        }
        catch (Exception ex)
        {
            checks.Add(new("辅助素材重链接专项运行完成", false, ex.ToString()));
        }
        finally
        {
            if (relinked is { } process) Close(process);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
        {
            Passed = checks.Count > 0 && checks.All(x => x.Passed),
            Mode = "auxiliary-relink-end-to-end",
            InputProject = sourceProject,
            IsolatedProject = isolatedProject,
            Sources = assets.Select(x => new { x.Key, x.CandidatePath, x.ExpectedHash }),
            MissingRelativePaths = assets.Select(x => x.MissingRelative),
            Checks = checks
        }, new JsonSerializerOptions { WriteIndented = true }));
        return checks;

        async Task<string> CreateColorImageAsync(string targetName, string color)
        {
            var target = Path.Combine(fixtureRoot, targetName);
            var ok = await RunFfmpegAsync(ffmpeg, ["-v", "error", "-f", "lavfi", "-i", $"color=c={color}:s=128x128:d=1", "-frames:v", "1", "-y", target]);
            if (!ok || !File.Exists(target)) throw new InvalidOperationException("无法生成图像重链接测试fixture：" + targetName);
            return target;
        }

        Asset MakeAsset(string key, string relative, string candidate, string type) =>
            new(key, relative, candidate, type, Hash(candidate));

        async Task<string> CreateToneAsync(string name, int frequency)
        {
            var path = Path.Combine(fixtureRoot, name);
            var ok = await RunFfmpegAsync(ffmpeg, ["-v", "error", "-f", "lavfi", "-i", $"sine=frequency={frequency}:sample_rate=48000:duration=8", "-c:a", "pcm_s16le", "-y", path]);
            if (!ok || !File.Exists(path)) throw new InvalidOperationException("无法生成独立音轨测试fixture：" + name);
            return path;
        }

        void PrepareProject(IReadOnlyList<Asset> inputAssets)
        {
            var document = JsonNode.Parse(File.ReadAllBytes(isolatedProject))?.AsObject()
                ?? throw new InvalidDataException("辅助素材重链接测试工程JSON无效。");
            var overlayPath = inputAssets.Single(x => x.Key == "overlay").MissingRelative;
            var overlays = document["overlays"]?.AsArray() ?? throw new InvalidDataException("测试工程缺少贴图片段。");
            if (overlays.Count == 0) throw new InvalidDataException("测试工程没有可用于重链接的贴图片段。");
            foreach (var overlayNode in overlays) overlayNode!["mediaPath"] = overlayPath;
            document["overlays"] = new JsonArray(new JsonObject
            {
                ["mediaPath"] = overlayPath, ["start"] = 0.5, ["duration"] = 3.0,
                ["rect"] = new JsonObject { ["x"] = .35, ["y"] = .35, ["width"] = .3, ["height"] = .3 },
                ["opacity"] = 1.0, ["enterMotion"] = "none", ["exitMotion"] = "none"
            });
            document["audioTracks"] = new JsonArray(
                AudioJson(inputAssets.Single(x => x.Key == "systemAudio").MissingRelative, "system"),
                AudioJson(inputAssets.Single(x => x.Key == "microphoneAudio").MissingRelative, "microphone"));
            var settings = document["settings"]?.AsObject() ?? throw new InvalidDataException("测试工程缺少settings。");
            settings["mainAudioMuted"] = true;
            var background = settings["backgroundStyle"]?.AsObject() ?? throw new InvalidDataException("测试工程缺少backgroundStyle。");
            background["kind"] = "image";
            background["imagePath"] = inputAssets.Single(x => x.Key == "background").MissingRelative;
            background["videoPath"] = null;
            Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(isolatedProject)!, "media"));
            foreach (var asset in inputAssets)
            {
                var destination = ResolveProjectPath(isolatedProject, asset.MissingRelative);
                if (File.Exists(destination)) File.Delete(destination);
            }
            File.WriteAllText(isolatedProject, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        static JsonObject AudioJson(string path, string source) => new()
        {
            ["mediaPath"] = path, ["sourceStart"] = 0, ["duration"] = 8, ["offsetSeconds"] = 0,
            ["volume"] = 1, ["muted"] = false, ["sourceRate"] = 1, ["syncSegments"] = null, ["source"] = source
        };
    }

    private static async Task<(bool Passed, string Detail)> VerifyCopiedProjectAsync(string projectPath, IReadOnlyList<Asset> assets, string ffmpeg)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(projectPath));
        var failed = new List<string>();
        foreach (var asset in assets)
        {
            var relative = asset.Key switch
            {
                "background" => document.RootElement.GetProperty("settings").GetProperty("backgroundStyle").GetProperty("imagePath").GetString(),
                "overlay" => document.RootElement.GetProperty("overlays")[0].GetProperty("mediaPath").GetString(),
                "systemAudio" => document.RootElement.GetProperty("audioTracks").EnumerateArray().Single(x => x.GetProperty("source").GetString() == "system").GetProperty("mediaPath").GetString(),
                "microphoneAudio" => document.RootElement.GetProperty("audioTracks").EnumerateArray().Single(x => x.GetProperty("source").GetString() == "microphone").GetProperty("mediaPath").GetString(),
                _ => null
            };
            if (relative is null || !relative.StartsWith("media/relocated-", StringComparison.OrdinalIgnoreCase)) { failed.Add(asset.Key + ":project path not relocated"); continue; }
            var copied = ResolveProjectPath(projectPath, relative);
            if (!File.Exists(copied)) { failed.Add(asset.Key + ":copy missing"); continue; }
            if (!string.Equals(Hash(copied), asset.ExpectedHash, StringComparison.OrdinalIgnoreCase)) { failed.Add(asset.Key + ":SHA256 mismatch"); continue; }
            var decodeArgs = asset.Type == "audio"
                ? new[] { "-v", "error", "-i", copied, "-map", "0:a:0", "-f", "null", "NUL" }
                : new[] { "-v", "error", "-i", copied, "-frames:v", "1", "-f", "null", "NUL" };
            if (!await RunFfmpegAsync(ffmpeg, decodeArgs)) failed.Add(asset.Key + ":decode failed");
        }
        var audioSources = document.RootElement.GetProperty("audioTracks").EnumerateArray().Select(x => x.GetProperty("source").GetString()).ToArray();
        var validSources = audioSources.Contains("system") && audioSources.Contains("microphone");
        if (!validSources) failed.Add("audio source identity changed");
        return (failed.Count == 0, $"四类引用={assets.Count}，副本哈希和FFmpeg解码均通过={failed.Count == 0}，System与Microphone来源保留={validSources}，问题=[{string.Join(";", failed)}]。");
    }

    private static bool ProjectReferencesResolve(string projectPath, IReadOnlyList<Asset> assets)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(projectPath));
        foreach (var asset in assets)
        {
            var path = asset.Key switch
            {
                "background" => document.RootElement.GetProperty("settings").GetProperty("backgroundStyle").GetProperty("imagePath").GetString(),
                "overlay" => document.RootElement.GetProperty("overlays")[0].GetProperty("mediaPath").GetString(),
                "systemAudio" => document.RootElement.GetProperty("audioTracks").EnumerateArray().Single(x => x.GetProperty("source").GetString() == "system").GetProperty("mediaPath").GetString(),
                "microphoneAudio" => document.RootElement.GetProperty("audioTracks").EnumerateArray().Single(x => x.GetProperty("source").GetString() == "microphone").GetProperty("mediaPath").GetString(),
                _ => null
            };
            if (path is null || !File.Exists(ResolveProjectPath(projectPath, path))) return false;
        }
        return true;
    }

    private static async Task<string?> WaitForRelocatedCopyAsync(string projectDirectory, Asset asset, string ffmpeg, TimeSpan timeout)
    {
        var mediaDirectory = Path.Combine(projectDirectory, "media");
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (Directory.Exists(mediaDirectory))
            {
                foreach (var candidate in Directory.EnumerateFiles(mediaDirectory, "relocated-*", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0) continue;
                        if (!Path.GetExtension(candidate).Equals(Path.GetExtension(asset.CandidatePath), StringComparison.OrdinalIgnoreCase)) continue;
                        if (!Hash(candidate).Equals(asset.ExpectedHash, StringComparison.OrdinalIgnoreCase)) continue;
                        var decodeArgs = asset.Type == "audio"
                            ? new[] { "-v", "error", "-i", candidate, "-map", "0:a:0", "-f", "null", "NUL" }
                            : new[] { "-v", "error", "-i", candidate, "-frames:v", "1", "-f", "null", "NUL" };
                        if (await RunFfmpegAsync(ffmpeg, decodeArgs)) return candidate;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            await Task.Delay(150);
        }
        return null;
    }

    private static string DescribeRelocatedFiles(string projectDirectory)
    {
        var directory = Path.Combine(projectDirectory, "media");
        if (!Directory.Exists(directory)) return "<media目录不存在>";
        try { return string.Join(" | ", Directory.EnumerateFiles(directory, "relocated-*", SearchOption.TopDirectoryOnly).Select(path => Path.GetFileName(path) + ":" + Hash(path))); }
        catch (Exception ex) { return ex.GetType().Name; }
    }

    private static bool SelectPath(AutomationElement dialog, IntPtr dialogHandle, string path, Action<byte, bool> sendKey, out string details)
    {
        try
        {
            var edits = dialog.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)).Cast<AutomationElement>().ToArray();
            var field = edits.FirstOrDefault(x => Safe(() => x.Current.AutomationId) == "1148") ?? edits.FirstOrDefault();
            if (field is null) { details = "filename edit missing"; return false; }
            if (!BeforeDialogInput(dialogHandle, "辅助素材重链接文件名焦点")) { details = "foreground dialog guard refused before SetFocus"; return false; }
            field.SetFocus();
            var focusedElement = Safe(() => AutomationElement.FocusedElement);
            var fieldId = Safe(() => field.Current.AutomationId);
            var focusedElementId = Safe(() => focusedElement?.Current.AutomationId);
            var focusMatches = Safe(() => field.Current.HasKeyboardFocus)
                && Safe(() => focusedElement?.Current.ProcessId) == Safe(() => dialog.Current.ProcessId)
                && (fieldId == focusedElementId || fieldId == "1148" && focusedElementId == "1148");
            if (!focusMatches) { details = $"filename focus mismatch; fieldId={fieldId}; focusedId={focusedElementId}; Open has no action yet"; return false; }
            if (!BeforeDialogInput(dialogHandle, "辅助素材重链接文件选择")) { details = "foreground dialog guard refused before SetValue"; return false; }
            var valuePattern = (ValuePattern)field.GetCurrentPattern(ValuePattern.Pattern);
            valuePattern.SetValue(path);
            Thread.Sleep(100);
            var valueMatches = Safe(() => valuePattern.Current.Value) == path;
            if (!valueMatches) { details = $"filename value mismatch before invoke; expected={path}; actual={Safe(() => valuePattern.Current.Value)}"; return false; }
            var buttons = dialog.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().ToArray();
            var open = buttons.FirstOrDefault(x => Safe(() => x.Current.AutomationId) == "1" && SupportsInvoke(x))
                ?? buttons.FirstOrDefault(x => (Safe(() => x.Current.Name).Contains("打开", StringComparison.OrdinalIgnoreCase)
                    || Safe(() => x.Current.Name).Equals("Open", StringComparison.OrdinalIgnoreCase)) && SupportsInvoke(x));
            if (open is not null)
            {
                if (!BeforeDialogInput(dialogHandle, "辅助素材重链接打开")) { details = "foreground dialog guard refused"; return false; }
                ((InvokePattern)open.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                details = $"filenameValueMatchedBeforeInvoke=true; value={path}; openInvoked=true; pattern=InvokePattern";
                return true;
            }
            if (Safe(() => valuePattern.Current.Value) != path) { details = "filename value changed before Enter"; return false; }
            if (!BeforeDialogInput(dialogHandle, "辅助素材重链接Enter")) { details = "foreground dialog guard refused before Enter"; return false; }
            sendKey(0x0D, false);
            details = $"filenameValueMatchedBeforeInvoke=true; value={path}; openInvoked=false; method=guarded-Enter; focusedFilename=true";
            return true;
        }
        catch (Exception ex) { details = ex.GetType().Name + ":" + ex.Message; return false; }
    }

    private static bool BeforeDialogInput(IntPtr expectedDialog, string operation) =>
        expectedDialog != IntPtr.Zero && GetForegroundWindow() == expectedDialog && InteractiveDesktopGuard.BeforeInput(operation);

    private static async Task<AutomationElement?> WaitForForegroundDialogAsync(int processId, string title, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until && !InteractiveDesktopGuard.IsAborted)
        {
            IntPtr matchedHandle = IntPtr.Zero;
            string matchedTitle = "";
            EnumWindows((handle, _) =>
            {
                GetWindowThreadProcessId(handle, out var ownerProcessId);
                if (ownerProcessId == (uint)processId && IsWindowVisible(handle))
                {
                    var caption = GetWindowCaption(handle);
                    if (caption.Contains(title, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedHandle = handle;
                        matchedTitle = caption;
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);
            if (matchedHandle != IntPtr.Zero)
            {
                try
                {
                    var modal = AutomationElement.FromHandle(matchedHandle);
                    if (Safe(() => modal.Current.ProcessId) == processId
                        && Safe(() => modal.Current.NativeWindowHandle) == matchedHandle.ToInt32()
                        && Safe(() => modal.Current.Name).Contains(title, StringComparison.OrdinalIgnoreCase))
                        return modal;
                }
                catch (ElementNotAvailableException) { }
            }
            await Task.Delay(80);
        }
        return null;
    }

    private static string GetWindowCaption(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0) return "";
        var buffer = new System.Text.StringBuilder(length + 1);
        _ = GetWindowText(handle, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string DescribeForegroundWindow()
    {
        var handle = GetForegroundWindow();
        if (handle == IntPtr.Zero) return "<none>";
        GetWindowThreadProcessId(handle, out var processId);
        try
        {
            var element = AutomationElement.FromHandle(handle);
            return $"HWND=0x{handle.ToInt64():X},PID={processId},type={Safe(() => element.Current.ControlType.ProgrammaticName)},name={Safe(() => element.Current.Name)}";
        }
        catch (Exception ex) { return $"HWND=0x{handle.ToInt64():X},PID={processId},UIA={ex.GetType().Name}"; }
    }

    private static async Task<AutomationElement?> WaitForWindowAsync(int processId, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            var window = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition).Cast<AutomationElement>()
                .FirstOrDefault(x => Safe(() => x.Current.ProcessId) == processId && Safe(() => x.Current.ControlType) == ControlType.Window && Safe(() => x.Current.Name).Contains("FrameTrail", StringComparison.OrdinalIgnoreCase));
            if (window is not null) return window;
            await Task.Delay(100);
        }
        return null;
    }

    private static bool SupportsInvoke(AutomationElement element)
    {
        try { return element.TryGetCurrentPattern(InvokePattern.Pattern, out _); }
        catch { return false; }
    }

    private static async Task<(bool Passed, string Detail)> WaitForFrameTrailVisualReadyAsync(AutomationElement mainWindow, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        var preview = FindByAutomationId(mainWindow, "PreviewImage");
        var timeline = FindByAutomationId(mainWindow, "TimelineView");
        string previewStatus = "missing", timelineStatus = "missing";
        while (watch.Elapsed < timeout && !InteractiveDesktopGuard.IsAborted)
        {
            previewStatus = Safe(() => preview?.Current.ItemStatus) ?? "missing";
            timelineStatus = Safe(() => timeline?.Current.ItemStatus) ?? "missing";
            if (previewStatus.Equals("error", StringComparison.OrdinalIgnoreCase)
                || timelineStatus.Equals("error", StringComparison.OrdinalIgnoreCase)) break;
            if (previewStatus.Equals("ready", StringComparison.OrdinalIgnoreCase)
                && timelineStatus.Equals("ready", StringComparison.OrdinalIgnoreCase))
            {
                var windowBounds = Safe(() => mainWindow.Current.BoundingRectangle);
                var previewBounds = Safe(() => preview!.Current.BoundingRectangle);
                var timelineBounds = Safe(() => timeline!.Current.BoundingRectangle);
                var boundsValid = IsUsableRect(windowBounds) && IsUsableRect(previewBounds) && IsUsableRect(timelineBounds)
                    && windowBounds.Contains(previewBounds) && !preview!.Current.IsOffscreen && !timeline!.Current.IsOffscreen;
                var detail = $"PreviewImage={previewStatus}，TimelineView={timelineStatus}，窗口可见范围有效={boundsValid}，预览={FormatRect(previewBounds)}，时间线={FormatRect(timelineBounds)}。";
                return (boundsValid, detail);
            }
            await Task.Delay(150);
        }
        return (false, $"等待预览/时间线就绪失败：PreviewImage={previewStatus}，TimelineView={timelineStatus}，守卫中止={InteractiveDesktopGuard.IsAborted}。");
    }

    private static AutomationElement? FindByAutomationId(AutomationElement root, string automationId) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));

    private static bool IsUsableRect(System.Windows.Rect rect) => !rect.IsEmpty && double.IsFinite(rect.Left) && double.IsFinite(rect.Top)
        && double.IsFinite(rect.Width) && double.IsFinite(rect.Height) && rect.Width >= 2 && rect.Height >= 2;

    private static string FormatRect(System.Windows.Rect rect) =>
        $"({rect.Left:0},{rect.Top:0},{rect.Width:0},{rect.Height:0})";

    private static bool IsNearColor((byte R, byte G, byte B)? actual, byte r, byte g, byte b, int tolerance) => actual is { } color
        && Math.Abs(color.R - r) <= tolerance && Math.Abs(color.G - g) <= tolerance && Math.Abs(color.B - b) <= tolerance;

    private static string FormatPixel((byte R, byte G, byte B)? pixel) => pixel is { } p ? $"#{p.R:X2}{p.G:X2}{p.B:X2}" : "unavailable";

    private static async Task<(byte R, byte G, byte B)?> ReadExportPixelAsync(string ffmpeg, string videoPath, double time, double x, double y)
    {
        var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
        if (!File.Exists(ffprobe)) return null;
        var probe = new ProcessStartInfo(ffprobe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=s=x:p=0", videoPath }
        };
        using var probeProcess = Process.Start(probe) ?? throw new InvalidOperationException("无法启动ffprobe读取导出画面尺寸。");
        var dimensionsTask = probeProcess.StandardOutput.ReadToEndAsync();
        var probeError = probeProcess.StandardError.ReadToEndAsync();
        await probeProcess.WaitForExitAsync();
        if (probeProcess.ExitCode != 0) throw new InvalidDataException("ffprobe读取导出尺寸失败：" + await probeError);
        var dimensions = (await dimensionsTask).Trim().Split('x');
        if (dimensions.Length != 2 || !int.TryParse(dimensions[0], out var width) || !int.TryParse(dimensions[1], out var height)
            || width <= 0 || height <= 0) return null;
        var px = Math.Clamp((int)(x * width), 0, width - 1);
        var py = Math.Clamp((int)(y * height), 0, height - 1);
        var decode = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "-v", "error", "-i", videoPath, "-ss", time.ToString(System.Globalization.CultureInfo.InvariantCulture), "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1" }
        };
        using var decodeProcess = Process.Start(decode) ?? throw new InvalidOperationException("无法启动FFmpeg读取导出像素。");
        using var frame = new MemoryStream();
        var copyTask = decodeProcess.StandardOutput.BaseStream.CopyToAsync(frame);
        var decodeError = decodeProcess.StandardError.ReadToEndAsync();
        await copyTask;
        await decodeProcess.WaitForExitAsync();
        if (decodeProcess.ExitCode != 0) throw new InvalidDataException("FFmpeg读取导出像素失败：" + await decodeError);
        var pixels = frame.ToArray();
        var offset = checked((py * width + px) * 3);
        if (pixels.Length < offset + 3) return null;
        return (pixels[offset], pixels[offset + 1], pixels[offset + 2]);
    }

    private static Process Start(string appPath, params string[] args)
    {
        var start = new ProcessStartInfo(appPath) { UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new InvalidOperationException("无法启动FrameTrail重链接测试进程。");
    }

    private static void Close(Process process)
    {
        try { if (!process.HasExited) { process.CloseMainWindow(); if (!process.WaitForExit(5000)) process.Kill(entireProcessTree: true); } }
        finally { process.Dispose(); }
    }

    private static string FindFfmpeg(string appPath)
    {
        var path = Path.Combine(Path.GetDirectoryName(appPath)!, "tools", "ffmpeg", "ffmpeg.exe");
        return File.Exists(path) ? path : throw new FileNotFoundException("应用包内缺少FFmpeg，辅助素材回归无法解码/导出。");
    }

    private static async Task<bool> RunFfmpegAsync(string ffmpeg, IReadOnlyList<string> args)
    {
        var start = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动FFmpeg。");
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000)) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); return false; }
        _ = await stderr;
        return process.ExitCode == 0;
    }

    private static async Task<(double, double)> ReadToneAmplitudesAsync(string ffmpeg, string path)
    {
        using var process = Process.Start(new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            ArgumentList = { "-v", "error", "-i", path, "-map", "0:a:0", "-ac", "1", "-ar", "48000", "-f", "f32le", "pipe:1" }
        }) ?? throw new InvalidOperationException("无法启动FFmpeg解码M4A波形。");
        using var pcm = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(pcm);
        var stderr = process.StandardError.ReadToEndAsync();
        await copy;
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidDataException("FFmpeg无法解码导出M4A：" + await stderr);
        var bytes = pcm.ToArray();
        var count = bytes.Length / sizeof(float);
        if (count < 48_000) return (0, 0);
        var start = 48_000;
        var length = Math.Min(96_000, count - start);
        return (GoertzelAmplitude(bytes, start, length, 48_000, 440), GoertzelAmplitude(bytes, start, length, 48_000, 880));
    }

    private static double GoertzelAmplitude(byte[] bytes, int start, int count, int sampleRate, double frequency)
    {
        var omega = 2 * Math.PI * frequency / sampleRate;
        var coefficient = 2 * Math.Cos(omega);
        double s1 = 0, s2 = 0;
        for (var i = 0; i < count; i++)
        {
            var sample = BitConverter.ToSingle(bytes, (start + i) * sizeof(float));
            var s0 = sample + coefficient * s1 - s2;
            s2 = s1; s1 = s0;
        }
        return 2 * Math.Sqrt(s1 * s1 + s2 * s2 - coefficient * s1 * s2) / count;
    }

    private static string ResolveProjectPath(string projectFile, string relative) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(projectFile)!, relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static T Safe<T>(Func<T> get) { try { return get(); } catch { return default!; } }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private delegate bool EnumWindowsCallback(IntPtr hWnd, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);
}
