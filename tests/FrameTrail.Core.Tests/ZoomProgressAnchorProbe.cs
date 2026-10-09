using System.Text.Json;
using FrameTrail;

internal static class ZoomProgressAnchorProbe
{
    public static void Run()
    {
        var project = new FrameTrailProject
        {
            MediaPath = "capture.mp4",
            Media = new MediaAsset { Duration = 5 },
            Timeline = new ProjectTimeline { Segments = [new TimelineSegment { SourceStart = 0, SourceDuration = 5 }] },
            Settings = new FrameTrailSettings { AutoZoom = false },
            ZoomClips =
            [
                new ZoomClip
                {
                    Start = 1, End = 3, Scale = 2, EnterDuration = 1, ExitDuration = 1,
                    Easing = ZoomEasing.Linear, EnterProgressOffset = .25, ExitProgressOffset = .4
                }
            ]
        };

        var enter = FrameSceneEvaluator.Evaluate(project, 1.5).Zoom;
        var exit = FrameSceneEvaluator.Evaluate(project, 3.5).Zoom;
        if (Math.Abs(enter - 1.625) > 1e-9)
            throw new Exception($"Zoom entrance phase offset was not applied: {enter}.");
        if (Math.Abs(exit - 1.3) > 1e-9)
            throw new Exception($"Zoom exit phase offset was not applied: {exit}.");
        if (Math.Abs(FrameSceneEvaluator.Evaluate(project, 2.5).Zoom - 2) > 1e-9
            || Math.Abs(FrameSceneEvaluator.Evaluate(project, 3).Zoom - 2) > 1e-9)
            throw new Exception("Exit progress offset must not reduce the preceding full-scale hold interval.");

        var roundTrip = JsonSerializer.Deserialize<ZoomClip>(JsonSerializer.Serialize(project.ZoomClips[0]))!;
        if (roundTrip.EnterProgressOffset != .25 || roundTrip.ExitProgressOffset != .4)
            throw new Exception("Zoom progress phase offsets did not survive JSON persistence.");

        var legacy = JsonSerializer.Deserialize<ZoomClip>("{\"start\":1,\"end\":2}")!;
        if (legacy.EnterProgressOffset != 0 || legacy.ExitProgressOffset != 0)
            throw new Exception("Legacy zoom clips must default phase offsets to zero.");
    }
}
