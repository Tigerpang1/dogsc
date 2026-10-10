using FrameTrail.App;

internal static class RecordingMarkerHotKeyProbe
{
    private const int AlreadyRegistered = 1409;
    private const uint CtrlAltNoRepeat = 0x4003;
    private const uint CtrlAltShiftNoRepeat = 0x4007;

    public static void Verify(Action<bool, string, object?> check)
    {
        var hwnd = new IntPtr(123);

        var preferredCalls = new List<(int Id, uint Modifiers, uint Key)>();
        using (var preferred = new RecordingMarkerHotKey(
                   (_, id, modifiers, key) => { preferredCalls.Add((id, modifiers, key)); return true; },
                   (_, _) => true,
                   () => 0))
        {
            check(preferred.Register(hwnd) && preferred.ShortcutText == "Ctrl+Alt+M" && !preferred.IsFallback,
                "registers the preferred Ctrl+Alt+M shortcut",
                new { Calls = preferredCalls });
            check(preferredCalls.SequenceEqual([(RecordingMarkerHotKey.Id, CtrlAltNoRepeat, 0x4Du)]),
                "uses one stable hotkey ID, MOD_NOREPEAT, and virtual key M",
                new { Calls = preferredCalls });
        }

        var fallbackCalls = new List<uint>();
        var fallbackAttempt = 0;
        using (var fallback = new RecordingMarkerHotKey(
                   (_, _, modifiers, _) =>
                   {
                       fallbackCalls.Add(modifiers);
                       return ++fallbackAttempt == 2;
                   },
                   (_, _) => true,
                   () => AlreadyRegistered))
        {
            check(fallback.Register(hwnd) && fallback.IsFallback && fallback.ShortcutText == "Ctrl+Alt+Shift+M",
                "falls back to Ctrl+Alt+Shift+M when the preferred shortcut is occupied",
                new { Calls = fallbackCalls });
            check(fallbackCalls.SequenceEqual([CtrlAltNoRepeat, CtrlAltShiftNoRepeat]),
                "attempts the fallback only after the preferred shortcut reports error 1409",
                new { Calls = fallbackCalls });
        }

        var bothCalls = 0;
        var unregisterCalls = 0;
        using (var bothOccupied = new RecordingMarkerHotKey(
                   (_, _, _, _) => { bothCalls++; return false; },
                   (_, _) => { unregisterCalls++; return true; },
                   () => AlreadyRegistered))
        {
            check(!bothOccupied.Register(hwnd) && bothOccupied.ShortcutText is null && bothOccupied.RegistrationError == AlreadyRegistered,
                "leaves the marker button as the available path when both shortcuts are occupied",
                new { bothOccupied.ShortcutText, bothOccupied.RegistrationError, bothCalls });
            check(unregisterCalls == 0,
                "does not unregister a hotkey owned by another application",
                new { unregisterCalls });
        }

        var nonConflictCalls = 0;
        using (var nonConflict = new RecordingMarkerHotKey(
                   (_, _, _, _) => { nonConflictCalls++; return false; },
                   (_, _) => true,
                   () => 5))
        {
            check(!nonConflict.Register(hwnd) && nonConflict.RegistrationError == 5 && nonConflictCalls == 1,
                "does not try the fallback for errors other than 1409",
                new { nonConflict.RegistrationError, nonConflictCalls });
        }

        var retryCalls = 0;
        using (var retry = new RecordingMarkerHotKey(
                   (_, _, _, _) => ++retryCalls > 2,
                   (_, _) => true,
                   () => AlreadyRegistered))
        {
            var first = retry.Register(hwnd);
            var second = retry.Register(hwnd);
            check(!first && second && retry.ShortcutText == "Ctrl+Alt+M" && !retry.IsFallback,
                "can retry after an unavailable attempt and recover the preferred shortcut",
                new { retry.ShortcutText, retry.IsFallback, retry.RegistrationError, retryCalls });
        }

        var ownedUnregisterCalls = 0;
        var owned = new RecordingMarkerHotKey(
            (_, _, _, _) => true,
            (_, id) => { if (id == RecordingMarkerHotKey.Id) ownedUnregisterCalls++; return true; },
            () => 0);
        owned.Register(hwnd);
        owned.Dispose();
        owned.Dispose();
        check(ownedUnregisterCalls == 1,
            "unregisters its own successful registration exactly once on dispose",
            new { ownedUnregisterCalls });

        using (var unowned = new RecordingMarkerHotKey(
                   (_, _, _, _) => false,
                   (_, _) => { unregisterCalls++; return true; },
                   () => AlreadyRegistered))
        {
            unowned.Register(hwnd);
        }
        check(unregisterCalls == 0,
            "dispose leaves another application's occupied shortcut registered",
            new { unregisterCalls });
    }
}
