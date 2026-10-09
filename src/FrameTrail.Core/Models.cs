using System.Text.Json.Serialization;

namespace FrameTrail;

public readonly record struct NormalizedPoint(double X, double Y)
{
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);
    public NormalizedPoint Clamp(double inset = 0) => new(Math.Clamp(X, inset, 1 - inset), Math.Clamp(Y, inset, 1 - inset));
}

public enum PointerEventKind { Move, LeftClick, RightClick }
public sealed record PointerEvent(double Time, NormalizedPoint Location, PointerEventKind Kind = PointerEventKind.Move, string? CursorAsset = null);
public enum ZoomEasing { Linear, Cubic, Quintic, Spring }
public sealed record ZoomClip
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public double Start { get; set; }
    public double End { get; set; }
    public double Scale { get; set; } = 1.6;
    public NormalizedPoint Focus { get; set; } = new(.5, .5);
    public double EnterDuration { get; set; } = .7;
    public double ExitDuration { get; set; } = .7;
    public ZoomEasing Easing { get; set; } = ZoomEasing.Spring;
    public bool Automatic { get; set; }
}
public sealed record TimelineSegment
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public double SourceStart { get; set; }
    public double SourceDuration { get; set; }
    public double PlaybackRate { get; set; } = 1;
    [JsonIgnore] public double SourceEnd => SourceStart + SourceDuration;
    [JsonIgnore] public double OutputDuration => SourceDuration / PlaybackRate;
    public TimelineSegment Copy(Guid? id = null) => new() { Id = id ?? Id, SourceStart = SourceStart, SourceDuration = SourceDuration, PlaybackRate = PlaybackRate };
}

public sealed class ProjectTimeline
{
    public List<TimelineSegment> Segments { get; set; } = [];
    public double Duration => Segments.Sum(x => x.OutputDuration);

    public void Validate()
    {
        if (Segments.Count == 0) throw new InvalidDataException("时间线至少需要一个片段。");
        if (Segments.Select(x => x.Id).Distinct().Count() != Segments.Count) throw new InvalidDataException("片段 ID 重复。");
        foreach (var s in Segments)
            if (!double.IsFinite(s.SourceStart) || !double.IsFinite(s.SourceDuration) || !double.IsFinite(s.PlaybackRate) || s.SourceStart < 0 || s.SourceDuration <= 0 || s.PlaybackRate is < 1 or > 100 || !double.IsFinite(s.SourceEnd) || !double.IsFinite(s.OutputDuration))
                throw new InvalidDataException($"片段 {s.Id} 的时间或速度无效。");
    }

    public (TimelineSegment Left, TimelineSegment Right) Split(Guid id, double sourceTime)
    {
        var i = Index(id); var s = Segments[i];
        if (!(sourceTime > s.SourceStart && sourceTime < s.SourceEnd)) throw new ArgumentOutOfRangeException(nameof(sourceTime), "拆分点必须位于片段内部。");
        var left = s.Copy(); left.SourceDuration = sourceTime - s.SourceStart;
        var right = s.Copy(Guid.NewGuid()); right.SourceStart = sourceTime; right.SourceDuration = s.SourceEnd - sourceTime;
        Segments[i] = left; Segments.Insert(i + 1, right); return (left, right);
    }

