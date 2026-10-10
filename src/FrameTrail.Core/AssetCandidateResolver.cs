namespace FrameTrail;

/// <summary>Validates an existing project asset before asking the user to replace it.</summary>
public static class AssetCandidateResolver
{
    public static async Task<(string Path, T Info)> ResolveAsync<T>(
        string existingPath,
        Func<string, Task<T>> validateExisting,
        Func<string, Task<T>> validateReplacement,
        Func<Exception, Task> reportInvalid,
        Func<Task<string?>> select)
    {
        ArgumentNullException.ThrowIfNull(validateExisting);
        ArgumentNullException.ThrowIfNull(validateReplacement);
        ArgumentNullException.ThrowIfNull(reportInvalid);
        ArgumentNullException.ThrowIfNull(select);

        if (File.Exists(existingPath))
        {
            try { return (existingPath, await validateExisting(existingPath)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { await reportInvalid(ex); }
        }

        while (true)
        {
            var candidate = await select();
            if (candidate is null) throw new OperationCanceledException("素材重定位已取消，项目未修改。");
            try { return (candidate, await validateReplacement(candidate)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { await reportInvalid(ex); }
        }
    }
}
