namespace FrameTrail.App;

internal static class MemoAutoScroll
{
    internal readonly record struct Step(double Offset, bool ReachedEnd);

    internal static Step Advance(double currentOffset, double maximumOffset, double pixelsPerSecond, double elapsedSeconds)
    {
        var maximum = double.IsFinite(maximumOffset) ? Math.Max(0, maximumOffset) : 0;
        var current = double.IsFinite(currentOffset) ? Math.Clamp(currentOffset, 0, maximum) : 0;
        var speed = double.IsFinite(pixelsPerSecond) ? Math.Clamp(pixelsPerSecond, 8, 180) : 45;
        var elapsed = double.IsFinite(elapsedSeconds) ? Math.Max(0, elapsedSeconds) : 0;
        var next = Math.Min(maximum, current + speed * elapsed);
        return new Step(next, next >= maximum);
    }
}
