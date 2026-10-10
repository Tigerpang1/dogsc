using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace FrameTrail.Capture;

internal readonly record struct AudioPacketTiming(long StartStopwatchTicks, bool UsedArrivalFallback, bool TimestampError, bool DataDiscontinuity, double ArrivalLatencyMilliseconds);

internal static class AudioPacketClock
{
    internal const long QpcUnitsPerSecond = 10_000_000;

    internal static long Qpc100NanosecondsToStopwatchTicks(long qpc100Nanoseconds, long stopwatchFrequency)
    {
        if (qpc100Nanoseconds < 0 || stopwatchFrequency <= 0) throw new ArgumentOutOfRangeException();
        return checked((long)((decimal)qpc100Nanoseconds * stopwatchFrequency / QpcUnitsPerSecond));
    }

    internal static AudioPacketTiming Resolve(long qpcPosition100Nanoseconds, long arrivalStopwatchTicks,
        int frameCount, int sampleRate, AudioClientBufferFlags flags, long stopwatchFrequency)
    {
        if (frameCount < 0 || sampleRate <= 0 || stopwatchFrequency <= 0) throw new ArgumentOutOfRangeException();
        var timestampError = (flags & AudioClientBufferFlags.TimestampError) != 0;
        var discontinuity = (flags & AudioClientBufferFlags.DataDiscontinuity) != 0;
        var fallback = timestampError || qpcPosition100Nanoseconds <= 0;
        var packetDurationTicks = checked((long)((decimal)frameCount * stopwatchFrequency / sampleRate));
        var packetStartTicks = fallback
            ? Math.Max(0, arrivalStopwatchTicks - packetDurationTicks)
            : Qpc100NanosecondsToStopwatchTicks(qpcPosition100Nanoseconds, stopwatchFrequency);
        var arrivalLatency = fallback ? double.NaN : (arrivalStopwatchTicks - packetStartTicks - packetDurationTicks) * 1000d / stopwatchFrequency;
        return new AudioPacketTiming(packetStartTicks, fallback, timestampError, discontinuity, arrivalLatency);
    }
}

internal readonly record struct AudioPacketPlacement(long StartFrame, AudioPacketTiming Timing,
    bool DevicePositionMismatch, bool DevicePositionReset, bool ClockReanchored,
    long ClockBoundaryStopwatchTicks, double? QpcResidualMilliseconds, double? DeviceClockDriftPpm,
    string DevicePositionMode);

/// <summary>
/// Uses IAudioCaptureClient.GetBuffer's stream-relative frame position for ordinary
/// packets. If that position persistently disagrees with packet frame counts, it
/// switches to QPC placement; IAudioClock frequency is diagnostic-only because its
/// documented units are paired with IAudioClock.GetPosition, not GetBuffer.
/// </summary>
internal sealed class AudioPacketPositionMapper(long audioStartTicks, long sampleRate, long stopwatchFrequency)
{
    private long _nonmatchingPositionPackets;
    private bool _hasPosition;
    private long _anchorDevicePosition;
    private long _anchorFrame;
    private long _expectedDevicePosition;
    private long _logicalEndFrame;
    private long _initialQpcTicks;
    private long _initialFrame;
    private long _driftAnchorQpcTicks;
    private long _driftAnchorFrame;
    private long _latestQpcTicks;
    private long _latestFrame;
    private long _currentAnchorQpcTicks;
    private long _currentAnchorFrame;
    private bool _hasCurrentAnchor;
    private long _lastClockBoundaryTicks;
    private bool _hasClockBoundary;
    private bool _hasQpcAnchor;
    private double _lastDriftPpm;
    private double _maxQpcResidualMilliseconds;
    private long _positionResetCount;
    private bool _devicePositionTrusted;
    private bool _devicePositionUnavailable;
    private long _lastObservedDevicePosition;
    private int _lastObservedFrameCount;
    private long _repeatedPositionPackets;
    private long _lastPacketClockTicks;
    private int _lastPacketFrameCount;
    private bool _hasLastPacketClock;
    private bool _lastPacketClockReliable;

    internal long PositionResetCount => _positionResetCount;
    internal string DevicePositionMode => _devicePositionUnavailable ? "qpc-fallback" : _devicePositionTrusted ? "device-position" : "undetermined";
    internal double? DeviceClockDriftPpm => HasMeasuredDrift ? _lastDriftPpm : null;
    internal double MaxQpcResidualMilliseconds => _maxQpcResidualMilliseconds;

