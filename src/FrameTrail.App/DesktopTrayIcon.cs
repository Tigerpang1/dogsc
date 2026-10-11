using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace FrameTrail.App;

/// <summary>
/// Owns a Windows notification-area icon and forwards its activation and menu requests
/// to the application. Construct and dispose this class on the WPF dispatcher thread.
/// </summary>
internal sealed class DesktopTrayIcon : IDisposable
{
    private const uint CallbackMessage = WmApp + 0x31;
    private const uint WmApp = 0x8000;
    private const uint WmContextMenu = 0x007B;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmLButtonDoubleClick = 0x0203;
    private const uint WmRButtonUp = 0x0205;

    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetVersion = 0x00000004;

    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifShowTip = 0x00000080;
    private const uint NotifyIconVersion4 = 4;
    private const uint NinSelect = 0x0400;
    private const uint NinKeySelect = 0x0401;

    private const int IconId = 1;
    private const long ActivationDebounceMilliseconds = 400;
    private const long MenuDebounceMilliseconds = 250;

    private readonly Action activateRecorder;
    private readonly Action<Point> showMenu;
    private readonly Action<Exception>? reportFailure;
    private readonly HwndSource messageSink;
    private readonly IntPtr iconHandle;
    private readonly uint taskbarCreatedMessage;
    private bool iconRegistered;
    private bool disposed;
    private long lastActivationAt;
    private long lastMenuAt;
    private bool hasActivated;
    private bool hasShownMenu;
    private string tooltip = "FrameTrail";

    /// <summary>The latest runtime tray registration failure, if Explorer recovery failed.</summary>
    public Exception? LastFailure { get; private set; }

    /// <summary>Raised after Explorer recreates the notification area and the icon is registered again.</summary>
    public event Action? AvailabilityRestored;

    /// <summary>
    /// Creates and registers the tray icon. Throws a descriptive exception if Windows
    /// cannot create the message sink or register the icon.
    /// </summary>
    public DesktopTrayIcon(Action activateRecorder, Action<Point> showMenu, Action<Exception>? reportFailure = null)
    {
        this.activateRecorder = activateRecorder ?? throw new ArgumentNullException(nameof(activateRecorder));
        this.showMenu = showMenu ?? throw new ArgumentNullException(nameof(showMenu));
        this.reportFailure = reportFailure;

        var parameters = new HwndSourceParameters("FrameTrail notification icon")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0
        };

        try
        {
            messageSink = new HwndSource(parameters);
            messageSink.AddHook(WindowProc);
            taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
            if (taskbarCreatedMessage == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注册 TaskbarCreated 消息，托盘图标无法在资源管理器重启后恢复。");

            iconHandle = LoadApplicationIcon();
            RegisterIcon();
        }
        catch
        {
            if (messageSink is not null)
            {
                messageSink.RemoveHook(WindowProc);
                messageSink.Dispose();
            }
            if (iconHandle != IntPtr.Zero)
                DestroyIcon(iconHandle);
            throw;
        }
    }

    /// <summary>Updates the tooltip and indicates whether recording is active.</summary>
    public void SetRecording(bool recording)
    {
        VerifyDispatcher();
        ThrowIfDisposed();
        tooltip = recording ? "FrameTrail — 正在录制" : "FrameTrail";
        if (iconRegistered && !Shell_NotifyIcon(NimModify, CreateData(NifIcon | NifTip | NifMessage | NifShowTip)))
            throw TrayOperationFailed("更新托盘图标状态");
    }

    public void Dispose()
    {
        VerifyDispatcher();
        if (disposed) return;
        disposed = true;

        if (iconRegistered)
        {
            Shell_NotifyIcon(NimDelete, CreateData(NifMessage));
            iconRegistered = false;
        }

        messageSink.RemoveHook(WindowProc);
        messageSink.Dispose();
        if (iconHandle != IntPtr.Zero)
            DestroyIcon(iconHandle);
        GC.SuppressFinalize(this);
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (disposed) return IntPtr.Zero;

        if ((uint)message == taskbarCreatedMessage && taskbarCreatedMessage != 0)
        {
            var restored = false;
            try
            {
                RegisterIcon();
                restored = true;
            }
            catch (Exception ex)
            {
                LastFailure = ex;
                reportFailure?.Invoke(ex);
            }
            if (restored)
                AvailabilityRestored?.Invoke();
            handled = true;
            return IntPtr.Zero;
        }

        if ((uint)message != CallbackMessage)
            return IntPtr.Zero;

        var packed = unchecked((uint)lParam.ToInt64());
        var notification = packed & 0xFFFF;
        var incomingIconId = (ushort)((packed >> 16) & 0xFFFF);
        if (incomingIconId != IconId)
            return IntPtr.Zero;

        try
        {
            switch (notification)
            {
                case NinSelect:
                case NinKeySelect:
                case WmLButtonUp:
                case WmLButtonDoubleClick:
                    ActivateRecorder();
                    handled = true;
                    break;
                case WmContextMenu:
                    ShowMenu(DecodePoint(wParam));
                    handled = true;
                    break;
                case WmRButtonUp:
                    ShowMenu(DecodePoint(wParam));
                    handled = true;
                    break;
            }
        }
        catch (Exception error)
        {
            LastFailure = error;
            handled = true;
            reportFailure?.Invoke(error);
        }

        return IntPtr.Zero;
    }

