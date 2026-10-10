using System.Reflection;
using FrameTrail;
using FrameTrail.App;
using FrameTrail.Media;

internal static class MainMediaCoverageProbe
{
    public static void Verify(Action<bool, string, object?> check)
    {
        var method=typeof(MainWindow).GetMethod("MainMediaCoversTimeline",BindingFlags.Static|BindingFlags.NonPublic)
            ??throw new MissingMethodException(typeof(MainWindow).FullName,"MainMediaCoversTimeline");
        var project=new FrameTrailProject
        {
            Timeline=new(){Segments=[new(){SourceStart=0,SourceDuration=3},new(){SourceStart=7,SourceDuration=2}]}
        };
        bool Covers(MediaInfo info)=>(bool)(method.Invoke(null,[info,project])??false);

        var existingShort=new MediaInfo(640,360,8.94,30,true);
        var replacementCovered=new MediaInfo(640,360,8.96,30,true);
        check(!Covers(existingShort),
            "an existing decodable main video shorter than the project source range enters recovery validation",
            new{existingShort.Duration,RequiredEnd=9d});
        check(Covers(replacementCovered),
            "a main video within the existing 50ms source-coverage tolerance remains acceptable",
            new{replacementCovered.Duration,RequiredEnd=9d});
        check(!Covers(new MediaInfo(0,360,10,30,true))&&!Covers(new MediaInfo(640,360,double.NaN,30,true)),
            "main-video coverage also rejects missing video dimensions and non-finite duration",null);
    }
}