    internal AudioPacketPlacement Map(long devicePosition, int frameCount, long qpcPosition100Nanoseconds,
        long arrivalStopwatchTicks, AudioClientBufferFlags flags)
    {
        var timing = AudioPacketClock.Resolve(qpcPosition100Nanoseconds, arrivalStopwatchTicks, frameCount,
            checked((int)sampleRate), flags, stopwatchFrequency);
        var hasReliableQpc = qpcPosition100Nanoseconds > 0 && !timing.TimestampError;
        var qpcTicks = hasReliableQpc
            ? AudioPacketClock.Qpc100NanosecondsToStopwatchTicks(qpcPosition100Nanoseconds, stopwatchFrequency)
            : timing.StartStopwatchTicks;
        var qpcNominalFrame = TicksToFrame(qpcTicks - audioStartTicks);
        var qpcFrame = StopwatchTicksToSourceFrame(qpcTicks);
        // TIMESTAMP_ERROR makes the time correlation uncertain, but does not itself
        // announce a stream-position discontinuity. Keep a contiguous device position;
        // DATA_DISCONTINUITY is the flag that invalidates correlation with the prior packet.
        var discontinuity = timing.DataDiscontinuity;
        var rawMismatch = _hasPosition && !IsNearExpectedPosition(devicePosition, _expectedDevicePosition);
        var reset = false;
        var clockReanchored = false;
        long startFrame;

        if (!_hasPosition)
        {
            startFrame = qpcFrame;
            _hasPosition = true;
            _anchorDevicePosition = devicePosition;
            _anchorFrame = startFrame;
            _initialFrame = startFrame;
            _initialQpcTicks = qpcTicks;
            SetCurrentClockAnchor(qpcTicks, startFrame);
            _lastObservedDevicePosition = devicePosition;
            _lastObservedFrameCount = frameCount;
            _lastPacketClockTicks = qpcTicks;
            _lastPacketFrameCount = frameCount;
            _hasLastPacketClock = true;
            _lastPacketClockReliable = hasReliableQpc;
            if (hasReliableQpc)
            {
                _hasQpcAnchor = true;
                SetDriftAnchor(qpcTicks, startFrame);
            }
        }
        else if (_devicePositionUnavailable)
        {
            startFrame = MapWithoutDevicePosition(qpcTicks, discontinuity, hasReliableQpc, out clockReanchored);
            if (clockReanchored)
            {
                SetCurrentClockAnchor(qpcTicks, startFrame);
                ResetDriftAnchor(qpcTicks, startFrame, hasReliableQpc);
            }
        }
        else if (!_devicePositionTrusted)
        {
            var expectedStep = _lastObservedFrameCount;
            var expected = checked(_lastObservedDevicePosition + expectedStep);
            var consecutive = IsNearExpectedPosition(devicePosition, expected);
            if (consecutive)
            {
                _devicePositionTrusted = true;
                _anchorDevicePosition = devicePosition;
                _anchorFrame = discontinuity ? Math.Max(_logicalEndFrame, qpcFrame) : _logicalEndFrame;
                startFrame = _anchorFrame;
                if (discontinuity)
                {
                    SetCurrentClockAnchor(qpcTicks, startFrame);
                    ResetDriftAnchor(qpcTicks, startFrame, hasReliableQpc);
                    clockReanchored = true;
                }
            }
            else
            {
                if (Math.Abs(devicePosition - _lastObservedDevicePosition) <= 1 && frameCount > 0 && qpcTicks > _lastPacketClockTicks)
                    _repeatedPositionPackets++;
                else
                    _repeatedPositionPackets = 0;
                _nonmatchingPositionPackets++;

                // Some capture endpoints return a constant stream position without
                // setting TIMESTAMP_ERROR. Do not treat every such packet as a reset.
                // Require a sustained repeat before declaring the position unavailable.
                if (_repeatedPositionPackets >= 8 || _nonmatchingPositionPackets >= 8)
                {
                    _devicePositionUnavailable = true;
                    startFrame = MapWithoutDevicePosition(qpcTicks, discontinuity, hasReliableQpc, out clockReanchored);
                    if (clockReanchored)
                    {
                        SetCurrentClockAnchor(qpcTicks, startFrame);
                        ResetDriftAnchor(qpcTicks, startFrame, hasReliableQpc);
                    }
                }
                else
                {
                    startFrame = MapWithoutDevicePosition(qpcTicks, discontinuity, hasReliableQpc, out clockReanchored);
                    if (clockReanchored)
                    {
                        SetCurrentClockAnchor(qpcTicks, startFrame);
                        ResetDriftAnchor(qpcTicks, startFrame, hasReliableQpc);
                    }
                }
            }
            _lastObservedDevicePosition = devicePosition;
            _lastObservedFrameCount = frameCount;
        }
        else if (discontinuity)
        {
            // The API explicitly marks this packet's position/time correlation as unreliable.
            // Re-anchor this boundary from QPC (or arrival fallback), but keep a backwards
            // timestamp from creating a duplicate/overlapping output segment.
            startFrame = Math.Max(_logicalEndFrame, qpcFrame);
            Reanchor(devicePosition, startFrame);
            SetCurrentClockAnchor(qpcTicks, startFrame);
            ResetDriftAnchor(qpcTicks, startFrame, hasReliableQpc);
            clockReanchored = true;
        }
        else if (devicePosition < _expectedDevicePosition - 1)
        {
            // A stream-relative position reset is different from an ordinary packet jitter.
            // Preserve monotonic output and use QPC to retain any real elapsed-time gap.
            reset = true;
            _positionResetCount++;
            startFrame = Math.Max(_logicalEndFrame, qpcFrame);
            Reanchor(devicePosition, startFrame);
            SetCurrentClockAnchor(qpcTicks, startFrame);
            ResetDriftAnchor(qpcTicks, startFrame, hasReliableQpc);
            clockReanchored = true;
        }
        else if (devicePosition > _expectedDevicePosition + 1)
        {
            // GetBuffer documents devicePosition as the first packet frame's position
            // from stream start, so a forward jump is a real missing-frame interval.
            var missingFrames = devicePosition - _expectedDevicePosition;
            startFrame = checked(_logicalEndFrame + missingFrames);
        }
        else startFrame = _logicalEndFrame;

        // Keep packet diagnostics on the raw QPC/arrival timestamp, but do not let a
        // reset move the clock epoch behind the end boundary of samples already placed.
        // The previous packet's end is the earliest valid time for this packet's start.
        var clockBoundaryTicks = clockReanchored && _hasClockBoundary
            ? Math.Max(qpcTicks, _lastClockBoundaryTicks)
            : qpcTicks;
        if (clockReanchored && clockBoundaryTicks != qpcTicks)
        {
            SetCurrentClockAnchor(clockBoundaryTicks, startFrame);
            ResetDriftAnchor(clockBoundaryTicks, startFrame, hasReliableQpc);
        }

        if (hasReliableQpc)
        {
            var mappedFrame = _devicePositionUnavailable || !_devicePositionTrusted
                ? startFrame
                : checked(_anchorFrame + (devicePosition - _anchorDevicePosition));
            var residualFrames = qpcNominalFrame - mappedFrame;
            var residualMs = residualFrames * 1000d / sampleRate;
            _maxQpcResidualMilliseconds = Math.Max(_maxQpcResidualMilliseconds, Math.Abs(residualMs));
            if (!_hasQpcAnchor)
            {
                _hasQpcAnchor = true;
                SetDriftAnchor(qpcTicks, startFrame);
            }
            if (qpcTicks > _driftAnchorQpcTicks && startFrame >= _driftAnchorFrame)
            {
                var qpcElapsed = (double)(qpcTicks - _driftAnchorQpcTicks) / stopwatchFrequency;
                var deviceElapsed = (double)(startFrame - _driftAnchorFrame) / sampleRate;
                _lastDriftPpm = (deviceElapsed / qpcElapsed - 1d) * 1_000_000d;
                _latestQpcTicks = qpcTicks;
                _latestFrame = startFrame;
            }
        }

        _logicalEndFrame = checked(startFrame + frameCount);
        // Calculate the expected unit position from the epoch anchor instead of
        // rounding every packet independently; fractional conversions must not
        // accumulate into false gaps during long captures.
        _expectedDevicePosition = checked(_anchorDevicePosition + Math.Max(0, _logicalEndFrame - _anchorFrame));
        _lastPacketClockTicks = qpcTicks;
        _lastPacketFrameCount = frameCount;
        _hasLastPacketClock = true;
        _lastPacketClockReliable = hasReliableQpc;
        var packetDurationTicks = checked((long)((decimal)frameCount * stopwatchFrequency / sampleRate));
        var packetEndTicks = checked(clockBoundaryTicks + packetDurationTicks);
        _lastClockBoundaryTicks = _hasClockBoundary ? Math.Max(_lastClockBoundaryTicks, packetEndTicks) : packetEndTicks;
        _hasClockBoundary = true;
        return new AudioPacketPlacement(startFrame, timing, rawMismatch, reset, clockReanchored,
            clockBoundaryTicks,
            hasReliableQpc ? (qpcNominalFrame - startFrame) * 1000d / sampleRate : null,
            DeviceClockDriftPpm, DevicePositionMode);
    }

