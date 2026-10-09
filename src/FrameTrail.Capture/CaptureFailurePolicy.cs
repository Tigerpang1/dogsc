namespace FrameTrail.Capture;

/// <summary>Small, deterministic state rules shared by recorder failure handlers and the hardware-free probe.</summary>
internal static class CaptureFailurePolicy
{
    internal static void ApplyWindowFailure(Exception failure, CaptureState state,
        TaskCompletionSource<bool>? started, TaskCompletionSource<string>? completed, Action<Exception> remember)
    {
        remember(failure);
        if (state != CaptureState.Starting) return;
        started?.TrySetException(failure);
        completed?.TrySetException(failure);
    }

    internal static bool CanStopTrack(CaptureTrackState state) =>
        state is CaptureTrackState.Recording or CaptureTrackState.Paused or CaptureTrackState.Faulted;

    internal static bool HasNonEmptyCandidate(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length > 0; }
        catch { return false; }
    }
}
