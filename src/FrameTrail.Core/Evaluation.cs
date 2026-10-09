namespace FrameTrail;

public sealed record AutoZoomOptions(double Scale = 1.6, double EnterDuration = .7, double HoldDuration = .9, double ExitDuration = .7, double GroupGap = 1.4, double SafeInset = .12, ZoomEasing Easing = ZoomEasing.Spring);

/// <summary>Plans click-centered camera regions, extends holds through continuous pointer activity, and folds touching automatic envelopes.</summary>
public static class AutoZoomPlanner
{
    public static List<ZoomClip> Plan(IEnumerable<PointerEvent> events, AutoZoomOptions? options = null)
    {
        var o = options ?? new(); ValidateOptions(o);
        var validEvents = events.Where(x => double.IsFinite(x.Time) && x.Time >= 0 && x.Location.IsFinite).OrderBy(x => x.Time).ToArray();
        var clicks = validEvents.Where(x => x.Kind is PointerEventKind.LeftClick or PointerEventKind.RightClick).ToArray();
        var output = new List<ZoomClip>();
        var earliestNextStart = 0d;
        foreach (var group in Group(clicks, o.GroupGap))
        {
            var focusGroups = SpatialGroups(group, 1 / o.Scale * .5, 1 / o.Scale * .7);
            if (focusGroups.Count == 0) continue;
            var starts = new double[focusGroups.Count];
            starts[0] = Math.Max(earliestNextStart, Math.Max(0, focusGroups[0][0].Time - o.EnterDuration * .18));
            for (var i = 1; i < starts.Length; i++) starts[i] = Math.Max(focusGroups[i][0].Time - o.EnterDuration, starts[i - 1] + .16);
            for (var i = 0; i < focusGroups.Count; i++)
            {
                var last = i == focusGroups.Count - 1;
                var end = last ? Math.Max(focusGroups[i][^1].Time + o.HoldDuration, starts[i] + o.EnterDuration) : starts[i + 1];
                if (end <= starts[i] + .01) continue;
                var minX = focusGroups[i].Min(x => x.Location.X); var maxX = focusGroups[i].Max(x => x.Location.X);
                var minY = focusGroups[i].Min(x => x.Location.Y); var maxY = focusGroups[i].Max(x => x.Location.Y);
                output.Add(new ZoomClip { Start = starts[i], End = end, Scale = o.Scale, Focus = new NormalizedPoint((minX + maxX) * .5, (minY + maxY) * .5).Clamp(o.SafeInset), EnterDuration = o.EnterDuration, ExitDuration = last ? o.ExitDuration : 0, Easing = o.Easing, Automatic = true });
            }
            if (output.Count > 0) earliestNextStart = output[^1].End + output[^1].ExitDuration;
        }
        ExtendHolds(output, clicks, validEvents);
        return MergeTouching(output);
    }

