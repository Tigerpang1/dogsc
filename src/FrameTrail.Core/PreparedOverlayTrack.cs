namespace FrameTrail;

public sealed record FrameOverlayBackdrop(double ScreenSuppression = 0, double CameraSuppression = 0, double ScreenBlur = 0, double CameraBlur = 0)
{
    internal static FrameOverlayBackdrop From(OverlayClip c, double p) => new(c.HidesScreen ? p : 0, c.HidesCamera ? p : 0,
        c.HidesScreen ? 0 : c.BackdropBlur * p, c.BackdropBlurIncludesCamera ? c.BackdropBlur * p : 0);
    internal FrameOverlayBackdrop Merge(FrameOverlayBackdrop b) => new(Math.Max(ScreenSuppression, b.ScreenSuppression), Math.Max(CameraSuppression, b.CameraSuppression), Math.Max(ScreenBlur, b.ScreenBlur), Math.Max(CameraBlur, b.CameraBlur));
    internal FrameOverlayBackdrop Mix(FrameOverlayBackdrop b, double t) => new(ScreenSuppression + (b.ScreenSuppression - ScreenSuppression) * t,
        CameraSuppression + (b.CameraSuppression - CameraSuppression) * t, ScreenBlur + (b.ScreenBlur - ScreenBlur) * t, CameraBlur + (b.CameraBlur - CameraBlur) * t);
}

