using System.IO;

namespace FrameTrail.App;

internal static class BundledResourceLocator
{
    internal static string? FindDirectory(string appBaseDirectory, string relativeDirectory, string requiredFile)
    {
        if (string.IsNullOrWhiteSpace(appBaseDirectory)
            || !IsSinglePathSegment(relativeDirectory)
            || !IsSinglePathSegment(requiredFile)) return null;

        string baseDirectory;
        try { baseDirectory = Path.GetFullPath(appBaseDirectory); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return null; }

        var normalizedBaseDirectory = Path.TrimEndingDirectorySeparator(baseDirectory);
        var parentDirectory = Directory.GetParent(normalizedBaseDirectory)?.FullName;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, relativeDirectory),
            parentDirectory is null ? null : Path.Combine(parentDirectory, relativeDirectory)
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (candidate is null || !seen.Add(candidate) || !Directory.Exists(candidate)) continue;
            try
            {
                var marker = new FileInfo(Path.Combine(candidate, requiredFile));
                if (marker.Exists && marker.Length > 0) return candidate;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { }
        }

        return null;
    }

    internal static string? FindFile(string appBaseDirectory, string relativeDirectory, string requiredFile)
    {
        var directory = FindDirectory(appBaseDirectory, relativeDirectory, requiredFile);
        return directory is null ? null : Path.Combine(directory, requiredFile);
    }

    private static bool IsSinglePathSegment(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value != "."
        && value != ".."
        && string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal);
}