    private static List<List<PointerEvent>> SpatialGroups(List<PointerEvent> clicks, double maxX, double maxY)
    {
        var groups = new List<List<PointerEvent>>();
        foreach (var click in clicks)
        {
            if (groups.Count == 0) { groups.Add([click]); continue; }
            var group = groups[^1];
            var lowX = Math.Min(group.Min(x => x.Location.X), click.Location.X); var highX = Math.Max(group.Max(x => x.Location.X), click.Location.X);
            var lowY = Math.Min(group.Min(x => x.Location.Y), click.Location.Y); var highY = Math.Max(group.Max(x => x.Location.Y), click.Location.Y);
            if (highX - lowX <= maxX && highY - lowY <= maxY) group.Add(click); else groups.Add([click]);
        }
        return groups;
    }
    private static void ExtendHolds(List<ZoomClip> clips, PointerEvent[] clicks, PointerEvent[] events)
    {
        for (var i = 0; i < clips.Count; i++)
        {
            var clip = clips[i]; if (clip.ExitDuration <= .001) continue;
            var clickIndex = UpperBound(clicks, clip.End + .0001) - 1;
            if (clickIndex < 0 || clicks[clickIndex].Time < clip.Start - .0001) continue;
            var click = clicks[clickIndex];
            var nextStart = i + 1 < clips.Count ? clips[i + 1].Start : double.PositiveInfinity;
            var previous = click.Time; var activityEnd = click.Time;
            for (var eventIndex = UpperBound(events, click.Time); eventIndex < events.Length && events[eventIndex].Time < nextStart; eventIndex++)
            {
                var e = events[eventIndex];
                if (e.Time - previous > 1.4) break;
                previous = activityEnd = e.Time;
            }
            if (activityEnd <= click.Time + .0001) continue;
            clip.End = Math.Min(Math.Max(clip.End, activityEnd + .55), nextStart);
            if (double.IsFinite(nextStart)) clip.ExitDuration = Math.Min(clip.ExitDuration, Math.Max(nextStart - clip.End, 0));
        }
    }
    private static int UpperBound(PointerEvent[] values, double time)
    { var low = 0; var high = values.Length; while (low < high) { var mid = low + (high - low) / 2; if (values[mid].Time <= time) low = mid + 1; else high = mid; } return low; }
    private static List<ZoomClip> MergeTouching(List<ZoomClip> clips)
    {
        clips.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<ZoomClip>();
        foreach (var clip in clips)
        {
            if (merged.Count > 0)
            {
                var previous = merged[^1];
                if (previous.Automatic && clip.Automatic && previous.ExitDuration <= .0001 && Math.Abs(previous.End - clip.Start) <= .0001 && Math.Abs(previous.Scale - clip.Scale) <= .000001 && previous.Easing == clip.Easing)
                {
                    previous.End = Math.Max(previous.End, clip.End); previous.ExitDuration = clip.ExitDuration; continue;
                }
            }
            merged.Add(clip);
        }
        return merged;
    }
    private static IEnumerable<List<PointerEvent>> Group(PointerEvent[] clicks, double gap)
    {
        List<PointerEvent>? group = null;
        foreach (var click in clicks) { if (group is null || click.Time - group[^1].Time > gap) { if (group is not null) yield return group; group = []; } group.Add(click); }
        if (group is not null) yield return group;
    }
    private static void ValidateOptions(AutoZoomOptions o)
    {
        if (!double.IsFinite(o.Scale) || o.Scale < 1 || o.Scale > 20 || !double.IsFinite(o.EnterDuration) || o.EnterDuration is < 0 or > 5 || !double.IsFinite(o.HoldDuration) || o.HoldDuration < 0 || !double.IsFinite(o.ExitDuration) || o.ExitDuration is < 0 or > 5 || !double.IsFinite(o.GroupGap) || o.GroupGap < 0 || !double.IsFinite(o.SafeInset) || o.SafeInset is < 0 or >= .5) throw new ArgumentOutOfRangeException(nameof(o), "自动缩放参数超出范围。");
    }
}

public static class PointerTrack
{
    public static (NormalizedPoint? Position, double ClickStrength) Sample(IReadOnlyList<PointerEvent> source, double sourceTime)
        => new PreparedPointerTrack(source).Sample(sourceTime);
}

