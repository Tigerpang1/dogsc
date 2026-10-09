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
    public double SecondsSinceLastEvent(double sourceTime)
    {
        if (!double.IsFinite(sourceTime) || sourceTime < 0 || events.Length == 0) return double.PositiveInfinity;
        var index = UpperBound(events, sourceTime) - 1;
        return index < 0 ? double.PositiveInfinity : Math.Max(0, sourceTime - events[index].Time);
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
    private readonly ScreenMotionClip[] motionClips;
    private readonly double[] motionStarts;
    private readonly ScreenMotionState[] motionFromStates;
    private readonly OverlayClip[] overlays;
    private readonly BlurClip[] blurClips;
    private readonly CameraMotionClip[] cameraMotions;
    private readonly CameraSyncSegment[] cameraSyncSegments;
    private readonly double[] cameraMotionStarts;
    private readonly NormalizedRect canvasCrop;
    private readonly (NormalizedPoint Position, double Size)[] cameraMotionFromStates;
    public double Duration => outputEnds[^1];

    public PreparedFrameSceneEvaluator(FrameTrailProject project)
    {
        ArgumentNullException.ThrowIfNull(project); project.Timeline.Validate();
        if (!double.IsFinite(project.MouseClock.OffsetSeconds) || !double.IsFinite(project.MouseClock.Rate) || project.MouseClock.Rate <= 0) throw new InvalidDataException("鼠标时间校准参数无效。");
        this.project = project;
        canvasCrop = (project.Settings.Canvas?.Crop ?? NormalizedRect.Full).Clamp();
        segments = project.Timeline.Segments.ToArray(); outputEnds = new double[segments.Length];
        var output = 0d;
        for (var i = 0; i < segments.Length; i++) outputEnds[i] = output += segments[i].OutputDuration;
        pointerTrack = new PreparedPointerTrack(project.PointerEvents);
        motionClips = project.ScreenMotions.OrderBy(x => x.Start).ToArray();
        motionStarts = motionClips.Select(x => x.Start).ToArray();
        motionFromStates = new ScreenMotionState[motionClips.Length];
        for (var i = 0; i < motionClips.Length; i++)
        {
            if (i == 0) motionFromStates[i] = new ScreenMotionState();
            else
            {
                var previous = motionClips[i - 1]; var destination = motionClips[i].Start <= previous.End + previous.ReturnDuration ? motionClips[i].Target : new ScreenMotionState();
                motionFromStates[i] = EvaluateMotionClip(previous, motionClips[i].Start, motionFromStates[i - 1], destination);
            }
        }
        overlays = project.Overlays.OrderBy(x => x.Layer).ThenBy(x => x.Start).ToArray();
        blurClips = project.BlurClips.OrderBy(x => x.Start).ToArray();
        cameraMotions = project.CameraMotions.OrderBy(x => x.Start).ToArray();
        cameraSyncSegments = project.Camera?.SyncSegments?.OrderBy(x => x.TimelineStart).ToArray() ?? [];
        cameraMotionStarts = cameraMotions.Select(x => x.Start).ToArray();
        cameraMotionFromStates = new (NormalizedPoint, double)[cameraMotions.Length];
        var cameraBasePosition = project.Camera?.Position ?? new NormalizedPoint(.8, .8);
        var cameraBaseSize = project.Camera?.Size ?? .28;
        for (var i = 0; i < cameraMotions.Length; i++)
        {
            if (i == 0) cameraMotionFromStates[i] = (cameraBasePosition, cameraBaseSize);
            else
            {
                var previous = cameraMotions[i - 1];
                var destination = cameraMotions[i].Start <= previous.End + previous.ReturnDuration
                    ? (cameraMotions[i].TargetPosition, cameraMotions[i].TargetSize)
                    : (cameraBasePosition, cameraBaseSize);
                cameraMotionFromStates[i] = EvaluateCameraMotionClip(previous, cameraMotions[i].Start, cameraMotionFromStates[i - 1], destination);
            }
        }
        var sourceClips = project.ZoomClips.Count > 0
            ? project.ZoomClips
            : project.Settings.AutoZoom
                ? AutoZoomPlanner.Plan(project.PointerEvents.Select(e => e with { Time = project.MouseClock.ToSourceTime(e.Time) }))
                : [];
        // Automatic clip focuses originate in full-source pointer coordinates. ZoomClip.Focus
        // is otherwise canvas-crop-local, so normalize automatic focuses once for evaluation.
        clips = sourceClips.Select(x => x.Automatic ? x with { Focus = ToCanvasCropLocal(x.Focus) } : x)
            .OrderBy(x => x.Start).ToArray(); clipStarts = clips.Select(x => x.Start).ToArray(); clipPrefixEnd = new double[clips.Length];
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
            var adjacentPrevious = i > 0 && Math.Abs(clips[i - 1].End - clip.Start) <= 1d / 120
                ? clips[i - 1] : null;
            var requestedEnterDuration = adjacentPrevious is not null && clip.EnterDuration <= .001 ? .7 : clip.EnterDuration;
            var effectiveEnterDuration = Math.Min(requestedEnterDuration, Math.Max(clip.End - clip.Start, 0));
            var enter = effectiveEnterDuration <= 0 ? 1 : Ease(Math.Clamp((sourceTime - clip.Start) / effectiveEnterDuration, 0, 1), clip.Easing);
            var exit = clip.ExitDuration <= 0 ? 1 : 1 - Ease(Math.Clamp((sourceTime - clip.End) / clip.ExitDuration, 0, 1), clip.Easing);
            var progress = Math.Min(enter, exit);
            var fromScale = adjacentPrevious?.Scale ?? 1;
            var fromFocus = adjacentPrevious?.Focus ?? new NormalizedPoint(.5, .5);
            zoom = fromScale + (clip.Scale - fromScale) * progress;
            focus = new(fromFocus.X + (clip.Focus.X - fromFocus.X) * progress, fromFocus.Y + (clip.Focus.Y - fromFocus.Y) * progress);
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
            var cropLocalPointer = ToCanvasCropLocal(position);
            var dx = cropLocalPointer.X - focus.X; var dy = cropLocalPointer.Y - focus.Y;
            if (Math.Abs(dx) > safeExtent) focus = focus with { X = focus.X + Math.Sign(dx) * (Math.Abs(dx) - safeExtent) };
            if (Math.Abs(dy) > safeExtent) focus = focus with { Y = focus.Y + Math.Sign(dy) * (Math.Abs(dy) - safeExtent) };
        }
        var cropSize = 1 / Math.Max(zoom, 1);
        var origin = new NormalizedPoint(Math.Clamp(focus.X - cropSize / 2, 0, 1 - cropSize), Math.Clamp(focus.Y - cropSize / 2, 0, 1 - cropSize));
        var cursorSettings = project.Settings.Cursor ?? new CursorSettings { Visible = project.Settings.CursorVisible };
        var cursorVisible = project.Settings.CursorVisible && cursorSettings.Visible;
        if (cursorSettings.HideWhenIdle && pointerTrack.SecondsSinceLastEvent(pointerTime) > cursorSettings.IdleSeconds) cursorVisible = false;
        var cursor = cursorVisible ? pointer.Position : null;
        var strength = cursorVisible && cursorSettings.ClickEffectsEnabled ? pointer.ClickStrength : 0;
        var s = project.Settings;
        var canvas = s.Canvas ?? new CanvasSettings { AspectWidth = s.CanvasWidth, AspectHeight = s.CanvasHeight };
        var background = s.BackgroundStyle ?? new BackgroundSettings { Color = s.Background, EndColor = s.Background };
        var activeMotion = EvaluateScreenMotion(sourceTime);
        var initialSourceTime = MapOutputToSource(0);
        var screenOpening = EvaluateOpening(outputTime, initialSourceTime, OpeningElement.Screen);
        var cameraOpening = EvaluateOpening(outputTime, initialSourceTime, OpeningElement.Camera);
        var overlayOpening = EvaluateOpening(outputTime, initialSourceTime, OpeningElement.Overlays);
        var camera = EvaluateCamera(sourceTime, cameraOpening);
        var activeOverlays = EvaluateOverlays(sourceTime, overlayOpening, initialSourceTime);
        var blurRegions = EvaluateBlurRegions(sourceTime);
        var (resolvedFocus, focusProgress) = EvaluateFocus(sourceTime);
        if (resolvedFocus is { Target: FocusEffectTarget.Pointer } pointerFocus && pointer.Position is { } pointerLocation)
            resolvedFocus = pointerFocus with { Center = pointerLocation };
        else if (resolvedFocus is { Target: FocusEffectTarget.Animation } && activeMotion is { } motionState)
            resolvedFocus = resolvedFocus with { Center = motionState.Position };
        return new FrameScene(outputTime, sourceTime, origin, new(cropSize, cropSize), zoom, cursor, strength, s.Background, s.Padding, s.CornerRadius, s.CanvasWidth, s.CanvasHeight, s.Shadow,
            canvas, background, cursorSettings, activeMotion, resolvedFocus, focusProgress, camera, activeOverlays, blurRegions, cameraMotions,
            (int)Math.Max(0, Math.Round(project.Media.Width)), (int)Math.Max(0, Math.Round(project.Media.Height)), screenOpening, cameraOpening, overlayOpening);
    }

    /// <summary>Returns the scene at outputTime with a selected zoom clip's target crop applied immediately.</summary>
    public FrameScene EvaluateTargetForZoom(Guid zoomId, double outputTime)
    {
        var clip = clips.FirstOrDefault(x => x.Id == zoomId) ?? throw new KeyNotFoundException($"找不到缩放片段 {zoomId}。");
        var scene = Evaluate(outputTime);
        var scale = Math.Max(1, clip.Scale);
        var cropSize = 1 / scale;
        var focus = clip.Focus;
        var origin = new NormalizedPoint(Math.Clamp(focus.X - cropSize / 2, 0, 1 - cropSize), Math.Clamp(focus.Y - cropSize / 2, 0, 1 - cropSize));
        return scene with { CropOrigin = origin, CropSize = new(cropSize, cropSize), Zoom = scale };
    }

    private NormalizedPoint ToCanvasCropLocal(NormalizedPoint fullSource) => new(
        canvasCrop.Width > 0 ? (fullSource.X - canvasCrop.X) / canvasCrop.Width : .5,
        canvasCrop.Height > 0 ? (fullSource.Y - canvasCrop.Y) / canvasCrop.Height : .5);

    public ScreenMotionState EvaluateTargetForMotion(Guid motionId)
    {
        var clip = motionClips.FirstOrDefault(x => x.Id == motionId) ?? throw new KeyNotFoundException($"找不到屏幕运动片段 {motionId}。");
        return clip.Target with { Position = clip.Target.Position with { } };
    }

    private FrameOpeningState? EvaluateOpening(double outputTime, double sourceTime, OpeningElement element)
    {
        var opening = project.Settings.Opening?.ResolvePreset();
        if (opening is null || !opening.Enabled || outputTime >= opening.Duration || !opening.IncludedElements.Contains(element)) return null;
        var active = ActiveOpeningElements(opening, sourceTime);
        var index = active.IndexOf(element);
        if (index < 0) return null;
        var gaps = Math.Max(active.Count - 1, 0);
        var stagger = gaps == 0 ? opening.Stagger : Math.Min(opening.Stagger, Math.Max(opening.Duration - .18, 0) / gaps);
        var itemDuration = Math.Max(opening.Duration - stagger * gaps, .18);
        var linear = Math.Clamp((outputTime - stagger * index) / itemDuration, 0, 1);
        var progress = Ease(linear, opening.Easing);
        var start = OpeningStart(opening, element);
        var opacity = element == OpeningElement.Screen && opening.Preset is not (OpeningPreset.Converge or OpeningPreset.SideSlide or OpeningPreset.Light3D)
            ? opening.OpacityFrom + (1 - opening.OpacityFrom) * progress
            : Ease(Math.Clamp(linear * 1.65, 0, 1), opening.Easing);
        return new FrameOpeningState(
            start.Scale + (1 - start.Scale) * progress,
            start.OffsetX * (1 - progress), start.OffsetY * (1 - progress),
            start.Rotation * (1 - progress), opacity,
            start.RotationX * Math.Pow(1 - linear, 1.5), start.RotationY * Math.Pow(1 - linear, 1.5),
            start.Perspective * Math.Pow(1 - linear, 1.5));
    }

    private List<OpeningElement> ActiveOpeningElements(OpeningSettings opening, double initialSourceTime)
    {
        var available = new HashSet<OpeningElement> { OpeningElement.Screen };
        if (project.Camera is { Opacity: > 0, Duration: > 0 } camera && TryCameraSourceTime(camera, initialSourceTime, out _))
            available.Add(OpeningElement.Camera);
        if (overlays.Any(x => x.Start <= initialSourceTime + .0001 && x.End > initialSourceTime + .0001)) available.Add(OpeningElement.Overlays);
        var ordered = opening.ElementOrder.Where(x => opening.IncludedElements.Contains(x) && available.Contains(x)).Distinct().ToList();
        ordered.AddRange(opening.IncludedElements.Where(x => available.Contains(x) && !ordered.Contains(x)));
        return ordered;
    }

    private static (double Scale, double OffsetX, double OffsetY, double Rotation, double RotationX, double RotationY, double Perspective) OpeningStart(OpeningSettings opening, OpeningElement element)
    {
        if (element == OpeningElement.Screen)
        {
            var rotationY = opening.Preset == OpeningPreset.Light3D ? -22 : 0;
            var rotationX = opening.Preset == OpeningPreset.Light3D ? 9 : 0;
            var perspective = opening.Preset == OpeningPreset.Light3D ? .72 : 0;
            return (opening.ScaleFrom, opening.OffsetX, opening.OffsetY, opening.RotationFrom, rotationX, rotationY, perspective);
        }
        return opening.Preset switch
        {
            OpeningPreset.Converge => element == OpeningElement.Camera ? (.58, 1, .72, 0, 0, 0, 0) : (.62, -1, -.70, 0, 0, 0, 0),
            OpeningPreset.SideSlide => element == OpeningElement.Camera ? (.88, 1, 0, 0, 0, 0, 0) : (.9, -1, 0, 0, 0, 0, 0),
            OpeningPreset.Light3D => element == OpeningElement.Camera ? (.72, 1, .16, 0, 0, 0, 0) : (.76, -1, -.12, 0, 0, 0, 0),
            _ => (opening.ScaleFrom, opening.OffsetX, opening.OffsetY, opening.RotationFrom, 0, 0, 0)
        };
    }

    private ScreenMotionState? EvaluateScreenMotion(double time)
    {
        if (motionClips.Length == 0) return null;
        var index = UpperBound(motionStarts, time) - 1;
        if (index < 0) return null;
        var clip = motionClips[index];
        if (time > clip.End + clip.ReturnDuration) return null;
        var destination = index + 1 < motionClips.Length && motionClips[index + 1].Start <= clip.End + clip.ReturnDuration
            ? motionClips[index + 1].Target : new ScreenMotionState();
        return EvaluateMotionClip(clip, time, motionFromStates[index], destination);
    }

    private static ScreenMotionState EvaluateMotionClip(ScreenMotionClip clip, double time, ScreenMotionState from, ScreenMotionState destination)
    {
        if (clip.EnterDuration > 0 && time < clip.Start + clip.EnterDuration)
            return Interpolate(from, clip.Target, Ease(Math.Clamp((time - clip.Start) / clip.EnterDuration, 0, 1), clip.Easing));
        if (time <= clip.End) return clip.Target;
        if (clip.ReturnDuration <= 0) return destination;
        return Interpolate(clip.Target, destination, Ease(Math.Clamp((time - clip.End) / clip.ReturnDuration, 0, 1), clip.Easing));
    }

    private static ScreenMotionState Interpolate(ScreenMotionState a, ScreenMotionState b, double t) => new()
    {
        Position = new(a.Position.X + (b.Position.X - a.Position.X) * t, a.Position.Y + (b.Position.Y - a.Position.Y) * t),
        Scale = a.Scale + (b.Scale - a.Scale) * t,
        RotationX = a.RotationX + (b.RotationX - a.RotationX) * t,
        RotationY = a.RotationY + (b.RotationY - a.RotationY) * t,
        RotationZ = a.RotationZ + (b.RotationZ - a.RotationZ) * t,
        Perspective = a.Perspective + (b.Perspective - a.Perspective) * t
    };

    private FrameCameraScene? EvaluateCamera(double time, FrameOpeningState? opening)
    {
        var camera = project.Camera;
        if (camera is null || !TryCameraSourceTime(camera, time, out var local)) return null;
        var (position, size) = EvaluateCameraMotion(time, camera.Position, camera.Size);
        var canvasAspect = project.Settings.CanvasWidth / (double)project.Settings.CanvasHeight;
        var normalizedWidth = size / canvasAspect;
        var baseRect = new NormalizedRect(position.X - normalizedWidth / 2, position.Y - size / 2, normalizedWidth, size);
        if (opening is { } open)
        {
            var width = baseRect.Width * open.Scale; var height = baseRect.Height * open.Scale;
            baseRect = new(baseRect.X + baseRect.Width / 2 - width / 2 + open.OffsetX,
                baseRect.Y + baseRect.Height / 2 - height / 2 + open.OffsetY, width, height);
        }
        return new FrameCameraScene(camera.MediaPath, local, baseRect,
            camera.CornerRadius * (opening?.Scale ?? 1), camera.BorderWidth * (opening?.Scale ?? 1), camera.Opacity * (opening?.Opacity ?? 1), camera.Mirrored, camera.Volume, camera.Muted, camera.Shape, camera.Crop, camera.BorderColor, opening?.Rotation ?? 0);
    }

    private bool TryCameraSourceTime(CameraTrack camera, double screenSourceTime, out double cameraSourceTime)
    {
        if (cameraSyncSegments.Length == 0)
        {
            if (screenSourceTime >= camera.TimelineStart && screenSourceTime < camera.TimelineStart + camera.Duration)
            { cameraSourceTime = camera.SourceStart + screenSourceTime - camera.TimelineStart; return true; }
            cameraSourceTime = 0; return false;
        }
        var low = 0; var high = cameraSyncSegments.Length;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (cameraSyncSegments[mid].TimelineStart <= screenSourceTime) low = mid + 1; else high = mid;
        }
        var index = low - 1;
        if (index >= 0)
        {
            var segment = cameraSyncSegments[index];
            var offset = screenSourceTime - segment.TimelineStart;
            if (offset >= 0 && offset < segment.Duration) { cameraSourceTime = segment.SourceStart + offset; return true; }
        }
        cameraSourceTime = 0; return false;
    }

    private (NormalizedPoint Position, double Size) EvaluateCameraMotion(double time, NormalizedPoint basePosition, double baseSize)
    {
        var index = UpperBound(cameraMotionStarts, time) - 1;
        if (index < 0) return (basePosition, baseSize);
        var clip = cameraMotions[index];
        if (time > clip.End + clip.ReturnDuration) return (basePosition, baseSize);
        var destination = index + 1 < cameraMotions.Length && cameraMotions[index + 1].Start <= clip.End + clip.ReturnDuration
            ? (cameraMotions[index + 1].TargetPosition, cameraMotions[index + 1].TargetSize)
            : (basePosition, baseSize);
        return EvaluateCameraMotionClip(clip, time, cameraMotionFromStates[index], destination);
    }

    private static (NormalizedPoint Position, double Size) EvaluateCameraMotionClip(CameraMotionClip clip, double time,
        (NormalizedPoint Position, double Size) from, (NormalizedPoint Position, double Size) destination)
    {
        if (clip.EnterDuration > 0 && time < clip.Start + clip.EnterDuration)
            return InterpolateCamera(from, (clip.TargetPosition, clip.TargetSize), Ease(Math.Clamp((time - clip.Start) / clip.EnterDuration, 0, 1), clip.Easing));
        if (time <= clip.End) return (clip.TargetPosition, clip.TargetSize);
        if (clip.ReturnDuration <= 0) return destination;
        var progress = Ease(Math.Clamp((time - clip.End) / clip.ReturnDuration, 0, 1), clip.Easing);
        return InterpolateCamera((clip.TargetPosition, clip.TargetSize), destination, progress);
    }

    private static (NormalizedPoint Position, double Size) InterpolateCamera(
        (NormalizedPoint Position, double Size) from, (NormalizedPoint Position, double Size) to, double t) =>
        (new(from.Position.X + (to.Position.X - from.Position.X) * t, from.Position.Y + (to.Position.Y - from.Position.Y) * t),
            from.Size + (to.Size - from.Size) * t);

    private IReadOnlyList<FrameOverlayScene> EvaluateOverlays(double time, FrameOpeningState? opening, double initialSourceTime)
    {
        var result = new List<FrameOverlayScene>();
        var suppressInitialEntry = project.Settings.Opening is { Enabled: true } openingSettings
            && openingSettings.IncludedElements.Contains(OpeningElement.Overlays);
        foreach (var item in overlays)
        {
            if (time < item.Start || time >= item.End) continue;
            var enter = item.EnterDuration <= 0 ? 1 : Math.Clamp((time - item.Start) / item.EnterDuration, 0, 1);
            var exit = item.ExitDuration <= 0 ? 1 : Math.Clamp((item.End - time) / item.ExitDuration, 0, 1);
            if (suppressInitialEntry && item.Start <= initialSourceTime + .0001) enter = 1;
            var rect = item.Rect; var opacity = item.Opacity;
            ApplyOverlayMotion(item.EnterMotion, 1 - enter, ref rect, ref opacity);
            ApplyOverlayMotion(item.ExitMotion, 1 - exit, ref rect, ref opacity);
            var rotation = item.Rotation;
            if (opening is { } open)
            {
                var width = rect.Width * open.Scale; var height = rect.Height * open.Scale;
                rect = new(rect.X + rect.Width / 2 - width / 2 + open.OffsetX,
                    rect.Y + rect.Height / 2 - height / 2 + open.OffsetY, width, height);
                opacity *= open.Opacity; rotation += open.Rotation;
            }
            result.Add(new FrameOverlayScene(item.Id, item.MediaPath, rect, rotation, Math.Clamp(opacity, 0, 1), item.Layer, item.CornerRadius, item.Blur));
        }
        return result;
    }

    private static void ApplyOverlayMotion(OverlayMotion motion, double amount, ref NormalizedRect rect, ref double opacity)
    {
        if (amount <= 0) return;
        switch (motion)
        {
            case OverlayMotion.Fade: opacity *= 1 - amount; break;
            case OverlayMotion.Scale:
                var scale = 1 - .35 * amount; var dx = rect.Width * (1 - scale) / 2; var dy = rect.Height * (1 - scale) / 2;
                rect = new(rect.X + dx, rect.Y + dy, rect.Width * scale, rect.Height * scale); break;
            case OverlayMotion.SlideUp: rect = rect with { Y = rect.Y + rect.Height * amount }; break;
            case OverlayMotion.SlideDown: rect = rect with { Y = rect.Y - rect.Height * amount }; break;
            case OverlayMotion.SlideLeft: rect = rect with { X = rect.X + rect.Width * amount }; break;
            case OverlayMotion.SlideRight: rect = rect with { X = rect.X - rect.Width * amount }; break;
        }
    }

    private (FocusEffect? Effect, double Progress) EvaluateFocus(double time)
    {
        var index = UpperBound(motionStarts, time) - 1;
        if (index < 0) return (null, 0);
        var clip = motionClips[index];
        if (clip.Focus is null || time < clip.Start || time > clip.End + clip.ReturnDuration) return (null, 0);
        var progress = clip.EnterDuration > 0 && time < clip.Start + clip.EnterDuration
            ? Ease(Math.Clamp((time - clip.Start) / clip.EnterDuration, 0, 1), clip.Easing)
            : time <= clip.End ? 1
            : clip.ReturnDuration <= 0 ? 0 : 1 - Ease(Math.Clamp((time - clip.End) / clip.ReturnDuration, 0, 1), clip.Easing);
        return (clip.Focus, progress);
    }

    private IReadOnlyList<FrameBlurScene> EvaluateBlurRegions(double time)
    {
        var result = new List<FrameBlurScene>();
        foreach (var clip in blurClips)
        {
            if (time < clip.Start || time >= clip.End) continue;
            var enter = clip.EnterDuration <= 0 ? 1 : Ease(Math.Clamp((time - clip.Start) / clip.EnterDuration, 0, 1), ZoomEasing.Cubic);
            var exit = clip.ExitDuration <= 0 ? 1 : Ease(Math.Clamp((clip.End - time) / clip.ExitDuration, 0, 1), ZoomEasing.Cubic);
            result.Add(new FrameBlurScene(clip.Rect, clip.Strength, clip.Kind, clip.CornerRadius, Math.Min(enter, exit)));
        }
        return result;
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
    private static double Ease(double x, ZoomEasing easing)
    {
        x = Math.Clamp(x, 0, 1);
        return easing switch
        {
            ZoomEasing.Linear => x,
            ZoomEasing.Quintic => Bezier(x, .82, 0, .18, 1),
            ZoomEasing.Spring => Spring(x),
            _ => Bezier(x, .65, 0, .35, 1)
        };
    }

    // These control points and the normalized spring are the defaults in
    // RecorderCore/ZoomAnimation.swift. Solving x(t)=time is essential: using
    // the Bezier parameter directly produces a visibly different timeline.
    private static double Bezier(double x, double x1, double y1, double x2, double y2)
    {
        if (x is <= 0 or >= 1) return x;
        static double Coordinate(double t, double p1, double p2)
        {
            var u = 1 - t;
            return 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t;
        }
        static double Derivative(double t, double p1, double p2)
        {
            var u = 1 - t;
            return 3 * u * u * p1 + 6 * u * t * (p2 - p1) + 3 * t * t * (1 - p2);
        }

        var t = x;
        for (var i = 0; i < 7; i++)
        {
            var error = Coordinate(t, x1, x2) - x;
            if (Math.Abs(error) < .00001) return Coordinate(t, y1, y2);
            var derivative = Derivative(t, x1, x2);
            if (Math.Abs(derivative) < .00001) break;
            var next = t - error / derivative;
            if (next is < 0 or > 1) break;
            t = next;
        }

        var low = 0.0;
        var high = 1.0;
        t = x;
        for (var i = 0; i < 18; i++)
        {
            var value = Coordinate(t, x1, x2);
            if (Math.Abs(value - x) < .00001) return Coordinate(t, y1, y2);
            if (value < x) low = t; else high = t;
            t = (low + high) / 2;
        }
        return Coordinate(t, y1, y2);
    }

    private static double Spring(double x)
    {
        if (x is <= 0 or >= 1) return x;
        const double mass = 2.4, stiffness = 210, damping = 42, duration = .72;
        var omega0 = Math.Sqrt(stiffness / mass);
        var zeta = Math.Min(damping / (2 * Math.Sqrt(stiffness * mass)), .9999);
        var omegaD = omega0 * Math.Sqrt(Math.Max(1 - zeta * zeta, double.Epsilon));
        double Response(double seconds)
        {
            var envelope = Math.Exp(-zeta * omega0 * seconds);
            var oscillation = Math.Cos(omegaD * seconds) + zeta * omega0 / omegaD * Math.Sin(omegaD * seconds);
            return 1 - envelope * oscillation;
        }
        return Math.Clamp(Response(x * duration) / Response(duration), 0, 1);
    }
}
