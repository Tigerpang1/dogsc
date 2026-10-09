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

internal readonly record struct AudioPacketSegment(int InputFrameOffset, int FrameCount, long OutputStartFrame);

internal static class AudioPacketTimeline
{
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