    private long MapWithoutDevicePosition(long clockTicks, bool discontinuity, bool hasReliableQpc,
        out bool reanchored)
    {
        reanchored = false;
        if (discontinuity)
        {
            reanchored = true;
            return Math.Max(_logicalEndFrame, StopwatchTicksToSourceFrame(clockTicks));
        }

        // Arrival fallback timestamps include callback scheduling delay. Only
        // infer missing audio frames from an interval bounded by two reliable
        // QPC packet timestamps; a discontinuity flag remains authoritative above.
        if (hasReliableQpc && _hasLastPacketClock && _lastPacketClockReliable && clockTicks > _lastPacketClockTicks)
        {
            var deltaTicks = clockTicks - _lastPacketClockTicks;
            var expectedTicks = checked((long)((decimal)_lastPacketFrameCount * stopwatchFrequency / sampleRate));
            var toleranceTicks = Math.Max(stopwatchFrequency / 333, expectedTicks / 2); // 3 ms or half a packet.
            if (deltaTicks > expectedTicks + toleranceTicks)
            {
                var missingTicks = deltaTicks - expectedTicks;
                var missingFrames = checked((long)Math.Round((double)missingTicks * sampleRate / stopwatchFrequency));
                if (missingFrames > 0)
                {
                    reanchored = true;
                    return checked(_logicalEndFrame + missingFrames);
                }
            }
        }

        // QPC/arrival jitter below the gap threshold does not disturb contiguous
        // samples. This is also the safe path for endpoints with unusable positions.
        return _logicalEndFrame;
    }

