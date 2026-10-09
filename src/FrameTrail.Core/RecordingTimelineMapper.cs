namespace FrameTrail;

/// <summary>A half-open interval on the monotonic capture clock.</summary>
public readonly record struct ClockInterval(long StartTicks, long EndTicks);

/// <summary>An audio source interval and its matching location on the screen timeline, in seconds.</summary>
public readonly record struct MappedAudioInterval(double SourceStart, double Duration, double TimelineStart);

/// <summary>Maps independently recorded audio through screen and audio pauses onto the screen timeline.</summary>
public static class RecordingTimelineMapper
{
    /// <param name="screenOriginTicks">Estimated first-screen-frame time; earlier screen data is excluded.</param>
    /// <param name="screenCapture">Screen recorder wall-clock start and stop ticks.</param>
    /// <param name="screenPauses">Wall-clock intervals during which the screen recorder was paused.</param>
    /// <param name="audioCapture">Audio recorder wall-clock start and stop ticks.</param>
    /// <param name="audioPauses">Wall-clock intervals during which this audio recorder was paused.</param>
    /// <param name="ticksFrequency">Monotonic clock ticks per second, such as Stopwatch.Frequency.</param>
    public static IReadOnlyList<MappedAudioInterval> MapAudio(
        long screenOriginTicks,
        ClockInterval screenCapture,
        IReadOnlyList<ClockInterval> screenPauses,
        ClockInterval audioCapture,
        IReadOnlyList<ClockInterval> audioPauses,
        long ticksFrequency)
    {
        ArgumentNullException.ThrowIfNull(screenPauses);
        ArgumentNullException.ThrowIfNull(audioPauses);
        if (ticksFrequency <= 0) throw new ArgumentOutOfRangeException(nameof(ticksFrequency), "时钟频率必须大于零。");
        ValidateCapture(screenCapture, nameof(screenCapture));
        ValidateCapture(audioCapture, nameof(audioCapture));

        var screenStart = Math.Max(screenCapture.StartTicks, screenOriginTicks);
        if (screenStart >= screenCapture.EndTicks || audioCapture.StartTicks >= audioCapture.EndTicks)
            return [];

        var screenActive = SubtractPauses(new(screenStart, screenCapture.EndTicks), screenPauses);
        var audioActive = SubtractPauses(audioCapture, audioPauses);
        if (screenActive.Count == 0 || audioActive.Count == 0) return [];

        var result = new List<MappedAudioInterval>();
        foreach (var screen in screenActive)
        foreach (var audio in audioActive)
        {
            var start = Math.Max(screen.StartTicks, audio.StartTicks);
            var end = Math.Min(screen.EndTicks, audio.EndTicks);
            if (start >= end) continue;
            var sourceTicks = ActiveTicksBefore(audioActive, start);
            var timelineTicks = ActiveTicksBefore(screenActive, start);
            result.Add(new MappedAudioInterval(
                (double)(sourceTicks / ticksFrequency),
                (double)(((decimal)end - start) / ticksFrequency),
                (double)(timelineTicks / ticksFrequency)));
        }

        return result.OrderBy(x => x.TimelineStart).ToArray();
    }

    private static void ValidateCapture(ClockInterval capture, string name)
    {
        if (capture.EndTicks < capture.StartTicks)
            throw new ArgumentOutOfRangeException(name, "捕获区间的结束时间不能早于开始时间。");
    }

    private static List<ClockInterval> SubtractPauses(ClockInterval capture, IReadOnlyList<ClockInterval> pauses)
    {
        if (capture.StartTicks >= capture.EndTicks) return [];
        var clipped = pauses
            .Where(p => p.EndTicks > p.StartTicks && p.EndTicks > capture.StartTicks && p.StartTicks < capture.EndTicks)
            .Select(p => new ClockInterval(Math.Max(p.StartTicks, capture.StartTicks), Math.Min(p.EndTicks, capture.EndTicks)))
            .OrderBy(p => p.StartTicks)
            .ThenBy(p => p.EndTicks)
            .ToArray();

        var merged = new List<ClockInterval>();
        foreach (var pause in clipped)
        {
            if (merged.Count == 0 || pause.StartTicks > merged[^1].EndTicks) merged.Add(pause);
            else if (pause.EndTicks > merged[^1].EndTicks) merged[^1] = merged[^1] with { EndTicks = pause.EndTicks };
        }

        var active = new List<ClockInterval>();
        var cursor = capture.StartTicks;
        foreach (var pause in merged)
        {
            if (pause.StartTicks > cursor) active.Add(new(cursor, pause.StartTicks));
            cursor = Math.Max(cursor, pause.EndTicks);
        }
        if (cursor < capture.EndTicks) active.Add(new(cursor, capture.EndTicks));
        return active;
    }

    private static decimal ActiveTicksBefore(IReadOnlyList<ClockInterval> intervals, long tick)
    {
        decimal total = 0;
        foreach (var interval in intervals)
        {
            if (tick <= interval.StartTicks) break;
            var end = Math.Min(tick, interval.EndTicks);
            total += (decimal)end - interval.StartTicks;
            if (tick < interval.EndTicks) break;
        }
        return total;
    }
}
