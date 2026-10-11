[CmdletBinding()]
param([string]$ArchivePath, [switch]$Offline)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifestPath = Join-Path $repoRoot 'third_party/ffmpeg/manifest.json'
$toolchainsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts/toolchains'))
$cacheRoot = Join-Path $toolchainsRoot 'dogsc-native-8.1.2'
$legacyLibavRoot = Join-Path $toolchainsRoot 'libav-8.1-win-x64'

function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Assert-PathWithin([string]$Path, [string]$Boundary, [string]$Label) {
    $full = [IO.Path]::GetFullPath($Path)
    $base = [IO.Path]::GetFullPath($Boundary).TrimEnd('\', '/')
    if (-not $full.StartsWith($base + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label escaped its allowed directory: $full"
    }
    return $full
}
function Assert-NoReparseWithin([string]$Path, [string]$Boundary) {
    $full = Assert-PathWithin $Path $Boundary 'Path'
    $boundaryFull = [IO.Path]::GetFullPath($Boundary).TrimEnd('\','/')
    $cursor = $full
    while ($true) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing to follow a reparse point inside the allowed boundary: $cursor" }
        }
        if ($cursor -eq $boundaryFull) { break }
        $parent = Split-Path -Parent $cursor
        if (-not $parent -or $parent -eq $cursor) { throw "Path ancestry did not reach the expected boundary: $full" }
        $cursor = $parent
    }
}
function Assert-NoReparseTree([string]$Root, [string]$Boundary) {
    Assert-NoReparseWithin $Root $Boundary
    foreach ($item in Get-ChildItem -LiteralPath $Root -Force -Recurse) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing to use or remove a tree containing a reparse point: $($item.FullName)" }
    }
}
function Get-ManifestFiles($Manifest) {
    if ($Manifest.schemaVersion -ne 1 -or $Manifest.version -ne '8.1.2') { throw 'Unsupported DogSC native media manifest schema or version.' }
    $archive = $Manifest.archive
    if (-not $archive.file -or -not $archive.url -or [string]$archive.sha256 -notmatch '^[0-9a-fA-F]{64}$') {
        throw 'Native media manifest must contain archive file, URL, and a finalized SHA-256.'
    }
    $files = @($Manifest.runtimeFiles)
    $expected = @('cli/ffmpeg.exe','cli/ffprobe.exe','libav/avcodec-62.dll','libav/avformat-62.dll','libav/avutil-60.dll','libav/swscale-9.dll','libav/swresample-6.dll')
    if ($files.Count -ne $expected.Count) { throw 'Native media manifest must list exactly two CLI tools and five libav DLLs.' }
    $actual = @()
    foreach ($file in $files) {
        $path = ([string]$file.path).Replace('\','/')
        if ($path -notin $expected -or [string]$file.sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw "Invalid native runtime file entry: $path" }
        $actual += $path
    }
    if (@(Compare-Object ($expected | Sort-Object) ($actual | Sort-Object)).Count -ne 0) { throw 'Native media manifest runtime inventory is incomplete or duplicated.' }
    if (-not $Manifest.sourceArchive.file -or -not $Manifest.sourceArchive.url -or [string]$Manifest.sourceArchive.sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'Native media manifest must pin a corresponding source archive.' }
    $archiveUri = [Uri]$archive.url
    $sourceUri = [Uri]$Manifest.sourceArchive.url
    if (-not $archiveUri.IsAbsoluteUri -or $archiveUri.Scheme -ne 'https' -or $archiveUri.Host -ne 'github.com' -or
        -not $sourceUri.IsAbsoluteUri -or $sourceUri.Scheme -ne 'https' -or $sourceUri.Host -ne 'github.com') { throw 'Native and source archive URLs must be HTTPS GitHub release assets.' }
    $support = @($Manifest.supportFiles)
    if (@($support | Where-Object { ([string]$_.path).Replace('\','/') -notmatch '^libav/[^/]+$' }).Count -gt 0) { throw 'supportFiles paths must be direct files under libav/.' }
    foreach ($required in @('libav/LICENSE.txt','libav/provenance.json','libav/version.txt','libav/buildconf.txt')) {
        if (@($support | Where-Object { ([string]$_.path).Replace('\','/') -ceq $required }).Count -ne 1) { throw "Native media supportFiles must list $required exactly once." }
    }
    $source = @($Manifest.sourceFiles)
    if ($source.Count -eq 0) { throw 'Native media manifest sourceFiles is empty.' }
    $all = @($files) + @($support) + @($source)
    $allPaths = @()
    foreach ($entry in $all) {
        $path = ([string]$entry.path).Replace('\','/')
        if ([string]::IsNullOrWhiteSpace($path) -or $path.StartsWith('/') -or $path -match '(^|/)\.\.?(/|$)' -or $path.Contains(':') -or $path -match '[<>:"|?*]' -or [string]$entry.sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw "Invalid native manifest file entry: $path" }
        if ($allPaths -contains $path) { throw "Duplicate path in native manifest: $path" }
        $allPaths += $path
    }
    if ([IO.Path]::GetFileName([string]$Manifest.sourceArchive.file) -cne [string]$Manifest.sourceArchive.file) { throw 'sourceArchive.file must be a plain filename.' }
    if (@($source | Where-Object { ([string]$_.path).Replace('\','/').StartsWith('source-materials/',[StringComparison]::Ordinal) }).Count -ne $source.Count) { throw 'Every sourceFiles path must be under source-materials/.' }
    return ,([pscustomobject]@{ Runtime=$files; Support=$support; Source=$source; All=$all; Paths=$allPaths })
}
function Assert-ZipEntryName([string]$Name) {
    $normalized = $Name.Replace('\','/')
    if ([string]::IsNullOrWhiteSpace($normalized) -or $normalized.StartsWith('/') -or $normalized.Contains(':') -or $normalized.Contains('//') -or $normalized -match '(^|/)\.\.?(/|$)') { throw "Unsafe native archive path: $Name" }
    foreach ($part in $normalized.TrimEnd('/').Split('/')) {
        if ([string]::IsNullOrEmpty($part) -or $part.EndsWith('.') -or $part.EndsWith(' ') -or $part -match '[\x00-\x1f<>:"|?*]' -or $part -match '^(?i:CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\..*)?$') { throw "Unsafe native archive path component: $Name" }
    }
    if ($normalized -notmatch '^(cli|libav|source-materials)/') { throw "Unexpected native archive root: $Name" }
    return $normalized
}
function Assert-ArchiveInventory([string]$Path, $ManifestInventory) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $byPath = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        foreach ($entry in $zip.Entries) {
            $name = Assert-ZipEntryName $entry.FullName
            if ($byPath.ContainsKey($name)) { throw "Duplicate native archive entry: $name" }
            if ((($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw "Native archive contains a symbolic link: $name" }
            $byPath.Add($name, $entry)
        }
        $manifestMap = @{}
        foreach ($item in $ManifestInventory.All) { $manifestMap[([string]$item.path).Replace('\','/')] = ([string]$item.sha256).ToLowerInvariant() }
        $runtimeMap = @{}
        foreach ($item in $ManifestInventory.Runtime) { $runtimeMap[([string]$item.path).Replace('\','/')] = $true }
        $actualFiles = @($byPath.Keys | Where-Object { -not $byPath[$_].FullName.EndsWith('/') } | Sort-Object)
        if (@(Compare-Object ($manifestMap.Keys | Sort-Object) $actualFiles).Count -ne 0) { throw 'Native archive contains missing or unmanifested files.' }
        foreach ($expected in $manifestMap.Keys) {
            if (-not $byPath.ContainsKey($expected)) { throw "Native archive is missing manifest runtime file: $expected" }
            $entry = $byPath[$expected]
            $stream = $entry.Open(); $sha = [Security.Cryptography.SHA256]::Create()
            try { $actual = [Convert]::ToHexString($sha.ComputeHash($stream)).ToLowerInvariant() }
            finally { $sha.Dispose(); $stream.Dispose() }
            if ($actual -ne $manifestMap[$expected]) { throw "Native archive runtime hash mismatch: $expected" }
        }
        foreach ($name in $byPath.Keys) {
            if ($name -match '\.(?i:dll|exe)$' -and -not $runtimeMap.ContainsKey($name)) { throw "Unexpected executable or DLL outside runtimeFiles: $name" }
        }
        foreach ($required in @('cli/ffmpeg.exe','cli/ffprobe.exe')) { if (-not $manifestMap.ContainsKey($required)) { throw "Native manifest is missing CLI tool: $required" } }
        return $byPath.Count
    } finally { $zip.Dispose() }
}
function Expand-SafeArchive([string]$Path, [string]$Destination, $ManifestInventory) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $null = New-Item -ItemType Directory -Path $Destination
    $root = [IO.Path]::GetFullPath($Destination).TrimEnd('\','/')
    $boundary = $root + [IO.Path]::DirectorySeparatorChar
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        foreach ($entry in $zip.Entries) {
            $name = Assert-ZipEntryName $entry.FullName
            $target = [IO.Path]::GetFullPath((Join-Path $root ($name.Replace('/',[IO.Path]::DirectorySeparatorChar))))
            if (-not $target.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw "Native archive path escaped staging directory: $name" }
            if ($name.EndsWith('/')) { $null = New-Item -ItemType Directory -Path $target -Force; continue }
            $null = New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$target,$false)
        }
    } finally { $zip.Dispose() }
    foreach ($file in $ManifestInventory.All) {
        $target = Join-Path $root ([string]$file.path)
        if ((Get-Sha256 $target) -ne ([string]$file.sha256).ToLowerInvariant()) { throw "Extracted native runtime hash mismatch: $($file.path)" }
    }
}
function Assert-Cache([string]$Root, $Manifest, $ManifestInventory) {
    $archivePath = Join-Path $Root ([string]$Manifest.archive.file)
    if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf) -or (Get-Sha256 $archivePath) -ne ([string]$Manifest.archive.sha256).ToLowerInvariant()) { throw "Native media cache archive is missing or mismatched: $archivePath" }
    $null = Assert-ArchiveInventory $archivePath $ManifestInventory
    foreach ($file in $ManifestInventory.All) {
        $path = Join-Path $Root ([string]$file.path)
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Sha256 $path) -ne ([string]$file.sha256).ToLowerInvariant()) { throw "Native media cache file is missing or mismatched: $($file.path)" }
    }
    foreach ($directory in @('cli','libav')) {
        $expectedNames = @($ManifestInventory.Runtime | Where-Object { ([string]$_.path).StartsWith("$directory/",[StringComparison]::Ordinal) } | ForEach-Object { [IO.Path]::GetFileName([string]$_.path) })
        $actualNames = @(Get-ChildItem -LiteralPath (Join-Path $Root $directory) -File | Where-Object Extension -in @('.exe','.dll') | Select-Object -ExpandProperty Name)
        if (@(Compare-Object ($expectedNames | Sort-Object) ($actualNames | Sort-Object)).Count -ne 0) { throw "Native media cache has unexpected executable/DLL inventory in $directory." }
    }
    $root = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $expectedTree = @($ManifestInventory.All | ForEach-Object { ([string]$_.path).Replace('\','/') }) + ([string]$Manifest.archive.file)
    $actualTree = @(Get-ChildItem -LiteralPath $Root -Force -Recurse | ForEach-Object {
        if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Native media cache contains a reparse point: $($_.FullName)" }
        if (-not $_.PSIsContainer) { $_.FullName.Substring($root.Length).Replace('\','/') }
    } | Where-Object { $_ } | Sort-Object)
    if (@(Compare-Object ($expectedTree | Sort-Object) $actualTree).Count -ne 0) { throw 'Native media cache contains unmanifested or missing files.' }
}
function Invoke-NativeTool([string]$Path,[string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($Path); $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    foreach($argument in $Arguments){$null=$start.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::Start($start)
    try { $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync(); if(-not $process.WaitForExit(30000)){ $process.Kill($true); throw "Native media tool timed out: $Path" }; $text=$stdout.GetAwaiter().GetResult()+$stderr.GetAwaiter().GetResult(); if($process.ExitCode -ne 0){throw "Native media tool failed: $Path"}; return $text }
    finally { $process.Dispose() }
}
function Get-AvutilMetadata([string]$Path) {
    if (-not ('DogScNativeMedia.AvutilExports' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace DogScNativeMedia {
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr MetadataFunction();
    public static class AvutilExports {
        public static string Read(IntPtr module, string name) {
            IntPtr address = NativeLibrary.GetExport(module, name);
            var function = Marshal.GetDelegateForFunctionPointer<MetadataFunction>(address);
            IntPtr result = function();
            return Marshal.PtrToStringAnsi(result) ?? String.Empty;
        }
    }
}
'@
    }
    $module = [System.Runtime.InteropServices.NativeLibrary]::Load([IO.Path]::GetFullPath($Path))
    try {
        return [pscustomobject]@{
            License = [DogScNativeMedia.AvutilExports]::Read($module, 'avutil_license')
            Configuration = [DogScNativeMedia.AvutilExports]::Read($module, 'avutil_configuration')
        }
    } finally {
        [System.Runtime.InteropServices.NativeLibrary]::Free($module)
    }
}

if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "Native media manifest missing: $manifestPath" }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$manifestInventory = Get-ManifestFiles $manifest
$expectedArchiveHash = ([string]$manifest.archive.sha256).ToLowerInvariant()
$expectedArchiveFile = [string]$manifest.archive.file
if ([IO.Path]::GetFileName($expectedArchiveFile) -cne $expectedArchiveFile -or $expectedArchiveFile -match '[<>:"/\\|?*]') { throw 'Native media manifest archive filename is unsafe.' }
$cacheFull = [IO.Path]::GetFullPath($cacheRoot)
$toolchainsFull = [IO.Path]::GetFullPath($toolchainsRoot).TrimEnd('\') + '\'
if (-not $cacheFull.StartsWith($toolchainsFull,[StringComparison]::OrdinalIgnoreCase)) { throw 'Native cache path escaped artifacts/toolchains.' }
$archiveFull = if ($ArchivePath) { [IO.Path]::GetFullPath($ArchivePath) } else { Join-Path $cacheRoot $expectedArchiveFile }
if (-not (Test-Path -LiteralPath $archiveFull -PathType Leaf)) {
    if ($Offline) { throw "Native media archive unavailable in offline mode: $archiveFull" }
    $downloadParent = Split-Path -Parent $cacheRoot
    $downloadPath = Join-Path $downloadParent ('.native-download-' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $null = New-Item -ItemType Directory -Path $downloadParent -Force
        Assert-NoReparseWithin $downloadPath $downloadParent
        Invoke-WebRequest -Uri ([string]$manifest.archive.url) -OutFile $downloadPath
        if ((Get-Sha256 $downloadPath) -ne $expectedArchiveHash) { throw 'Downloaded native media archive SHA-256 mismatch.' }
        $archiveFull = $downloadPath
    } catch {
        if (Test-Path -LiteralPath $downloadPath) {
            $downloadFull = Assert-PathWithin $downloadPath $downloadParent 'Temporary download'
            Remove-Item -LiteralPath $downloadFull -Force
        }
        throw
    }
}
try {
if ((Get-Sha256 $archiveFull) -ne $expectedArchiveHash) { throw "Native media archive SHA-256 mismatch. Expected $expectedArchiveHash." }
$null = Assert-ArchiveInventory $archiveFull $manifestInventory

if (-not (Test-Path -LiteralPath $cacheRoot)) {
    $stage = Join-Path $toolchainsRoot ('.dogsc-native-stage-' + [guid]::NewGuid().ToString('N'))
    try {
        $null = New-Item -ItemType Directory -Path $toolchainsRoot -Force
        Assert-NoReparseWithin $stage $toolchainsRoot
        $null = New-Item -ItemType Directory -Path $stage
        $stagedArchive = Join-Path $stage $expectedArchiveFile
        Copy-Item -LiteralPath $archiveFull -Destination $stagedArchive
        if ((Get-Sha256 $stagedArchive) -ne $expectedArchiveHash) { throw 'Staged native media archive hash mismatch.' }
        Expand-SafeArchive $stagedArchive (Join-Path $stage 'payload') $manifestInventory
        foreach ($directory in @('cli','libav','source-materials')) { Move-Item -LiteralPath (Join-Path $stage "payload/$directory") -Destination (Join-Path $stage $directory) }
        Remove-Item -LiteralPath (Join-Path $stage 'payload') -Force
        $null = Assert-PathWithin $cacheRoot $toolchainsRoot 'Native cache'
        Move-Item -LiteralPath $stage -Destination $cacheRoot
        $stage = $null
    } finally {
        if ($stage -and (Test-Path -LiteralPath $stage)) {
            $stageFull = Assert-PathWithin $stage $toolchainsRoot 'Native staging directory'
            Assert-NoReparseTree $stageFull $toolchainsRoot
            Remove-Item -LiteralPath $stageFull -Recurse -Force
        }
    }
} elseif (-not (Test-Path -LiteralPath $archiveFull -PathType Leaf) -and $archiveFull -eq (Join-Path $cacheRoot $expectedArchiveFile)) {
    throw "Native media cache exists without its pinned archive; inspect before replacing: $cacheRoot"
}

Assert-NoReparseTree $cacheRoot $toolchainsRoot
Assert-Cache $cacheRoot $manifest $manifestInventory
} finally {
    if ($downloadPath -and (Test-Path -LiteralPath $downloadPath)) {
        $downloadFull = Assert-PathWithin $downloadPath $downloadParent 'Temporary download'
        Assert-NoReparseWithin $downloadFull $downloadParent
        Remove-Item -LiteralPath $downloadFull -Force
    }
}
$ffmpeg = Join-Path $cacheRoot 'cli/ffmpeg.exe'
$ffprobe = Join-Path $cacheRoot 'cli/ffprobe.exe'
$versionText = Invoke-NativeTool $ffmpeg @('-hide_banner','-version')
if ($versionText -notmatch 'ffmpeg version n?8\.1\.2(?:\D|$)') { throw 'FFmpeg CLI version does not match the pinned 8.1.2 manifest.' }
$buildconf = Invoke-NativeTool $ffmpeg @('-hide_banner','-buildconf')
foreach ($requiredFlag in @('--enable-gpl','--enable-libx264','--enable-version3','--enable-static','--disable-shared')) {
    if ($buildconf -notmatch [regex]::Escape($requiredFlag)) { throw "FFmpeg CLI build configuration is missing $requiredFlag." }
}
if ($buildconf -match '--enable-nonfree') { throw 'FFmpeg CLI must not enable nonfree components.' }
$licenseOutput = Invoke-NativeTool $ffmpeg @('-hide_banner','-L')
if ($licenseOutput -notmatch '(?is)GNU General Public License.*version 3') { throw 'FFmpeg CLI license output does not identify GPL v3.' }
$probeVersion = Invoke-NativeTool $ffprobe @('-hide_banner','-version')
if ($probeVersion -notmatch 'ffprobe version n?8\.1\.2(?:\D|$)') { throw 'FFprobe CLI version does not match the pinned 8.1.2 manifest.' }
$dllBuildconf = Get-Content -LiteralPath (Join-Path $cacheRoot 'libav/buildconf.txt') -Raw
if ($dllBuildconf -notmatch '--enable-shared' -or $dllBuildconf -notmatch '--disable-static' -or
    $dllBuildconf -notmatch '--enable-version3' -or $dllBuildconf -match '--enable-gpl|--enable-nonfree') { throw 'libav DLL build configuration must be shared LGPLv3 and must not enable GPL/nonfree components.' }
$dllLicense = Get-Content -LiteralPath (Join-Path $cacheRoot 'libav/LICENSE.txt') -Raw
if ($dllLicense -notmatch '(?is)GNU Lesser General Public License.*version 3') { throw 'libav DLL license must identify LGPL v3.' }
$avutilMetadata = Get-AvutilMetadata (Join-Path $cacheRoot 'libav/avutil-60.dll')
if ($avutilMetadata.License -notmatch '(?is)LGPL version 3 or later') { throw 'avutil-60.dll does not declare LGPL version 3 or later.' }
if ($avutilMetadata.Configuration -notmatch '--enable-shared' -or $avutilMetadata.Configuration -notmatch '--disable-static' -or
    $avutilMetadata.Configuration -notmatch '--enable-version3' -or $avutilMetadata.Configuration -match '--enable-gpl|--enable-nonfree') {
    throw 'avutil-60.dll configuration must be shared LGPLv3 and must not enable GPL/nonfree components.'
}

$null = New-Item -ItemType Directory -Path $toolchainsRoot -Force
$legacyFull = Assert-PathWithin $legacyLibavRoot $toolchainsRoot 'Legacy libav output'
Assert-NoReparseWithin $legacyFull $toolchainsRoot
$null = New-Item -ItemType Directory -Path $legacyFull -Force
Assert-NoReparseTree $legacyFull $toolchainsRoot
if ((Get-Item -LiteralPath $legacyFull -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing to write through a reparse point: $legacyFull" }
$allowed = @('avcodec-62.dll','avformat-62.dll','avutil-60.dll','swscale-9.dll','swresample-6.dll','LICENSE.txt','provenance.json','version.txt','buildconf.txt')
$unexpected = @(Get-ChildItem -LiteralPath $legacyFull -Force | Where-Object Name -notin $allowed)
if ($unexpected.Count -gt 0) { throw "Legacy libav destination contains unowned entries; inspect manually: $($unexpected.Name -join ', ')" }
foreach ($file in $manifestInventory.Runtime | Where-Object { ([string]$_.path).StartsWith('libav/',[StringComparison]::Ordinal) }) {
    Copy-Item -LiteralPath (Join-Path $cacheRoot ([string]$file.path)) -Destination (Join-Path $legacyFull ([IO.Path]::GetFileName([string]$file.path))) -Force
}
foreach ($file in $manifestInventory.Support | Where-Object { ([string]$_.path).StartsWith('libav/',[StringComparison]::Ordinal) }) {
    Copy-Item -LiteralPath (Join-Path $cacheRoot ([string]$file.path)) -Destination (Join-Path $legacyFull ([IO.Path]::GetFileName([string]$file.path))) -Force
}
Write-Host "Verified DogSC native media at $cacheRoot"