    private static bool IsNearExpectedPosition(long actual, long expected) => Math.Abs(actual - expected) <= 1;

    /// <summary>Maps a Stopwatch boundary into the same source-frame domain as captured packets.</summary>
    internal long StopwatchTicksToSourceFrame(long stopwatchTicks)
    {
        if (!_hasPosition) return TicksToFrame(stopwatchTicks - audioStartTicks);
        if (HasMeasuredDrift && _latestFrame >= _driftAnchorFrame)
        {
            var framesPerTick = (double)(_latestFrame - _driftAnchorFrame) /
                                (_latestQpcTicks - _driftAnchorQpcTicks);
            return checked(_driftAnchorFrame + (long)Math.Round((stopwatchTicks - _driftAnchorQpcTicks) * framesPerTick));
        }
        if (_hasCurrentAnchor)
            return checked(_currentAnchorFrame + TicksToFrame(stopwatchTicks - _currentAnchorQpcTicks));
        return checked(_initialFrame + TicksToFrame(stopwatchTicks - _initialQpcTicks));
    }

    private void SetDriftAnchor(long qpcTicks, long sourceFrame)
    {
        _driftAnchorQpcTicks = qpcTicks;
        _driftAnchorFrame = sourceFrame;
        _latestQpcTicks = qpcTicks;
        _latestFrame = sourceFrame;
        _lastDriftPpm = 0;
    }

    private void ResetDriftAnchor(long qpcTicks, long sourceFrame, bool hasReliableQpc)
    {
        // A stream-position reset starts a new local clock epoch. Never extrapolate
        // the previous epoch's measured rate through this new QPC/device correlation.
        _hasQpcAnchor = hasReliableQpc;
        SetDriftAnchor(qpcTicks, sourceFrame);
    }

    private void SetCurrentClockAnchor(long qpcTicks, long sourceFrame)
    {
        _currentAnchorQpcTicks = qpcTicks;
        _currentAnchorFrame = sourceFrame;
        _hasCurrentAnchor = true;
    }

    private bool HasMeasuredDrift => _hasQpcAnchor && _latestQpcTicks - _driftAnchorQpcTicks >= stopwatchFrequency;

    private void Reanchor(long devicePosition, long sourceFrame)
    {
        _anchorDevicePosition = devicePosition;
        _anchorFrame = sourceFrame;
    }

    private long TicksToFrame(long ticks) => checked((long)((decimal)ticks * sampleRate / stopwatchFrequency));
}

internal readonly record struct AudioPacketSegment(int InputFrameOffset, int FrameCount, long OutputStartFrame);

