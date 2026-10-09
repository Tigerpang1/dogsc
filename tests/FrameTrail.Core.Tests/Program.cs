using System.Text.Json;
using FrameTrail;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static FrameTrailProject Copy(FrameTrailProject p) => JsonSerializer.Deserialize<FrameTrailProject>(JsonSerializer.Serialize(p))!;

var timeline = new ProjectTimeline { Segments = [new TimelineSegment { SourceStart = 10, SourceDuration = 8, PlaybackRate = 2 }, new TimelineSegment { SourceStart = 30, SourceDuration = 4 }] };
var first = timeline.Segments[0]; var split = timeline.Split(first.Id, 14);
Check(Math.Abs(timeline.MapOutputToSource(1) - 12) < 1e-9, "split mapping before junction");
Check(Math.Abs(timeline.MapOutputToSource(2) - 14) < 1e-9, "split mapping at junction");
Check(Math.Abs(timeline.MapOutputToSource(3) - 16) < 1e-9, "split mapping after junction");
timeline.SetPlaybackRate(split.Right.Id, 4);
Check(Math.Abs(timeline.MapOutputToSource(2.5) - 16) < 1e-9, "retimed segment mapping");
Check(Math.Abs(timeline.MapSourceToOutput(16)!.Value - 2.5) < 1e-9, "inverse source mapping");
timeline.Reorder(timeline.Segments[^1].Id, 0);
Check(Math.Abs(timeline.MapOutputToSource(0) - 30) < 1e-9, "reorder mapping");
var discontinuous = new ProjectTimeline { Segments = [new() { SourceStart = 0, SourceDuration = 2 }, new() { SourceStart = 10, SourceDuration = 3 }] };
Check(Math.Abs(discontinuous.MapOutputToSource(2) - 10) < 1e-9, "cut boundary maps to following source segment");
var repeatedSource = new ProjectTimeline { Segments = [new() { SourceStart = 5, SourceDuration = 2 }, new() { SourceStart = 5, SourceDuration = 2 }] };
Check(Math.Abs(repeatedSource.MapOutputToSource(2) - 5) < 1e-9 && repeatedSource.MapSourceToOutput(6) == 1, "repeated source range remains deterministic");
Check(repeatedSource.MapSourceToOutput(5) == 0, "source-to-output selects first duplicate occurrence");

var clicks = new[] {
    new PointerEvent(1, new(.2, .3), PointerEventKind.LeftClick),
    new PointerEvent(1.7, new(.8, .7), PointerEventKind.RightClick),
    new PointerEvent(5, new(.5, .5), PointerEventKind.LeftClick)
};
var clips = AutoZoomPlanner.Plan(clicks);
Check(clips.Count == 2 && clips[0].End < clips[1].Start, "click grouping");
var animationProject = new FrameTrailProject { MediaPath = "capture.mp4", Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 10 }] }, ZoomClips = [clips[0]], Settings = new() { AutoZoom = false } };
var animationLeft = FrameSceneEvaluator.Evaluate(animationProject, clips[0].Start + .2).Zoom;
var animationRight = FrameSceneEvaluator.Evaluate(animationProject, clips[0].Start + .201).Zoom;
Check(Math.Abs(animationRight - animationLeft) < .01, "zoom animation continuity");
var returnEnd = clips[0].End + clips[0].ExitDuration;
Check(Math.Abs(FrameSceneEvaluator.Evaluate(animationProject, returnEnd).Zoom - FrameSceneEvaluator.Evaluate(animationProject, returnEnd - .001).Zoom) < .01, "zoom return continuity");
var activityPlan = AutoZoomPlanner.Plan([new(1, new(.5, .5), PointerEventKind.LeftClick), new(1.8, new(.55, .5)), new(2.2, new(.6, .5))]);
Check(activityPlan[0].End > 1.9, "automatic zoom hold extends through pointer activity");

