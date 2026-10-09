namespace FrameTrail;

/// <summary>Normalized top-left rectangle stored as relative values, independent of preview/export size.</summary>
public readonly record struct NormalizedRect(double X, double Y, double Width, double Height)
{
    public static NormalizedRect Full => new(0, 0, 1, 1);
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height);
    public NormalizedRect Clamp()
    {
        var x = Math.Clamp(double.IsFinite(X) ? X : 0, 0, 1);
        var y = Math.Clamp(double.IsFinite(Y) ? Y : 0, 0, 1);
        return new(x, y, Math.Clamp(double.IsFinite(Width) ? Width : 0, 0, 1 - x), Math.Clamp(double.IsFinite(Height) ? Height : 0, 0, 1 - y));
    }
}

public enum BackgroundKind { Solid, LinearGradient, Image, Video, Pattern, Dynamic }
public enum BackgroundPatternKind { Grid, Dots }
public enum DynamicBackgroundPreset { Aurora, Ocean, Sunset }
public enum CameraShape { RoundedRectangle, Circle }
public enum OpeningPreset { Custom, ZoomUp, Fade, SlideUp, RotateIn, Converge, SideSlide, Light3D }
public enum OpeningElement { Screen, Camera, Overlays }
public enum BackgroundImageFit { Cover, Contain, Stretch }
public enum ScreenFrameKind { None, WindowLight, WindowDark, BrowserLight, BrowserDark, Phone, Tablet }
public enum CursorStyle { SystemArrow, Dot, Ring }
public enum ClickEffectStyle { Pulse, Ring, None }
public enum FocusEffectShape { Radial, Linear }
public enum FocusEffectTarget { Animation, Pointer, Fixed }
public enum OverlayMotion { None, Fade, Scale, SlideUp, SlideDown, SlideLeft, SlideRight }
public enum BlurKind { Blur, Pixelate }

public sealed record OpeningSettings
{
    public OpeningPreset Preset { get; set; } = OpeningPreset.Custom;
    public bool Enabled { get; set; }
    public double Duration { get; set; } = .8;
    public double ScaleFrom { get; set; } = .85;
    public double OffsetX { get; set; }
    public double OffsetY { get; set; } = .04;
    public double RotationFrom { get; set; }
    public double OpacityFrom { get; set; }
    public ZoomEasing Easing { get; set; } = ZoomEasing.Quintic;
    public double Stagger { get; set; } = .16;
    // Existing projects contain only the original screen opening; keep that behavior on load.
    public List<OpeningElement> IncludedElements { get; set; } = [OpeningElement.Screen];
    public List<OpeningElement> ElementOrder { get; set; } = [OpeningElement.Screen, OpeningElement.Camera, OpeningElement.Overlays];

    public OpeningSettings ResolvePreset() => Preset switch
    {
        OpeningPreset.ZoomUp => this with { ScaleFrom = .72, OffsetY = .045, OffsetX = 0, RotationFrom = 0, OpacityFrom = .25 },
        OpeningPreset.Fade => this with { ScaleFrom = 1, OffsetX = 0, OffsetY = 0, RotationFrom = 0, OpacityFrom = 0 },
        OpeningPreset.SlideUp => this with { ScaleFrom = 1, OffsetX = 0, OffsetY = .16, RotationFrom = 0, OpacityFrom = .15 },
        OpeningPreset.RotateIn => this with { ScaleFrom = .86, OffsetX = 0, OffsetY = .02, RotationFrom = -9, OpacityFrom = .2 },
        OpeningPreset.Converge => this with { ScaleFrom = .72, OffsetX = -1, OffsetY = -.62, RotationFrom = 0, OpacityFrom = 0 },
        OpeningPreset.SideSlide => this with { ScaleFrom = .94, OffsetX = -1, OffsetY = 0, RotationFrom = 0, OpacityFrom = 0 },
        OpeningPreset.Light3D => this with { ScaleFrom = .8, OffsetX = -1, OffsetY = .08, RotationFrom = 0, OpacityFrom = 0 },
        _ => this
    };
}

public sealed record FrameOpeningState(double Scale = 1, double OffsetX = 0, double OffsetY = 0, double Rotation = 0, double Opacity = 1,
    double RotationX = 0, double RotationY = 0, double Perspective = 0);

