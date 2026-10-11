[CmdletBinding()]
param(
    [string]$ArchivePath,
    [switch]$Offline
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$asset = 'ffmpeg-n8.1.2-34-g9b6c8969e0-win64-lgpl-shared-8.1.zip'
$releaseTag = 'autobuild-2026-07-31-14-10'
$assetUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/$releaseTag/$asset"
$expectedSha256 = 'c222a490dde4e7059f45495deef6bfb98dbcacc2b43df5b607546252037aa95c'
$toolchainRoot = Join-Path $repoRoot 'artifacts/toolchains/ffmpeg-n8.1.2-34-g9b6c8969e0-win64-lgpl-shared'
$output = Join-Path $repoRoot 'artifacts/toolchains/libav-8.1-win-x64'
$defaultArchive = Join-Path $toolchainRoot $asset
$unpackedRoot = Join-Path $toolchainRoot 'unpacked/ffmpeg-n8.1.2-34-g9b6c8969e0-win64-lgpl-shared-8.1'
$licenseSource = Join-Path $unpackedRoot 'LICENSE.txt'
$required = @('avcodec-62.dll', 'avformat-62.dll', 'avutil-60.dll', 'swscale-9.dll', 'swresample-6.dll')
$archivePackageRoot = 'ffmpeg-n8.1.2-34-g9b6c8969e0-win64-lgpl-shared-8.1/'

function Get-ArchiveRequiredFileHashes([string]$Path) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $hashes = @{}
        foreach ($entry in $zip.Entries) {
            $entryName = $entry.FullName.Replace('\', '/')
            if (-not $entryName.StartsWith($archivePackageRoot, [StringComparison]::Ordinal)) { continue }
            $relative = $entryName.Substring($archivePackageRoot.Length)
            if ($relative -notmatch '^bin/[^/]+\.dll$' -and $relative -ne 'bin/ffmpeg.exe' -and $relative -ne 'LICENSE.txt') { continue }
            $stream = $entry.Open()
            try {
                $sha = [Security.Cryptography.SHA256]::Create()
                try { $hashes[$relative] = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
                finally { $sha.Dispose() }
            }
            finally { $stream.Dispose() }
        }
        if (-not $hashes.ContainsKey('bin/ffmpeg.exe') -or -not $hashes.ContainsKey('LICENSE.txt')) { throw 'Pinned archive is missing ffmpeg.exe or LICENSE.txt.' }
        if (@($hashes.Keys | Where-Object { $_ -match '^bin/[^/]+\.dll$' }).Count -eq 0) { throw 'Pinned archive contains no runtime DLLs.' }
        return $hashes
    } finally { $zip.Dispose() }
}

function Assert-UnpackedFilesMatchArchive([string]$Root, [hashtable]$ArchiveHashes) {
    $archiveDllNames = @($ArchiveHashes.Keys | Where-Object { $_ -match '^bin/[^/]+\.dll$' } | ForEach-Object { [IO.Path]::GetFileName($_) } | Sort-Object)
    $localDllNames = @(Get-ChildItem -LiteralPath (Join-Path $Root 'bin') -File -Filter '*.dll' | Select-Object -ExpandProperty Name | Sort-Object)
    if (($archiveDllNames -join "`n") -cne ($localDllNames -join "`n")) { throw 'Cached extraction DLL inventory differs from the pinned archive.' }
    foreach ($relative in $ArchiveHashes.Keys) {
        $localRelative = $relative.Replace('/', '\')
        $localPath = Join-Path $Root $localRelative
        if (-not (Test-Path -LiteralPath $localPath -PathType Leaf)) { throw "Cached extraction is missing archive member: $relative" }
        $localHash = (Get-FileHash -LiteralPath $localPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($localHash -ne $ArchiveHashes[$relative]) { throw "Cached extraction does not match verified archive: $relative" }
    }
}

if (-not $ArchivePath) { $ArchivePath = $defaultArchive }
$ArchivePath = [IO.Path]::GetFullPath($ArchivePath)
if (-not (Test-Path -LiteralPath $ArchivePath -PathType Leaf)) {
    if ($Offline) { throw "FFmpeg archive not found in offline mode: $ArchivePath" }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $ArchivePath) | Out-Null
    Invoke-WebRequest -Uri $assetUrl -OutFile $ArchivePath
}

$archiveHash = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($archiveHash -ne $expectedSha256) {
    throw "FFmpeg archive SHA-256 mismatch. Expected $expectedSha256; got $archiveHash"
}
$archiveFileHashes = Get-ArchiveRequiredFileHashes $ArchivePath

$bin = Join-Path $unpackedRoot 'bin'
if (-not (Test-Path -LiteralPath $bin -PathType Container) -or -not (Test-Path -LiteralPath $licenseSource -PathType Leaf)) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    New-Item -ItemType Directory -Force -Path $toolchainRoot | Out-Null
    $extractRoot = Join-Path $toolchainRoot ('.extract-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $extractRoot | Out-Null
    try {
        $rootFull = [IO.Path]::GetFullPath($extractRoot).TrimEnd('\') + '\'
        $zip = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
        try {
            foreach ($entry in $zip.Entries) {
                $destination = [IO.Path]::GetFullPath((Join-Path $extractRoot $entry.FullName.Replace('/', '\')))
                if (-not $destination.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "Unsafe archive entry path: $($entry.FullName)"
                }
                if ($entry.FullName.EndsWith('/')) {
                    New-Item -ItemType Directory -Force -Path $destination | Out-Null
                } else {
                    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
                    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination, $true)
                }
            }
        } finally { $zip.Dispose() }
        $candidate = Join-Path $extractRoot 'ffmpeg-n8.1.2-34-g9b6c8969e0-win64-lgpl-shared-8.1'
        if (-not (Test-Path (Join-Path $candidate 'bin/ffmpeg.exe'))) { throw 'Pinned archive layout did not match the expected package root.' }
        Assert-UnpackedFilesMatchArchive -Root $candidate -ArchiveHashes $archiveFileHashes
        $toolchainRootFull = [IO.Path]::GetFullPath($toolchainRoot).TrimEnd('\')
        $candidateFull = [IO.Path]::GetFullPath($candidate)
        $unpackedFull = [IO.Path]::GetFullPath($unpackedRoot)
        if (-not $candidateFull.StartsWith($toolchainRootFull + '\', [StringComparison]::OrdinalIgnoreCase) -or
            -not $unpackedFull.StartsWith($toolchainRootFull + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Extraction candidate or destination escaped the toolchain directory.'
        }
        if (Test-Path -LiteralPath $unpackedFull) { throw "Incomplete unpacked tree exists; inspect manually before replacing: $unpackedFull" }
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $unpackedFull) | Out-Null
        Move-Item -LiteralPath $candidate -Destination $unpackedRoot
    } finally {
        $resolvedExtract = [IO.Path]::GetFullPath($extractRoot)
        $artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
        if ($resolvedExtract.StartsWith($artifactsRoot + '\', [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedExtract)) {
            Remove-Item -LiteralPath $resolvedExtract -Recurse -Force
        }
    }
}

# The archive is the source of truth even when a prior extraction already exists.
Assert-UnpackedFilesMatchArchive -Root $unpackedRoot -ArchiveHashes $archiveFileHashes

foreach ($name in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $bin $name) -PathType Leaf)) { throw "Required runtime DLL missing: $name" }
}
if (-not (Test-Path -LiteralPath $licenseSource -PathType Leaf)) { throw 'The pinned package LICENSE.txt is missing.' }

$ffmpeg = Join-Path $bin 'ffmpeg.exe'
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $ffmpeg
$start.Arguments = '-hide_banner -version'
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$process = [Diagnostics.Process]::Start($start)
$versionOut = $process.StandardOutput.ReadToEnd() + $process.StandardError.ReadToEnd()
$process.WaitForExit()
if ($process.ExitCode -ne 0 -or $versionOut -notmatch 'ffmpeg version n8\.1\.2-34-g9b6c8969e0') { throw "Unexpected FFmpeg version output: $versionOut" }

$start.Arguments = '-hide_banner -buildconf'
$process = [Diagnostics.Process]::Start($start)
$buildconf = $process.StandardOutput.ReadToEnd() + $process.StandardError.ReadToEnd()
$process.WaitForExit()
if ($process.ExitCode -ne 0) { throw 'ffmpeg -buildconf failed.' }
if ($buildconf -match '--enable-gpl|--enable-nonfree') { throw 'Pinned FFmpeg configuration enables GPL or nonfree components.' }
if ($buildconf -notmatch '--enable-shared' -or $buildconf -notmatch '--disable-static') { throw 'Pinned FFmpeg is not a shared-only build.' }

$start.Arguments = '-hide_banner -L'
$process = [Diagnostics.Process]::Start($start)
$licenseOutput = $process.StandardOutput.ReadToEnd() + $process.StandardError.ReadToEnd()
$process.WaitForExit()
if ($process.ExitCode -ne 0 -or $licenseOutput -notmatch '(?s)GNU Lesser General Public License.*version 3') { throw 'FFmpeg runtime license check did not report LGPL version 3.' }

if (-not (Test-Path -LiteralPath $output)) { New-Item -ItemType Directory -Force -Path $output | Out-Null }
$allowed = @($required) + @('LICENSE.txt', 'provenance.json', 'buildconf.txt', 'version.txt')
$unexpected = @(Get-ChildItem -LiteralPath $output -Force | Where-Object { $_.Name -notin $allowed })
if ($unexpected.Count -gt 0) { throw "Runtime output contains unexpected entries; inspect manually: $($unexpected.Name -join ', ')" }

$dllManifest = @()
foreach ($name in $required) {
    $source = Join-Path $bin $name
    Copy-Item -LiteralPath $source -Destination (Join-Path $output $name) -Force
    $dllManifest += [ordered]@{ file = $name; sha256 = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant() }
}
Copy-Item -LiteralPath $licenseSource -Destination (Join-Path $output 'LICENSE.txt') -Force
Set-Content -LiteralPath (Join-Path $output 'buildconf.txt') -Value $buildconf.TrimEnd() -Encoding utf8
Set-Content -LiteralPath (Join-Path $output 'version.txt') -Value $versionOut.TrimEnd() -Encoding utf8
$provenance = [ordered]@{
    component = 'FFmpeg shared runtime for native frame decoding'
    version = 'n8.1.2-34-g9b6c8969e0-20260731'
    sourceCommit = '9b6c8969e05b4f0b29f0f85cd501be6b3e582e6b'
    archive = $asset
    archiveUrl = $assetUrl
    archiveSha256 = $archiveHash
    releaseTag = $releaseTag
    license = 'GNU LGPL version 3; dynamically linked shared libraries'
    runtimeFiles = $dllManifest
    configurationChecks = @('version matched pinned build', 'shared enabled', 'static disabled', 'GPL and nonfree configure flags absent', 'runtime license reports LGPL v3')
    sourceMaterial = 'artifacts/research/libav-source-materials/README.md'
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
}
$provenance | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'provenance.json') -Encoding utf8
Write-Host "Prepared verified FFmpeg runtime at $output"
