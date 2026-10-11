using FrameTrail.App;
using FrameTrail.Capture;

internal static class WindowCaptureSourceFilterProbe
{
    public static void Verify(Action<bool, string, object?> check)
    {
        const int appPid = 4200;
        const int otherPid = 7300;
        var mainHandle = new IntPtr(1001);
        var windows = new[]
        {
            new WindowInfo("main", "DogSC editor", appPid, mainHandle, new PixelRect(0, 0, 800, 600)),
            new WindowInfo("bar", "DogSC recording bar", appPid, new IntPtr(1002), new PixelRect(10, 10, 400, 60)),
            new WindowInfo("memo", "DogSC memo", appPid, new IntPtr(1003), new PixelRect(20, 20, 400, 300)),
            new WindowInfo("external", "DogSC", otherPid, new IntPtr(2001), new PixelRect(30, 30, 400, 300))
        };

        var excluded = WindowCaptureSourceFilter.Filter(windows, appPid, mainHandle, excludeEditorDuringRecording: true);
        check(excluded.Select(window => window.Id).SequenceEqual(["external"]),
            "when editor exclusion is enabled, own main and auxiliary windows are filtered while external same-name windows remain",
            new { Results = excluded.Select(window => new { window.Id, window.Title, window.ProcessId }).ToArray() });

        var editorAvailable = WindowCaptureSourceFilter.Filter(windows, appPid, mainHandle, excludeEditorDuringRecording: false);
        check(editorAvailable.Select(window => window.Id).ToHashSet().SetEquals(["main", "external"]),
            "when editor exclusion is disabled, only the own main window is added back; own recording bar and memo remain filtered",
            new { Results = editorAvailable.Select(window => new { window.Id, window.Title, window.ProcessId }).ToArray() });

        var unknownOwner = new WindowInfo("unknown", "DogSC external app", null, new IntPtr(3001), new PixelRect(40, 40, 400, 300));
        var withUnknownOwner = WindowCaptureSourceFilter.Filter(windows.Append(unknownOwner), appPid, mainHandle, excludeEditorDuringRecording: true);
        check(withUnknownOwner.Any(window => window.Id == unknownOwner.Id),
            "windows without a process ID are preserved by the source list filter",
            new { Results = withUnknownOwner.Select(window => new { window.Id, window.ProcessId }).ToArray() });
    }
}
