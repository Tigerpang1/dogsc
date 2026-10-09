using FrameTrail;

internal static class CameraMotionStateProbe
{
    internal static void Run()
    {
        const double canvasAspect = 16d / 9;
        var track = new CameraTrack
        {
            Position = new NormalizedPoint(.82, .8),
            Size = .28,
            CornerRadius = .084,
            Opacity = 1,
            Shape = CameraShape.RoundedRectangle
        };
        var basis = CameraMotionEvaluator.Base(track)
            ?? throw new InvalidOperationException("A present camera track must resolve a base pose.");
        Near(basis.Position.X, .82, "base x position");
        Near(basis.Position.Y, .8, "base y position");
        Near(basis.Size, .28, "base size is measured against the canvas short edge");
        Near(basis.AspectRatio, 1, "legacy rounded camera shape keeps its square frame geometry");
        Near(basis.CornerFraction, .3, "legacy radius is converted to a fraction of camera size");
        Near(basis.ToNormalizedCornerRadius(canvasAspect), .084, "base corner radius preserves the previous short-edge radius");
        var authored = CameraMotionEvaluator.Base(track with { Layout = CameraMotionLayoutKind.RoundedRectangle, CornerRadius = .75 },
            CameraMotionLayoutKind.RoundedRectangle, 1, canvasAspect)!.Value;
        Near(authored.CornerFraction, .375, "explicit camera roundness is a 0..1 value resolved to half-radius fraction");
        Near(authored.ToNormalizedCornerRadius(canvasAspect), .105, "explicit camera roundness follows size rather than legacy absolute radius");
        var baseRect = basis.ToNormalizedRect(canvasAspect);
        Near(baseRect.Width, .1575, "landscape normalized width accounts for canvas aspect");
        Near(baseRect.Height, .28, "landscape normalized height uses short-edge size");
        Check(CameraMotionEvaluator.Base(null) is null, "missing legacy camera remains absent");
        var legacyTarget = CameraMotionEvaluator.Target(track, new(.7, .65), .4,
            layout: null, roundness: 0, opacity: 1, cameraAspectRatio: 1.6, canvasAspectRatio: canvasAspect)!.Value;
        var legacyMid = CameraMotionEvaluator.Interpolate(basis, legacyTarget, .5)!.Value;
        Near(legacyMid.ToNormalizedCornerRadius(canvasAspect), .084, "legacy layout keeps its absolute corner radius while size animates");

        var transparentTarget = CameraMotionEvaluator.Target(track, new(.4, .6), .4,
            CameraMotionLayoutKind.RoundedRectangle, roundness: .2, opacity: .4, cameraAspectRatio: 1.6, canvasAspectRatio: canvasAspect)
            ?? throw new InvalidOperationException("A present camera must resolve an explicit target.");
        var transparentMid = CameraMotionEvaluator.Interpolate(basis, transparentTarget, .5)
            ?? throw new InvalidOperationException("A pair of camera poses must interpolate.");
        Near(transparentMid.Opacity, .7, "halfway opacity is continuous");
        Near(transparentMid.Position.X, .61, "halfway position x");
        Near(transparentMid.Position.Y, .7, "halfway position y");
        Near(transparentMid.Size, .34, "halfway size");
        Near(transparentMid.CornerFraction, .2, "halfway roundness is continuous");

        var hiddenTarget = CameraMotionEvaluator.Target(track, new(.1, .2), .6,
            CameraMotionLayoutKind.Vertical, roundness: .6, opacity: 0, cameraAspectRatio: 1.6, canvasAspectRatio: canvasAspect)!.Value;
        var hiddenMid = CameraMotionEvaluator.Interpolate(basis, hiddenTarget, .5)!.Value;
        Near(hiddenMid.Position.X, basis.Position.X, "fade-out retains inherited x position while invisible");
        Near(hiddenMid.Size, basis.Size, "fade-out retains inherited size while invisible");
        Near(hiddenMid.AspectRatio, basis.AspectRatio, "fade-out retains inherited shape while invisible");
        Near(hiddenMid.Opacity, .5, "hidden target fades opacity from the visible source");
        var hiddenSourceMid = CameraMotionEvaluator.Interpolate(basis with { Opacity = 0 }, transparentTarget, .5)!.Value;
        Near(hiddenSourceMid.Position.X, transparentTarget.Position.X, "fade-in uses target geometry while becoming visible");
        Near(hiddenSourceMid.AspectRatio, transparentTarget.AspectRatio, "fade-in uses target shape while becoming visible");
        Near(hiddenSourceMid.Opacity, .2, "hidden source fades opacity toward the visible target");

        var circleTarget = CameraMotionEvaluator.Target(track, new(.82, .8), .28,
            CameraMotionLayoutKind.Circle, roundness: 0, opacity: 1, cameraAspectRatio: 1.6, canvasAspectRatio: canvasAspect)!.Value;
        var roundedToCircle = CameraMotionEvaluator.Interpolate(basis, circleTarget, .5)!.Value;
        Near(roundedToCircle.CornerFraction, .4, "rounded-rectangle to circle interpolates corner fraction without an enum switch");
        Near(roundedToCircle.AspectRatio, 1, "square-to-circle aspect remains continuous");
        var horizontalTarget = CameraMotionEvaluator.Target(track, new(.5, .5), .3,
            CameraMotionLayoutKind.Horizontal, roundness: .8, opacity: 1, cameraAspectRatio: 1.6, canvasAspectRatio: canvasAspect)!.Value;
        Near(horizontalTarget.AspectRatio, 1.5, "horizontal target aspect ratio matches the upstream 3:2 preset");
        Near(horizontalTarget.CornerFraction, .4, "horizontal target roundness resolves continuously");
        var verticalTarget = CameraMotionEvaluator.Target(track, new(.5, .5), .3,
            CameraMotionLayoutKind.Vertical, roundness: .6, opacity: 1, cameraAspectRatio: 1.6, canvasAspectRatio: canvasAspect)!.Value;
        Near(verticalTarget.AspectRatio, .72, "vertical target aspect ratio matches the upstream preset");
        var originalTarget = CameraMotionEvaluator.Target(track, new(.5, .5), .3,
            CameraMotionLayoutKind.Original, roundness: .4, opacity: 1, cameraAspectRatio: 1.6, canvasAspectRatio: canvasAspect)!.Value;
        Near(originalTarget.AspectRatio, 1.6, "original target uses the cropped camera aspect ratio");

        var fullscreenTarget = CameraMotionEvaluator.Target(track, new(.5, .5), .35,
            CameraMotionLayoutKind.Fullscreen, roundness: 0, opacity: .6, cameraAspectRatio: 1.6, canvasAspectRatio: canvasAspect)!.Value;
        Near(fullscreenTarget.FullscreenProgress, 1, "fullscreen target reaches full progress");
        Near(fullscreenTarget.AspectRatio, canvasAspect, "fullscreen target uses canvas aspect");
        var fullscreenMid = CameraMotionEvaluator.Interpolate(basis, fullscreenTarget, .5)!.Value;
        Near(fullscreenMid.FullscreenProgress, .5, "fullscreen progress interpolates continuously");
        Near(fullscreenMid.Opacity, .8, "fullscreen opacity interpolates continuously");
        Near(fullscreenMid.CornerFraction, .15, "fullscreen roundness interpolates continuously");
        var midRect = fullscreenMid.ToNormalizedRect(canvasAspect);
        Near(midRect.X, .2684765625, "halfway fullscreen rect x uses interpolated aspect before fullscreen geometry");
        Near(midRect.Y, .24625, "halfway fullscreen rect y");
        Near(midRect.Width, .623046875, "halfway fullscreen rect width");
        Near(midRect.Height, .6575, "halfway fullscreen rect height");
        Near(fullscreenMid.ToNormalizedCornerRadius(canvasAspect), .098625, "midpoint radius uses interpolated corner fraction and geometry");
        Near(fullscreenTarget.ToNormalizedRect(canvasAspect).X, 0, "fullscreen x reaches canvas edge");
        Near(fullscreenTarget.ToNormalizedRect(canvasAspect).Y, 0, "fullscreen y reaches canvas edge");
        Near(fullscreenTarget.ToNormalizedRect(canvasAspect).Width, 1, "fullscreen width fills canvas");
        Near(fullscreenTarget.ToNormalizedRect(canvasAspect).Height, 1, "fullscreen height fills canvas");
        Near(fullscreenTarget.ToNormalizedCornerRadius(canvasAspect), 0, "fullscreen corner radius reaches zero");

        var before = CameraMotionEvaluator.Interpolate(basis, fullscreenTarget, .5 - 1e-6)!.Value;
        var after = CameraMotionEvaluator.Interpolate(basis, fullscreenTarget, .5 + 1e-6)!.Value;
        Check(Math.Abs(after.Position.X - before.Position.X) < 1e-5
            && Math.Abs(after.Size - before.Size) < 1e-5
            && Math.Abs(after.AspectRatio - before.AspectRatio) < 1e-5
            && Math.Abs(after.CornerFraction - before.CornerFraction) < 1e-5
            && Math.Abs(after.FullscreenProgress - before.FullscreenProgress) < 1e-5
            && Math.Abs(after.Opacity - before.Opacity) < 1e-5, "adjacent camera samples are continuous across the midpoint");
        Check(CameraMotionEvaluator.Interpolate(null, fullscreenTarget, .5) is null, "a missing camera pose remains absent during interpolation");
        Check(CameraMotionEvaluator.Target(null, new(.5, .5), .5, CameraMotionLayoutKind.Fullscreen, 0, 1, 1, canvasAspect) is null,
            "a missing camera track does not create a target pose");
    }

    private static void Near(double actual, double expected, string message)
    {
        if (!double.IsFinite(actual) || Math.Abs(actual - expected) > 1e-9)
            throw new InvalidOperationException($"Camera motion probe failed ({message}): expected {expected}, got {actual}.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Camera motion probe failed: " + message);
    }
}
