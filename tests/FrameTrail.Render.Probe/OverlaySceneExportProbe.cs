using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FrameTrail;
using FrameTrail.Media;

internal static class OverlaySceneExportProbe
{
    internal static async Task Run()
    {
        const int width = 320, height = 180;
        var root = Path.GetFullPath(Path.Combine("artifacts", "validation", "overlay-export-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.mp4");
        await MediaTools.RunAsync(MediaTools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "color=c=white:s=320x180:r=30:d=1.2", "-an", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-y", source]);
        var png = Path.Combine(root, "red.png");
        var raw = new byte[16 * 16 * 4];
        for (var i = 0; i < raw.Length; i += 4) { raw[i + 2] = 255; raw[i + 3] = 255; }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, raw, 16 * 4)));
        using (var file = File.Create(png)) encoder.Save(file);
        var project = new FrameTrailProject
        {
            MediaPath = "source.mp4", Media = new() { Width = width, Height = height, Duration = 1.2 },
            Timeline = new() { Segments = [new() { SourceDuration = 1.2 }] },
            Settings = new() { CanvasWidth = width, CanvasHeight = height, AutoZoom = false, CursorVisible = false, Padding = 0, CornerRadius = 0, Shadow = 0 },
            Overlays = [new() { MediaPath = "red.png", Duration = 1.2, Rect = new(.3, .3, .25, .25), EnterMotion = OverlayMotion.None, ExitMotion = OverlayMotion.None,
                AnimationCurve = ElementMotionCurve.Swift, BorderWidth = 18, BorderColor = "#0000FF", ShadowOpacity = .7, ShadowRadius = 12, ShadowOffsetY = 120 }]
        };
        var projectFile = Path.Combine(root, "project.frametrail");
        FrameTrailProjectStore.Save(projectFile, project);
        var output = Path.Combine(root, "overlay.mp4");
        var exported = await new ExportEngine().ExportAsync(project, source, output, new(width, height, 30, false, false, projectFile));
        var preview = new byte[width * height * 4];
        await using (var session = await PreviewPlaybackSession.CreateAsync(project, source, projectFile, width, height))
            await session.ReadFrameAsync(.5, preview);
        var decoded = await MediaTools.DecodeFrameAsync(output, .5, width, height, CancellationToken.None);
        foreach (var pixels in new[] { preview, decoded })
        {
            var center = Pixel(pixels, 135, 75); var border = Pixel(pixels, 94, 75); var shadow = Pixel(pixels, 135, 112);
            Check(center.R > 230 && center.G < 20, "overlay asset is loaded and drawn");
            Check(border.B > 170 && border.R < 80, "authored blue border reaches preview and encoded media");
            Check(shadow.R < 180 && shadow.G < 180 && Math.Abs(shadow.R - shadow.G) < 10, "authored shadow reaches preview and encoded media outside the card");
        }

        // A fullscreen camera must remain under stickers and cover screen-frame text.
        project.Camera = new() { MediaPath = "source.mp4", Duration = 1.2, AssetAspectRatio = 16d / 9, Layout = CameraMotionLayoutKind.Fullscreen };
        var cameraPreview = new byte[preview.Length];
        await using (var session = await PreviewPlaybackSession.CreateAsync(project, source, projectFile, width, height))
            await session.ReadFrameAsync(.5, cameraPreview);
        Check(Pixel(cameraPreview, 135, 75).R > 230 && Pixel(cameraPreview, 135, 75).G < 20, "stickers composite above fullscreen camera footage");
        project.Overlays[0].HidesScreen = true;
        project.Overlays[0].HidesCamera = true;
        project.Overlays[0].BackdropBlur = 12;
        project.Overlays[0].BackdropBlurIncludesCamera = true;
        var hiddenOutput = Path.Combine(root, "hidden-backdrop.mp4");
        await new ExportEngine().ExportAsync(project, source, hiddenOutput, new(width, height, 30, false, false, projectFile));
        var hiddenFrame = await MediaTools.DecodeFrameAsync(hiddenOutput, .5, width, height, CancellationToken.None);
        await using (var session = await PreviewPlaybackSession.CreateAsync(project, source, projectFile, width, height))
            await session.ReadFrameAsync(.5, cameraPreview);
        foreach (var pixels in new[] { cameraPreview, hiddenFrame })
        {
            var background = Pixel(pixels, 250, 130);
            Check(Math.Abs(background.R - 32) < 8 && Math.Abs(background.G - 33) < 8 && Math.Abs(background.B - 36) < 8,
                "screen/camera hiding reaches preview and encoded media while preserving the authored background");
            Check(Pixel(pixels, 135, 75).R > 230 && Pixel(pixels, 135, 75).G < 20, "sticker stays visible during backdrop suppression");
        }
        project.Overlays.Clear(); project.Camera=null;
        project.BlurClips=[new(){Start=0,Duration=1.2,Kind=BlurKind.Spotlight,Rect=new(.3,.3,.25,.25),SpotlightDimming=.6,TransitionStyle=RegionTransitionStyle.None}];
        var spotlightOutput=Path.Combine(root,"spotlight.mp4");
        await new ExportEngine().ExportAsync(project,source,spotlightOutput,new(width,height,30,false,false,projectFile));
        var spotlightFrame=await MediaTools.DecodeFrameAsync(spotlightOutput,.5,width,height,CancellationToken.None);
        await using(var session=await PreviewPlaybackSession.CreateAsync(project,source,projectFile,width,height))
            await session.ReadFrameAsync(.5,cameraPreview);
        foreach(var pixels in new[]{cameraPreview,spotlightFrame})
        {
            Check(Pixel(pixels,135,75).R>245,"spotlight preserves a clear focus in preview and MP4");
            Check(Pixel(pixels,250,130).R is >95 and <110,"spotlight dims outside the focus in preview and MP4");
        }
        await File.WriteAllTextAsync(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { passed = true, borderAndShadowPreview = true,
            borderAndShadowEncoded = true, cameraBelowStickers = true, backdropSuppressionPreviewAndExport = true, spotlightPreviewAndExport=true, frames = exported.FrameCount, output }));
        Console.WriteLine("通过：贴图边框/阴影真实预览导出与摄像头叠放；" + root);
    }
    private static (int B, int G, int R) Pixel(byte[] pixels, int x, int y)
    { var at = (y * 320 + x) * 4; return (pixels[at], pixels[at + 1], pixels[at + 2]); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("Overlay export probe: " + message); }
}