internal static class AudioClockSegmentMath
{
    internal static CaptureAudioClockSegment? Create(long startTicks, long endTicks, long startFrame, long endFrame,
        int sampleRate, long stopwatchFrequency)
    {
        if (sampleRate <= 0 || stopwatchFrequency <= 0 || endTicks <= startTicks || endFrame <= startFrame)
            return null;
        var frameCount = endFrame - startFrame;
        var elapsedTicks = endTicks - startTicks;
        var sourceRate = (frameCount / (double)sampleRate) / (elapsedTicks / (double)stopwatchFrequency);
        if (!double.IsFinite(sourceRate) || sourceRate <= 0) return null;
        return new CaptureAudioClockSegment(startTicks, endTicks, startFrame, endFrame, sampleRate, sourceRate);
    }
}

internal static class AudioPacketTimeline
{
    internal static long SourceFrameToActiveOutputFrame(long sourceFrame,
        IReadOnlyList<(long StartFrame, long EndFrame)> pausedRanges)
    {
        ArgumentNullException.ThrowIfNull(pausedRanges);
        var boundary = Math.Max(0, sourceFrame);
        long removedBefore = 0;
        foreach (var pause in pausedRanges.OrderBy(range => range.StartFrame))
        {
            var start = Math.Max(0, pause.StartFrame);
            var end = Math.Max(start, pause.EndFrame);
            if (start >= boundary) break;
            removedBefore = checked(removedBefore + Math.Max(0, Math.Min(end, boundary) - start));
        }
        return Math.Max(0, boundary - removedBefore);
    }

    internal static IReadOnlyList<AudioPacketSegment> MapPacket(long sourceStartFrame, int frameCount,
        IReadOnlyList<(long StartFrame, long EndFrame)> pausedRanges,
        long sourceEndFrame = long.MaxValue, long outputEndFrame = long.MaxValue)
    {
        if (frameCount < 0 || sourceEndFrame < 0 || outputEndFrame < 0) throw new ArgumentOutOfRangeException();
        var sourceEnd = Math.Min(checked(sourceStartFrame + frameCount), sourceEndFrame);
        var result = new List<AudioPacketSegment>();
        var cursor = Math.Max(0, sourceStartFrame);
        var initialOffset = checked((int)(cursor - sourceStartFrame));
        if (sourceEnd <= cursor) return result;
        foreach (var pause in pausedRanges.OrderBy(x => x.StartFrame))
        {
            var pauseStart = Math.Max(0, pause.StartFrame);
            var pauseEnd = Math.Max(pauseStart, pause.EndFrame);
            if (pauseStart >= sourceEnd) break;
            if (pauseStart > cursor)
            {
                var keptEnd = Math.Min(pauseStart, sourceEnd);
                if (keptEnd > cursor)
                    AddMappedSegment(result, sourceStartFrame, initialOffset, cursor, keptEnd, pausedRanges, outputEndFrame);
                cursor = keptEnd;
            }
            var droppedStart = Math.Max(cursor, pauseStart);
            var droppedEnd = Math.Min(sourceEnd, pauseEnd);
            if (droppedEnd > droppedStart)
            {
                cursor = droppedEnd;
            }
            if (cursor >= sourceEnd) break;
        }
        if (cursor < sourceEnd)
            AddMappedSegment(result, sourceStartFrame, initialOffset, cursor, sourceEnd, pausedRanges, outputEndFrame);
        return result;
    }

    private static void AddMappedSegment(List<AudioPacketSegment> result, long sourceStartFrame, int initialOffset,
        long keptStart, long keptEnd, IReadOnlyList<(long StartFrame, long EndFrame)> pausedRanges, long outputEndFrame)
    {
        long removedBefore = 0;
        foreach (var pause in pausedRanges)
        {
            var start = Math.Max(0, pause.StartFrame);
            var end = Math.Max(start, pause.EndFrame);
            if (start >= keptStart) continue;
            removedBefore = checked(removedBefore + Math.Max(0, Math.Min(end, keptStart) - start));
        }
        var outputStart = keptStart - removedBefore;
        var keptOutputEnd = Math.Min(outputStart + (keptEnd - keptStart), outputEndFrame);
        if (keptOutputEnd <= outputStart) return;
        var inputOffset = checked(initialOffset + (int)(keptStart - Math.Max(0, sourceStartFrame)));
        result.Add(new AudioPacketSegment(inputOffset, checked((int)(keptOutputEnd - outputStart)), outputStart));
    }
}
