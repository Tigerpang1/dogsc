using System.Text.Json;
using NAudio.CoreAudioApi;

namespace FrameTrail.Capture;

/// <summary>Bounded, content-free counters for diagnosing packet loss and timeline silence.</summary>
internal sealed class AudioPacketDiagnostics(long startTicks, long stopwatchFrequency, int bucketSeconds = 10, int maximumBuckets = 2160)
{
    private readonly object _gate = new();
    private readonly SortedDictionary<long, MutableBucket> _buckets = new();
    private readonly List<AudioPacketPositionSample> _firstPositionSamples = [];
    private readonly Queue<AudioPacketPositionSample> _lastPositionSamples = new();
    private long _packetCount, _packetFrames, _silentFrames, _silentSourceFramesWritten, _packetSourceFramesWritten, _actuallyWrittenFrames, _overlapDroppedFrames;
    private long _pumpSilenceFrames, _packetGapSilenceFrames, _finalizationSilenceFrames;
    private long _timestampFallbackPackets, _timestampErrorPackets, _discontinuities, _devicePositionMismatches, _droppedBuckets;
    private long _devicePositionResets;
    private long _pauseBoundaryCount;
    private double _qpcPositionResidualAbsMaxMs;
    private long _pauseBoundaryNominalOffsetMaxFrames;
    private double? _deviceClockDriftPpmLatest;
    private double? _predictedClockDifferenceMs;
    private string? _devicePositionMode;
    private long? _audioClockFrequency;
    private string? _audioClockFrequencyError;
    private int? _mixSampleRate, _mixBlockAlign, _mixChannels, _mixBitsPerSample;
    private long? _lastPacketTicks;
    private double _latencyMin = double.PositiveInfinity, _latencyMax = double.NegativeInfinity;

