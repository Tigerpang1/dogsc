using NAudio.CoreAudioApi;

internal interface IPlaybackAudioSession
{
    uint ProcessId { get; }
    string EndpointId { get; }
    string EndpointName { get; }
    string EndpointState { get; }
    bool Mute { get; set; }
    float Volume { get; set; }
}

internal interface IPlaybackAudioSessionProvider : IDisposable
{
    IReadOnlyList<IPlaybackAudioSession> FindByProcessId(uint processId);
}

internal sealed record PlaybackSessionVolumeResult(
    uint ProcessId,
    string EndpointId,
    string EndpointName,
    string EndpointState,
    bool MatchesSelectedLoopbackEndpoint,
    bool OriginalMute,
    float OriginalVolume,
    bool RequestedMute,
    float RequestedVolume,
    bool? AppliedMuteReadback,
    float? AppliedVolumeReadback,
    bool? RestoredMute,
    float? RestoredVolume,
    IReadOnlyList<string> RestorationErrors);

internal sealed class PlaybackSessionVolumeApplyException : Exception
{
    internal PlaybackSessionVolumeApplyException(string message, Exception innerException, PlaybackSessionVolumeResult rollbackResult)
        : base(message, innerException) => RollbackResult = rollbackResult;

    internal PlaybackSessionVolumeResult RollbackResult { get; }
}

internal sealed class PlaybackSessionVolumeLease : IDisposable
{
    private const float ReadbackTolerance = 0.0001f;
    private readonly IPlaybackAudioSessionProvider _provider;
    private readonly IPlaybackAudioSession _session;
    private readonly uint _processId;
    private readonly string _endpointId;
    private readonly string _endpointName;
    private readonly string _endpointState;
    private readonly string _selectedEndpointId;
    private readonly bool _originalMute;
    private readonly float _originalVolume;
    private readonly float _requestedVolume;
    private bool? _appliedMuteReadback;
    private float? _appliedVolumeReadback;
    private bool _restored;
    private PlaybackSessionVolumeResult? _restoreResult;

    private PlaybackSessionVolumeLease(IPlaybackAudioSessionProvider provider, IPlaybackAudioSession session,
        uint processId, string endpointId, string endpointName, string endpointState, string selectedEndpointId,
        bool originalMute, float originalVolume, float requestedVolume)
    {
        _provider = provider;
        _session = session;
        _processId = processId;
        _endpointId = endpointId;
        _endpointName = endpointName;
        _endpointState = endpointState;
        _selectedEndpointId = selectedEndpointId;
        _originalMute = originalMute;
        _originalVolume = originalVolume;
        _requestedVolume = requestedVolume;
    }

