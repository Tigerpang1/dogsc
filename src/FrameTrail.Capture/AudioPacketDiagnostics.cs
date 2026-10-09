using System.Text.Json;

namespace FrameTrail.Capture;

/// <summary>Bounded, content-free counters for diagnosing packet loss and timeline silence.</summary>
internal sealed class AudioPacketDiagnostics(long startTicks, long stopwatchFrequency, int bucketSeconds = 10, int maximumBuckets = 2160)
{
    private readonly object _gate = new();
    private readonly SortedDictionary<long, MutableBucket> _buckets = new();
    private long _packetCount, _packetFrames, _silentFrames, _silentSourceFramesWritten, _packetSourceFramesWritten, _actuallyWrittenFrames, _overlapDroppedFrames;
    private long _pumpSilenceFrames, _packetGapSilenceFrames, _finalizationSilenceFrames;
    private long _timestampFallbackPackets, _timestampErrorPackets, _discontinuities, _devicePositionMismatches, _droppedBuckets;
    private long? _lastPacketTicks;
    private double _latencyMin = double.PositiveInfinity, _latencyMax = double.NegativeInfinity;

    internal void Packet(long arrivalTicks, int frames, bool silent, double arrivalLatencyMs, bool timestampFallback,
        bool timestampError, bool discontinuity, bool devicePositionMismatch)
    {
        lock (_gate)
        {
            var bucket = GetBucket(arrivalTicks);
            bucket.PacketCount++; bucket.PacketFrames += frames;
            _packetCount++; _packetFrames += frames;
            if (silent) { bucket.SilentFrames += frames; _silentFrames += frames; }
            if (timestampFallback) { bucket.TimestampFallbackPackets++; _timestampFallbackPackets++; }
            if (timestampError) { bucket.TimestampErrorPackets++; _timestampErrorPackets++; }
            if (discontinuity) { bucket.Discontinuities++; _discontinuities++; }
            if (devicePositionMismatch) { bucket.DevicePositionMismatches++; _devicePositionMismatches++; }
            if (double.IsFinite(arrivalLatencyMs))
            {
                bucket.LatencyMin = Math.Min(bucket.LatencyMin, arrivalLatencyMs);
                bucket.LatencyMax = Math.Max(bucket.LatencyMax, arrivalLatencyMs);
                _latencyMin = Math.Min(_latencyMin, arrivalLatencyMs);
                _latencyMax = Math.Max(_latencyMax, arrivalLatencyMs);
            }
            _lastPacketTicks = _lastPacketTicks is long previous ? Math.Max(previous, arrivalTicks) : arrivalTicks;
        }
    }

    internal void Written(long eventTicks, long frames, bool endpointSilent)
    {
        if (frames <= 0) return;
        lock (_gate)
        {
            var bucket = GetBucket(eventTicks);
            bucket.PacketSourceFramesWritten += frames; bucket.ActuallyWrittenFrames += frames;
            _packetSourceFramesWritten += frames; _actuallyWrittenFrames += frames;
            if (endpointSilent) { bucket.SilentSourceFramesWritten += frames; _silentSourceFramesWritten += frames; }
        }
    }
    internal void OverlapDropped(long eventTicks, long frames) { if (frames <= 0) return; lock (_gate) { GetBucket(eventTicks).OverlapDroppedFrames += frames; _overlapDroppedFrames += frames; } }
    internal void SilenceWritten(long eventTicks, long frames, AudioSilenceSource source)
    {
        if (frames <= 0) return;
        lock (_gate)
        {
            var bucket = GetBucket(eventTicks);
            bucket.ActuallyWrittenFrames += frames; _actuallyWrittenFrames += frames;
            switch (source)
            {
                case AudioSilenceSource.Pump: bucket.PumpSilenceFrames += frames; _pumpSilenceFrames += frames; break;
                case AudioSilenceSource.PacketGap: bucket.PacketGapSilenceFrames += frames; _packetGapSilenceFrames += frames; break;
                case AudioSilenceSource.Finalization: bucket.FinalizationSilenceFrames += frames; _finalizationSilenceFrames += frames; break;
                default: throw new ArgumentOutOfRangeException(nameof(source));
            }
        }
    }

