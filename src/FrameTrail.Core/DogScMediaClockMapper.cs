namespace FrameTrail;

/// <summary>Builds auxiliary-media mappings equivalent to DogSC MediaTimelinePlan.</summary>
internal static class DogScMediaClockMapper
{
    internal readonly record struct Anchor(double SourceTime, double Offset, int Order, Guid Id);

    /// <summary>
    /// Maps project screen-source time to an auxiliary asset clock. SourceEndTime
    /// determines the placement's nominal timeline end; corrections are clamped
    /// only at the known lower asset bound (zero), matching MediaTimelinePlan.
    /// </summary>
    internal static List<CameraSyncSegment> Build(
        double timelineStart,
        double sourceStart,
        double sourceRate,
        double? sourceEndTime,
        double screenDuration,
        IReadOnlyList<Anchor> rawAnchors)
    {
        if (!double.IsFinite(timelineStart) || timelineStart < 0
            || !double.IsFinite(sourceStart) || sourceStart < 0
            || !double.IsFinite(sourceRate) || sourceRate <= 0
            || !double.IsFinite(screenDuration) || screenDuration <= 0)
            return [];

        var nominalSourceEnd = sourceEndTime is { } requestedEnd && double.IsFinite(requestedEnd)
            ? Math.Max(sourceStart, requestedEnd)
            : sourceStart + Math.Max(screenDuration - timelineStart, 0) * sourceRate;
        var nominalDuration = Math.Max(0, (nominalSourceEnd - sourceStart) / sourceRate);
        var timelineEnd = Math.Min(screenDuration, timelineStart + nominalDuration);
        if (timelineEnd - timelineStart <= 1e-6) return [];

        var anchors = rawAnchors
            .Where(x => double.IsFinite(x.SourceTime) && double.IsFinite(x.Offset))
            .Select(x => new Anchor(Math.Max(0, x.SourceTime), Math.Clamp(x.Offset, -10, 10), x.Order, x.Id))
            .OrderBy(x => x.SourceTime).ThenBy(x => x.Id.ToString("D"), StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Order).ToArray();
        var hasCorrection = anchors.Length > 0;
        if (!hasCorrection && Math.Abs(sourceRate - 1) <= 1e-12)
            return [];

        var boundaries = new List<double> { timelineStart };
        boundaries.AddRange(anchors.Select(x => x.SourceTime)
            .Where(x => x > timelineStart + 1e-6 && x < timelineEnd - 1e-6));
        boundaries.Add(timelineEnd);
        var result = new List<CameraSyncSegment>(boundaries.Count - 1);
        for (var i = 0; i + 1 < boundaries.Count; i++)
        {
            var start = boundaries[i];
            var end = boundaries[i + 1];
            var sourceAtStart = sourceStart + (start - timelineStart) * sourceRate + OffsetAt(anchors, start);
            var sourceAtEnd = sourceStart + (end - timelineStart) * sourceRate + OffsetAt(anchors, end);
            if (sourceAtStart < 0) sourceAtStart = 0;
            if (sourceAtEnd < 0) sourceAtEnd = 0;
            var duration = end - start;
            var mappedRate = (sourceAtEnd - sourceAtStart) / duration;
            // DogSC drops slices whose corrected asset clock would run backward
            // or stand still; retaining them would make a decoder seek nonsensically.
            if (!double.IsFinite(mappedRate) || mappedRate <= 1e-9) continue;
            if (mappedRate > 100)
                throw new InvalidDataException("DogSC 辅助媒体同步锚点产生超过 FrameTrail 安全上限的源时钟速率；请先在原工程中修正该锚点。");
            result.Add(new(sourceAtStart, start, duration, mappedRate));
        }
        return result;
    }

    private static double OffsetAt(IReadOnlyList<Anchor> anchors, double time)
    {
        if (anchors.Count == 0) return 0;
        var first = anchors[0];
        if (first.SourceTime > 1e-6 && time <= first.SourceTime)
            return first.Offset * Math.Clamp(time / first.SourceTime, 0, 1);
        if (time <= first.SourceTime) return first.Offset;
        var low = 1;
        var high = anchors.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (anchors[middle].SourceTime < time) low = middle + 1;
            else high = middle;
        }
        if (low >= anchors.Count) return anchors[^1].Offset;
        var left = anchors[low - 1];
        var right = anchors[low];
        var span = right.SourceTime - left.SourceTime;
        if (span <= 1e-6) return right.Offset;
        var progress = Math.Clamp((time - left.SourceTime) / span, 0, 1);
        return left.Offset + (right.Offset - left.Offset) * progress;
    }
}
