function Assert-FrameTrailSourceZipLayout {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$SourceZipPath,
        [Parameter(Mandatory)][string]$ExtractionRoot
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $root = [IO.Path]::GetFullPath($ExtractionRoot).TrimEnd('\', '/')
    $boundary = $root + [IO.Path]::DirectorySeparatorChar
    $archive = [IO.Compression.ZipFile]::OpenRead($SourceZipPath)
    try {
        $entriesByPath = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
        $fileCount = 0
        $directoryCount = 0

        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            $isDirectory = $name.EndsWith('/', [StringComparison]::Ordinal)

            if ([string]::IsNullOrWhiteSpace($name) -or $name.StartsWith('/', [StringComparison]::Ordinal) -or
                $name.Contains(':') -or $name.Contains('//') -or $name -match '(^|/)\.\.?(/|$)') {
                throw "Unsafe ZIP entry path: $($entry.FullName)"
            }
            if (-not $name.StartsWith('FrameTrail/', [StringComparison]::Ordinal)) {
                throw "Source ZIP entry is outside the FrameTrail/ root: $($entry.FullName)"
            }

            $normalized = $name.TrimEnd('/')
            $parts = $normalized.Split('/')
            if ($parts.Where({ [string]::IsNullOrEmpty($_) }).Count -gt 0) {
                throw "ZIP entry has ambiguous empty path components: $($entry.FullName)"
            }
            foreach ($part in $parts) {
                if ($part.EndsWith('.') -or $part.EndsWith(' ')) {
                    throw "ZIP entry component ends with a dot or space: $($entry.FullName)"
                }
                if ($part -match '[\x00-\x1F<>:"|?*]') {
                    throw "ZIP entry component contains a Windows-invalid character: $($entry.FullName)"
                }
                if ($part -match '^(?i:CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\..*)?$') {
                    throw "ZIP entry component uses a reserved Windows device name: $($entry.FullName)"
                }
            }
            if ($normalized -eq 'FrameTrail' -and -not $isDirectory) {
                throw "The FrameTrail root entry must be a directory: $($entry.FullName)"
            }
            if ($entriesByPath.ContainsKey($normalized)) {
                throw "Duplicate ZIP entry after case-insensitive path normalization: $normalized"
            }

            $target = [IO.Path]::GetFullPath((Join-Path $root ($normalized.Replace('/', [IO.Path]::DirectorySeparatorChar))))
            if ($target -ne $root -and -not $target.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) {
                throw "ZIP entry escapes extraction root: $($entry.FullName)"
            }

            $entriesByPath.Add($normalized, [pscustomobject]@{ Name=$normalized; IsDirectory=$isDirectory })
            if ($isDirectory) { $directoryCount++ } else { $fileCount++ }
        }

        if ($fileCount -eq 0 -or -not $entriesByPath.ContainsKey('FrameTrail/SOURCE-METADATA.json') -or
            $entriesByPath['FrameTrail/SOURCE-METADATA.json'].IsDirectory) {
            throw 'Source ZIP must contain files and FrameTrail/SOURCE-METADATA.json.'
        }

        foreach ($item in $entriesByPath.Values) {
            $parts = $item.Name.Split('/')
            for ($index = 1; $index -lt $parts.Length; $index++) {
                $parent = [string]::Join('/', $parts[0..($index - 1)])
                if ($entriesByPath.ContainsKey($parent) -and -not $entriesByPath[$parent].IsDirectory) {
                    throw "ZIP file/directory path conflict: '$parent' is a file and an ancestor of '$($item.Name)'."
                }
            }
        }

        return [pscustomobject]@{
            EntryCount = $entriesByPath.Count
            FileCount = $fileCount
            DirectoryCount = $directoryCount
            Root = 'FrameTrail/'
            DuplicateComparison = 'OrdinalIgnoreCase'
        }
    }
    finally { $archive.Dispose() }
}
