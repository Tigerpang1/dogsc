namespace FrameTrail.Core.Tests;

public static class AssetCandidateResolverProbe
{
    public static async Task RunAsync()
    {
        var root=Path.Combine(Path.GetTempPath(),"frametrail-asset-candidates-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var existing=Path.Combine(root,"existing.media");
            var bad=Path.Combine(root,"bad.media");
            var good=Path.Combine(root,"good.media");
            File.WriteAllText(existing,"corrupt-existing");
            File.WriteAllText(bad,"corrupt-selected");
            File.WriteAllText(good,"valid");
            var reports=0;var selections=0;
            var result=await AssetCandidateResolver.ResolveAsync(existing,Validate,Validate,
                _=>{reports++;return Task.CompletedTask;},
                ()=>Task.FromResult<string?>(++selections==1?bad:good));
            Check(result.Path==good&&result.Info=="valid"&&reports==2&&selections==2,
                "existing corrupt asset reports failure, then retries invalid and valid selections before accepting");

            var cancelReports=0;var canceled=false;
            try
            {
                await AssetCandidateResolver.ResolveAsync(existing,Validate,Validate,
                    _=>{cancelReports++;return Task.CompletedTask;},
                    ()=>Task.FromResult<string?>(null));
            }
            catch(OperationCanceledException){canceled=true;}
            Check(canceled&&cancelReports==1&&File.ReadAllText(existing)=="corrupt-existing",
                "cancel after an existing asset fails validation leaves the original asset untouched");

            var shortButReadable=Path.Combine(root,"short-but-readable.media");
            File.WriteAllText(shortButReadable,"valid-short");
            var replacementValidationCalled=false;
            var existingResult=await AssetCandidateResolver.ResolveAsync(shortButReadable,
                path=>Task.FromResult(File.ReadAllText(path)),
                path=>{replacementValidationCalled=true;return Validate(path);},
                _=>Task.CompletedTask,
                ()=>Task.FromResult<string?>(null));
            Check(existingResult.Info=="valid-short"&&!replacementValidationCalled,
                "readable existing assets use existing-asset validation and are not forced through replacement coverage rules");
            Console.WriteLine("Asset candidate recovery tests passed: corrupt existing asset prompted for replacement; invalid selection retried; cancellation preserved original.");
        }
        finally { try{Directory.Delete(root,true);}catch{} }

        static Task<string> Validate(string path)
        {
            var content=File.ReadAllText(path);
            return content=="valid"?Task.FromResult(content):Task.FromException<string>(new InvalidDataException("synthetic decode failure"));
        }
        static void Check(bool passed,string message)
        { if(!passed)throw new InvalidOperationException(message); }
    }
}
