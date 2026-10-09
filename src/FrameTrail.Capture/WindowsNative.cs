using System.Runtime.InteropServices;
using System.Text;

namespace FrameTrail.Capture;

internal static class WindowsNative
{
    internal const int SmXVirtualScreen = 76, SmYVirtualScreen = 77, SmCxVirtualScreen = 78, SmCyVirtualScreen = 79;
    internal delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct MonitorInfoEx
    {
        public int Size; public Rect Monitor; public Rect Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [DllImport("user32.dll")] internal static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfoEx info);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect value, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowTextW(IntPtr window, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern bool GetPhysicalCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }

    internal static PixelRect ToPixelRect(Rect r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    internal static bool TryGetVisibleWindowRect(IntPtr window, out Rect rect)
    {
        // DWMWA_EXTENDED_FRAME_BOUNDS reports the visible frame in physical screen coordinates.
        if (DwmGetWindowAttribute(window, 9, out rect, Marshal.SizeOf<Rect>()) == 0) return true;
        return GetWindowRect(window, out rect);
    }
    internal static IReadOnlyList<DisplayInfo> EnumerateDisplays()
    {
        var result = new List<DisplayInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr handle, IntPtr dc, ref Rect rect, IntPtr data) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>(), Device = string.Empty };
            if (GetMonitorInfoW(handle, ref info))
                result.Add(new DisplayInfo(info.Device, info.Device, ToPixelRect(info.Monitor), (info.Flags & 1) != 0));
            return true;
        }, IntPtr.Zero);
        return result;
    }
    internal static PixelRect VirtualBounds => new(GetSystemMetrics(SmXVirtualScreen), GetSystemMetrics(SmYVirtualScreen),
        GetSystemMetrics(SmCxVirtualScreen), GetSystemMetrics(SmCyVirtualScreen));
    internal static IDisposable UsePhysicalCoordinates() => new DpiContextScope();
    private sealed class DpiContextScope : IDisposable
    {
        private readonly IntPtr _previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        public void Dispose() { if (_previous != IntPtr.Zero) SetThreadDpiAwarenessContext(_previous); }
    }
}
