internal static class PlaybackSessionVolumeProbe
{
    internal static void Run()
    {
        var endpoint = "test-loopback-endpoint";
        VerifyNormalRestore(endpoint);
        VerifyExceptionalScopeRestore(endpoint);
        VerifyApplyFailureRollback(endpoint);
        VerifyApplyReadbackMismatch(endpoint);
        VerifyRestoreSetterFailure(endpoint);
        VerifyNoSessionAndDuplicateSession(endpoint);
        VerifyForeignProcessAndWrongEndpoint(endpoint);
        VerifyLongLeaseMonitor(endpoint);
        Console.WriteLine("Playback session volume probe passed: normal/exception restore, idempotence, apply/restore setter failures, absent/duplicate session rejection, exact-PID scoping, route checks, 360 simulated 5-second lease observations, and provider cleanup; no audio device or process was used.");
    }

    private static void VerifyNormalRestore(string endpoint)
    {
        var owned = new FakeSession(4200, endpoint, mute: true, volume: 0f);
        var foreign = new FakeSession(4100, endpoint, mute: false, volume: 0.8f);
        var provider = new FakeProvider([foreign, owned]);
        PlaybackSessionVolumeResult result;
        PlaybackSessionVolumeLease lease;
        using (lease = PlaybackSessionVolumeLease.Apply(provider, 4200, endpoint, 0.03f))
        {
            Require(!owned.Mute && Math.Abs(owned.Volume - 0.03f) < 0.0001f, "The owned session should be temporarily unmuted at the requested low volume.");
            Require(foreign.Mute == false && Math.Abs(foreign.Volume - 0.8f) < 0.0001f, "A foreign process session must remain untouched.");
        }
        result = lease.Restore();
        var repeated = lease.Restore();

        Require(owned.Mute && owned.Volume == 0f, "Normal scope exit must restore the original muted/zero-volume state.");
        Require(result.AppliedMuteReadback == false && Math.Abs(result.AppliedVolumeReadback!.Value - 0.03f) < 0.0001f,
            "The report must contain actual temporary-state readbacks.");
        Require(result.RestoredMute == true && result.RestoredVolume == 0f && result.RestorationErrors.Count == 0,
            "The report must confirm the restored values without errors.");
        Require(provider.DisposeCount == 1 && result == repeated, "Restore and Dispose must be idempotent and dispose once.");
    }

    private static void VerifyExceptionalScopeRestore(string endpoint)
    {
        var owned = new FakeSession(4210, endpoint, mute: false, volume: 0.2f);
        var provider = new FakeProvider([owned]);
        var lease = PlaybackSessionVolumeLease.Apply(provider, 4210, endpoint, 0.03f);
        try
        {
            using (lease)
            {
                Require(Math.Abs(owned.Volume - 0.03f) < 0.0001f, "The temporary setting should be active inside the scope.");
                throw new ProbeSentinelException();
            }
        }
        catch (ProbeSentinelException) { }
        var result = lease.Restore();
        Require(!owned.Mute && Math.Abs(owned.Volume - 0.2f) < 0.0001f,
            "Exceptional scope exit must restore an originally unmuted, nonzero session.");
        Require(result.RestoredMute == false && Math.Abs(result.RestoredVolume!.Value - 0.2f) < 0.0001f && result.RestorationErrors.Count == 0,
            "The exception path report must confirm exact original values.");
        Require(provider.DisposeCount == 1, "Exceptional scope exit should dispose once.");
    }

    private static void VerifyApplyFailureRollback(string endpoint)
    {
        var failing = new FakeSession(4300, endpoint, mute: true, volume: 0f) { ThrowOnUnmuteOnce = true };
        var provider = new FakeProvider([failing]);
        try
        {
            using var ignored = PlaybackSessionVolumeLease.Apply(provider, 4300, endpoint, 0.03f);
            throw new InvalidOperationException("The injected unmute failure should prevent lease creation.");
        }
        catch (PlaybackSessionVolumeApplyException ex)
        {
            var rollback = ex.RollbackResult;
            Require(rollback.AppliedMuteReadback == true && Math.Abs(rollback.AppliedVolumeReadback!.Value - 0.03f) < 0.0001f,
                "Apply failure must preserve the partial temporary-state readbacks.");
            Require(rollback.RestoredMute == true && rollback.RestoredVolume == 0f && rollback.RestorationErrors.Count == 0,
                "Apply failure must report successful rollback to the original state.");
        }
        Require(failing.Mute && failing.Volume == 0f && provider.DisposeCount == 1,
            "An exception while applying settings must restore the original state and dispose the provider.");
    }