    private void ActivateRecorder()
    {
        var now = Environment.TickCount64;
        if (hasActivated && now - lastActivationAt < ActivationDebounceMilliseconds) return;
        hasActivated = true;
        lastActivationAt = now;
        activateRecorder();
    }

    private void ShowMenu(Point point)
    {
        var now = Environment.TickCount64;
        if (hasShownMenu && now - lastMenuAt < MenuDebounceMilliseconds) return;
        hasShownMenu = true;
        lastMenuAt = now;
        _ = SetForegroundWindow(messageSink.Handle);

        // The notification-area callback is sent while Explorer is still processing
        // the tray click. Opening a StaysOpen=false WPF ContextMenu here can acquire
        // mouse capture before that click has finished, so WPF sees the click as an
        // outside interaction and closes the menu immediately. Queue the open until
        // this native callback has unwound; this is dispatcher ordering, not a delay.
        messageSink.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (disposed) return;
            try
            {
                showMenu(point);
            }
            catch (Exception error)
            {
                LastFailure = error;
                reportFailure?.Invoke(error);
            }
        }));
    }

    private void RegisterIcon()
    {
        iconRegistered = false;
        var data = CreateData(NifMessage | NifIcon | NifTip | NifShowTip);
        if (!Shell_NotifyIcon(NimAdd, data))
            throw TrayOperationFailed("添加托盘图标");

        iconRegistered = true;
        data.uVersion = NotifyIconVersion4;
        if (!Shell_NotifyIcon(NimSetVersion, data))
        {
            Shell_NotifyIcon(NimDelete, data);
            iconRegistered = false;
            throw TrayOperationFailed("设置托盘图标消息版本");
        }

        if (!Shell_NotifyIcon(NimModify, CreateData(NifTip | NifShowTip)))
        {
            Shell_NotifyIcon(NimDelete, data);
            iconRegistered = false;
            throw TrayOperationFailed("启用托盘标准提示");
        }

        LastFailure = null;
    }

    private NotifyIconData CreateData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = messageSink.Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = iconHandle,
        szTip = tooltip.Length > 127 ? tooltip[..127] : tooltip
    };

    private static IntPtr LoadApplicationIcon()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "FrameTrail.exe");
        if (!File.Exists(executable))
            executable = Environment.ProcessPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            throw new InvalidOperationException("无法定位 FrameTrail.exe，托盘图标无法读取应用图标资源。");

        IntPtr largeIcon = IntPtr.Zero;
        IntPtr smallIcon = IntPtr.Zero;
        var count = ExtractIconEx(executable, 0, ref largeIcon, ref smallIcon, 1);
        if (count == 0 || smallIcon == IntPtr.Zero)
        {
            if (largeIcon != IntPtr.Zero) DestroyIcon(largeIcon);
            throw TrayOperationFailed("从 FrameTrail.exe 读取应用图标");
        }

        if (largeIcon != IntPtr.Zero) DestroyIcon(largeIcon);
        return smallIcon;
    }

    private static Point DecodePoint(IntPtr packedPoint)
    {
        var packed = unchecked((uint)packedPoint.ToInt64());
        var x = unchecked((short)(packed & 0xFFFF));
        var y = unchecked((short)((packed >> 16) & 0xFFFF));
        return new Point(x, y);
    }

    private void VerifyDispatcher()
    {
        if (messageSink is not null && !messageSink.Dispatcher.CheckAccess())
            throw new InvalidOperationException("DesktopTrayIcon 必须在创建它的 WPF Dispatcher 线程上使用和释放。");
    }

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(DesktopTrayIcon));
    }

    private static Win32Exception TrayOperationFailed(string operation)
    {
        var error = Marshal.GetLastWin32Error();
        return new Win32Exception(error, $"{operation}失败。Windows 错误码：{error}。");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string? szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string? szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, in NotifyIconData lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint ExtractIconEx(string szFileName, int nIconIndex, ref IntPtr phiconLarge, ref IntPtr phiconSmall, uint nIcons);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
