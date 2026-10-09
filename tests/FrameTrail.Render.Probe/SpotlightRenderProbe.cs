using FrameTrail;
using FrameTrail.Media;

internal static class SpotlightRenderProbe
{
    public static void Run()
    {
        const int w=200,h=100; var source=new byte[w*h*4];
        for(var y=0;y<h;y++)for(var x=0;x<w;x++){var at=(y*w+x)*4;var v=(byte)(x%6<3?40:240);source[at]=source[at+1]=source[at+2]=v;source[at+3]=255;}
        var scene=new FrameScene(0,0,new(.25,.25),new(.5,.5),2,null,0,"#FFFFFF",0,0,w,h,0);
        var spot=new FrameBlurScene(new(.45,.45,.1,.1),1,BlurKind.Spotlight,0,1,.6,true);
        using var renderer=new GpuFrameRenderer(w,h,w,h);
        var plain=Render(scene); var focused=Render(scene with {BlurRegions=[spot]});
        for(var y=43;y<57;y++)for(var x=84;x<116;x++)
            Check(Pixel(focused,x,y)==Pixel(plain,x,y),"the selected source area stays untouched through crop and zoom");
        Check(Math.Abs(Pixel(focused,15,85)-Pixel(plain,15,85)*.4)<2,"outside pixels are dimmed, rather than blurring the focus");
        var fading=Render(scene with {BlurRegions=[spot with {Opacity=.5}]});
        Check(Pixel(fading,100,50)==Pixel(plain,100,50),"entering spotlight preserves focus pixels");
        Check(Math.Abs(Pixel(fading,15,85)-Pixel(plain,15,85)*.7)<2,"transition fades only outside dimming");
        var offscreen=Render(scene with {BlurRegions=[spot with {Rect=new(0,0,.1,.1)}]});
        Check(offscreen.AsSpan().SequenceEqual(plain),"a spotlight outside the source crop does not darken the entire canvas");
        var second=spot with {Rect=new(.29,.29,.06,.06),SpotlightDimming=.2};
        var union=Render(scene with {BlurRegions=[spot,second]});
        Check(Pixel(union,28,14)==Pixel(plain,28,14)&&Pixel(union,100,50)==Pixel(plain,100,50),"multiple spotlight areas form a clear union");
        var camera=new FrameLayerInput(1,1,[255,0,0,255],new(.05,.7,.1,.2),Layer:int.MinValue+1);
        var layers=new byte[source.Length];renderer.Render(source,scene with{BlurRegions=[spot]},layers,[camera]);
        Check(layers[(80*w+20)*4]>245,"camera is composited after the screen spotlight");
        Console.WriteLine("通过：Spotlight源区域裁切/缩放、清晰焦点、外区压暗、过渡和多焦点合并");
        byte[] Render(FrameScene frame){var pixels=new byte[source.Length];renderer.Render(source,frame,pixels);return pixels;}
        static int Pixel(byte[] pixels,int x,int y)=>pixels[(y*w+x)*4];
    }
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException("Spotlight render: "+message);}
}
