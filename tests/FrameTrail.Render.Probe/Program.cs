using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FrameTrail;
using FrameTrail.Media;
using Vortice.D3DCompiler;

const int sourceWidth = 320, sourceHeight = 180, outputWidth = 640, outputHeight = 360;
var source = MakeCheckerboard(sourceWidth, sourceHeight);
var hlslPath = FindWorkspaceFile(Path.Combine("src", "FrameTrail.Media", "Scene.hlsl"));
var hlsl = File.ReadAllText(hlslPath);
_ = Compiler.Compile(hlsl, "VSMain", "Scene.hlsl", "vs_5_0");
_ = Compiler.Compile(hlsl, "PSMain", "Scene.hlsl", "ps_5_0");
var plain = new byte[outputWidth * outputHeight * 4];
var zoomed = new byte[plain.Length];
var cursor = new byte[plain.Length];
var baseScene = new FrameScene(0, 0, new(0, 0), new(1, 1), 1, null, 0, "#202124", .07, .055, 1920, 1080, .2);
using var renderer = new GpuFrameRenderer(sourceWidth, sourceHeight, outputWidth, outputHeight);
renderer.Render(source, baseScene, plain);
var zoomScene = baseScene with { CropOrigin = new(.25, .25), CropSize = new(.5, .5), Zoom = 2 };
renderer.Render(source, zoomScene, zoomed);
var cursorScene = baseScene with { Cursor = new(.52, .5), ClickStrength = .7 };
renderer.Render(source, cursorScene, cursor);

Check(plain[0] == 36 && plain[1] == 33 && plain[2] == 32 && plain[3] == 255, "background color at canvas corner");
var cornerProbe = (14 * outputWidth + 25) * 4;
Check(Math.Abs(plain[cornerProbe] - 36) < 3 && Math.Abs(plain[cornerProbe + 1] - 33) < 3, "screen corner is clipped to background");
Check(CountDifferent(plain, zoomed) > 10000, "zoom crop changes output pixels");
Check(CountDifferent(plain, cursor) > 50, "cursor and click pulse change output pixels");
var offCrop = new byte[plain.Length];
renderer.Render(source, zoomScene with { Cursor = new(.95, .95), ClickStrength = .9 }, offCrop);
Check(CountDifferent(zoomed, offCrop) == 0, "off-crop cursor and click ring are hidden");
var ringClip = new byte[plain.Length];
renderer.Render(source, baseScene with { Cursor = new(0, 0), ClickStrength = .7 }, ringClip);
var roundedCornerPoint = ((int)(outputHeight * .07 + 15) * outputWidth + (int)(outputWidth * .07 + 15)) * 4;
Check(ringClip[roundedCornerPoint] == plain[roundedCornerPoint] && ringClip[roundedCornerPoint + 1] == plain[roundedCornerPoint + 1], "click ring is clipped by rounded screen corner");
var ratio960 = CursorRelativeWidth(960, 540, sourceWidth, sourceHeight);
var ratio1080 = CursorRelativeWidth(1920, 1080, sourceWidth, sourceHeight);
Check(Math.Abs(ratio960 - ratio1080) < .01, "cursor width scales with canvas short edge");

var watch = Stopwatch.StartNew();
const int frames = 40;
for (var i = 0; i < frames; i++) renderer.Render(source, baseScene with { OutputTime = i / 30d }, plain);
watch.Stop();
var pngPath = Path.Combine(AppContext.BaseDirectory, "frame-probe.png");
var bitmap = BitmapSource.Create(outputWidth, outputHeight, 96, 96, PixelFormats.Bgra32, null, cursor, outputWidth * 4);
var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
using (var file = File.Create(pngPath)) encoder.Save(file);
Console.WriteLine($"Backend: {renderer.Backend}");
Console.WriteLine($"Average render plus readback: {watch.Elapsed.TotalMilliseconds / frames:F2} ms/frame ({frames} frames)");
Console.WriteLine($"Probe PNG: {pngPath}");

static byte[] MakeCheckerboard(int width, int height)
{
    var buffer = new byte[width * height * 4];
    for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
    {
        var light = ((x / 24) + (y / 24)) % 2 == 0;
        var offset = (y * width + x) * 4;
        buffer[offset] = light ? (byte)55 : (byte)210;
        buffer[offset + 1] = light ? (byte)165 : (byte)55;
        buffer[offset + 2] = light ? (byte)230 : (byte)30;
        buffer[offset + 3] = 255;
    }
    // A pair of contrasting lines provide a stable visual landmark in the PNG.
    for (var y = 52; y < 128; y++) for (var x = 148; x < 172; x++)
    { var o = (y * width + x) * 4; buffer[o] = 10; buffer[o + 1] = 10; buffer[o + 2] = 10; }
    for (var y = 88; y < 92; y++) for (var x = 100; x < 220; x++)
    { var o = (y * width + x) * 4; buffer[o] = 255; buffer[o + 1] = 255; buffer[o + 2] = 255; }
    return buffer;
}
static int CountDifferent(byte[] a, byte[] b)
{ var count = 0; for (var i = 0; i < a.Length; i += 4) if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2]) count++; return count; }
static double CursorRelativeWidth(int width, int height, int sourceWidth, int sourceHeight)
{
    var source = MakeCheckerboard(sourceWidth, sourceHeight); var baseline = new byte[width * height * 4]; var decorated = new byte[baseline.Length];
    var scene = new FrameScene(0, 0, new(0, 0), new(1, 1), 1, null, 0, "#202124", .07, .055, 1920, 1080, .2);
    using var renderer = new GpuFrameRenderer(sourceWidth, sourceHeight, width, height);
    renderer.Render(source, scene, baseline); renderer.Render(source, scene with { Cursor = new(.52, .5) }, decorated);
    var bounds = DifferentBounds(baseline, decorated, width, height);
    return (bounds.Right - bounds.Left + 1) / (double)Math.Min(width, height);
}
static (int Left, int Right) DifferentBounds(byte[] a, byte[] b, int width, int height)
{
    var left = width; var right = -1;
    for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
    {
        var i = (y * width + x) * 4;
        if (a[i] == b[i] && a[i + 1] == b[i + 1] && a[i + 2] == b[i + 2]) continue;
        left = Math.Min(left, x); right = Math.Max(right, x);
    }
    return (left, right);
}
static void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException($"Probe failed: {name}"); }
static string FindWorkspaceFile(string relativePath)
{
    foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
    {
        var current = new DirectoryInfo(start);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
    }
    throw new FileNotFoundException($"Cannot find {relativePath} from current probe directory.");
}