/// <summary>Pre-sorted pointer samples; each frame uses binary search instead of sorting and scanning the recording.</summary>
public sealed class PreparedPointerTrack
{
    private readonly PointerEvent[] events;
    private readonly PointerEvent[] clicks;
    public PreparedPointerTrack(IEnumerable<PointerEvent> source)
    {
        events = source.Where(x => double.IsFinite(x.Time) && x.Time >= 0 && x.Location.IsFinite).OrderBy(x => x.Time).ToArray();
        clicks = events.Where(x => x.Kind is PointerEventKind.LeftClick or PointerEventKind.RightClick).ToArray();
    }
    public (NormalizedPoint? Position, double ClickStrength) Sample(double sourceTime)
    {
        if (!double.IsFinite(sourceTime) || sourceTime < 0 || events.Length == 0) return (null, 0);
        var upper = UpperBound(events, sourceTime);
        if (upper == 0) return (null, 0);
        var lowerIndex = upper - 1; var lower = events[lowerIndex]; NormalizedPoint location;
        if (upper == events.Length || events[upper].Time - lower.Time > .5 || events[upper].Time <= lower.Time) location = lower.Location;
        else
        {
            var next = events[upper]; var duration = next.Time - lower.Time; var t = Math.Clamp((sourceTime - lower.Time) / duration, 0, 1);
            var previous = lowerIndex > 0 && lower.Time > events[lowerIndex - 1].Time ? events[lowerIndex - 1] : lower;
            var after = upper + 1 < events.Length && events[upper + 1].Time > next.Time ? events[upper + 1] : next;
            location = new NormalizedPoint(
                Hermite(lower.Location.X, next.Location.X, Tangent(previous.Time, previous.Location.X, lower.Time, lower.Location.X, next.Time, next.Location.X), Tangent(lower.Time, lower.Location.X, next.Time, next.Location.X, after.Time, after.Location.X), duration, t),
                Hermite(lower.Location.Y, next.Location.Y, Tangent(previous.Time, previous.Location.Y, lower.Time, lower.Location.Y, next.Time, next.Location.Y), Tangent(lower.Time, lower.Location.Y, next.Time, next.Location.Y, after.Time, after.Location.Y), duration, t)).Clamp();
        }
        var clickIndex = UpperBound(clicks, sourceTime) - 1;
        var click = clickIndex >= 0 && sourceTime - clicks[clickIndex].Time <= .18 ? clicks[clickIndex] : null;
        var strength = click is null ? 0 : Math.Sin(Math.PI * Math.Clamp((sourceTime - click.Time) / .18, 0, 1));
        return (location, strength);
    }
    private static int UpperBound(PointerEvent[] values, double time)
    { var low = 0; var high = values.Length; while (low < high) { var mid = low + (high - low) / 2; if (values[mid].Time <= time) low = mid + 1; else high = mid; } return low; }
    private static double Tangent(double t0, double v0, double t1, double v1, double t2, double v2)
    {
        var d0 = t1 - t0; var d1 = t2 - t1; if (d0 <= 0 || d1 <= 0) return 0;
        var s0 = (v1 - v0) / d0; var s1 = (v2 - v1) / d1; if (s0 * s1 <= 0) return 0;
        var w0 = 2 * d1 + d0; var w1 = d1 + 2 * d0;
        return (w0 + w1) / (w0 / s0 + w1 / s1);
    }
    private static double Hermite(double a, double b, double ma, double mb, double duration, double t)
    { var t2 = t * t; var t3 = t2 * t; return (2*t3-3*t2+1)*a + (t3-2*t2+t)*duration*ma + (-2*t3+3*t2)*b + (t3-t2)*duration*mb; }
}

public static class FrameSceneEvaluator
{
    public static FrameScene Evaluate(FrameTrailProject project, double outputTime)
        => new PreparedFrameSceneEvaluator(project).Evaluate(outputTime);
}

/// <summary>Build once per export or preview session; per-frame work maps segments and samples tracks with binary search.</summary>
public sealed class PreparedFrameSceneEvaluator
{
    private readonly FrameTrailProject project;
    private readonly TimelineSegment[] segments;
    private readonly double[] outputEnds;
    private readonly ZoomClip[] clips;
    private readonly double[] clipStarts;
    private readonly double[] clipPrefixEnd;
    private readonly PreparedPointerTrack pointerTrack;
    public double Duration => outputEnds[^1];

    public PreparedFrameSceneEvaluator(FrameTrailProject project)
    {
        ArgumentNullException.ThrowIfNull(project); project.Timeline.Validate();
        if (!double.IsFinite(project.MouseClock.OffsetSeconds) || !double.IsFinite(project.MouseClock.Rate) || project.MouseClock.Rate <= 0) throw new InvalidDataException("鼠标时间校准参数无效。");
        this.project = project;
        segments = project.Timeline.Segments.ToArray(); outputEnds = new double[segments.Length];
        var output = 0d;
        for (var i = 0; i < segments.Length; i++) outputEnds[i] = output += segments[i].OutputDuration;
        pointerTrack = new PreparedPointerTrack(project.PointerEvents);
        var sourceClips = project.ZoomClips.Count > 0
            ? project.ZoomClips
            : project.Settings.AutoZoom
                ? AutoZoomPlanner.Plan(project.PointerEvents.Select(e => e with { Time = project.MouseClock.ToSourceTime(e.Time) }))
                : [];
        clips = sourceClips.OrderBy(x => x.Start).ToArray(); clipStarts = clips.Select(x => x.Start).ToArray(); clipPrefixEnd = new double[clips.Length];
        var maxEnd = double.NegativeInfinity;
        for (var i = 0; i < clips.Length; i++) clipPrefixEnd[i] = maxEnd = Math.Max(maxEnd, clips[i].End + clips[i].ExitDuration);
    }

