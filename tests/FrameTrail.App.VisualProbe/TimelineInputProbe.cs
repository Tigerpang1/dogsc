using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Reflection;
using FrameTrail;
using FrameTrail.App;

namespace FrameTrail.App.VisualProbe;

internal static class TimelineInputProbe
{
    public static void Verify(Action<bool, string, object?> check)
    {
        var screenId = Guid.NewGuid();
        var zoomId = Guid.NewGuid();
        var otherZoomId = Guid.NewGuid();
        var project = new FrameTrailProject
        {
            Media = new() { Width = 1920, Height = 1080, Duration = 10, FrameRate = 24 },
            Timeline = new() { Segments = [new() { Id = screenId, SourceStart = 0, SourceDuration = 10 }] },
            ZoomClips = [new() { Id = zoomId, Start = 1, End = 4 }, new() { Id = otherZoomId, Start = 5, End = 8 }]
        };
        var control = new EditorTimelineControl { Project = project };
        control.Refresh();
        control.Measure(new Size(1152, 400));
        control.Arrange(new Rect(0, 0, 1152, control.Height));
        control.UpdateLayout();
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
            typeof(EditorTimelineControl).GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(control, [context]);

        var events = new List<string>();
        var seeks = new List<double>();
        var scrubs = new List<double>();
        control.SeekRequested += time => { seeks.Add(time); events.Add("seek"); };
        control.ScrubRequested += time => { scrubs.Add(time); events.Add("scrub"); };
        control.SelectionRequested += selection => events.Add("selection:" + selection.Kind);

        control.HandlePointerMove(new Point(276, 12), false);
        control.HandlePointerMove(new Point(80, 155), false);
        var expectedFrame = 29d / 24;
        check(seeks.Count == 0 && scrubs.Count == 0,
            "passive pointer movement does not seek or request a preview", new { Seeks = seeks, Scrubs = scrubs, ExpectedFrame = expectedFrame });

        control.HandlePointerDown(new Point(276, 12), MouseButton.Left, ModifierKeys.None, false);
        control.HandlePointerUp(MouseButton.Left, new Point(276, 12), false);
        check(seeks.Count == 1 && scrubs.Count == 0 && Math.Abs(seeks[0] - expectedFrame) < 1e-9,
            "left click seeks immediately once without a redundant release seek", new { Seeks = seeks, Scrubs = scrubs });

        seeks.Clear(); scrubs.Clear();
        control.HandlePointerDown(new Point(1152, 12), MouseButton.Left, ModifierKeys.None, false);
        control.HandlePointerUp(MouseButton.Left, new Point(1152, 12), false);
        check(seeks.Count == 1 && Math.Abs(seeks[0] - 239d / 24) < 1e-9,
            "click at the duration endpoint seeks to the last decodable source frame", new { Seeks = seeks, Expected = 239d / 24 });

        seeks.Clear(); scrubs.Clear(); events.Clear();
        var scrubStart = new Point(300, 12);
        var scrubMove = new Point(500, 12);
        var scrubRelease = new Point(700, 12);
        control.HandlePointerDown(scrubStart, MouseButton.Left, ModifierKeys.None, false);
        control.HandlePointerMove(scrubMove, true);
        control.HandlePointerUp(MouseButton.Left, scrubRelease, false);
        var expectedScrubMove = Math.Floor((scrubMove.X - 152) / 1000 * 10 * 24) / 24;
        var expectedScrubRelease = Math.Floor((scrubRelease.X - 152) / 1000 * 10 * 24) / 24;
        check(seeks.Count == 1 && scrubs.Count == 2 && Math.Abs(scrubs[0] - expectedScrubMove) < 1e-9 &&
              Math.Abs(scrubs[^1] - expectedScrubRelease) < 1e-9,
            "held left drag scrubs and release commits its final position", new { Seeks = seeks, Scrubs = scrubs, Expected = new[] { expectedScrubMove, expectedScrubRelease } });

        seeks.Clear(); scrubs.Clear();
        control.HandlePointerDown(scrubStart, MouseButton.Left, ModifierKeys.None, false);
        control.HandlePointerMove(scrubMove, true);
        control.HandlePointerUp(MouseButton.Left, scrubStart, false);
        check(scrubs.Count == 2 && Math.Abs(scrubs[^1] - FrameAt(scrubStart.X)) < 1e-9,
            "release at the drag origin still commits the final scrub position", new { Scrubs = scrubs, Expected = FrameAt(scrubStart.X) });
        var seeksAfterRelease = seeks.Count; var scrubsAfterRelease = scrubs.Count;
        control.HandlePointerMove(new Point(900, 12), false);
        check(seeks.Count == seeksAfterRelease && scrubs.Count == scrubsAfterRelease,
            "pointer movement after release leaves the scrubbed frame unchanged", new { Seeks = seeks, Scrubs = scrubs });

        events.Clear();
        seeks.Clear();
        var clipPoint = new Point(352, 152);
        control.HandlePointerDown(clipPoint, MouseButton.Left, ModifierKeys.None, false);
        control.HandlePointerUp(MouseButton.Left, clipPoint, false);
        check(events.SequenceEqual(["selection:zoom", "seek"]) && seeks.Count == 1 && control.Selection?.Id == zoomId,
            "effect clip click selects first, then seeks immediately without a mouse-up seek", new { Events = events, Seeks = seeks, Selection = control.Selection?.Id });

        events.Clear();
        control.HandlePointerDown(clipPoint, MouseButton.Left, ModifierKeys.Control, false);
        control.HandlePointerUp(MouseButton.Left, clipPoint, false);
        check(!control.MultiSelection.Contains(new TimelineSelection("zoom", zoomId)) && control.Selection?.Id == zoomId,
            "Ctrl click preserves the existing multi-select toggle behavior", new { Selected = control.MultiSelection.Select(item => item.Id).ToArray() });

        var changes = new List<TimelineChange>();
        control.ChangeRequested += changes.Add;
        control.HandlePointerDown(new Point(255, 152), MouseButton.Left, ModifierKeys.None, false);
        control.HandlePointerMove(new Point(275, 152), true);
        control.HandlePointerUp(MouseButton.Left, new Point(275, 152), false);
        check(changes.Count == 1 && changes[0].Operation == "trim-start" && changes[0].Selection.Id == zoomId,
            "dragging an effect edge still performs its trim operation", new { Changes = changes });

        changes.Clear();
        control.HandlePointerDown(new Point(500, 70), MouseButton.Left, ModifierKeys.None, false);
        control.HandlePointerMove(new Point(520, 70), true);
        control.HandlePointerUp(MouseButton.Left, new Point(520, 70), false);
        check(changes.Count == 1 && changes[0].Operation == "reorder" && changes[0].Selection.Id == screenId,
            "dragging the screen segment still performs its reorder operation", new { Changes = changes });

        var beforeRightClick = seeks.Count;
        control.HandlePointerDown(clipPoint, MouseButton.Right, ModifierKeys.None, false);
        control.HandlePointerUp(MouseButton.Right, clipPoint, false);
        check(seeks.Count == beforeRightClick, "right-button release does not run left-button seek handling", new { Seeks = seeks.Count });

        events.Clear();
        seeks.Clear();
        var screenPoint = new Point(200, 70);
        control.HandlePointerDown(screenPoint, MouseButton.Left, ModifierKeys.None, false);
        control.HandlePointerUp(MouseButton.Left, screenPoint, false);
        check(events.SequenceEqual(["selection:screen", "seek"]) && seeks.Count == 1 && Math.Abs(seeks[0] - 11d / 24) < 1e-9,
            "screen segment click selects first, then immediately seeks to its displayed source frame", new { Events = events, Seeks = seeks });

        events.Clear();
        seeks.Clear();
        var emptyTrackPoint = new Point(1000, 215);
        control.HandlePointerDown(emptyTrackPoint, MouseButton.Left, ModifierKeys.None, false);
        control.HandlePointerUp(MouseButton.Left, emptyTrackPoint, false);
        check(events.SequenceEqual(["seek"]) && seeks.Count == 1 && Math.Abs(seeks[0] - 203d / 24) < 1e-9,
            "empty track click immediately seeks to the displayed source frame", new { Events = events, Seeks = seeks });

        var rateProject = new FrameTrailProject
        {
            Media = new() { Duration = 10, FrameRate = 24 },
            Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 10, PlaybackRate = 2 }] }
        };
        var rateControl = new EditorTimelineControl { Project = rateProject };
        rateControl.Measure(new Size(1152, 400));
        rateControl.Arrange(new Rect(0, 0, 1152, 182));
        var ratePoint = new Point(400, 10);
        var rateTimelineTime = (ratePoint.X - 152d) / (1152 - 152) * rateProject.Timeline.Duration;
        var rateSourceFrame = Math.Floor(rateTimelineTime * rateProject.Timeline.Segments[0].PlaybackRate * rateProject.Media.FrameRate);
        var expectedRateFrame = rateSourceFrame / rateProject.Media.FrameRate / rateProject.Timeline.Segments[0].PlaybackRate;
        var passiveSeeks = new List<double>();
        var passiveScrubs = new List<double>();
        rateControl.SeekRequested += passiveSeeks.Add;
        rateControl.ScrubRequested += passiveScrubs.Add;
        rateControl.HandlePointerMove(ratePoint, false);
        check(passiveSeeks.Count == 0 && passiveScrubs.Count == 0,
            "passive movement does not preview a speed-mapped source frame", new { Seeks = passiveSeeks, Scrubs = passiveScrubs, TimelineTime = rateTimelineTime, SourceFrame = rateSourceFrame });
        var rateSeeks = new List<double>();
        rateControl.SeekRequested -= passiveSeeks.Add;
        rateControl.SeekRequested += rateSeeks.Add;
        rateControl.HandlePointerDown(ratePoint, MouseButton.Left, ModifierKeys.None, false);
        rateControl.HandlePointerUp(MouseButton.Left, ratePoint, false);
        check(rateSeeks.Count == 1 && Math.Abs(rateSeeks[0] - expectedRateFrame) < 1e-9,
            "click maps playback speed to the matching source frame", new { Seeks = rateSeeks, Expected = expectedRateFrame });

        static double FrameAt(double x) => Math.Floor((x - 152) / 1000 * 10 * 24) / 24;
    }
}