/// <summary>DogSC StickerTransitionTrack joins and backdrop bridges, prepared once per edit.</summary>
public sealed class PreparedOverlayTrack
{
    public sealed record Sample(OverlayClip Clip, double Visibility, OverlayMotion Motion, double Progress);
    private sealed class Entry(OverlayClip clip)
    {
        public readonly OverlayClip Clip = clip;
        private double Budget => Clip.EnterDuration + Clip.ExitDuration > Clip.Duration ? Clip.Duration / (Clip.EnterDuration + Clip.ExitDuration) : 1;
        public double Enter => Clip.EnterDuration * Budget;
        public double Exit => Clip.ExitDuration * Budget;
        public double JoinEnter => Clip.EnterMotion == OverlayMotion.None ? 0 : Enter;
        public double JoinExit => Clip.ExitMotion == OverlayMotion.None ? 0 : Exit;
        public Join? EntryJoin, ExitJoin;
    }
    private sealed record Join(double Start, double End, FrameOverlayBackdrop From, FrameOverlayBackdrop To, ElementMotionCurve Curve)
    {
        public bool Contains(double t) => t >= Start && t < End;
        public double Progress(double t) => End > Start ? Math.Clamp((t - Start) / (End - Start), 0, 1) : t >= End ? 1 : 0;
    }
    private readonly Entry[] entries;
    private readonly List<Join> joins = [];
    private readonly List<(double Start, double End, FrameOverlayBackdrop Treatment)> bridges = [];
    public PreparedOverlayTrack(IEnumerable<OverlayClip> clips)
    {
        entries = clips.OrderBy(c => c.Layer).ThenBy(c => c.Id).Select(c => new Entry(c)).ToArray();
        var endpoints = entries.Where(e => e.Clip.AnimationCurve is not null).SelectMany(e => new[] { (Time: e.Clip.Start, Entry: e, Start: true), (Time: e.Clip.End, Entry: e, Start: false) }).OrderBy(e => e.Time).ToArray();
        for (var cursor = 0; cursor < endpoints.Length;)
        {
            var time = endpoints[cursor].Time; var group = new List<(double Time, Entry Entry, bool Start)>();
            do { group.Add(endpoints[cursor++]); } while (cursor < endpoints.Length && endpoints[cursor].Time - time <= 1e-7);
            var outgoing = group.Where(x => !x.Start).Select(x => x.Entry).ToArray();
            var incoming = group.Where(x => x.Start).Select(x => x.Entry).ToArray();
            var shared = outgoing.Intersect(incoming).ToHashSet(); outgoing = outgoing.Where(e => !shared.Contains(e)).ToArray(); incoming = incoming.Where(e => !shared.Contains(e)).ToArray();
            if (outgoing.Length == 0 || incoming.Length == 0) continue;
            var from = outgoing.Aggregate(new FrameOverlayBackdrop(), (a, e) => a.Merge(FrameOverlayBackdrop.From(e.Clip, 1)));
            var to = incoming.Aggregate(new FrameOverlayBackdrop(), (a, e) => a.Merge(FrameOverlayBackdrop.From(e.Clip, 1)));
            var join = new Join(outgoing.Min(e => e.Clip.End) - outgoing.Min(e => e.JoinExit), incoming.Max(e => e.Clip.Start) + incoming.Min(e => e.JoinEnter), from, to, incoming[^1].Clip.AnimationCurve!.Value);
            joins.Add(join); foreach (var e in outgoing) e.ExitJoin = join; foreach (var e in incoming) e.EntryJoin = join;
        }
        var ordered = entries.Where(e => e.Clip.AnimationCurve is not null).OrderBy(e => e.Clip.Start).ThenBy(e => e.Clip.End).ToArray();
        for (var i = 0; i < ordered.Length; i++)
        for (var j = i + 1; j < ordered.Length; j++)
        {
            var a = ordered[i]; var b = ordered[j];
            if (b.Clip.Start >= a.Clip.End - 1e-7) break;
            if (b.Clip.End <= a.Clip.End) continue;
            var start = a.Clip.End - a.JoinExit; var end = b.Clip.Start + b.JoinEnter;
            if (end <= start) continue;
            var x = FrameOverlayBackdrop.From(a.Clip, 1); var y = FrameOverlayBackdrop.From(b.Clip, 1);
            var treatment = new FrameOverlayBackdrop(Math.Min(x.ScreenSuppression, y.ScreenSuppression), Math.Min(x.CameraSuppression, y.CameraSuppression), Math.Min(x.ScreenBlur, y.ScreenBlur), Math.Min(x.CameraBlur, y.CameraBlur));
            if (treatment == new FrameOverlayBackdrop()) continue;
            if (bridges.Count >= 65536) throw new InvalidDataException("贴图重叠交接超过 65536 个安全上限，请减少同时交叠的贴图片段。");
            bridges.Add((start, end, treatment));
        }
    }
    public (IReadOnlyList<Sample> Overlays, FrameOverlayBackdrop Backdrop) Evaluate(double time, bool suppressInitialEntry = false, double initialSourceTime = 0)
    {
        var backdrop = new FrameOverlayBackdrop(); var samples = new List<Sample>();
        foreach (var b in bridges) if (time >= b.Start && time < b.End) backdrop = backdrop.Merge(b.Treatment);
        foreach (var j in joins) if (j.Contains(time)) backdrop = backdrop.Merge(j.From.Mix(j.To, OpeningMotionEvaluator.Progress(j.Progress(time), j.Curve)));
        foreach (var e in entries)
        {
            var c = e.Clip; var joinedEntry = e.EntryJoin?.Contains(time) == true; var joinedExit = e.ExitJoin?.Contains(time) == true;
            if ((time < c.Start || time >= c.End) && !joinedEntry && !joinedExit) continue;
            var enter = e.EntryJoin?.Progress(time) ?? (suppresses(c) || e.Enter <= 0 ? 1 : Math.Clamp((time - c.Start) / e.Enter, 0, 1));
            var exit = e.ExitJoin is { } join ? 1 - join.Progress(time) : e.Exit <= 0 ? 1 : Math.Clamp((c.End - time) / e.Exit, 0, 1);
            if (c.AnimationCurve is { } curve) { enter = OpeningMotionEvaluator.Progress(enter, curve); exit = OpeningMotionEvaluator.Progress(exit, curve); }
            var visibility = Math.Min(c.EnterMotion == OverlayMotion.None ? 1 : enter, c.ExitMotion == OverlayMotion.None ? 1 : exit);
            var exiting = e.ExitJoin is { } ej ? time >= ej.Start : e.Exit > 0 && c.End - time < e.Exit;
            if (!joinedEntry && !joinedExit) backdrop = backdrop.Merge(FrameOverlayBackdrop.From(c, visibility));
            samples.Add(new(c, visibility, exiting ? c.ExitMotion : c.EnterMotion, exiting ? exit : enter));
        }
        return (samples, backdrop);
        bool suppresses(OverlayClip c) => suppressInitialEntry && c.Start <= initialSourceTime + .0001;
    }
}