    private static void VerifyRestoreSetterFailure(string endpoint)
    {
        var failing = new FakeSession(4310, endpoint, mute: true, volume: 0f) { ThrowOnRestoreVolumeOnce = true };
        var provider = new FakeProvider([failing]);
        var lease = PlaybackSessionVolumeLease.Apply(provider, 4310, endpoint, 0.03f);
        var result = lease.Restore();
        lease.Dispose();
        Require(result.RestorationErrors.Any(error => error.Contains("restore volume", StringComparison.Ordinal)),
            "A failed restoration setter must be recorded as an error.");
        Require(result.RestoredMute == true && Math.Abs(result.RestoredVolume!.Value - 0.03f) < 0.0001f,
            "The readback must expose that the original gain was not restored.");
        Require(provider.DisposeCount == 1, "Provider disposal must run even when a restore setter fails.");
    }

    private static void VerifyApplyReadbackMismatch(string endpoint)
    {
        var clamped = new FakeSession(4320, endpoint, mute: true, volume: 0f) { ClampVolumeTo = 0.04f };
        var provider = new FakeProvider([clamped]);
        try
        {
            using var ignored = PlaybackSessionVolumeLease.Apply(provider, 4320, endpoint, 0.03f);
            throw new InvalidOperationException("A clamped temporary volume should fail its readback check.");
        }
        catch (PlaybackSessionVolumeApplyException ex)
        {
            Require(ex.RollbackResult.RequestedVolume == 0.03f && ex.RollbackResult.AppliedVolumeReadback == 0.04f,
                "Apply rollback evidence must distinguish requested volume from actual readback.");
            Require(ex.RollbackResult.RestoredVolume == 0f && ex.RollbackResult.RestorationErrors.Count == 0,
                "The clamped setting should roll back to the original volume.");
        }
        Require(clamped.Mute && clamped.Volume == 0f && provider.DisposeCount == 1,
            "A temporary volume readback mismatch must restore and dispose.");
    }

    private static void VerifyNoSessionAndDuplicateSession(string endpoint)
    {
        var emptyProvider = new FakeProvider([]);
        ExpectRejected(emptyProvider, 4400, endpoint, "found 0");

        var first = new FakeSession(4410, endpoint, mute: true, volume: 0f);
        var second = new FakeSession(4410, endpoint, mute: true, volume: 0f);
        var duplicateProvider = new FakeProvider([first, second]);
        ExpectRejected(duplicateProvider, 4410, endpoint, "found 2");
        Require(first.Mute && first.Volume == 0f && second.Mute && second.Volume == 0f,
            "Absent or duplicate sessions must not be changed.");
    }

    private static void VerifyForeignProcessAndWrongEndpoint(string endpoint)
    {
        var foreign = new FakeSession(4500, endpoint, mute: false, volume: 0.8f);
        var foreignProvider = new FakeProvider([foreign]);
        ExpectRejected(foreignProvider, 4501, endpoint, "found 0");
        Require(!foreign.Mute && Math.Abs(foreign.Volume - 0.8f) < 0.0001f, "A different PID must remain untouched.");

        var wrongEndpoint = new FakeSession(4510, "different-endpoint", mute: true, volume: 0f);
        var routeProvider = new FakeProvider([wrongEndpoint]);
        ExpectRejected(routeProvider, 4510, endpoint, "not selected loopback endpoint");
        Require(wrongEndpoint.Mute && wrongEndpoint.Volume == 0f, "A route mismatch must not modify the owned session.");
    }

