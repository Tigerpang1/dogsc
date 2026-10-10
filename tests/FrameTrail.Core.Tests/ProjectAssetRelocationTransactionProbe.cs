namespace FrameTrail.Core.Tests;

public static class ProjectAssetRelocationTransactionProbe
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "frametrail-relocation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.bin");
            File.WriteAllText(source, "source asset");
            var rolledBackCopy = Path.Combine(root, "rollback", "asset.bin");
            var failedCommit = false;
            using (var transaction = new ProjectAssetRelocationTransaction())
            {
                transaction.Copy(source, rolledBackCopy);
                try { transaction.Commit(() => throw new IOException("injected persistence failure")); }
                catch (IOException) { failedCommit = true; }
            }
            Check(failedCommit && !File.Exists(rolledBackCopy), "failed project commit rolls back copied relocation assets");

            var committedCopy = Path.Combine(root, "commit", "asset.bin");
            var persistCalls = 0;
            using (var transaction = new ProjectAssetRelocationTransaction())
            {
                transaction.Copy(source, committedCopy);
                transaction.Commit(() => persistCalls++);
            }
            Check(persistCalls == 1 && File.Exists(committedCopy)
                && File.ReadAllText(committedCopy) == "source asset",
                "successful project commit persists once and keeps copied relocation assets");

            var collisionTarget = Path.Combine(root, "collision", "asset.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(collisionTarget)!);
            File.WriteAllText(collisionTarget, "pre-existing target");
            using (var transaction = new ProjectAssetRelocationTransaction())
            {
                try { transaction.Copy(source, collisionTarget); }
                catch (IOException) { }
            }
            Check(File.ReadAllText(collisionTarget) == "pre-existing target",
                "destination collision never deletes a file not owned by the transaction");

            var sourceReadTarget = Path.Combine(root, "source-read", "asset.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(sourceReadTarget)!);
            File.WriteAllText(sourceReadTarget, "pre-existing after source failure");
            using (var transaction = new ProjectAssetRelocationTransaction())
            {
                try { transaction.Copy(Path.Combine(root, "missing-source.bin"), sourceReadTarget); }
                catch (FileNotFoundException) { }
            }
            Check(File.ReadAllText(sourceReadTarget) == "pre-existing after source failure",
                "source read failure never deletes an unrelated existing destination");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void Check(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
    }
}
