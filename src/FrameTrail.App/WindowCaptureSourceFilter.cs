using FrameTrail.Capture;

namespace FrameTrail.App;

internal static class WindowCaptureSourceFilter
{
    internal static IReadOnlyList<WindowInfo> Filter(
        IEnumerable<WindowInfo> windows,
        int currentProcessId,
        IntPtr mainWindowHandle,
        bool excludeEditorDuringRecording)
    {
        ArgumentNullException.ThrowIfNull(windows);
        return windows.Where(window =>
                window.ProcessId != currentProcessId ||
                (window.Handle == mainWindowHandle && !excludeEditorDuringRecording))
            .ToArray();
    }
}
