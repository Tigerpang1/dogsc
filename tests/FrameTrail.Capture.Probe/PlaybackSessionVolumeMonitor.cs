internal sealed record PlaybackSessionVolumeObservation(
    double ElapsedSeconds,
    uint ProcessId,
    string EndpointId,
    string EndpointName,
    string EndpointState,
    bool Mute,
    float Volume);

internal sealed class PlaybackSessionVolumeMonitor(uint processId, string selectedEndpointId, float requestedVolume)
{
    private const float ReadbackTolerance = 0.0001f;

    internal PlaybackSessionVolumeObservation Observe(IPlaybackAudioSessionProvider provider, double elapsedSeconds)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));

        var matches = provider.FindByProcessId(processId)
            .Where(session => session.ProcessId == processId)
            .ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException($"Expected exactly one playback session for owned ffplay PID {processId}; found {matches.Length}.");

        var session = matches[0];
        var actualPid = session.ProcessId;
        var endpointId = session.EndpointId;
        var endpointName = session.EndpointName;
        var endpointState = session.EndpointState;
        if (actualPid != processId)
            throw new InvalidOperationException($"Playback session PID changed: expected {processId}, got {actualPid}.");
        if (!string.Equals(endpointId, selectedEndpointId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Owned ffplay PID {processId} moved to endpoint '{endpointId}', expected '{selectedEndpointId}'.");
        if (!string.Equals(endpointState, "Active", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Owned ffplay endpoint '{endpointId}' is no longer active: state={endpointState}.");

        var mute = session.Mute;
        var volume = session.Volume;
        if (mute)
            throw new InvalidOperationException($"Owned ffplay session became muted at {elapsedSeconds:F3}s.");
        if (!float.IsFinite(volume) || Math.Abs(volume - requestedVolume) > ReadbackTolerance)
            throw new InvalidOperationException($"Owned ffplay session volume changed at {elapsedSeconds:F3}s: expected {requestedVolume:R}, got {volume:R}.");

        return new PlaybackSessionVolumeObservation(elapsedSeconds, actualPid, endpointId, endpointName,
            endpointState, mute, volume);
    }
}
