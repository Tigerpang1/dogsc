using System.IO;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>Prevents UI smoke automation from sending input unless explicitly opted in
/// and the currently foreground window belongs to the target FrameTrail process.</summary>
internal static class InteractiveDesktopGuard
{
    private static readonly object Gate = new();
    private static bool _consented;
    private static bool _aborted;
    private static int _targetProcessId;
    private static readonly HashSet<int> OwnedProcessIds = [];
    private static string _progressPath = string.Empty;
    private static string _abortReason = string.Empty;
    private static readonly HashSet<int> StartupActivationAttemptedProcessIds = [];
    private static readonly Dictionary<byte, byte> PressedKeys = [];
    private static readonly HashSet<uint> PressedMouseButtons = [];
    private static Func<uint>? _foregroundPidOverride;
    private static Action<byte, byte, uint>? _keySinkOverride;
    private static Action<uint>? _mouseSinkOverride;

    public static bool HasExplicitConsent(string[] args) =>
        args.Any(arg => string.Equals(arg, "--interactive-desktop", StringComparison.Ordinal));

    public static void Initialize(bool consented, string progressPath)
    {
        lock (Gate)
        {
            _consented = consented;
            _progressPath = progressPath;
            _aborted = false;
            _targetProcessId = 0;
            OwnedProcessIds.Clear();
            _abortReason = string.Empty;
            StartupActivationAttemptedProcessIds.Clear();
            PressedKeys.Clear();
            PressedMouseButtons.Clear();
        }
    }

    public static void BindTargetProcess(int processId)
    {
        lock (Gate)
        {
            if (_consented && !_aborted && processId > 0)
            {
                OwnedProcessIds.Add(processId);
                _targetProcessId = processId;
            }
        }
    }

    public static bool IsOwnedProcessId(int processId)
    {
        lock (Gate) return processId > 0 && OwnedProcessIds.Contains(processId);
    }

    public static bool BeforeOwnedFocusChange(int processId)
    {
        lock (Gate)
        {
            if (!_consented || _aborted || processId <= 0 || !OwnedProcessIds.Contains(processId))
                return false;
            if (!ForegroundIsOwnedLocked())
            {
                AbortLocked($"前台窗口已切离本次启动的测试应用；已停止输入。目标进程={processId}。本轮结果标记为需复核。");
                return false;
            }
            _targetProcessId = processId;
            return true;
        }
    }

    public static bool BindInputTargetIfForeground(int processId, string operation)
    {
        lock (Gate)
        {
            if (!_consented || _aborted || processId <= 0 || !OwnedProcessIds.Contains(processId))
                return false;
            var foregroundMatchesTarget = _foregroundPidOverride is not null
                ? _foregroundPidOverride() == (uint)processId
                : IsOwnedByProcess(GetForegroundWindow(), processId);
            if (!foregroundMatchesTarget)
            {
                var foregroundIsOwned = _foregroundPidOverride is not null
                    ? OwnedProcessIds.Contains((int)_foregroundPidOverride())
                    : IsAnyOwnedWindowLocked(GetForegroundWindow());
                if (!foregroundIsOwned)
                    AbortLocked($"前台窗口已切离本次启动的测试应用；已停止输入。操作={operation}，目标进程={processId}。");
                return false;
            }
            _targetProcessId = processId;
            return true;
        }
    }

