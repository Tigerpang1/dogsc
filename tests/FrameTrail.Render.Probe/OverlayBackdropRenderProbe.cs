using FrameTrail;
using FrameTrail.Media;

internal static class OverlayBackdropRenderProbe
{
    internal static void Run()
    {
        const int width = 192, height = 108;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4; var value = (byte)((x / 6 % 2) * 255);
            pixels[i] = pixels[i + 1] = pixels[i + 2] = value; pixels[i + 3] = 255;
        }
        var scene = new FrameScene(0, 0, new(0,0), new(1,1), 1, null, 0, "#204060", 0, 0, width, height, 0);
        var camera = new FrameLayerInput(width, height, pixels, new(.6,.35,.3,.45), Layer: int.MinValue + 1);
        var sticker = new FrameLayerInput(1, 1, [0,0,255,255], new(.05,.1,.2,.2));
        using var renderer = new GpuFrameRenderer(width, height, width, height);
        var crisp = Render(new()); var soft = Render(new(ScreenBlur: 7)); var bothSoft = Render(new(ScreenBlur: 7, CameraBlur: 7));
        Check(Contrast(soft, 10, 60, 95, 100) < Contrast(crisp, 10, 60, 95, 100) * .3, "screen/background composite is softened before camera and stickers");
        Check(RegionEqual(crisp, soft, 125, 45, 160, 73), "camera stays byte-identical when it is excluded from backdrop blur");
        Check(Contrast(bothSoft, 125, 45, 160, 73) < Contrast(soft, 125, 45, 160, 73) * .5, "opted-in camera is independently softened");
        Check(Pixel(bothSoft, 25, 20).R > 245 && Pixel(bothSoft, 25, 20).G < 5, "foreground sticker remains sharp over softened layers");
        var hidden = Render(new(1, 1, 7, 7));
        var outside = Pixel(hidden, 100, 80);
        Check(Math.Abs(outside.R - 32) <= 1 && Math.Abs(outside.G - 64) <= 1 && Math.Abs(outside.B - 96) <= 1, "hidden screen uses the authored background");
        var cameraHidden = Pixel(hidden, 130, 60);
        Check(Math.Abs(cameraHidden.R - 32) <= 1 && Math.Abs(cameraHidden.B - 96) <= 1, "camera suppression removes its footage and blurred edges");
        Check(Pixel(hidden, 25, 20).R > 245, "screen/camera suppression leaves the sticker visible");
        var normalAgain = Render(new());
        Check(crisp.AsSpan().SequenceEqual(normalAgain), "multipass rendering does not leak state into following normal frames");
        Console.WriteLine("通过：GPU独立背景/摄像头高斯柔化、隐藏与多pass状态恢复");
        byte[] Render(FrameOverlayBackdrop treatment) { var output = new byte[pixels.Length]; renderer.Render(pixels, scene with { OverlayBackdrop = treatment }, output, [camera, sticker]); return output; }
        static bool RegionEqual(byte[] a, byte[] b, int x0, int y0, int x1, int y1) { for (var y=y0;y<y1;y++) for(var x=x0;x<x1;x++) for(var c=0;c<4;c++) if(a[(y*width+x)*4+c]!=b[(y*width+x)*4+c]) return false; return true; }
        static double Contrast(byte[] a,int x0,int y0,int x1,int y1) { double sum=0, sum2=0; var n=0; for(var y=y0;y<y1;y++) for(var x=x0;x<x1;x++) { var v=a[(y*width+x)*4];sum+=v;sum2+=v*v;n++;}return sum2/n-Math.Pow(sum/n,2); }
        static (int R,int G,int B) Pixel(byte[] a,int x,int y) {var i=(y*width+x)*4; return(a[i+2],a[i+1],a[i]);}
    }
    private static void Check(bool value, string message) { if(!value) throw new InvalidOperationException("Overlay backdrop render: "+message); }
}
