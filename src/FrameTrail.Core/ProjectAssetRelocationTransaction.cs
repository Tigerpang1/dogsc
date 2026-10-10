namespace FrameTrail;

/// <summary>Stages copied project assets and removes them unless the project update commits.</summary>
public sealed class ProjectAssetRelocationTransaction : IDisposable
{
    private readonly List<string> copiedFiles = [];
    private bool committed;
    private bool rollbackAttempted;

    public void Copy(string source, string destination)
    {
        ObjectDisposedException.ThrowIf(rollbackAttempted, this);
        if (committed) throw new InvalidOperationException("The relocation transaction has already committed.");
        var target = Path.GetFullPath(destination);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        copiedFiles.Add(target);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    public void Commit(Action persistProject)
    {
        ArgumentNullException.ThrowIfNull(persistProject);
        ObjectDisposedException.ThrowIf(rollbackAttempted, this);
        if (committed) throw new InvalidOperationException("The relocation transaction has already committed.");
        persistProject();
        committed = true;
    }

    public IReadOnlyList<Exception> Rollback()
    {
        if (committed || rollbackAttempted) return [];
        rollbackAttempted = true;
        var errors = new List<Exception>();
        foreach (var path in copiedFiles.AsEnumerable().Reverse())
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { errors.Add(ex); }
        }
        return errors;
    }

    public void Dispose() => _ = Rollback();
}