    public FrameScene Evaluate(double outputTime)
    {
        if (!double.IsFinite(outputTime) || outputTime < 0 || outputTime > Duration) throw new ArgumentOutOfRangeException(nameof(outputTime));
        var sourceTime = MapOutputToSource(outputTime);
        var zoom = 1d; var focus = new NormalizedPoint(.5, .5); var followsPointer = false;
        var upperClip = UpperBound(clipStarts, sourceTime);
        for (var i = upperClip - 1; i >= 0 && clipPrefixEnd[i] >= sourceTime; i--)
        {
            var clip = clips[i]; if (sourceTime < clip.Start || sourceTime > clip.End + clip.ExitDuration) continue;
            var enter = clip.EnterDuration <= 0 ? 1 : Ease(Math.Clamp((sourceTime - clip.Start) / clip.EnterDuration, 0, 1), clip.Easing);
            var exit = clip.ExitDuration <= 0 ? 1 : 1 - Ease(Math.Clamp((sourceTime - clip.End) / clip.ExitDuration, 0, 1), clip.Easing);
            var progress = Math.Min(enter, exit);
            zoom = 1 + (clip.Scale - 1) * progress;
            focus = new(.5 + (clip.Focus.X - .5) * progress, .5 + (clip.Focus.Y - .5) * progress);
            followsPointer = clip.Automatic && progress > 0;
            break;
        }
        var pointerTime = (sourceTime - project.MouseClock.OffsetSeconds) / project.MouseClock.Rate;
        var pointer = pointerTrack.Sample(pointerTime);
        if (followsPointer && pointer.Position is { } position)
        {
            // Keep the pointer in a central safety area. The camera starts following only when it leaves that area,
            // which yields continuous framing through long pointer activity without a per-click camera reset.
            var safeExtent = .35 / zoom;
            var dx = position.X - focus.X; var dy = position.Y - focus.Y;
            if (Math.Abs(dx) > safeExtent) focus = focus with { X = focus.X + Math.Sign(dx) * (Math.Abs(dx) - safeExtent) };
            if (Math.Abs(dy) > safeExtent) focus = focus with { Y = focus.Y + Math.Sign(dy) * (Math.Abs(dy) - safeExtent) };
        }
        var cropSize = 1 / Math.Max(zoom, 1);
        var origin = new NormalizedPoint(Math.Clamp(focus.X - cropSize / 2, 0, 1 - cropSize), Math.Clamp(focus.Y - cropSize / 2, 0, 1 - cropSize));
        var cursor = project.Settings.CursorVisible ? pointer.Position : null;
        var strength = project.Settings.CursorVisible ? pointer.ClickStrength : 0;
        var s = project.Settings;
        return new FrameScene(outputTime, sourceTime, origin, new(cropSize, cropSize), zoom, cursor, strength, s.Background, s.Padding, s.CornerRadius, s.CanvasWidth, s.CanvasHeight, s.Shadow);
    }

    private double MapOutputToSource(double outputTime)
    {
        var index = Array.BinarySearch(outputEnds, outputTime);
        if (index < 0) index = ~index;
        else if (index < outputEnds.Length - 1) index++;
        if (index >= segments.Length) index = segments.Length - 1;
        var segment = segments[index]; var outputStart = index == 0 ? 0 : outputEnds[index - 1];
        return segment.SourceStart + Math.Clamp(outputTime - outputStart, 0, segment.OutputDuration) * segment.PlaybackRate;
    }
    private static int UpperBound(double[] values, double time)
    { var low = 0; var high = values.Length; while (low < high) { var mid = low + (high - low) / 2; if (values[mid] <= time) low = mid + 1; else high = mid; } return low; }
    private static double Ease(double x, ZoomEasing easing) => easing switch
    {
        ZoomEasing.Linear => x,
        ZoomEasing.Quintic => x * x * x * (x * (x * 6 - 15) + 10),
        ZoomEasing.Spring => Math.Clamp(1 - Math.Exp(-7 * x) * (Math.Cos(9 * x) + .78 * Math.Sin(9 * x)), 0, 1),
        _ => x * x * (3 - 2 * x)
    };
}
