using FrameTrail.Capture;
using System.Text.Json;

internal static class AudioPacketDiagnosticsProbe
{
    internal static void Run()
    {
        const long start = 1_000_000;
        const long frequency = 1_000_000;
        var diagnostics = new AudioPacketDiagnostics(start, frequency, bucketSeconds: 10, maximumBuckets: 3);

        // An ordinary packet and an endpoint-reported SILENT packet are distinct from clock-fill silence.
        diagnostics.Packet(start + 1_000_000, 480, silent: false, arrivalLatencyMs: -3.5, timestampFallback: false,
            timestampError: false, discontinuity: false, devicePositionMismatch: false);
        diagnostics.Written(start + 1_000_000, 480, endpointSilent: false);
        diagnostics.Packet(start + 2_000_000, 480, silent: true, arrivalLatencyMs: 4.25, timestampFallback: false,
            timestampError: false, discontinuity: true, devicePositionMismatch: true);
        diagnostics.Written(start + 2_000_000, 480, endpointSilent: true);

        // No packet in this interval is represented by pump-created silence; a late packet overlap is separate.
        diagnostics.SilenceWritten(start + 12_000_000, 960, AudioSilenceSource.Pump);
        diagnostics.Packet(start + 22_000_000, 480, silent: false, arrivalLatencyMs: double.NaN, timestampFallback: true,
            timestampError: true, discontinuity: false, devicePositionMismatch: false);
        diagnostics.OverlapDropped(start + 22_000_000, 120);
        diagnostics.Written(start + 22_000_000, 360, endpointSilent: false);
        diagnostics.SilenceWritten(start + 42_000_000, 1920, AudioSilenceSource.Finalization);

        var snapshot = diagnostics.Snapshot(start + 45_000_000);
        Require(snapshot.PacketCount == 3, "packet total");
        Require(snapshot.PacketFrames == 1_440 && snapshot.SilentFlagFrames == 480, "packet and endpoint SILENT frame totals");
        Require(snapshot.SilentSourceFramesWritten == 480 && snapshot.PacketSourceFramesWritten == 1_320 &&
            snapshot.ActuallyWrittenFrames == 4_200 && snapshot.OverlapDroppedFrames == 120, "late-overlap and written-frame accounting");
        Require(snapshot.PumpGeneratedSilenceFrames == 960 && snapshot.PacketGapSilenceFrames == 0 &&
            snapshot.FinalizationSilenceFrames == 1_920, "missing-packet and stop-finalization silence accounting");
        Require(snapshot.DataDiscontinuities == 1 && snapshot.DevicePositionMismatches == 1, "WASAPI packet flags and device positions");
        Require(snapshot.TimestampFallbackPackets == 1 && snapshot.TimestampErrorPackets == 1 &&
            snapshot.Buckets.Any(bucket => bucket.TimestampFallbackPackets == 1 && bucket.TimestampErrorPackets == 1), "total and per-bucket timestamp error and fallback counts");
        Require(snapshot.ArrivalLatencyMinMs == -3.5 && snapshot.ArrivalLatencyMaxMs == 4.25, "QPC-to-arrival min/max");
        Require(snapshot.LastPacketElapsedMs == 22_000, "last packet arrival time");
        Require(snapshot.RetainedBucketCount <= 3 && snapshot.Buckets.Any(bucket => bucket.Index == 4), "bounded recent time buckets");
        Require(snapshot.DroppedBuckets == 1, "retention eviction count");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(snapshot));
        Require(json.RootElement.GetProperty("PacketCount").GetInt64() == 3 &&
            json.RootElement.GetProperty("Buckets").GetArrayLength() == 3, "sidecar JSON fields are serializable and retained");
        var positionDiagnostics = new AudioPacketDiagnostics(start, frequency, bucketSeconds: 10, maximumBuckets: 3);
        positionDiagnostics.Packet(start + 1_000_000, 480, false, 0, false, false, false, false,
            qpcPositionResidualMs: 0.25, deviceClockDriftPpm: 100);
        positionDiagnostics.Packet(start + 2_000_000, 480, false, 0, false, false, true, true,
            devicePositionReset: true, qpcPositionResidualMs: -0.5, deviceClockDriftPpm: -50);
        positionDiagnostics.PauseBoundaryMapped(start + 2_500_000, 4);
        var positionSnapshot = positionDiagnostics.Snapshot(start + 3_000_000);
        Require(positionSnapshot.DevicePositionResets == 1, "position resets are counted");
        Require(Math.Abs(positionSnapshot.QpcPositionResidualAbsMaxMs - 0.5) < 1e-9, "absolute QPC residual maximum");
        Require(positionSnapshot.DeviceClockDriftPpmLatest == -50, "latest device clock drift");
        Require(positionSnapshot.PauseBoundaryCount == 1 && positionSnapshot.PauseBoundaryNominalOffsetMaxFrames == 4 &&
                positionSnapshot.Buckets.Any(bucket => bucket.PauseBoundaryCount == 1 && bucket.PauseBoundaryNominalOffsetMaxFrames == 4),
            "pause boundary offset from nominal Stopwatch time is visible in total and time bucket");
        Require(positionSnapshot.Buckets.Any(bucket => bucket.DevicePositionResets == 1 &&
                    bucket.DeviceClockDriftPpmMin == -50 && bucket.DeviceClockDriftPpmMax == 100),
            "per-bucket reset and device clock drift range");
        Console.WriteLine("Audio packet diagnostics pure test passed: silent endpoint packets, missing-packet fill, overlap drops, QPC/arrival range, discontinuity flags, last packet time, and ring bound.");
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException($"Audio packet diagnostics check failed: {label}.");
    }
}
