using FrameTrail;

/// <summary>Regression derived from upstream RecorderCore/ZoomAnimation.swift adjacent-clip sampling.</summary>
internal static class ZoomHandoffReturnProbe
{
    public static void Run()
    {
        var project = new FrameTrailProject
        {
            MediaPath = "capture.mp4",
            Media = new MediaAsset { Width = 1920, Height = 1080, Duration = 4 },
            Timeline = new ProjectTimeline { Segments = [new TimelineSegment { SourceStart = 0, SourceDuration = 4 }] },
            Settings = new FrameTrailSettings { AutoZoom = false },
            ZoomClips =
            [
                new() { Start = 1, End = 2, Scale = 1.6, Focus = new(.25, .4), EnterDuration = .4, ExitDuration = 0, Easing = ZoomEasing.Linear },
                new() { Start = 2, End = 3, Scale = 2.2, Focus = new(.75, .6), EnterDuration = .5, ExitDuration = .4,
                    Easing = ZoomEasing.Linear, ExitProgressOffset = .25 }
            ]
        };
        var evaluator = new PreparedFrameSceneEvaluator(project);

        var incomingStart = evaluator.Evaluate(2);
        Near(incomingStart.Zoom, 1.6, "adjacent entry starts at the preceding clip scale");
        Near(incomingStart.CropOrigin.X, 0, "adjacent entry starts at the preceding focus with edge-clamped crop");
        Near(incomingStart.CropOrigin.Y, .4 - 1 / 1.6 / 2, "adjacent entry inherits preceding vertical focus");

        var incomingMidpoint = evaluator.Evaluate(2.25);
        Near(incomingMidpoint.Zoom, 1.9, "adjacent entry interpolates from previous scale to target scale");
        Near(incomingMidpoint.CropOrigin.X, .5 - 1 / 1.9 / 2, "adjacent entry interpolates its focus anchor");
        Near(incomingMidpoint.CropOrigin.Y, .5 - 1 / 1.9 / 2, "adjacent entry interpolates both focus axes");

        var hold = evaluator.Evaluate(3);
        Near(hold.Zoom, 2.2, "exit offset does not affect the final hold sample");
        Near(hold.CropOrigin.X, .75 - 1 / 2.2 / 2, "adjacent clip holds its own target focus through the boundary");

        // Swift ZoomAnimation.swift computes exitProgress=1-easing(offset+(1-offset)*local)
        // only after endTime. Linear easing with offset .25 therefore starts return at .75.
        var returnStart = evaluator.Evaluate(3 + 1e-8);
        Near(returnStart.Zoom, 1 + 1.2 * .75, "nonzero exit anchor starts return from base zoom, not the preceding clip scale");
        Near(returnStart.CropOrigin.X, Math.Min(.75 - 1 / returnStart.Zoom / 2, 1 - 1 / returnStart.Zoom),
            "return keeps the current clip's focus while zooming out");

        var returnEnd = evaluator.Evaluate(3.4);
        Near(returnEnd.Zoom, 1, "adjacent clip return reaches base canvas zoom");
        Near(returnEnd.CropOrigin.X, 0, "base zoom restores full-frame crop regardless of the retained focus anchor");
        Near(returnEnd.CropOrigin.Y, 0, "base zoom restores full-frame crop on both axes");
    }

    private static void Near(double actual, double expected, string message)
    {
        if (!double.IsFinite(actual) || Math.Abs(actual - expected) > 2e-6)
            throw new InvalidOperationException($"Zoom handoff return probe failed: {message}; expected {expected}, got {actual}.");
    }
}