    public void Trim(Guid id, double sourceStart, double sourceEnd)
    {
        var s = Segments[Index(id)];
        if (!double.IsFinite(sourceStart) || !double.IsFinite(sourceEnd) || sourceStart < s.SourceStart || sourceEnd > s.SourceEnd || sourceEnd <= sourceStart) throw new ArgumentOutOfRangeException(nameof(sourceStart), "裁剪范围必须是片段内的非空区间。");
        s.SourceStart = sourceStart; s.SourceDuration = sourceEnd - sourceStart;
    }
    public void SetPlaybackRate(Guid id, double rate)
    {
        if (!double.IsFinite(rate) || rate is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(rate));
        Segments[Index(id)].PlaybackRate = rate;
    }
    public void Reorder(Guid id, int newIndex)
    {
        var old = Index(id); if ((uint)newIndex >= (uint)Segments.Count) throw new ArgumentOutOfRangeException(nameof(newIndex));
        var value = Segments[old]; Segments.RemoveAt(old); Segments.Insert(newIndex, value);
    }
    public double MapOutputToSource(double outputTime)
    {
        if (!double.IsFinite(outputTime) || outputTime < 0 || outputTime > Duration) throw new ArgumentOutOfRangeException(nameof(outputTime));
        var elapsed = 0d;
        for (var i = 0; i < Segments.Count; i++) { var s = Segments[i]; var end = elapsed + s.OutputDuration; if (outputTime < end || i == Segments.Count - 1) return s.SourceStart + Math.Clamp(outputTime - elapsed, 0, s.OutputDuration) * s.PlaybackRate; elapsed = end; }
        var last = Segments[^1]; return last.SourceEnd;
    }
    public double? MapSourceToOutput(double sourceTime)
    {
        if (!double.IsFinite(sourceTime) || sourceTime < 0) throw new ArgumentOutOfRangeException(nameof(sourceTime));
        var elapsed = 0d;
        foreach (var s in Segments) { if (sourceTime >= s.SourceStart && sourceTime <= s.SourceEnd) return elapsed + (sourceTime - s.SourceStart) / s.PlaybackRate; elapsed += s.OutputDuration; }
        return null;
    }
    private int Index(Guid id) { var i = Segments.FindIndex(x => x.Id == id); return i >= 0 ? i : throw new KeyNotFoundException($"找不到片段 {id}。"); }
}

public sealed record MouseClockCalibration(double OffsetSeconds = 0, double Rate = 1)
{
    public double ToSourceTime(double pointerTime)
    {
        if (!double.IsFinite(pointerTime) || !double.IsFinite(OffsetSeconds) || !double.IsFinite(Rate) || Rate <= 0) throw new ArgumentOutOfRangeException(nameof(pointerTime));
        return Math.Max(0, pointerTime * Rate + OffsetSeconds);
    }
}

public sealed class FrameTrailProject
{
    public string Format { get; init; } = "FrameTrailProject";
    public int FormatVersion { get; init; } = 1;
    public Guid ProjectId { get; init; } = Guid.NewGuid();
    public string MediaPath { get; set; } = "";
    public MediaAsset Media { get; set; } = new();
    public ProjectTimeline Timeline { get; set; } = new();
    public List<PointerEvent> PointerEvents { get; set; } = [];
    public List<RecordingMarker> Markers { get; set; } = [];
    public MouseClockCalibration MouseClock { get; set; } = new();
    public List<ZoomClip> ZoomClips { get; set; } = [];
    public FrameTrailSettings Settings { get; set; } = new();
}

public sealed record MediaAsset
{
    public double Width { get; set; }
    public double Height { get; set; }
    public double Duration { get; set; }
}
public sealed record RecordingMarker(Guid Id, double SourceTime, string Label);

public sealed record FrameTrailSettings
{
    public int CanvasWidth { get; set; } = 1920;
    public int CanvasHeight { get; set; } = 1080;
    public string Background { get; set; } = "#202124";
    public double Padding { get; set; } = .08;
    public double CornerRadius { get; set; } = .02;
    public double Shadow { get; set; } = .3;
    public bool CursorVisible { get; set; } = true;
    public bool AutoZoom { get; set; } = true;
}

public sealed record FrameScene(double OutputTime, double SourceTime, NormalizedPoint CropOrigin, NormalizedPoint CropSize, double Zoom, NormalizedPoint? Cursor, double ClickStrength, string Background, double Padding, double CornerRadius, int CanvasWidth, int CanvasHeight, double Shadow);
