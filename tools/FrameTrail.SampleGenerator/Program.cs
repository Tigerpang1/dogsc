using FrameTrail;
using FrameTrail.Media;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
private static async Task Main(string[] args)
{
var output = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine("artifacts", "sample", "FrameTrail-Demo"));
Directory.CreateDirectory(output);
var source = Path.Combine(output, "synthetic-screen.mp4");
var camera = Path.Combine(output, "synthetic-camera.mp4");
var tone = Path.Combine(output, "independent-tone.wav");
var badge = Path.Combine(output, "demo-badge.png");
var projectPath = Path.Combine(output, "FrameTrail-Demo.frametrail");
var exportPath = Path.Combine(output, "FrameTrail-Demo.mp4");
var stillPath = Path.Combine(output, "preview.png");
CreateBadge(badge);

await MediaTools.RunAsync(MediaTools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i",
    "testsrc2=size=1280x720:rate=30:duration=8",
    "-an", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "18", "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-y", source]);
await MediaTools.RunAsync(MediaTools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i",
    "testsrc2=size=640x360:rate=30:duration=8,hue=h=35",
    "-an", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "20", "-pix_fmt", "yuv420p", "-y", camera]);
await MediaTools.RunAsync(MediaTools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=8",
    "-c:a", "pcm_s16le", "-y", tone]);
var sourceInfo = await MediaTools.ProbeAsync(source);
var project = new FrameTrailProject
{
    DisplayName = "FrameTrail Synthetic Demo",
    MediaPath = Path.GetFileName(source),
    Media = new() { Width = sourceInfo.Width, Height = sourceInfo.Height, Duration = sourceInfo.Duration },
    Timeline = new() { Segments = [new() { SourceStart = 0, SourceDuration = sourceInfo.Duration }] },
    PointerEvents = [
        new(.35, new(.24, .34)), new(.8, new(.32, .4), PointerEventKind.LeftClick),
        new(1.15, new(.42, .48)), new(3.5, new(.68, .58)), new(4.2, new(.74, .46), PointerEventKind.RightClick),
        new(5.0, new(.62, .38)), new(7.2, new(.54, .52))
    ],
    ZoomClips = [
        new() { Start = .7, End = 1.65, Scale = 1.5, Focus = new(.42, .48), EnterDuration = .28, ExitDuration = .3, Easing = ZoomEasing.Spring },
        new() { Start = 4.05, End = 5.0, Scale = 1.65, Focus = new(.7, .48), EnterDuration = .32, ExitDuration = .3, Easing = ZoomEasing.Cubic }
    ],
    ScreenMotions = [new() { Start = 2.05, Duration = 1.25, EnterDuration = .4, ReturnDuration = .4,
        Target = new() { Position = new(.5, .5), Scale = .91, RotationX = 5, RotationY = -7, RotationZ = 1, Perspective = .6 },
        Focus = new() { Shape = FocusEffectShape.Radial, Target = FocusEffectTarget.Fixed, Center = new(.5, .5), Size = .56, Blur = .16, Dimming = .08 } }],
    Camera = new() { MediaPath = Path.GetFileName(camera), SourceStart = 0, Duration = 8, TimelineStart = 0,
        Position = new(.81, .79), Size = .28, Shape = CameraShape.RoundedRectangle, CornerRadius = .1, BorderWidth = 3,
        BorderColor = "#F2C14E", Muted = true, Volume = 0 },
    CameraMotions = [new() { Start = 5.35, Duration = 1.2, EnterDuration = .35, ReturnDuration = .35, TargetPosition = new(.77, .76), TargetSize = .34 }],
    Overlays = [new() { MediaPath = Path.GetFileName(badge), Start = .4, Duration = 7.2, Rect = new(.66, .055, .29, .105),
        EnterMotion = OverlayMotion.Fade, ExitMotion = OverlayMotion.Fade, EnterDuration = .35, ExitDuration = .45, CornerRadius = .025 }],
    AudioTracks = [new() { MediaPath = Path.GetFileName(tone), SourceStart = 0, Duration = 8, OffsetSeconds = .15, Volume = .16, Muted = false }],
    Settings = new()
    {
        CanvasWidth = 1280, CanvasHeight = 720, Padding = .075, CornerRadius = .025, Shadow = .28,
        BackgroundStyle = new() { Kind = BackgroundKind.Dynamic, Preset = DynamicBackgroundPreset.Ocean, Speed = .45 },
        Cursor = new() { Visible = true, Style = CursorStyle.SystemArrow, Scale = 1.05, ClickEffectsEnabled = true, ClickEffect = ClickEffectStyle.Pulse, ClickColor = "#F2C14E", ClickDuration = .34 },
        Opening = new() { Enabled = true, Preset = OpeningPreset.Light3D, Duration = .95, Stagger = .14,
            IncludedElements = [OpeningElement.Screen, OpeningElement.Camera, OpeningElement.Overlays],
            ElementOrder = [OpeningElement.Screen, OpeningElement.Camera, OpeningElement.Overlays] }
    }
};

FrameTrailProjectStore.Save(projectPath, project);
var reopened = FrameTrailProjectStore.Load(projectPath);
var reopenedMedia = FrameTrailProjectStore.ResolveMediaPath(projectPath, reopened);
if (!File.Exists(reopenedMedia) || reopened.Media.Width != 1280 || reopened.Timeline.Duration < 7.9)
    throw new InvalidDataException("示例工程保存后重开检查失败。");
var result = await new ExportEngine().ExportAsync(reopened, reopenedMedia, exportPath,
    new ExportOptions(1280, 720, 30, HardwarePreferred: true, ProjectFilePath: projectPath));
var outputInfo = await MediaTools.ProbeAsync(exportPath);
if (outputInfo.Width != 1280 || outputInfo.Height != 720 || outputInfo.FrameRate < 29.9 || Math.Abs(outputInfo.Duration - 8) > .12 || !outputInfo.HasAudio)
    throw new InvalidDataException($"示例成片核验失败：{outputInfo}");
await MediaTools.RunAsync(MediaTools.Ffmpeg, ["-v", "error", "-ss", "3", "-i", exportPath, "-frames:v", "1", "-y", stillPath]);
Console.WriteLine($"Saved and reopened: {projectPath}");
Console.WriteLine($"Exported: {exportPath}");
Console.WriteLine($"Preview still: {stillPath}");
Console.WriteLine($"ffprobe: {outputInfo.Width}x{outputInfo.Height} {outputInfo.FrameRate:F3}fps {outputInfo.Duration:F3}s audio={outputInfo.HasAudio}; {result.Encoder}/{result.Renderer}");
}

static void CreateBadge(string path)
{
    var visual = new DrawingVisual();
    using (var context = visual.RenderOpen())
    {
        var bounds = new Rect(0, 0, 520, 130);
        context.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(242, 201, 78)), null, bounds, 18, 18);
        var title = new FormattedText("FRAME TRAIL", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 42, new SolidColorBrush(Color.FromRgb(23, 33, 43)), 1.0);
        var subtitle = new FormattedText("SYNTHETIC SAMPLE", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI Semibold"), 20, new SolidColorBrush(Color.FromRgb(23, 33, 43)), 1.0);
        context.DrawText(title, new Point((520 - title.Width) / 2, 10));
        context.DrawText(subtitle, new Point((520 - subtitle.Width) / 2, 78));
    }
    var bitmap = new RenderTargetBitmap(520, 130, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(visual);
    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = File.Create(path); encoder.Save(stream);
}
}