    internal void Packet(long arrivalTicks, int frames, bool silent, double arrivalLatencyMs, bool timestampFallback,
        bool timestampError, bool discontinuity, bool devicePositionMismatch, bool devicePositionReset = false,
        double? qpcPositionResidualMs = null, double? deviceClockDriftPpm = null)
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
            if (devicePositionReset) { bucket.DevicePositionResets++; _devicePositionResets++; }
            if (qpcPositionResidualMs is double residual && double.IsFinite(residual))
            {
                bucket.QpcPositionResidualAbsMaxMs = Math.Max(bucket.QpcPositionResidualAbsMaxMs, Math.Abs(residual));
                _qpcPositionResidualAbsMaxMs = Math.Max(_qpcPositionResidualAbsMaxMs, Math.Abs(residual));
            }
            if (deviceClockDriftPpm is double drift && double.IsFinite(drift))
            {
                bucket.DeviceClockDriftPpmMin = Math.Min(bucket.DeviceClockDriftPpmMin, drift);
                bucket.DeviceClockDriftPpmMax = Math.Max(bucket.DeviceClockDriftPpmMax, drift);
                _deviceClockDriftPpmLatest = drift;
            }
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
    internal void PauseBoundaryMapped(long eventTicks, long absoluteNominalOffsetFrames)
    {
        lock (_gate)
        {
            var bucket = GetBucket(eventTicks);
            bucket.PauseBoundaryCount++;
            bucket.PauseBoundaryNominalOffsetMaxFrames = Math.Max(bucket.PauseBoundaryNominalOffsetMaxFrames, absoluteNominalOffsetFrames);
            _pauseBoundaryCount++;
            _pauseBoundaryNominalOffsetMaxFrames = Math.Max(_pauseBoundaryNominalOffsetMaxFrames, absoluteNominalOffsetFrames);
        }
    }
    internal void SetClockEstimate(double driftPpm, double predictedDifferenceMs)
    {
        lock (_gate)
        {
            _deviceClockDriftPpmLatest = double.IsFinite(driftPpm) ? driftPpm : null;
            _predictedClockDifferenceMs = double.IsFinite(predictedDifferenceMs) ? predictedDifferenceMs : null;
        }
    }
    internal void SetDevicePositionMode(string? mode)
    {
        lock (_gate) _devicePositionMode = mode;
    }
    internal void SetAudioClockFrequency(long? frequency, string? error)
    {
        lock (_gate)
        {
            _audioClockFrequency = frequency;
            _audioClockFrequencyError = error;
        }
    }
    internal void SetStreamFormat(int sampleRate, int blockAlign, int channels, int bitsPerSample)
    {
        lock (_gate)
        {
            _mixSampleRate = sampleRate > 0 ? sampleRate : null;
            _mixBlockAlign = blockAlign > 0 ? blockAlign : null;
            _mixChannels = channels > 0 ? channels : null;
            _mixBitsPerSample = bitsPerSample > 0 ? bitsPerSample : null;
        }
    }
    internal void PositionSample(long packetIndex, long devicePosition, int frameCount, long qpcPosition100Nanoseconds,
        long arrivalTicks, AudioClientBufferFlags flags, string mode)
    {
        lock (_gate)
        {
            var sample = new AudioPacketPositionSample(packetIndex, devicePosition, frameCount,
                qpcPosition100Nanoseconds, arrivalTicks, (int)flags, mode);
            if (_firstPositionSamples.Count < 32)
                _firstPositionSamples.Add(sample);
            else
            {
                _lastPositionSamples.Enqueue(sample);
                while (_lastPositionSamples.Count > 16) _lastPositionSamples.Dequeue();
            }
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
            var observedPositionRate = EstimatePositionUnitsPerSecond(_firstPositionSamples);
            var observedPositionRateToMixRate = observedPositionRate is > 0 && _mixSampleRate is > 0
                ? observedPositionRate.Value / _mixSampleRate.Value : (double?)null;
            var audioClockToObservedRate = observedPositionRate is > 0 && _audioClockFrequency is > 0
                ? _audioClockFrequency.Value / observedPositionRate.Value : (double?)null;
            var audioClockBlockFrameToObservedRate = observedPositionRate is > 0 && _audioClockFrequency is > 0 && _mixBlockAlign is > 0
                ? _audioClockFrequency.Value / (double)_mixBlockAlign.Value / observedPositionRate.Value : (double?)null;
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
                DevicePositionResets: _devicePositionResets, QpcPositionResidualAbsMaxMs: _qpcPositionResidualAbsMaxMs,
                PauseBoundaryCount: _pauseBoundaryCount, PauseBoundaryNominalOffsetMaxFrames: _pauseBoundaryNominalOffsetMaxFrames,
                DeviceClockDriftPpmLatest: _deviceClockDriftPpmLatest, PredictedClockDifferenceMs: _predictedClockDifferenceMs,
                DevicePositionMode: _devicePositionMode,
                AudioClockFrequency: _audioClockFrequency,
                AudioClockFrequencyError: _audioClockFrequencyError,
                MixSampleRate: _mixSampleRate,
                MixBlockAlign: _mixBlockAlign,
                MixChannels: _mixChannels,
                MixBitsPerSample: _mixBitsPerSample,
                ObservedDevicePositionUnitsPerSecond: observedPositionRate,
                ObservedPositionRateToMixRateRatio: observedPositionRateToMixRate,
                AudioClockFrequencyToObservedPositionRateRatio: audioClockToObservedRate,
                AudioClockBlockAlignAdjustedFrequencyToObservedPositionRateRatio: audioClockBlockFrameToObservedRate,
                ArrivalLatencyMinMs: double.IsFinite(_latencyMin) ? _latencyMin : null,
                ArrivalLatencyMaxMs: double.IsFinite(_latencyMax) ? _latencyMax : null,
                LastPacketElapsedMs: _lastPacketTicks is long last ? (last - startTicks) * 1000d / stopwatchFrequency : null,
                ObservedElapsedMs: (nowTicks - startTicks) * 1000d / stopwatchFrequency, Buckets: buckets,
                PositionSamples: _firstPositionSamples.Concat(_lastPositionSamples).ToArray());
        }
    }

    private static double? EstimatePositionUnitsPerSecond(IReadOnlyList<AudioPacketPositionSample> samples)
    {
        var ordered = samples.Where(sample => sample.QpcPosition100Nanoseconds > 0)
            .OrderBy(sample => sample.PacketIndex).ToArray();
        if (ordered.Length < 8) return null;
        if (ordered.Zip(ordered.Skip(1), (left, right) =>
                right.QpcPosition100Nanoseconds > left.QpcPosition100Nanoseconds &&
                right.DevicePosition >= left.DevicePosition).Any(valid => !valid))
            return null;

        var originQpc = ordered[0].QpcPosition100Nanoseconds;
        var originPosition = ordered[0].DevicePosition;
        var xs = ordered.Select(sample => (sample.QpcPosition100Nanoseconds - originQpc) / 10_000_000d).ToArray();
        var ys = ordered.Select(sample => (double)(sample.DevicePosition - originPosition)).ToArray();
        if (xs[^1] < 0.05) return null;
        var meanX = xs.Average();
        var meanY = ys.Average();
        var covariance = 0d;
        var variance = 0d;
        for (var index = 0; index < xs.Length; index++)
        {
            var dx = xs[index] - meanX;
            covariance += dx * (ys[index] - meanY);
            variance += dx * dx;
        }
        if (variance <= 0) return null;
        var rate = covariance / variance;
        return double.IsFinite(rate) && rate > 0 ? rate : null;
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
        bucket.Discontinuities, bucket.DevicePositionMismatches, bucket.DevicePositionResets,
        bucket.PauseBoundaryCount, bucket.PauseBoundaryNominalOffsetMaxFrames,
        bucket.QpcPositionResidualAbsMaxMs,
        double.IsFinite(bucket.DeviceClockDriftPpmMin) ? bucket.DeviceClockDriftPpmMin : null,
        double.IsFinite(bucket.DeviceClockDriftPpmMax) ? bucket.DeviceClockDriftPpmMax : null,
        bucket.SilentSourceFramesWritten,
        bucket.PacketSourceFramesWritten, bucket.ActuallyWrittenFrames,
        bucket.OverlapDroppedFrames, bucket.PumpSilenceFrames, bucket.PacketGapSilenceFrames, bucket.FinalizationSilenceFrames);

    private sealed class MutableBucket(long index, double startElapsedMs)
    {
        internal long Index { get; } = index;
        internal double StartElapsedMs { get; } = startElapsedMs;
        internal long PacketCount, PacketFrames, SilentFrames, TimestampFallbackPackets, TimestampErrorPackets, Discontinuities, DevicePositionMismatches;
        internal long DevicePositionResets;
        internal long PauseBoundaryCount, PauseBoundaryNominalOffsetMaxFrames;
        internal long SilentSourceFramesWritten, PacketSourceFramesWritten, ActuallyWrittenFrames, OverlapDroppedFrames;
        internal long PumpSilenceFrames, PacketGapSilenceFrames, FinalizationSilenceFrames;
        internal double LatencyMin = double.PositiveInfinity, LatencyMax = double.NegativeInfinity;
        internal double QpcPositionResidualAbsMaxMs;
        internal double DeviceClockDriftPpmMin = double.PositiveInfinity, DeviceClockDriftPpmMax = double.NegativeInfinity;
    }
}