/// <summary>Canvas-authored source crop and screen-card placement. Dimensions use the current project canvas ratio.</summary>
public sealed record CanvasSettings
{
    public int AspectWidth { get; set; } = 1920;
    public int AspectHeight { get; set; } = 1080;
    public NormalizedRect Crop { get; set; } = NormalizedRect.Full;
    public NormalizedPoint ContentPosition { get; set; } = new(.5, .5);
    public double ContentScale { get; set; } = 1;
    public ScreenFrameKind Frame { get; set; } = ScreenFrameKind.None;
    public double FrameScale { get; set; } = 1;
    public double BorderWidth { get; set; }
    public string BorderColor { get; set; } = "#FFFFFF";
    public string Title { get; set; } = "";
    public string Address { get; set; } = "";
}

public sealed record BackgroundSettings
{
    public BackgroundKind Kind { get; set; } = BackgroundKind.Solid;
    public string Color { get; set; } = "#202124";
    public string EndColor { get; set; } = "#202124";
    public double AngleDegrees { get; set; } = 90;
    public string? ImagePath { get; set; }
    public string? VideoPath { get; set; }
    public bool VideoLoop { get; set; } = true;
    public double VideoStartTime { get; set; }
    public BackgroundImageFit ImageFit { get; set; } = BackgroundImageFit.Cover;
    public double Blur { get; set; }
    public BackgroundPatternKind Pattern { get; set; } = BackgroundPatternKind.Grid;
    public double PatternSpacing { get; set; } = 48;
    public double PatternStrength { get; set; } = .14;
    public string PatternColor { get; set; } = "#FFFFFF";
    public DynamicBackgroundPreset Preset { get; set; } = DynamicBackgroundPreset.Aurora;
    public double Speed { get; set; } = 1;
}

public sealed record CursorSettings
{
    public bool Visible { get; set; } = true;
    public CursorStyle Style { get; set; } = CursorStyle.SystemArrow;
    public double Scale { get; set; } = 1;
    public bool HideWhenIdle { get; set; }
    public double IdleSeconds { get; set; } = 1.5;
    public bool ClickEffectsEnabled { get; set; } = true;
    public ClickEffectStyle ClickEffect { get; set; } = ClickEffectStyle.Pulse;
    public string ClickColor { get; set; } = "#FFE066";
    public double ClickDuration { get; set; } = .3;
}

public sealed record ScreenMotionState
{
    public NormalizedPoint Position { get; set; } = new(.5, .5);
    public double Scale { get; set; } = 1;
    public double RotationX { get; set; }
    public double RotationY { get; set; }
    public double RotationZ { get; set; }
    public double Perspective { get; set; } = .65;
}

/// <summary>One source-time screen transform. It transitions from the previous target, holds, then optionally returns.</summary>
public sealed record ScreenMotionClip
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public double Start { get; set; }
    public double Duration { get; set; } = 1;
    public double EnterDuration { get; set; } = .6;
    public double ReturnDuration { get; set; } = .6;
    public ZoomEasing Easing { get; set; } = ZoomEasing.Cubic;
    public ScreenMotionState Target { get; set; } = new();
    public FocusEffect? Focus { get; set; }
    public Guid? GroupId { get; set; }
    public double End => Start + Duration;
}

public sealed record FocusEffect
{
    public FocusEffectShape Shape { get; set; } = FocusEffectShape.Radial;
    public FocusEffectTarget Target { get; set; } = FocusEffectTarget.Animation;
    public NormalizedPoint Center { get; set; } = new(.5, .5);
    public double Size { get; set; } = .45;
    public double Blur { get; set; } = .35;
    public double Softness { get; set; } = .5;
    public double Dimming { get; set; } = .18;
    public double AngleDegrees { get; set; }
}

/// <summary>Project-relative image layer. All placement and animation values are source-time authored.</summary>
public sealed record OverlayClip
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string MediaPath { get; set; } = "";
    public double Start { get; set; }
    public double Duration { get; set; } = 1;
    public NormalizedRect Rect { get; set; } = new(.35, .35, .3, .3);
    public double Rotation { get; set; }
    public double Opacity { get; set; } = 1;
    public int Layer { get; set; }
    public OverlayMotion EnterMotion { get; set; } = OverlayMotion.Fade;
    public OverlayMotion ExitMotion { get; set; } = OverlayMotion.Fade;
    public double EnterDuration { get; set; } = .25;
    public double ExitDuration { get; set; } = .25;
    public double CornerRadius { get; set; }
    public double Blur { get; set; }
    public double End => Start + Duration;
}

