namespace FrameTrail;

/// <summary>Tracks that can be split independently from the primary video timeline.</summary>
public enum TimelineEffectKind { Zoom, ScreenMotion, CameraMotion, Overlay, Blur }

/// <summary>Source-clock editing operations for effect clips.</summary>
public static class EffectTimelineOperations
{
    private const double MotionMinimumSide = .05;
    private const double SimpleMinimumSide = .01;
    private const double DefaultMotionLeadIn = .7;

    /// <summary>
    /// Splits one effect at an original source-time coordinate. The project is only changed
    /// after the clip, split bounds, and any required sampled motion state have been resolved.
    /// </summary>
    public static Guid Split(FrameTrailProject project, TimelineEffectKind kind, Guid id, double sourceTime)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (id == Guid.Empty) throw new ArgumentException("效果片段 ID 不能为空。", nameof(id));
        if (!double.IsFinite(sourceTime) || sourceTime < 0) throw new ArgumentOutOfRangeException(nameof(sourceTime));

        return kind switch
        {
            TimelineEffectKind.Zoom => SplitZoom(project, id, sourceTime),
            TimelineEffectKind.ScreenMotion => SplitScreenMotion(project, id, sourceTime),
            TimelineEffectKind.CameraMotion => SplitCameraMotion(project, id, sourceTime),
            TimelineEffectKind.Overlay => SplitSimple(project.Overlays, id, sourceTime, static (x, start, duration) => x with { Start = start, Duration = duration }),
            TimelineEffectKind.Blur => SplitSimple(project.BlurClips, id, sourceTime, static (x, start, duration) => x with { Start = start, Duration = duration }),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static Guid SplitZoom(FrameTrailProject project, Guid id, double cut)
    {
        var clips = project.ZoomClips;
        var index = FindIndex(clips, id);
        var original = clips[index];
        RequireInterior(original.Start, original.End, cut, .16, "缩放");

        // DogSC keeps the requested transition intent on both halves. The left half
        // terminates at the cut; the right half gets a real lead-in for later edits.
        var left = CopyZoom(original);
        left.End = cut;
        left.ExitDuration = 0;
        left.ExitProgressOffset = 0;

        var right = CopyZoom(original) with { Id = NewId(clips.Select(x => x.Id)) };
        right.Start = cut;
        right.EnterDuration = Math.Max(original.EnterDuration, .15);
        InsertPair(clips, index, left, right);
        return right.Id;
    }

    private static Guid SplitScreenMotion(FrameTrailProject project, Guid id, double cut)
    {
        var clips = project.ScreenMotions;
        var index = FindIndex(clips, id);
        var original = clips[index];
        var timing = SplitMotionTiming(original.Start, original.Duration, original.EnterDuration, cut);

        // Sample before mutating so the left target is the exact resolved screen state
        // at the cut, including inherited motion from an adjacent clip.
        var baked = new PreparedFrameSceneEvaluator(project).SampleScreenMotionAtSourceTime(timing.Cut);
        var left = CopyScreenMotion(original);
        left.Duration = timing.LeftDuration;
        left.EnterDuration = timing.LeftEnterDuration;
        left.ReturnDuration = 0;
        left.Target = CopyScreenState(baked);

        var right = CopyScreenMotion(original) with { Id = NewId(clips.Select(x => x.Id)) };
        right.Start = timing.Cut;
        right.Duration = timing.RightDuration;
        right.EnterDuration = timing.RightEnterDuration;
        InsertSorted(clips, index, left, right, static x => x.Start);
        return right.Id;
    }

    private static Guid SplitCameraMotion(FrameTrailProject project, Guid id, double cut)
    {
        var clips = project.CameraMotions;
        var index = FindIndex(clips, id);
        var original = clips[index];
        if (project.Camera is null) throw new InvalidOperationException("当前项目没有摄像头轨道，无法拆分摄像头运动。");
        var timing = SplitMotionTiming(original.Start, original.Duration, original.EnterDuration, cut);

        var baked = new PreparedFrameSceneEvaluator(project).SampleCameraMotionAtSourceTime(timing.Cut);
        var left = CopyCameraMotion(original);
        left.Duration = timing.LeftDuration;
        left.EnterDuration = timing.LeftEnterDuration;
        left.ReturnDuration = 0;
        // The upstream editor keeps the clip's selected layout/roundness on the baked
        // left target; only the changing position, size, and opacity are sampled.
        left.TargetPosition = baked.Position.Clamp();
        var effectiveLayout = original.TargetLayout ?? (original.TargetRoundness is not null
            ? CameraMotionLayoutKind.RoundedRectangle : project.Camera.Layout);
        var canvasAspect = (project.Settings.Canvas?.AspectWidth ?? project.Settings.CanvasWidth)
            / (double)Math.Max(2, project.Settings.Canvas?.AspectHeight ?? project.Settings.CanvasHeight);
        left.TargetSize = baked.Size * (effectiveLayout is null ? Math.Min(1, canvasAspect) : 1);
        left.TargetOpacity = baked.Opacity;

        var right = CopyCameraMotion(original) with { Id = NewId(clips.Select(x => x.Id)) };
        right.Start = timing.Cut;
        right.Duration = timing.RightDuration;
        right.EnterDuration = timing.RightEnterDuration;
        InsertSorted(clips, index, left, right, static x => x.Start);
        return right.Id;
    }

    private static Guid SplitSimple<T>(List<T> clips, Guid id, double cut, Func<T, double, double, T> withRange)
        where T : class
    {
        var index = FindIndex(clips, id);
        var original = clips[index];
        var (start, end) = original switch
        {
            OverlayClip overlay => (overlay.Start, overlay.End),
            BlurClip blur => (blur.Start, blur.End),
            _ => throw new InvalidOperationException("不支持的效果类型。")
        };
        RequireInterior(start, end, cut, SimpleMinimumSide, "效果");
        var rightId = NewId(clips.Select(GetId));
        var left = withRange(original, start, cut - start);
        var right = withRange(original, cut, end - cut);
        right = SetId(right, rightId);
        clips[index] = left;
        clips.Insert(index + 1, right);
        var sorted = clips.OrderBy(GetStart).ToArray();
        clips.Clear(); clips.AddRange(sorted);
        return rightId;
    }

    private static (double Cut, double LeftDuration, double RightDuration, double LeftEnterDuration, double RightEnterDuration)
        SplitMotionTiming(double start, double duration, double enterDuration, double requestedCut)
    {
        var end = start + duration;
        RequireInterior(start, end, requestedCut, MotionMinimumSide, "运动");
        var cut = requestedCut;
        var leftDuration = cut - start;
        var rightDuration = end - cut;
        var leftEnter = Math.Min(enterDuration, leftDuration);
        var enterEnd = start + Math.Min(enterDuration, duration);
        var remaining = enterEnd - cut;
        var rightEnter = Math.Min(remaining > .0001 ? remaining : DefaultMotionLeadIn, rightDuration);
        return (cut, leftDuration, rightDuration, leftEnter, rightEnter);
    }

    private static void RequireInterior(double start, double end, double cut, double minimumSide, string label)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || end <= start
            || cut - start < minimumSide || end - cut < minimumSide)
            throw new ArgumentOutOfRangeException(nameof(cut), $"{label}拆分点两侧都必须至少保留 {minimumSide:0.##} 秒。");
    }

    private static int FindIndex<T>(IReadOnlyList<T> clips, Guid id) where T : class
    {
        for (var i = 0; i < clips.Count; i++) if (GetId(clips[i]) == id) return i;
        throw new KeyNotFoundException($"找不到效果片段 {id}。");
    }

    private static Guid GetId<T>(T clip) where T : class => clip switch
    {
        ZoomClip x => x.Id, ScreenMotionClip x => x.Id, CameraMotionClip x => x.Id,
        OverlayClip x => x.Id, BlurClip x => x.Id,
        _ => throw new InvalidOperationException("不支持的效果类型。")
    };

    private static double GetStart<T>(T clip) where T : class => clip switch
    {
        OverlayClip x => x.Start, BlurClip x => x.Start,
        _ => throw new InvalidOperationException("不支持的效果类型。")
    };

    private static T SetId<T>(T clip, Guid id) where T : class => clip switch
    {
        OverlayClip x => (T)(object)(x with { Id = id }),
        BlurClip x => (T)(object)(x with { Id = id }),
        _ => throw new InvalidOperationException("不支持的效果类型。")
    };

    private static Guid NewId(IEnumerable<Guid> existing)
    {
        var ids = existing.ToHashSet();
        Guid id;
        do id = Guid.NewGuid(); while (id == Guid.Empty || ids.Contains(id));
        return id;
    }

    private static ZoomClip CopyZoom(ZoomClip x) => x with { Focus = x.Focus with { }, FocusEffect = x.FocusEffect is null ? null : x.FocusEffect with { } };
    private static ScreenMotionClip CopyScreenMotion(ScreenMotionClip x) => x with { Target = CopyScreenState(x.Target), Focus = x.Focus is null ? null : x.Focus with { } };
    private static ScreenMotionState CopyScreenState(ScreenMotionState x) => x with { Position = x.Position with { } };
    private static CameraMotionClip CopyCameraMotion(CameraMotionClip x) => x with { TargetPosition = x.TargetPosition with { } };

    private static void InsertPair<T>(List<T> clips, int index, T left, T right) where T : class
    { clips[index] = left; clips.Insert(index + 1, right); }

    private static void InsertSorted<T>(List<T> clips, int index, T left, T right, Func<T, double> start) where T : class
    {
        clips[index] = left;
        clips.Insert(index + 1, right);
        var sorted = clips.OrderBy(start).ToArray();
        clips.Clear(); clips.AddRange(sorted);
    }
}
