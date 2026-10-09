using FrameTrail;

internal static class SlowMotionProbe
{
    internal static void Run()
    {
        var project = new FrameTrailProject
        {
            MediaPath = "screen.mp4", Media = new() { Width = 640, Height = 360, Duration = 4 },
            Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 4 }] },
            Settings = new() { AutoZoom = false }
        };
        var (left, right) = project.Timeline.Split(project.Timeline.Segments[0].Id, 2);
        project.Timeline.SetPlaybackRate(left.Id, .5);
        project.Timeline.SetPlaybackRate(right.Id, .25);
        project.Timeline.Reorder(right.Id, 0);
        project.Timeline.Trim(left.Id, .5, 1.5);
        project.Timeline.Validate();
        Check(project.Timeline.Duration == 10, "slow trim/reorder yields eight plus two output seconds");
        Check(project.Timeline.MapOutputToSource(4) == 3 && project.Timeline.MapOutputToSource(8) == .5
            && project.Timeline.MapOutputToSource(9) == 1, "slow source mapping honors the reordered cut");
        Check(project.Timeline.MapSourceToOutput(1) == 9, "slow inverse mapping retains fractional rates");
        project.PointerEvents = [new(0, new(.1, .2)), new(3, new(.7, .8), PointerEventKind.LeftClick)];
        var scene = new PreparedFrameSceneEvaluator(project).Evaluate(4);
        Check(scene.SourceTime == 3 && scene.ActiveClickKind == PointerEventKind.LeftClick,
            "slow output samples pointer and click at the source-time event");
        var root = Path.Combine(Path.GetTempPath(), "frametrail-slow-core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "project.frametrail");
            FrameTrailProjectStore.Save(file, project);
            var loaded = FrameTrailProjectStore.Load(file);
            Check(loaded.Timeline.Segments[0].PlaybackRate == .25 && loaded.Timeline.Segments[1].PlaybackRate == .5
                && loaded.Timeline.Duration == 10, "slow rates survive project save/reopen");
        }
        finally { Directory.Delete(root, true); }
        foreach (var bad in new[] { 0, -.5, .249, double.NaN, double.PositiveInfinity })
        {
            try { project.Timeline.SetPlaybackRate(left.Id, bad); throw new InvalidOperationException("invalid rate accepted"); }
            catch (ArgumentOutOfRangeException) { }
        }
        Console.WriteLine("通过：0.25×/0.5×慢放的裁剪、重排、指针时间映射与保存重开");
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Slow motion probe failed: " + message); }
}
