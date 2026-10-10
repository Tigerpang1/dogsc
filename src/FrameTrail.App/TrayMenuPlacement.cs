using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;

namespace FrameTrail.App;

/// <summary>Opens the tray context menu at a physical screen point and keeps it in that monitor's work area.</summary>
internal static class TrayMenuPlacement
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int DpiAwarenessContextPerMonitorAwareV2 = -4;
    private const int WorkAreaPaddingPixels = 4;

    internal readonly record struct RectPx(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Math.Max(0, Right - Left);
        public int Height => Math.Max(0, Bottom - Top);
    }

    /// <summary>
    /// Opens a context menu at a point expressed in physical screen pixels, then
    /// positions its native popup window using its measured pixel size.
    /// </summary>
    public static void Open(ContextMenu menu, Point physicalAnchor)
    {
        ArgumentNullException.ThrowIfNull(menu);
        menu.Dispatcher.VerifyAccess();
        if (!double.IsFinite(physicalAnchor.X) || !double.IsFinite(physicalAnchor.Y))
            throw new ArgumentOutOfRangeException(nameof(physicalAnchor), "菜单锚点必须是有限的屏幕坐标。");

        var anchorX = checked((int)Math.Round(physicalAnchor.X));
        var anchorY = checked((int)Math.Round(physicalAnchor.Y));

        if (menu.IsOpen)
            menu.IsOpen = false;

        menu.PlacementTarget = null;
        menu.Placement = PlacementMode.AbsolutePoint;
        // This initial placement only makes the popup HWND available. The Opened handler
        // moves it in native physical pixels after WPF has measured the menu.
        menu.HorizontalOffset = physicalAnchor.X;
        menu.VerticalOffset = physicalAnchor.Y;

        RoutedEventHandler opened = null!;
        opened = (_, _) =>
        {
            menu.Opened -= opened;
            try
            {
                PlaceOpenedMenu(menu, anchorX, anchorY);
            }
            catch (Exception ex)
            {
                App.Log(ex);
            }
        };

        menu.Opened += opened;
        menu.IsOpen = true;
    }

    private static void PlaceOpenedMenu(ContextMenu menu, int anchorX, int anchorY)
    {
        if (PresentationSource.FromVisual(menu) is not HwndSource source || source.Handle == IntPtr.Zero)
            throw new InvalidOperationException("无法取得托盘菜单 Popup 的 HWND。");

        var hwnd = source.Handle;
        var previousContext = SetThreadDpiAwarenessContext(new IntPtr(DpiAwarenessContextPerMonitorAwareV2));
        if (previousContext == IntPtr.Zero)
            throw NativeFailure("切换到逐显示器 DPI 坐标");

        try
        {
            var anchor = new NativePoint(anchorX, anchorY);
            var monitor = MonitorFromPoint(anchor, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
                throw NativeFailure("查找托盘菜单所在的显示器");

            var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref monitorInfo))
                throw NativeFailure("读取托盘菜单显示器工作区");

            var workArea = new RectPx(monitorInfo.Work.Left, monitorInfo.Work.Top, monitorInfo.Work.Right, monitorInfo.Work.Bottom);
            if (!SetWindowPos(hwnd, IntPtr.Zero, anchorX, anchorY, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate))
                throw NativeFailure("将托盘菜单移至目标显示器以读取 DPI");

            var dpi = GetDpiForWindow(hwnd);
            if (dpi == 0) dpi = 96;
            var maxWidth = Math.Max(1, workArea.Width - 2 * WorkAreaPaddingPixels) * 96d / dpi;
            var maxHeight = Math.Max(1, workArea.Height - 2 * WorkAreaPaddingPixels) * 96d / dpi;
            menu.MaxWidth = Math.Min(menu.MaxWidth, maxWidth);
            menu.MaxHeight = Math.Min(menu.MaxHeight, maxHeight);
            menu.UpdateLayout();

            if (!GetWindowRect(hwnd, out var current))
                throw NativeFailure("读取托盘菜单窗口大小");

            var width = Math.Max(1, current.Right - current.Left);
            var height = Math.Max(1, current.Bottom - current.Top);
            var left = anchorX + width > workArea.Right ? anchorX - width : anchorX;
            var top = anchorY + height > workArea.Bottom ? anchorY - height : anchorY;
            var requested = new MainWindowPlacement.RectPx(left, top, left + width, top + height);
            var workAreaRect = new MainWindowPlacement.RectPx(workArea.Left, workArea.Top, workArea.Right, workArea.Bottom);
            var fitted = MainWindowPlacement.ClampAndFit(requested, workAreaRect, WorkAreaPaddingPixels);

            if (!SetWindowPos(hwnd, IntPtr.Zero, fitted.Left, fitted.Top, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate))
                throw NativeFailure("将托盘菜单放入显示器工作区");

            _ = SetForegroundWindow(hwnd);
            menu.Focus();
            FocusFirstEnabledMenuItem(menu);
        }
        finally
        {
            _ = SetThreadDpiAwarenessContext(previousContext);
        }
    }

    private static void FocusFirstEnabledMenuItem(ContextMenu menu)
    {
        for (var index = 0; index < menu.Items.Count; index++)
        {
            if (menu.ItemContainerGenerator.ContainerFromIndex(index) is MenuItem { IsEnabled: true, Focusable: true } item)
            {
                Keyboard.Focus(item);
                return;
            }
        }
    }

    private static Win32Exception NativeFailure(string operation)
    {
        var error = Marshal.GetLastWin32Error();
        return new Win32Exception(error, $"{operation}失败。Windows 错误码：{error}。");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
        public NativePoint(int x, int y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