    private static void VerifyLongLeaseMonitor(string endpoint)
    {
        const uint pid = 4600;
        var owned = new FakeSession(pid, endpoint, mute: true, volume: 0f);
        var foreign = new FakeSession(4601, endpoint, mute: false, volume: 0.8f);
        var initialProvider = new FakeProvider([owned]);
        var lease = PlaybackSessionVolumeLease.Apply(initialProvider, pid, endpoint, temporaryVolume: 1f);
        var monitor = new PlaybackSessionVolumeMonitor(pid, endpoint, requestedVolume: 1f);
        for (var check = 1; check <= 360; check++)
        {
            var provider = new FakeProvider([foreign, owned]);
            var observation = monitor.Observe(provider, check * 5.0);
            Require(observation.ProcessId == pid && observation.EndpointId == endpoint &&
                    observation.Mute == false && Math.Abs(observation.Volume - 1f) < 0.0001f,
                "Every simulated five-second lease check must confirm only the exact PID and selected endpoint.");
            provider.Dispose();
            Require(provider.DisposeCount == 1, "Each transient lease check provider must be disposed.");
        }
        Require(!foreign.Mute && Math.Abs(foreign.Volume - 0.8f) < 0.0001f,
            "Long-lease monitoring must leave a different process session unchanged.");

        owned.Mute = true;
        try
        {
            using var provider = new FakeProvider([owned]);
            monitor.Observe(provider, 1800);
            throw new InvalidOperationException("A muted owned session must abort the long lease.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("became muted", StringComparison.Ordinal)) { }
        owned.Mute = false;

        var routeChanged = new FakeSession(pid, "different-endpoint", mute: false, volume: 1f);
        try
        {
            using var provider = new FakeProvider([routeChanged]);
            monitor.Observe(provider, 1805);
            throw new InvalidOperationException("A route change must abort the long lease.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("moved to endpoint", StringComparison.Ordinal)) { }
        Require(routeChanged.Volume == 1f && !routeChanged.Mute,
            "A route mismatch must not alter the mismatched session.");

        var restored = lease.Restore();
        Require(restored.RestoredMute == true && restored.RestoredVolume == 0f && restored.RestorationErrors.Count == 0,
            "The simulated long lease must restore the original mute and volume after monitoring.");
    }

    private static void ExpectRejected(FakeProvider provider, uint pid, string endpoint, string message)
    {
        try
        {
            using var ignored = PlaybackSessionVolumeLease.Apply(provider, pid, endpoint, 0.03f);
            throw new InvalidOperationException("An invalid session selection was accepted.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains(message, StringComparison.Ordinal)) { }
        Require(provider.DisposeCount == 1, "A rejected session selection must dispose its provider.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ProbeSentinelException : Exception { }

    private sealed class FakeProvider(IReadOnlyList<IPlaybackAudioSession> sessions) : IPlaybackAudioSessionProvider
    {
        public int DisposeCount { get; private set; }
        private bool _disposed;
        public IReadOnlyList<IPlaybackAudioSession> FindByProcessId(uint processId) => sessions;
        public void Dispose() { if (_disposed) return; _disposed = true; DisposeCount++; }
    }

    private sealed class FakeSession(uint processId, string endpointId, bool mute, float volume) : IPlaybackAudioSession
    {
        private bool _mute = mute;
        private float _volume = volume;
        public bool ThrowOnUnmuteOnce { get; set; }
        public bool ThrowOnRestoreVolumeOnce { get; set; }
        public float? ClampVolumeTo { get; set; }
        public uint ProcessId { get; } = processId;
        public string EndpointId { get; } = endpointId;
        public string EndpointName => "fake endpoint";
        public string EndpointState => "Active";
        public bool Mute
        {
            get => _mute;
            set
            {
                if (!value && ThrowOnUnmuteOnce)
                {
                    ThrowOnUnmuteOnce = false;
                    throw new InvalidOperationException("injected unmute failure");
                }
                _mute = value;
            }
        }
        public float Volume
        {
            get => _volume;
            set
            {
                if (value == 0f && ThrowOnRestoreVolumeOnce)
                {
                    ThrowOnRestoreVolumeOnce = false;
                    throw new InvalidOperationException("injected restore volume failure");
                }
                _volume = value == 0.03f && ClampVolumeTo.HasValue ? ClampVolumeTo.Value : value;
            }
        }
    }
}