/// <summary>Source-time canvas region effect. Strength is normalized to [0,1].</summary>
public sealed record BlurClip
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public double Start { get; set; }
    public double Duration { get; set; } = 1;
    public NormalizedRect Rect { get; set; } = new(.25, .25, .5, .5);
    public double Strength { get; set; } = .65;
    public BlurKind Kind { get; set; } = BlurKind.Blur;
    public double CornerRadius { get; set; } = .02;
    public double EnterDuration { get; set; } = .15;
    public double ExitDuration { get; set; } = .15;
    public double End => Start + Duration;
}

/// <summary>Independent camera movie mapped onto the main-video source clock.</summary>
public sealed record CameraTrack
{
    public string MediaPath { get; set; } = "";
    public double SourceStart { get; set; }
    public double Duration { get; set; }
    public double TimelineStart { get; set; }
    public NormalizedPoint Position { get; set; } = new(.82, .8);
    public double Size { get; set; } = .28;
    public double CornerRadius { get; set; } = .12;
    public double BorderWidth { get; set; }
    public string BorderColor { get; set; } = "#FFFFFF";
    public double Opacity { get; set; } = 1;
    public double Volume { get; set; } = 1;
    public bool Muted { get; set; } = true;
    public bool Mirrored { get; set; }
    public CameraShape Shape { get; set; } = CameraShape.RoundedRectangle;
    public NormalizedRect Crop { get; set; } = NormalizedRect.Full;
    /// <summary>Optional disjoint mappings from camera-asset time to the original screen-source clock.</summary>
    public List<CameraSyncSegment>? SyncSegments { get; set; }
}

/// <summary>Maps [SourceStart, SourceStart+Duration) in a camera asset to [TimelineStart, TimelineStart+Duration) in original screen source time.</summary>
public sealed record CameraSyncSegment(double SourceStart, double TimelineStart, double Duration);

/// <summary>Edits segmented camera mappings without collapsing pause gaps.</summary>
public static class CameraTimelineOperations
{
    public static void TrimTimelineStart(CameraTrack track, double newScreenSourceStart)
        => TrimByTimeline(track, newScreenSourceStart, double.PositiveInfinity);

    public static void TrimTimelineEnd(CameraTrack track, double newScreenSourceEnd)
        => TrimByTimeline(track, double.NegativeInfinity, newScreenSourceEnd);