    public static bool WaitForTargetForeground(int processId, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until && !IsAborted)
        {
            lock (Gate)
            {
                if (!OwnedProcessIds.Contains(processId)) return false;
                var foregroundWindow = GetForegroundWindow();
                var foregroundPid = ForegroundPidLocked();
                if (IsOwnedByProcess(foregroundWindow, processId))
                {
                    _targetProcessId = processId;
                    return true;
                }
                if (!IsAnyOwnedWindowLocked(foregroundWindow))
                {
                    AbortLocked($"前台窗口切离本次启动的测试应用；已停止输入。等待目标进程={processId}，当前前台进程={foregroundPid}。");
                    return false;
                }
            }
            Thread.Sleep(40);
        }
        return false;
    }

    public static bool IsAborted
    {
        get { lock (Gate) return _aborted; }
    }

    public static string AbortReason
    {
        get { lock (Gate) return _abortReason; }
    }

    public static bool BeforeInput(string operation)
    {
        lock (Gate)
        {
            return BeforeInputLocked(operation);
        }
    }

    public static bool ActivateTargetOnceAtStartup(IntPtr targetWindow)
    {
        lock (Gate)
        {
            var processId = _targetProcessId;
            if (targetWindow == IntPtr.Zero || !TryBeginStartupActivationLocked(processId))
                return false;
            if (IsOwnedByProcess(GetForegroundWindow(), processId)) return true;
            _ = ShowWindowNative(targetWindow, SwRestore);
            var foregroundWindow = GetForegroundWindow();
            var foregroundThread = foregroundWindow == IntPtr.Zero ? 0u : GetWindowThreadProcessId(foregroundWindow, out _);
            var currentThread = GetCurrentThreadId();
            var attached = foregroundThread != 0 && foregroundThread != currentThread
                && AttachThreadInput(currentThread, foregroundThread, true);
            try { _ = SetForegroundWindowNative(targetWindow); }
            finally { if (attached) _ = AttachThreadInput(currentThread, foregroundThread, false); }
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(1);
            while (DateTime.UtcNow < until)
            {
                if (IsOwnedByProcess(GetForegroundWindow(), processId)) return true;
                Thread.Sleep(40);
            }
            GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundPid);
            AbortLocked($"启动时的一次性窗口激活未成功；已停止输入。前台进程={foregroundPid}，目标进程={processId}。本轮结果标记为需复核。");
            return false;
        }
    }

    private static bool TryBeginStartupActivationLocked(int processId)
    {
        if (!_consented || _aborted || processId <= 0 || processId != _targetProcessId
            || !OwnedProcessIds.Contains(processId))
            return false;
        return StartupActivationAttemptedProcessIds.Add(processId);
    }

    public static bool IsTargetForeground(int processId)
    {
        lock (Gate) return !_aborted && OwnedProcessIds.Contains(processId) && IsOwnedByProcess(GetForegroundWindow(), processId);
    }

    public static bool WaitForTargetForegroundAtStartup(IntPtr targetWindow, TimeSpan timeout)
    {
        int targetPid;
        lock (Gate)
        {
            if (!_consented || _aborted || _targetProcessId <= 0 || targetWindow == IntPtr.Zero)
                return false;
            targetPid = _targetProcessId;
        }

        var nextProgress = DateTime.UtcNow;
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (IsAborted) return false;
            var foreground = GetForegroundWindow();
            if (IsOwnedByProcess(foreground, targetPid))
            {
                AppendProgress($"测试应用已自然成为前台：PID={targetPid}，HWND=0x{targetWindow.ToInt64():X}。");
                return true;
            }

            if (DateTime.UtcNow >= nextProgress)
            {
                GetWindowThreadProcessId(foreground, out var foregroundPid);
                AppendProgress($"等待测试应用自然成为前台：PID={targetPid}，HWND=0x{targetWindow.ToInt64():X}，当前前台PID={foregroundPid}；请在等待期限内手动切换一次。");
                nextProgress = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            }
            Thread.Sleep(100);
        }

        lock (Gate)
        {
            AbortLocked($"等待测试应用自然成为前台超时（{timeout.TotalSeconds:0}秒）；未发送输入，结果未完成。目标PID={targetPid}，HWND=0x{targetWindow.ToInt64():X}。");
        }
        return false;
    }

    public static bool RunGuardedInput(string operation, Func<bool> action)
    {
        lock (Gate)
        {
            if (!BeforeInputLocked(operation)) return false;
            return action();
        }
    }

    public static bool SendKeyEvent(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo)
    {
        lock (Gate)
        {
            if (!BeforeInputLocked("keybd_event")) return false;
            if (_keySinkOverride is not null) _keySinkOverride(virtualKey, scanCode, flags);
            else KeybdEventNative(virtualKey, scanCode, flags, extraInfo);
            if ((flags & KeyUp) != 0) PressedKeys.Remove(virtualKey);
            else PressedKeys[virtualKey] = scanCode;
            return true;
        }
    }

    public static bool SendMouseEvent(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo)
    {
        lock (Gate)
        {
            if (!BeforeInputLocked("mouse_event")) return false;
            if (_mouseSinkOverride is not null) _mouseSinkOverride(flags);
            else MouseEventNative(flags, dx, dy, data, extraInfo);
            foreach (var pair in MouseDownToUp)
            {
                if ((flags & pair.Key) != 0) PressedMouseButtons.Add(pair.Key);
                if ((flags & pair.Value) != 0) PressedMouseButtons.Remove(pair.Key);
            }
            return true;
        }
    }

    public static bool ForegroundMatchesForProbe(int targetProcessId, int foregroundProcessId) =>
        targetProcessId > 0 && foregroundProcessId == targetProcessId;

    public static bool RunPureSelfTest()
    {
        var noConsent = !HasExplicitConsent([]);
        var explicitConsent = HasExplicitConsent(["--interactive-desktop"]);
        var sameProcess = ForegroundMatchesForProbe(1234, 1234);
        var differentProcess = !ForegroundMatchesForProbe(1234, 5678);
        var noTarget = !ForegroundMatchesForProbe(0, 0);
        Initialize(consented: true, string.Empty);
        BindTargetProcess(1234);
        var firstActivationAllowed = TryBeginStartupActivationForProbe(1234);
        var samePidActivationRejected = !TryBeginStartupActivationForProbe(1234);
        BindTargetProcess(5678);
        var secondOwnedPidActivationAllowed = TryBeginStartupActivationForProbe(5678);
        var unownedPidActivationRejected = !TryBeginStartupActivationForProbe(9999);
        var outputs = new List<string>();
        uint foreground = 1234;
        _foregroundPidOverride = () => foreground;
        _keySinkOverride = (key, _, flags) => outputs.Add($"key:{key}:{flags}");
        _mouseSinkOverride = flags => outputs.Add($"mouse:{flags}");
        Initialize(consented: false, string.Empty);
        BindTargetProcess(1234);
        var noConsentRejected = !SendKeyEvent(67, 46, 0, UIntPtr.Zero) && outputs.Count == 0;
        Initialize(consented: true, string.Empty);
        BindTargetProcess(1234);
        var acceptedKeyDown = SendKeyEvent(65, 30, 0, UIntPtr.Zero);
        foreground = 5678;
        var rejectedNextKeyDown = !SendKeyEvent(66, 48, 0, UIntPtr.Zero);
        var keyReleased = outputs.Contains("key:65:2") && !outputs.Contains("key:66:0");

        outputs.Clear();
        foreground = 1234;
        Initialize(consented: true, string.Empty);
        BindTargetProcess(1234);
        var acceptedMouseDown = SendMouseEvent(LeftDown, 0, 0, 0, UIntPtr.Zero);
        foreground = 5678;
        var rejectedNextMouseAction = !SendMouseEvent(Move, 1, 0, 0, UIntPtr.Zero);
        var mouseReleased = outputs.Contains($"mouse:{LeftUp}") && !outputs.Contains($"mouse:{Move}");

        outputs.Clear();
        foreground = 1234;
        Initialize(consented: true, string.Empty);
        BindTargetProcess(1234);
        BindTargetProcess(5678);
        var ownedMismatchDoesNotAbort = !BeforeInput("owned process switch pending") && !IsAborted;
        var ownedTransitionAllowed = BeforeOwnedFocusChange(5678) && !IsAborted;
        foreground = 5678;
        var ownedTargetAccepted = BindInputTargetIfForeground(5678, "owned transition test");
        foreground = 8888;
        var unrelatedForegroundRejected = !BeforeOwnedFocusChange(5678) && IsAborted;

        outputs.Clear();
        foreground = 1234;
        Initialize(consented: true, string.Empty);
        BindTargetProcess(1234);
        var heldKey = SendKeyEvent(65, 30, 0, UIntPtr.Zero);
        Complete();
        var normalCompletionReleased = heldKey && outputs.Contains("key:65:2");
        _foregroundPidOverride = null;
        _keySinkOverride = null;
        _mouseSinkOverride = null;
        return noConsent && explicitConsent && sameProcess && differentProcess && noTarget
            && firstActivationAllowed && samePidActivationRejected && secondOwnedPidActivationAllowed && unownedPidActivationRejected
            && noConsentRejected
            && acceptedKeyDown && rejectedNextKeyDown && keyReleased
            && acceptedMouseDown && rejectedNextMouseAction && mouseReleased
            && ownedMismatchDoesNotAbort && ownedTransitionAllowed && ownedTargetAccepted && unrelatedForegroundRejected
            && normalCompletionReleased;
    }

    private static bool TryBeginStartupActivationForProbe(int processId)
    {
        lock (Gate) return TryBeginStartupActivationLocked(processId);
    }

    public static void Complete()
    {
        lock (Gate)
        {
            var keys = PressedKeys.Count;
            var buttons = PressedMouseButtons.Count;
            ReleaseOwnedInputsLocked();
            if (keys > 0 || buttons > 0)
                AppendProgress($"Smoke 清理已释放本次按下的按键{keys}个、鼠标按钮{buttons}个。");
        }
    }

    private static bool BeforeInputLocked(string operation)
    {
        if (!_consented)
        {
            AbortLocked($"缺少 --interactive-desktop；已拒绝输入：{operation}");
            return false;
        }

        if (_aborted) return false;
        if (_targetProcessId <= 0)
        {
            AbortLocked($"未绑定测试应用进程；已拒绝输入：{operation}");
            return false;
        }

        if (_foregroundPidOverride is not null)
        {
            var simulatedPid = _foregroundPidOverride();
            if (simulatedPid == (uint)_targetProcessId && OwnedProcessIds.Contains(_targetProcessId)) return true;
            if (OwnedProcessIds.Contains((int)simulatedPid)) return false;
            AbortLocked($"前台窗口已切离测试应用；已停止后续输入。操作={operation}，前台进程={simulatedPid}，目标进程={_targetProcessId}。本轮结果标记为需复核。");
            return false;
        }

        var foreground = GetForegroundWindow();
        GetWindowThreadProcessId(foreground, out var foregroundPid);
        if (OwnedProcessIds.Contains(_targetProcessId) && IsOwnedByProcess(foreground, _targetProcessId)) return true;
        if (IsAnyOwnedWindowLocked(foreground)) return false;
        AbortLocked($"前台窗口已切离测试应用；已停止后续输入。操作={operation}，前台句柄=0x{foreground.ToInt64():X}，前台进程={foregroundPid}，目标进程={_targetProcessId}。本轮结果标记为需复核。");
        return false;
    }

    private static uint ForegroundPidLocked()
    {
        if (_foregroundPidOverride is not null) return _foregroundPidOverride();
        var foreground = GetForegroundWindow();
        GetWindowThreadProcessId(foreground, out var processId);
        return processId;
    }

    private static bool ForegroundIsOwnedLocked() => _foregroundPidOverride is not null
        ? OwnedProcessIds.Contains((int)_foregroundPidOverride())
        : IsAnyOwnedWindowLocked(GetForegroundWindow());

    private static bool IsAnyOwnedWindowLocked(IntPtr window)
    {
        foreach (var processId in OwnedProcessIds)
            if (IsOwnedByProcess(window, processId)) return true;
        return false;
    }

    public static bool ForegroundBelongsToOwnedProcess(int processId)
    {
        lock (Gate) return OwnedProcessIds.Contains(processId);
    }

    private static bool IsOwnedByProcess(IntPtr foreground, int expectedProcessId)
    {
        var current = foreground;
        for (var depth = 0; current != IntPtr.Zero && depth < 8; depth++)
        {
            GetWindowThreadProcessId(current, out var processId);
            if (processId == (uint)expectedProcessId)
                return true;
            current = GetWindow(current, GwOwner);
        }
        return false;
    }

    private static void AbortLocked(string reason)
    {
        _aborted = true;
        _abortReason = reason;
        ReleaseOwnedInputsLocked();
        if (string.IsNullOrWhiteSpace(_progressPath))
            return;
        try
        {
            File.AppendAllText(_progressPath, $"{DateTimeOffset.Now:O} 交互中止：{_abortReason}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { }
    }

    private static void AppendProgress(string message)
    {
        if (string.IsNullOrWhiteSpace(_progressPath)) return;
        try { File.AppendAllText(_progressPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}", Encoding.UTF8); }
        catch { }
    }

    private static void ReleaseOwnedInputsLocked()
    {
        var releasedKeys = 0;
        foreach (var key in PressedKeys.ToArray())
        {
            try
            {
                if (_keySinkOverride is not null) _keySinkOverride(key.Key, key.Value, KeyUp);
                else KeybdEventNative(key.Key, key.Value, KeyUp, UIntPtr.Zero);
                releasedKeys++;
            }
            catch { }
        }
        PressedKeys.Clear();

        var releasedButtons = 0;
        foreach (var button in PressedMouseButtons.ToArray())
        {
            try
            {
                var up = MouseDownToUp[button];
                if (_mouseSinkOverride is not null) _mouseSinkOverride(up);
                else MouseEventNative(up, 0, 0, 0, UIntPtr.Zero);
                releasedButtons++;
            }
            catch { }
        }
        PressedMouseButtons.Clear();
        if (releasedKeys > 0 || releasedButtons > 0)
            _abortReason += $" 已释放本测试按下的按键{releasedKeys}个、鼠标按钮{releasedButtons}个。";
    }

    private const uint GwOwner = 4;
    private const int SwRestore = 9;
    private const uint KeyUp = 2;
    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;
    private const uint RightDown = 0x0008;
    private const uint RightUp = 0x0010;
    private const uint MiddleDown = 0x0020;
    private const uint MiddleUp = 0x0040;
    private const uint XDown = 0x0080;
    private const uint XUp = 0x0100;
    private const uint Move = 0x0001;
    private static readonly IReadOnlyDictionary<uint, uint> MouseDownToUp = new Dictionary<uint, uint>
    {
        [LeftDown] = LeftUp, [RightDown] = RightUp, [MiddleDown] = MiddleUp, [XDown] = XUp
    };

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")] private static extern bool SetForegroundWindowNative(IntPtr hWnd);
    [DllImport("user32.dll", EntryPoint = "ShowWindow")] private static extern bool ShowWindowNative(IntPtr hWnd, int command);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", EntryPoint = "keybd_event")] private static extern void KeybdEventNative(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
    [DllImport("user32.dll", EntryPoint = "mouse_event")] private static extern void MouseEventNative(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
}
