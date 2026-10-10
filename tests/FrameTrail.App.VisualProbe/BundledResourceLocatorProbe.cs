using System.IO;
using FrameTrail.App;

internal static class BundledResourceLocatorProbe
{
    internal static void Verify(Action<bool, string, object?> check)
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), "FrameTrail-BundledResourceLocator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        try
        {
            var installedBase = Path.Combine(fixtureRoot, "installed");
            WriteResource(installedBase, "docs", "build-and-release.md", "release notes");
            WriteResource(installedBase, "licenses", "DogSC-MIT.txt", "license text");
            check(BundledResourceLocator.FindFile(installedBase, "docs", "build-and-release.md") ==
                    Path.Combine(installedBase, "docs", "build-and-release.md"),
                "installed layout resolves docs from app base", BundledResourceLocator.FindFile(installedBase, "docs", "build-and-release.md"));
            check(BundledResourceLocator.FindDirectory(installedBase, "licenses", "DogSC-MIT.txt") ==
                    Path.Combine(installedBase, "licenses"),
                "installed layout resolves a populated license directory from app base", BundledResourceLocator.FindDirectory(installedBase, "licenses", "DogSC-MIT.txt"));
            var installedBaseWithSeparator = installedBase + Path.DirectorySeparatorChar;
            check(BundledResourceLocator.FindFile(installedBaseWithSeparator, "docs", "build-and-release.md") ==
                    Path.Combine(installedBase, "docs", "build-and-release.md"),
                "installed layout resolves resources when app base has a trailing separator", BundledResourceLocator.FindFile(installedBaseWithSeparator, "docs", "build-and-release.md"));

            var portableRoot = Path.Combine(fixtureRoot, "portable");
            var portableBase = Path.Combine(portableRoot, "app");
            Directory.CreateDirectory(portableBase);
            WriteResource(portableRoot, "docs", "build-and-release.md", "release notes");
            WriteResource(portableRoot, "licenses", "DogSC-MIT.txt", "license text");
            check(BundledResourceLocator.FindFile(portableBase, "docs", "build-and-release.md") ==
                    Path.Combine(portableRoot, "docs", "build-and-release.md"),
                "portable layout resolves docs from the app directory parent", BundledResourceLocator.FindFile(portableBase, "docs", "build-and-release.md"));
            check(BundledResourceLocator.FindDirectory(portableBase, "licenses", "DogSC-MIT.txt") ==
                    Path.Combine(portableRoot, "licenses"),
                "portable layout resolves licenses from the app directory parent", BundledResourceLocator.FindDirectory(portableBase, "licenses", "DogSC-MIT.txt"));
            var portableBaseWithSeparator = portableBase + Path.DirectorySeparatorChar;
            check(BundledResourceLocator.FindFile(portableBaseWithSeparator, "docs", "build-and-release.md") ==
                    Path.Combine(portableRoot, "docs", "build-and-release.md"),
                "portable layout resolves resources when app base has a trailing separator", BundledResourceLocator.FindFile(portableBaseWithSeparator, "docs", "build-and-release.md"));

            Directory.CreateDirectory(Path.Combine(portableBase, "licenses"));
            File.WriteAllText(Path.Combine(portableBase, "licenses", "DogSC-MIT.txt"), string.Empty);
            check(BundledResourceLocator.FindDirectory(portableBase, "licenses", "DogSC-MIT.txt") ==
                    Path.Combine(portableRoot, "licenses"),
                "empty app-base license marker does not shadow the populated portable package directory", BundledResourceLocator.FindDirectory(portableBase, "licenses", "DogSC-MIT.txt"));

            var emptyOnlyBase = Path.Combine(fixtureRoot, "empty-only", "app");
            var emptyOnlyLicenses = Path.Combine(emptyOnlyBase, "licenses");
            Directory.CreateDirectory(emptyOnlyLicenses);
            File.WriteAllText(Path.Combine(emptyOnlyLicenses, "DogSC-MIT.txt"), string.Empty);
            check(BundledResourceLocator.FindDirectory(emptyOnlyBase, "licenses", "DogSC-MIT.txt") is null,
                "an empty required license file is treated as missing", BundledResourceLocator.FindDirectory(emptyOnlyBase, "licenses", "DogSC-MIT.txt"));

            var missingBase = Path.Combine(fixtureRoot, "missing", "app");
            Directory.CreateDirectory(missingBase);
            var missingParent = Directory.GetParent(missingBase)!.FullName;
            check(BundledResourceLocator.FindFile(missingBase, "docs", "build-and-release.md") is null
                  && BundledResourceLocator.FindDirectory(missingBase, "licenses", "DogSC-MIT.txt") is null,
                "missing bundled resources return null", new { Docs = BundledResourceLocator.FindFile(missingBase, "docs", "build-and-release.md"), Licenses = BundledResourceLocator.FindDirectory(missingBase, "licenses", "DogSC-MIT.txt") });
            check(!Directory.Exists(Path.Combine(missingBase, "licenses")) && !Directory.Exists(Path.Combine(missingParent, "licenses")),
                "missing license lookup does not create an empty directory", null);

            var grandparentRoot = Path.Combine(fixtureRoot, "grandparent-only");
            var grandparentBase = Path.Combine(grandparentRoot, "parent", "app");
            Directory.CreateDirectory(grandparentBase);
            WriteResource(grandparentRoot, "docs", "build-and-release.md", "must not be found two levels up");
            check(BundledResourceLocator.FindFile(grandparentBase, "docs", "build-and-release.md") is null,
                "locator does not search beyond the app base and its immediate parent", null);
        }
        finally
        {
            try { Directory.Delete(fixtureRoot, recursive: true); } catch (IOException) { }
        }
    }

    private static void WriteResource(string root, string directory, string file, string content)
    {
        var path = Path.Combine(root, directory);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, file), content);
    }
}