    public static void Move(CameraTrack track, double deltaScreenSource)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (!double.IsFinite(deltaScreenSource)) throw new ArgumentOutOfRangeException(nameof(deltaScreenSource));
        track.TimelineStart += deltaScreenSource;
        if (track.SyncSegments is { Count: > 0 } segments)
            Set(track, segments.Select(x => x with { TimelineStart = x.TimelineStart + deltaScreenSource }).ToList());
    }

    public static void TrimAssetStart(CameraTrack track, double newAssetSourceStart, double assetDuration)
        => TrimByAsset(track, newAssetSourceStart, assetDuration);

    public static void TrimAssetEnd(CameraTrack track, double newAssetSourceEnd, double assetDuration)
        => TrimByAsset(track, double.NegativeInfinity, Math.Min(newAssetSourceEnd, assetDuration));

    /// <summary>Slips the asset window while keeping every mapped screen-source time fixed.</summary>
    public static double SlipAssetStart(CameraTrack track, double newAssetSourceStart, double assetDuration)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (!double.IsFinite(newAssetSourceStart) || !double.IsFinite(assetDuration) || assetDuration <= 0)
            throw new ArgumentOutOfRangeException(nameof(assetDuration), "摄像头素材起点或时长无效。");
        var source = Segments(track);
        var currentFirst = source.Min(x => x.SourceStart);
        var minSource = source.Min(x => x.SourceStart);
        var maxEnd = source.Max(x => x.SourceStart + x.Duration);
        var minimumDelta = -minSource;
        var maximumDelta = Math.Max(minimumDelta, assetDuration - maxEnd);
        var delta = Math.Clamp(newAssetSourceStart - currentFirst, minimumDelta, maximumDelta);
        if (!double.IsFinite(delta)) delta = 0;
        if (track.SyncSegments is not { Count: > 0 })
        {
            track.SourceStart = Math.Clamp(track.SourceStart + delta, 0, Math.Max(0, assetDuration - track.Duration));
            return track.SourceStart;
        }
        Set(track, source.Select(x => x with { SourceStart = x.SourceStart + delta }).ToList());
        return track.SourceStart;
    }

    private static void TrimByTimeline(CameraTrack track, double start, double end)
    {
        ArgumentNullException.ThrowIfNull(track);
        if ((!double.IsFinite(start) && !double.IsNegativeInfinity(start)) || (!double.IsFinite(end) && !double.IsPositiveInfinity(end)) || start >= end)
            throw new ArgumentOutOfRangeException(nameof(start), "摄像头裁切时间范围无效。");
        var source = Segments(track);
        var result = new List<CameraSyncSegment>();
        foreach (var item in source)
        {
            var left = Math.Max(item.TimelineStart, start); var right = Math.Min(item.TimelineStart + item.Duration, end);
            if (right - left > 1e-6) result.Add(new(item.SourceStart + left - item.TimelineStart, left, right - left));
        }
        Set(track, result);
    }

    private static void TrimByAsset(CameraTrack track, double start, double end)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (!double.IsFinite(end) || end <= 0 || (!double.IsFinite(start) && !double.IsNegativeInfinity(start)) || start >= end)
            throw new ArgumentOutOfRangeException(nameof(end), "摄像头素材裁切范围无效。");
        var source = Segments(track);
        var result = new List<CameraSyncSegment>();
        foreach (var item in source)
        {
            var left = Math.Max(item.SourceStart, start); var right = Math.Min(item.SourceStart + item.Duration, end);
            if (right - left > 1e-6) result.Add(new(left, item.TimelineStart + left - item.SourceStart, right - left));
        }
        Set(track, result);
    }

    private static List<CameraSyncSegment> Segments(CameraTrack track)
        => track.SyncSegments is { Count: > 0 } segments
            ? segments.OrderBy(x => x.TimelineStart).ToList()
            : [new(track.SourceStart, track.TimelineStart, track.Duration)];

    private static void Set(CameraTrack track, List<CameraSyncSegment> segments)
    {
        if (segments.Count == 0) throw new InvalidOperationException("裁切后摄像头轨道没有可用片段。");
        segments.Sort((a, b) => a.TimelineStart.CompareTo(b.TimelineStart));
        for (var i = 1; i < segments.Count; i++)
            if (segments[i - 1].TimelineStart + segments[i - 1].Duration > segments[i].TimelineStart + 1e-6)
                throw new InvalidOperationException("编辑会导致摄像头同步片段重叠。");
        track.SyncSegments = segments;
        track.SourceStart = segments[0].SourceStart;
        track.TimelineStart = segments[0].TimelineStart;
        track.Duration = segments[^1].TimelineStart + segments[^1].Duration - track.TimelineStart;
    }
}

/// <summary>Source-time camera inset motion; target coordinates are normalized to the project canvas.</summary>
public sealed record CameraMotionClip
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public double Start { get; set; }
    public double Duration { get; set; } = 1;
    public double EnterDuration { get; set; } = .35;
    public double ReturnDuration { get; set; } = .35;
    public ZoomEasing Easing { get; set; } = ZoomEasing.Cubic;
    public NormalizedPoint TargetPosition { get; set; } = new(.8, .8);
    public double TargetSize { get; set; } = .28;
    public double End => Start + Duration;
}

/// <summary>Separate audio file placed on the main-video source clock; offset may be negative.</summary>
public sealed record AudioTrack
{
    public string MediaPath { get; set; } = "";
    public double SourceStart { get; set; }
    public double Duration { get; set; }
    public double OffsetSeconds { get; set; }
    public double Volume { get; set; } = 1;
    public bool Muted { get; set; }
}

public sealed record FrameCameraScene(string MediaPath, double SourceTime, NormalizedRect Rect, double CornerRadius, double BorderWidth, double Opacity, bool Mirrored, double Volume, bool Muted, CameraShape Shape = CameraShape.RoundedRectangle, NormalizedRect Crop = default, string BorderColor = "#FFFFFF", double Rotation = 0);
public sealed record FrameOverlayScene(Guid Id, string MediaPath, NormalizedRect Rect, double Rotation, double Opacity, int Layer, double CornerRadius, double Blur);
public sealed record FrameBlurScene(NormalizedRect Rect, double Strength, BlurKind Kind, double CornerRadius, double Opacity);
