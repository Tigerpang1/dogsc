namespace FrameTrail;

/// <summary>DogSC FrameScene.stickerAnimationTransform and independent entry/exit timing.</summary>
public static class OverlayMotionEvaluator
{
    public static (NormalizedRect Rect, double Opacity) Sample(OverlayClip clip, double time, bool suppressEntry = false)
    {
        var enterDuration = Math.Max(clip.EnterDuration, 0);
        var exitDuration = Math.Max(clip.ExitDuration, 0);
        var total = enterDuration + exitDuration;
        if (total > clip.Duration && total > 0)
        {
            enterDuration *= clip.Duration / total;
            exitDuration *= clip.Duration / total;
        }
        var enter = suppressEntry || enterDuration <= 0 ? 1 : Math.Clamp((time - clip.Start) / enterDuration, 0, 1);
        var exit = exitDuration <= 0 ? 1 : Math.Clamp((clip.End - time) / exitDuration, 0, 1);
        var curve = clip.AnimationCurve ?? ElementMotionCurve.Swift;
        enter = OpeningMotionEvaluator.Progress(enter, curve);
        exit = OpeningMotionEvaluator.Progress(exit, curve);
        var visibility = Math.Min(clip.EnterMotion == OverlayMotion.None ? 1 : enter, clip.ExitMotion == OverlayMotion.None ? 1 : exit);
        var exiting = exitDuration > 0 && clip.End - time < exitDuration;
        var preset = exiting ? clip.ExitMotion : clip.EnterMotion;
        var progress = exiting ? exit : enter;
        var rect = Transform(clip.Rect, preset, progress);
        return (rect, clip.Opacity * visibility);
    }

    public static NormalizedRect Transform(NormalizedRect rect, OverlayMotion motion, double progress)
    {
        var p = Math.Clamp(progress, 0, 1);
        var travel = 1 - p;
        var centerX = rect.X + rect.Width / 2; var centerY = rect.Y + rect.Height / 2;
        var halfExtent = Math.Max(rect.Width, .02) / 2;
        var leading = Math.Min(-.85, -halfExtent - .06); var trailing = Math.Max(1.85, 1 + halfExtent + .06);
        var dx = motion switch
        {
            OverlayMotion.SlideLeft or OverlayMotion.SlideTopLeft or OverlayMotion.SlideBottomLeft => (leading - centerX) * travel,
            OverlayMotion.SlideRight or OverlayMotion.SlideTopRight or OverlayMotion.SlideBottomRight => (trailing - centerX) * travel,
            _ => 0
        };
        var dy = motion switch
        {
            OverlayMotion.SlideUp or OverlayMotion.SlideTopLeft or OverlayMotion.SlideTopRight => (leading - centerY) * travel,
            OverlayMotion.SlideDown or OverlayMotion.SlideBottomLeft or OverlayMotion.SlideBottomRight => (trailing - centerY) * travel,
            _ => 0
        };
        var scale = motion == OverlayMotion.Scale ? .72 + p * .28 : 1;
        return new(centerX - rect.Width * scale / 2 + dx, centerY - rect.Height * scale / 2 + dy, rect.Width * scale, rect.Height * scale);
    }

    public static OverlayMotion AutomaticExit(OverlayMotion motion) => motion switch
    {
        OverlayMotion.SlideLeft => OverlayMotion.SlideRight, OverlayMotion.SlideRight => OverlayMotion.SlideLeft,
        OverlayMotion.SlideUp => OverlayMotion.SlideDown, OverlayMotion.SlideDown => OverlayMotion.SlideUp,
        OverlayMotion.SlideTopLeft => OverlayMotion.SlideBottomRight, OverlayMotion.SlideTopRight => OverlayMotion.SlideBottomLeft,
        OverlayMotion.SlideBottomLeft => OverlayMotion.SlideTopRight, OverlayMotion.SlideBottomRight => OverlayMotion.SlideTopLeft,
        _ => motion
    };
}