    internal static PlaybackSessionVolumeLease Apply(IPlaybackAudioSessionProvider provider, uint processId,
        string selectedEndpointId, float temporaryVolume)
    {
        ArgumentNullException.ThrowIfNull(provider);
        try
        {
            if (processId == 0) throw new ArgumentOutOfRangeException(nameof(processId));
            if (string.IsNullOrWhiteSpace(selectedEndpointId)) throw new ArgumentException("A selected loopback endpoint is required.", nameof(selectedEndpointId));
            if (!float.IsFinite(temporaryVolume) || temporaryVolume is < 0 or > 1)
                throw new ArgumentOutOfRangeException(nameof(temporaryVolume));

            var matchingSessions = provider.FindByProcessId(processId)
                .Where(session => session.ProcessId == processId)
                .ToArray();
            if (matchingSessions.Length != 1)
                throw new InvalidOperationException($"Expected exactly one audio session for owned ffplay PID {processId}; found {matchingSessions.Length}.");

            var session = matchingSessions[0];
            // Cache all endpoint/process identity before any setting is changed. Restore
            // and result serialization never depend on COM identity getters staying alive.
            var actualProcessId = session.ProcessId;
            var endpointId = session.EndpointId;
            var endpointName = session.EndpointName;
            var endpointState = session.EndpointState;
            if (!string.Equals(endpointId, selectedEndpointId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Owned ffplay PID {processId} is routed to endpoint '{endpointId}', not selected loopback endpoint '{selectedEndpointId}'.");

            var originalMute = session.Mute;
            var originalVolume = session.Volume;
            var lease = new PlaybackSessionVolumeLease(provider, session, actualProcessId, endpointId, endpointName,
                endpointState, selectedEndpointId, originalMute, originalVolume, temporaryVolume);
            try
            {
                // Lower the process-local session volume before clearing mute.
                session.Volume = temporaryVolume;
                session.Mute = false;
                lease._appliedMuteReadback = session.Mute;
                lease._appliedVolumeReadback = session.Volume;
                if (lease._appliedMuteReadback != false ||
                    !lease._appliedVolumeReadback.HasValue ||
                    Math.Abs(lease._appliedVolumeReadback.Value - temporaryVolume) > ReadbackTolerance)
                    throw new InvalidOperationException("The process-local audio session did not retain the requested low-volume, unmuted state.");
                return lease;
            }
            catch (Exception applyError)
            {
                // Preserve any partial apply state before rolling it back, including when
                // one of the COM setters or readbacks throws.
                try { lease._appliedMuteReadback = session.Mute; } catch { }
                try { lease._appliedVolumeReadback = session.Volume; } catch { }
                var rollback = lease.Restore();
                throw new PlaybackSessionVolumeApplyException(
                    rollback.RestorationErrors.Count == 0
                        ? "Could not apply the requested process-local playback session state; the original state was restored."
                        : "Could not apply the requested process-local playback session state and rollback reported errors.",
                    applyError, rollback);
            }
        }
        catch
        {
            try { provider.Dispose(); } catch { }
            throw;
        }
    }

    internal PlaybackSessionVolumeResult Restore()
    {
        if (_restored) return _restoreResult!;
        _restored = true;
        var errors = new List<string>();
        bool? restoredMute = null;
        float? restoredVolume = null;

        try
        {
            // If the previous state was muted, mute first. Otherwise restore gain before
            // applying the previous mute bit. No endpoint master volume is touched.
            if (_originalMute) TrySet(() => _session.Mute = true, "mute", errors);
            TrySet(() => _session.Volume = _originalVolume, "volume", errors);
            if (!_originalMute) TrySet(() => _session.Mute = false, "mute", errors);

            try { restoredMute = _session.Mute; } catch (Exception ex) { errors.Add($"read restored mute: {ex.Message}"); }
            try { restoredVolume = _session.Volume; } catch (Exception ex) { errors.Add($"read restored volume: {ex.Message}"); }
            if (restoredMute.HasValue && restoredMute.Value != _originalMute)
                errors.Add($"restored mute mismatch: expected {_originalMute}, got {restoredMute.Value}");
            if (restoredVolume.HasValue && Math.Abs(restoredVolume.Value - _originalVolume) > ReadbackTolerance)
                errors.Add($"restored volume mismatch: expected {_originalVolume:R}, got {restoredVolume.Value:R}");
        }
        finally
        {
            try { _provider.Dispose(); }
            catch (Exception ex) { errors.Add($"dispose session provider: {ex.Message}"); }
            _restoreResult = Snapshot(restoredMute, restoredVolume, errors.ToArray());
        }
        return _restoreResult!;
    }

    private PlaybackSessionVolumeResult Snapshot(bool? restoredMute, float? restoredVolume, IReadOnlyList<string> errors) =>
        new(_processId, _endpointId, _endpointName, _endpointState,
            string.Equals(_endpointId, _selectedEndpointId, StringComparison.OrdinalIgnoreCase),
            _originalMute, _originalVolume, RequestedMute: false, _requestedVolume,
            _appliedMuteReadback, _appliedVolumeReadback, restoredMute, restoredVolume, errors);

    private static void TrySet(Action setter, string property, ICollection<string> errors)
    {
        try { setter(); }
        catch (Exception ex) { errors.Add($"restore {property}: {ex.Message}"); }
    }

    public void Dispose() => Restore();
}

internal sealed class NAudioPlaybackAudioSessionProvider : IPlaybackAudioSessionProvider
{
    private readonly List<MMDevice> _retainedDevices = [];
    private bool _disposed;

    public IReadOnlyList<IPlaybackAudioSession> FindByProcessId(uint processId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var enumerator = new MMDeviceEnumerator();
        var matches = new List<IPlaybackAudioSession>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            var matchedOnDevice = false;
            try
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (var index = 0; index < sessions.Count; index++)
                {
                    var session = sessions[index];
                    if (session.GetProcessID != processId) continue;
                    matches.Add(new NAudioPlaybackAudioSession(device, session));
                    matchedOnDevice = true;
                }
            }
            catch
            {
                device.Dispose();
                throw;
            }

            if (matchedOnDevice) _retainedDevices.Add(device);
            else device.Dispose();
        }
        return matches;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var device in _retainedDevices) device.Dispose();
        _retainedDevices.Clear();
    }

    private sealed class NAudioPlaybackAudioSession(MMDevice device, AudioSessionControl session) : IPlaybackAudioSession
    {
        public uint ProcessId => session.GetProcessID;
        public string EndpointId => device.ID;
        public string EndpointName => device.FriendlyName;
        public string EndpointState => device.State.ToString();
        public bool Mute { get => session.SimpleAudioVolume.Mute; set => session.SimpleAudioVolume.Mute = value; }
        public float Volume { get => session.SimpleAudioVolume.Volume; set => session.SimpleAudioVolume.Volume = value; }
    }
}
