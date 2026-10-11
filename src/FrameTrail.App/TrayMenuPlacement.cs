using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FrameTrail.App;

/// <summary>Positions an activatable tray menu window in physical monitor coordinates.</summary>
internal static class TrayMenuPlacement
{
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int PaddingPixels = 4;

    public static void PlaceWindow(Window window, Point physicalAnchor)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.Dispatcher.VerifyAccess();
        if (!double.IsFinite(physicalAnchor.X) || !double.IsFinite(physicalAnchor.Y))
            throw new ArgumentOutOfRangeException(nameof(physicalAnchor));
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("托盘菜单尚未创建窗口。");
        var x = checked((int)Math.Round(physicalAnchor.X));
        var y = checked((int)Math.Round(physicalAnchor.Y));
        var previousContext = SetThreadDpiAwarenessContext(new IntPtr(-4));
        if (previousContext == IntPtr.Zero) throw NativeFailure("设置托盘菜单 DPI");
        try
        {
            var monitor = MonitorFromPoint(new NativePoint(x, y), 2);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
                throw NativeFailure("读取托盘菜单工作区");
            // Moving without resizing obtains the anchor monitor's DPI before fitting.
            if (!SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate))
                throw NativeFailure("定位托盘菜单显示器");
            var dpi = GetDpiForWindow(hwnd);
            if (dpi == 0) dpi = 96;
            var availableWidth = Math.Max(1, info.Work.Right - info.Work.Left - 2 * PaddingPixels) * 96d / dpi;
            var availableHeight = Math.Max(1, info.Work.Bottom - info.Work.Top - 2 * PaddingPixels) * 96d / dpi;
            window.MinWidth = Math.Min(window.MinWidth, availableWidth);
            window.MinHeight = Math.Min(window.MinHeight, availableHeight);
            window.MaxWidth = Math.Min(window.MaxWidth, availableWidth);
            window.MaxHeight = Math.Min(window.MaxHeight, availableHeight);
            window.UpdateLayout();
            if (!GetWindowRect(hwnd, out var bounds)) throw NativeFailure("读取托盘菜单大小");
            var fitted = Fit(x, y, Math.Max(1, bounds.Right - bounds.Left), Math.Max(1, bounds.Bottom - bounds.Top),
                new MainWindowPlacement.RectPx(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom));
            if (!SetWindowPos(hwnd, IntPtr.Zero, fitted.Left, fitted.Top, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate))
                throw NativeFailure("将托盘菜单放入工作区");
        }
        finally { _ = SetThreadDpiAwarenessContext(previousContext); }
    }

    internal static MainWindowPlacement.RectPx Fit(int x, int y, int width, int height, MainWindowPlacement.RectPx workArea)
    {
        var left = (long)x + width > workArea.Right ? x - width : x;
        var top = (long)y + height > workArea.Bottom ? y - height : y;
        return MainWindowPlacement.ClampAndFit(new(left, top, left + width, top + height), workArea, PaddingPixels);
    }

    private static Win32Exception NativeFailure(string operation) => new(Marshal.GetLastWin32Error(), operation + "失败。");

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint(int x, int y) { public readonly int X = x; public readonly int Y = y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
