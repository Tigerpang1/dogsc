using FrameTrail;

internal static class RecordingMarkerTimelineProbe
{
    internal static void Run()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        // These source times represent marker samples after a capture pause has been
        // removed from the recording clock: the pause itself adds no media time.
        var project = new FrameTrailProject
        {
            MediaPath = "media/screen.mp4",
            Media = new() { Width = 1920, Height = 1080, Duration = 8, FrameRate = 60 },
            Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 8 }] },
            Markers =
            [
                new(Guid.Parse("10000000-0000-0000-0000-000000000001"), 2, "暂停前"),
                new(Guid.Parse("10000000-0000-0000-0000-000000000002"), 4, "暂停后接点"),
                new(Guid.Parse("10000000-0000-0000-0000-000000000003"), 4.5, "暂停后"),
                new(Guid.Parse("10000000-0000-0000-0000-000000000004"), 7.5, "裁切范围外")
            ]
        };

        var root = Path.Combine(Path.GetTempPath(), "frametrail-marker-timeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "markers.frametrail");
            FrameTrailProjectStore.Save(file, project);

            var loaded = FrameTrailProjectStore.Load(file);
            var originalSegmentId = loaded.Timeline.Segments[0].Id;
            loaded.Timeline.Trim(originalSegmentId, 1, 7);
            var split = loaded.Timeline.Split(originalSegmentId, 4);
            loaded.Timeline.SetPlaybackRate(split.Left.Id, 2);
            loaded.Timeline.SetPlaybackRate(split.Right.Id, .5);
            loaded.Timeline.Reorder(split.Right.Id, 0);
            FrameTrailProjectStore.Save(file, loaded);

            var reopened = FrameTrailProjectStore.Load(file);
            Check(reopened.Markers.Select(marker => (marker.Id, marker.SourceTime, marker.Label)).SequenceEqual(
                    project.Markers.Select(marker => (marker.Id, marker.SourceTime, marker.Label))),
                "paused-recording marker source times, identity, and labels survive trim, split, reorder, and project reopen");

            // This is the same source-to-output mapping used by the marker jump command.
            Check(reopened.Timeline.MapSourceToOutput(2) == 6.5,
                "a pre-pause marker jumps through a trimmed and 2x segment moved after the resumed segment");
            Check(reopened.Timeline.MapSourceToOutput(4) == 0,
                "a marker on a split boundary resolves to the first matching timeline occurrence after reorder");
            Check(reopened.Timeline.MapSourceToOutput(4.5) == 1,
                "a post-pause marker jumps through the reordered 0.5x source interval");
            Check(reopened.Timeline.MapSourceToOutput(7.5) is null,
                "a marker whose source interval was trimmed away has no output jump target");

            Console.WriteLine("Recording marker timeline tests passed: pause-compacted source times, trim/split/reorder/rate mapping, reopen, and jump targets.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
