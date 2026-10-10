using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

[assembly: InternalsVisibleTo("FrameTrail.App.VisualProbe")]

namespace FrameTrail.App;

/// <summary>Places only the main editor window in native monitor coordinates.</summary>
internal static class MainWindowPlacement
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int MonitorDpiAwarenessContextPerMonitorAwareV2 = -4;
    private const double PaddingDip = 16;

    internal readonly record struct RectPx(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Math.Max(0, Right - Left);
        public int Height => Math.Max(0, Bottom - Top);
    }

    public static void Attach(MainWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var initialMinWidth = window.MinWidth;
        var initialMinHeight = window.MinHeight;
        var placed = false;

        window.Loaded += (_, _) =>
        {
            if (placed) return;
            placed = true;
            if (window.WindowState == WindowState.Normal)
                PlaceInitial(window, initialMinWidth, initialMinHeight);
        };

        window.DpiChanged += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (window.IsLoaded && window.WindowState == WindowState.Normal)
                ClampCurrent(window, initialMinWidth, initialMinHeight);
        }));
    }

    internal static RectPx CenterAndFit(RectPx window, RectPx workArea, int padding)
    {
        var availableWidth = Math.Max(1, workArea.Width - 2 * Math.Max(0, padding));
        var availableHeight = Math.Max(1, workArea.Height - 2 * Math.Max(0, padding));
        var width = Math.Min(Math.Max(1, window.Width), availableWidth);
        var height = Math.Min(Math.Max(1, window.Height), availableHeight);
        var left = workArea.Left + (workArea.Width - width) / 2;
        var top = workArea.Top + (workArea.Height - height) / 2;
        return new RectPx(left, top, left + width, top + height);
    }

    internal static RectPx ClampAndFit(RectPx window, RectPx workArea, int padding)
    {
        var availableWidth = Math.Max(1, workArea.Width - 2 * Math.Max(0, padding));
        var availableHeight = Math.Max(1, workArea.Height - 2 * Math.Max(0, padding));
        var width = Math.Min(Math.Max(1, window.Width), availableWidth);
        var height = Math.Min(Math.Max(1, window.Height), availableHeight);
        var minLeft = workArea.Left + Math.Max(0, padding);
        var minTop = workArea.Top + Math.Max(0, padding);
        var maxLeft = workArea.Right - Math.Max(0, padding) - width;
        var maxTop = workArea.Bottom - Math.Max(0, padding) - height;
        var left = Math.Clamp(window.Left, minLeft, Math.Max(minLeft, maxLeft));
        var top = Math.Clamp(window.Top, minTop, Math.Max(minTop, maxTop));
        return new RectPx(left, top, left + width, top + height);
    }

    private static void PlaceInitial(Window window, double initialMinWidth, double initialMinHeight)
    {
        if (!TryGetGeometry(window, out var hwnd, out var current, out var workArea, out var dpi)) return;
        var padding = DipToPixels(PaddingDip, dpi);
        var target = CenterAndFit(current, workArea, padding);
        UpdateMinimumSize(window, initialMinWidth, initialMinHeight, workArea, padding, dpi);
        Apply(hwnd, target);
    }

    private static void ClampCurrent(Window window, double initialMinWidth, double initialMinHeight)
    {
        if (!TryGetGeometry(window, out var hwnd, out var current, out var workArea, out var dpi)) return;
        var padding = DipToPixels(PaddingDip, dpi);
        var target = ClampAndFit(current, workArea, padding);
        UpdateMinimumSize(window, initialMinWidth, initialMinHeight, workArea, padding, dpi);
        if (target != current) Apply(hwnd, target);
    }

    private static bool TryGetGeometry(Window window, out IntPtr hwnd, out RectPx current, out RectPx workArea, out uint dpi)
    {
        hwnd = new WindowInteropHelper(window).Handle;
        current = default;
        workArea = default;
        dpi = 96;
        if (hwnd == IntPtr.Zero) return false;

        var previousContext = SetThreadDpiAwarenessContext(new IntPtr(MonitorDpiAwarenessContextPerMonitorAwareV2));
        if (previousContext == IntPtr.Zero) return false;
        try
        {
            if (!GetWindowRect(hwnd, out var windowRect)) return false;
            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero) return false;
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info)) return false;
            dpi = GetDpiForWindow(hwnd);
            if (dpi == 0) dpi = 96;
            current = FromNative(windowRect);
            workArea = FromNative(info.Work);
            return current.Width > 0 && current.Height > 0 && workArea.Width > 0 && workArea.Height > 0;
        }
        finally { _ = SetThreadDpiAwarenessContext(previousContext); }
    }

    private static void UpdateMinimumSize(Window window, double initialMinWidth, double initialMinHeight, RectPx workArea, int padding, uint dpi)
    {
        var scale = Math.Max(1, dpi) / 96d;
        var availableWidthDip = Math.Max(1, workArea.Width - 2 * padding) / scale;
        var availableHeightDip = Math.Max(1, workArea.Height - 2 * padding) / scale;
        window.MinWidth = Math.Min(initialMinWidth, availableWidthDip);
        window.MinHeight = Math.Min(initialMinHeight, availableHeightDip);
    }

    private static void Apply(IntPtr hwnd, RectPx rect)
    {
        var previousContext = SetThreadDpiAwarenessContext(new IntPtr(MonitorDpiAwarenessContextPerMonitorAwareV2));
        if (previousContext == IntPtr.Zero)
        {
            App.Log(new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法进入主窗口定位所需的 DPI 上下文。"));
            return;
        }
        try
        {
            if (!SetWindowPos(hwnd, IntPtr.Zero, rect.Left, rect.Top, rect.Width, rect.Height, SwpNoZOrder | SwpNoActivate))
                App.Log(new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法将主窗口放入显示器工作区。"));
        }
        finally { _ = SetThreadDpiAwarenessContext(previousContext); }
    }

    private static int DipToPixels(double dip, uint dpi) => (int)Math.Round(dip * Math.Max(1, dpi) / 96d);
    private static RectPx FromNative(NativeRect rect) => new(rect.Left, rect.Top, rect.Right, rect.Bottom);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