internal sealed record AudioPacketDiagnosticsSnapshot(int BucketSeconds, int MaximumRetainedBuckets, int RetainedBucketCount,
    long DroppedBuckets,
    long PacketCount, long PacketFrames, long SilentFlagFrames, long SilentSourceFramesWritten,
    long PacketSourceFramesWritten, long ActuallyWrittenFrames,
    long OverlapDroppedFrames, long PumpGeneratedSilenceFrames, long PacketGapSilenceFrames,
    long FinalizationSilenceFrames, long TimestampFallbackPackets, long TimestampErrorPackets, long DataDiscontinuities,
    long DevicePositionMismatches, long DevicePositionResets, double QpcPositionResidualAbsMaxMs,
    long PauseBoundaryCount, long PauseBoundaryNominalOffsetMaxFrames, double? DeviceClockDriftPpmLatest,
    double? PredictedClockDifferenceMs, string? DevicePositionMode,
    long? AudioClockFrequency, string? AudioClockFrequencyError,
    int? MixSampleRate, int? MixBlockAlign, int? MixChannels, int? MixBitsPerSample,
    double? ObservedDevicePositionUnitsPerSecond, double? ObservedPositionRateToMixRateRatio,
    double? AudioClockFrequencyToObservedPositionRateRatio,
    double? AudioClockBlockAlignAdjustedFrequencyToObservedPositionRateRatio,
    double? ArrivalLatencyMinMs, double? ArrivalLatencyMaxMs,
    double? LastPacketElapsedMs, double ObservedElapsedMs, IReadOnlyList<AudioPacketDiagnosticBucket> Buckets,
    IReadOnlyList<AudioPacketPositionSample> PositionSamples);

internal sealed record AudioPacketPositionSample(long PacketIndex, long DevicePosition, int FrameCount,
    long QpcPosition100Nanoseconds, long ArrivalStopwatchTicks, int Flags, string MapperMode);

internal sealed record AudioPacketDiagnosticBucket(long Index, double StartElapsedMs, long PacketCount,
    long PacketFrames, long SilentFlagFrames, long TimestampFallbackPackets, long TimestampErrorPackets,
    double? ArrivalLatencyMinMs, double? ArrivalLatencyMaxMs,
    long DataDiscontinuities, long DevicePositionMismatches, long DevicePositionResets,
    long PauseBoundaryCount, long PauseBoundaryNominalOffsetMaxFrames,
    double QpcPositionResidualAbsMaxMs, double? DeviceClockDriftPpmMin, double? DeviceClockDriftPpmMax,
    long SilentSourceFramesWritten,
    long PacketSourceFramesWritten, long ActuallyWrittenFrames, long OverlapDroppedFrames,
    long PumpGeneratedSilenceFrames, long PacketGapSilenceFrames, long FinalizationSilenceFrames);

internal enum AudioSilenceSource { Pump, PacketGap, Finalization }
