using FrameTrail;

internal static class PointerDynamicsProbe
{
    internal static void Run()
    {
        var events = new[]
        {
            new PointerEvent(0, new(.1, .8)),
            new PointerEvent(.1, new(.3, .8)),
            new PointerEvent(.15, new(.4, .8), PointerEventKind.RightClick),
            new PointerEvent(.2, new(.6, .8))
        };
        var track = new PreparedPointerTrack(events, PointerSmoothing.Rapid);
        var dynamics = new PreparedPointerDynamics(track, events);
        var active = dynamics.Sample(.2, PointerSmoothing.Rapid, 2, .1);
        Require(active.Position is not null && active.RotationRadians < 0,
            "rightward cursor motion leans the arrow body behind its hotspot");
        Require(active.ClickProgress is > .49 and < .51 && active.ActiveClickKind == PointerEventKind.RightClick,
            "active click is found by source-time search and reports normalized progress and kind");
        Require(dynamics.Sample(.26, PointerSmoothing.Rapid, 2, .1).ClickProgress is null,
            "click animation expires at the requested duration");
        Require(Math.Abs(dynamics.Sample(.2, PointerSmoothing.Rapid, 0, .1).RotationRadians) < 1e-12,
            "zero tilt strength disables velocity-driven rotation");
        Require(Math.Abs(dynamics.Sample(.2, PointerSmoothing.Rapid, 4, .1).RotationRadians)
                > Math.Abs(active.RotationRadians),
            "higher tilt strength increases the restrained cursor response");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("Pointer dynamics probe failed: " + message);
    }
}