    internal AudioPacketDiagnosticsSnapshot Snapshot(long nowTicks)
    {
        lock (_gate)
        {
            var buckets = _buckets.Values.Select(ToImmutable).ToArray();
            return new AudioPacketDiagnosticsSnapshot(
                BucketSeconds: bucketSeconds, MaximumRetainedBuckets: maximumBuckets, RetainedBucketCount: buckets.Length,
                DroppedBuckets: _droppedBuckets, PacketCount: _packetCount,
                PacketFrames: _packetFrames, SilentFlagFrames: _silentFrames, SilentSourceFramesWritten: _silentSourceFramesWritten,
                PacketSourceFramesWritten: _packetSourceFramesWritten,
                ActuallyWrittenFrames: _actuallyWrittenFrames,
                OverlapDroppedFrames: _overlapDroppedFrames, PumpGeneratedSilenceFrames: _pumpSilenceFrames,
                PacketGapSilenceFrames: _packetGapSilenceFrames, FinalizationSilenceFrames: _finalizationSilenceFrames,
                TimestampFallbackPackets: _timestampFallbackPackets, TimestampErrorPackets: _timestampErrorPackets,
                DataDiscontinuities: _discontinuities, DevicePositionMismatches: _devicePositionMismatches,
                ArrivalLatencyMinMs: double.IsFinite(_latencyMin) ? _latencyMin : null,
                ArrivalLatencyMaxMs: double.IsFinite(_latencyMax) ? _latencyMax : null,
                LastPacketElapsedMs: _lastPacketTicks is long last ? (last - startTicks) * 1000d / stopwatchFrequency : null,
                ObservedElapsedMs: (nowTicks - startTicks) * 1000d / stopwatchFrequency, Buckets: buckets);
        }
    }

    internal async Task WriteJsonAsync(string path, long nowTicks, CancellationToken cancellationToken = default)
    {
        var snapshot = Snapshot(nowTicks);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, snapshot, new JsonSerializerOptions { WriteIndented = true }, cancellationToken).ConfigureAwait(false);
    }

    private MutableBucket GetBucket(long ticks)
    {
        var index = Math.Max(0, (long)((decimal)Math.Max(0, ticks - startTicks) / stopwatchFrequency / bucketSeconds));
        if (_buckets.TryGetValue(index, out var existing)) return existing;
        var created = new MutableBucket(index, index * bucketSeconds * 1000d);
        _buckets.Add(index, created);
        while (_buckets.Count > maximumBuckets) { _buckets.Remove(_buckets.Keys.First()); _droppedBuckets++; }
        return created;
    }

    private static AudioPacketDiagnosticBucket ToImmutable(MutableBucket bucket) => new(
        bucket.Index, bucket.StartElapsedMs, bucket.PacketCount, bucket.PacketFrames, bucket.SilentFrames,
        bucket.TimestampFallbackPackets, bucket.TimestampErrorPackets,
        bucket.LatencyMin is double min && double.IsFinite(min) ? min : null,
        bucket.LatencyMax is double max && double.IsFinite(max) ? max : null,
        bucket.Discontinuities, bucket.DevicePositionMismatches, bucket.SilentSourceFramesWritten,
        bucket.PacketSourceFramesWritten, bucket.ActuallyWrittenFrames,
        bucket.OverlapDroppedFrames, bucket.PumpSilenceFrames, bucket.PacketGapSilenceFrames, bucket.FinalizationSilenceFrames);

    private sealed class MutableBucket(long index, double startElapsedMs)
    {
        internal long Index { get; } = index;
        internal double StartElapsedMs { get; } = startElapsedMs;
        internal long PacketCount, PacketFrames, SilentFrames, TimestampFallbackPackets, TimestampErrorPackets, Discontinuities, DevicePositionMismatches;
        internal long SilentSourceFramesWritten, PacketSourceFramesWritten, ActuallyWrittenFrames, OverlapDroppedFrames;
        internal long PumpSilenceFrames, PacketGapSilenceFrames, FinalizationSilenceFrames;
        internal double LatencyMin = double.PositiveInfinity, LatencyMax = double.NegativeInfinity;
    }
}

internal sealed record AudioPacketDiagnosticsSnapshot(int BucketSeconds, int MaximumRetainedBuckets, int RetainedBucketCount,
    long DroppedBuckets,
    long PacketCount, long PacketFrames, long SilentFlagFrames, long SilentSourceFramesWritten,
    long PacketSourceFramesWritten, long ActuallyWrittenFrames,
    long OverlapDroppedFrames, long PumpGeneratedSilenceFrames, long PacketGapSilenceFrames,
    long FinalizationSilenceFrames, long TimestampFallbackPackets, long TimestampErrorPackets, long DataDiscontinuities,
    long DevicePositionMismatches, double? ArrivalLatencyMinMs, double? ArrivalLatencyMaxMs,
    double? LastPacketElapsedMs, double ObservedElapsedMs, IReadOnlyList<AudioPacketDiagnosticBucket> Buckets);

internal sealed record AudioPacketDiagnosticBucket(long Index, double StartElapsedMs, long PacketCount,
    long PacketFrames, long SilentFlagFrames, long TimestampFallbackPackets, long TimestampErrorPackets,
    double? ArrivalLatencyMinMs, double? ArrivalLatencyMaxMs,
    long DataDiscontinuities, long DevicePositionMismatches, long SilentSourceFramesWritten,
    long PacketSourceFramesWritten, long ActuallyWrittenFrames, long OverlapDroppedFrames,
    long PumpGeneratedSilenceFrames, long PacketGapSilenceFrames, long FinalizationSilenceFrames);

internal enum AudioSilenceSource { Pump, PacketGap, Finalization }