var track = new[] { new PointerEvent(0, new(.1, .2)), new PointerEvent(.1, new(.2, .3)), new PointerEvent(.2, new(.4, .5)) };
var pointer = PointerTrack.Sample(track, .15).Position;
Check(pointer is not null && pointer.Value.X > .2 && pointer.Value.X < .4, "monotone pointer interpolation");

var root = Path.Combine(Path.GetTempPath(), "frametrail-core-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
try
{
    var file = Path.Combine(root, "sample.frametrail");
    var project = new FrameTrailProject { MediaPath = "media/recording.mp4", Media = new() { Width = 1920, Height = 1080, Duration = 40 }, Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 40 }] }, PointerEvents = clicks.ToList() };
    FrameTrailProjectStore.Save(file, project);
    project.Settings.Background = "#ffffff";
    FrameTrailProjectStore.Save(file, project);
    Check(File.Exists(file + FrameTrailProjectStore.BackupSuffix), "backup creation");
    var restored = FrameTrailProjectStore.RestoreBackup(file);
    Check(restored.Settings.Background == "#202124", "backup restore content");
    Check(FrameTrailProjectStore.ResolveMediaPath(file, restored) == Path.Combine(root, "media", "recording.mp4"), "relative media path");
    restored.MediaPath = "../secret.mp4";
    try { FrameTrailProjectStore.Save(file, restored); throw new Exception("path traversal was accepted"); } catch (InvalidDataException) { }
    var futureFile = Path.Combine(root, "future.frametrail");
    var futureProject = new FrameTrailProject { FormatVersion = 2, MediaPath = "media/source.mp4", Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 1 }] } };
    File.WriteAllText(futureFile, JsonSerializer.Serialize(futureProject, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    try { _ = FrameTrailProjectStore.Load(futureFile); throw new Exception("future project version was accepted"); } catch (InvalidDataException) { }
}
finally { Directory.Delete(root, true); }

var history = new UndoRedoHistory<FrameTrailProject>(new FrameTrailProject(), Copy);
var changed = Copy(history.Current); changed.Settings.Background = "#abcdef"; history.Commit(changed);
Check(history.Undo().Settings.Background == "#202124" && history.Redo().Settings.Background == "#abcdef", "undo redo");

var sceneProject = new FrameTrailProject { MediaPath = "capture.mp4", Timeline = new() { Segments = [new() { SourceStart = 4, SourceDuration = 10 }] }, PointerEvents = [new(4, new(.5, .5), PointerEventKind.LeftClick)], Settings = new() { AutoZoom = true } };
var scene = FrameSceneEvaluator.Evaluate(sceneProject, 1);
Check(Math.Abs(scene.SourceTime - 5) < 1e-9 && scene.CropSize.X <= 1 && scene.Zoom >= 1, "frame scene source mapping and crop");
var prepared = new PreparedFrameSceneEvaluator(sceneProject);
foreach (var t in new[] { 0d, .1, 1, 4.9, 9.99, 10d })
{
    var direct = FrameSceneEvaluator.Evaluate(sceneProject, t); var cached = prepared.Evaluate(t);
    Check(direct == cached, $"prepared evaluation matches compatibility evaluator at {t}");
}
var negativeOffsetProject = new FrameTrailProject
{
    MediaPath = "capture.mp4",
    Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = 2 }] },
    MouseClock = new(-.5, 1),
    PointerEvents = [new(1.25, new(.7, .6))],
    Settings = new() { AutoZoom = false }
};
Check(negativeOffsetProject.MouseClock.ToSourceTime(.1) == 0, "negative calibration clamps pre-recording time");
var offsetScene = new PreparedFrameSceneEvaluator(negativeOffsetProject).Evaluate(.75);
Check(offsetScene.Cursor == new NormalizedPoint(.7, .6), "negative mouse offset maps cursor clock independently");

Console.WriteLine("FrameTrail.Core tests passed.");

