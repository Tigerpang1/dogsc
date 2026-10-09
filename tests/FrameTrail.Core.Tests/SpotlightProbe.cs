using FrameTrail;

internal static class SpotlightProbe
{
    public static void Run()
    {
        var clip = new BlurClip { Start = 1, Duration = 2, Kind = BlurKind.Spotlight,
            Rect = new(.2, .3, .4, .2), SourceSpace = true, SpotlightDimming = .62,
            EnterDuration = 1, ExitDuration = 1, TransitionStyle = RegionTransitionStyle.Linear };
        var project = new FrameTrailProject { MediaPath = "source.mp4", Media = new() { Width=320, Height=180, Duration=5 },
            Timeline = new() { Segments = [new() { SourceStart=0, SourceDuration=5 }] },
            Settings = new() { AutoZoom=false }, BlurClips = [clip] };
        Near(new PreparedFrameSceneEvaluator(project).Evaluate(1.25).BlurRegions![0].Opacity, .25, "linear entry");
        Near(new PreparedFrameSceneEvaluator(project).Evaluate(2.75).BlurRegions![0].Opacity, .25, "linear exit");
        clip.TransitionStyle=RegionTransitionStyle.Smooth;
        Near(new PreparedFrameSceneEvaluator(project).Evaluate(1.25).BlurRegions![0].Opacity, .103515625, "quintic entry");
        clip.TransitionStyle=RegionTransitionStyle.None;
        Near(new PreparedFrameSceneEvaluator(project).Evaluate(1.01).BlurRegions![0].Opacity, 1, "no transition");
        var path=Path.Combine(Path.GetTempPath(),"frametrail-spotlight-"+Guid.NewGuid().ToString("N")+".frametrail");
        try
        {
            FrameTrailProjectStore.Save(path,project);
            var restored=FrameTrailProjectStore.Load(path).BlurClips.Single();
            if(restored.Kind!=BlurKind.Spotlight || !restored.SourceSpace || restored.Rect!=clip.Rect
                || restored.TransitionStyle!=RegionTransitionStyle.None || restored.SpotlightDimming!=.62)
                throw new Exception("Spotlight persistence lost authored fields");
            clip.SpotlightDimming=double.NaN;
            try { FrameTrailProjectStore.Save(path,project); throw new Exception("Invalid spotlight dimming accepted"); }
            catch(InvalidDataException) { }
        }
        finally { if(File.Exists(path))File.Delete(path); if(File.Exists(path+".bak"))File.Delete(path+".bak"); }
        Console.WriteLine("通过：Spotlight三种过渡、持久化与非法压暗值拒绝");
    }
    private static void Near(double actual,double expected,string name)
    { if(Math.Abs(actual-expected)>1e-9)throw new Exception($"Spotlight {name}: {actual} != {expected}"); }
}
