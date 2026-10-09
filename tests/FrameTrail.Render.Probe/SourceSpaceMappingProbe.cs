using FrameTrail;
using FrameTrail.Media;

namespace FrameTrail.Render.Probe;

public static class SourceSpaceMappingProbe
{
    public static void Run()
    {
        var canvas = new CanvasSettings
        {
            AspectWidth = 16,
            AspectHeight = 9,
            Crop = new(.08, .06, .84, .88),
            ContentPosition = new(.52, .48),
            ContentScale = 1.12,
            Frame = ScreenFrameKind.WindowLight
        };
        var scenes = new[]
        {
            new FrameScene(.2, .2, new(.2, .12), new(.6, .72), 1.67, null, 0, "#202124", .08, .04, 640, 360, .2,
                Canvas: canvas, SourceWidth: 1280, SourceHeight: 720),
            new FrameScene(.7, .7, new(.2, .12), new(.6, .72), 1.67, null, 0, "#202124", .08, .04, 640, 360, .2,
                Canvas: canvas, ScreenMotion: new() { Position = new(.48, .54), Scale = 1.24, RotationX = 13, RotationY = -9, RotationZ = 7, Perspective = .8 },
                SourceWidth: 1280, SourceHeight: 720, Opening: new(Scale: .91, OffsetX: .025, OffsetY: -.02, Rotation: 3)),
            new FrameScene(.9, .9, new(.18, .08), new(.64, .84), 1.56, null, 0, "#202124", .06, .05, 360, 640, .2,
                Canvas: canvas with { AspectWidth = 9, AspectHeight = 16, Frame = ScreenFrameKind.Phone },
                ScreenMotion: new() { Position = new(.5, .52), Scale = .9, RotationX = -7, RotationY = 12, Perspective = .45 },
                SourceWidth: 720, SourceHeight: 1280)
        };
        var sourcePoints = new[] { new NormalizedPoint(.48, .48), new(.51, .51), new(.57, .63) };
        foreach (var (scene, index) in scenes.Select((scene, index) => (scene, index)))
        foreach (var expected in sourcePoints)
        {
            var canvasPoint = default(NormalizedPoint);
            var actual = default(NormalizedPoint);
            if (!GpuFrameRenderer.TryMapSourcePointToCanvas(scene, scene.SourceWidth, scene.SourceHeight, scene.CanvasWidth, scene.CanvasHeight, expected, out canvasPoint)
                || !GpuFrameRenderer.TryMapCanvasPointToSource(scene, scene.SourceWidth, scene.SourceHeight, scene.CanvasWidth, scene.CanvasHeight, canvasPoint, out actual)
                || Math.Abs(expected.X - actual.X) > 2e-5 || Math.Abs(expected.Y - actual.Y) > 2e-5)
                throw new InvalidOperationException($"source-space mapping roundtrip failed for scene {index}: {expected} -> {canvasPoint} -> {actual}");
        }

        var partialRegion = new NormalizedRect(.1, .2, .4, .5);
        var partialCorner = new NormalizedPoint(partialRegion.X, partialRegion.Y);
        if (!GpuFrameRenderer.TryMapSourcePointToCanvas(scenes[0], 1280, 720, 640, 360, partialCorner, out var projected)
            || !projected.IsFinite)
            throw new InvalidOperationException("partially cropped source-space region must retain finite projected handles");

        Console.WriteLine("通过：SourceSpace 选区坐标在裁切、缩放、3D、开场和设备画布间往返一致");
    }
}
