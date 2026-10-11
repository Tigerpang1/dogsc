[CmdletBinding()]
param([switch]$Offline, [string]$PackageDirectory, [string]$ArchivePath)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$cacheRoot = Join-Path $repoRoot 'artifacts/toolchains/dogsc-native-8.1.2'
$ensureArgs = @{}
if ($Offline) { $ensureArgs.Offline = $true }
if ($ArchivePath) { $ensureArgs.ArchivePath = $ArchivePath }
& (Join-Path $PSScriptRoot 'ensure-native-media.ps1') @ensureArgs

$sourceRoot = Join-Path $cacheRoot 'source-materials'
if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) { throw "Verified native source materials are missing: $sourceRoot" }
$destination = if ($PackageDirectory) {
    $packageFull = [IO.Path]::GetFullPath($PackageDirectory)
    $artifactsBoundary = $artifactsRoot.TrimEnd('\') + '\'
    if (-not $packageFull.StartsWith($artifactsBoundary,[StringComparison]::OrdinalIgnoreCase)) { throw "PackageDirectory must be inside artifacts: $packageFull" }
    Join-Path $packageFull 'licenses/libav-corresponding-source'
} else { Join-Path $artifactsRoot 'research/dogsc-native-source-materials' }

function Get-TreeHashes([string]$Root) {
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $items = @(Get-ChildItem -LiteralPath $Root -Force -Recurse)
    if (@($items | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }).Count -gt 0) { throw "Source materials contain a reparse point: $Root" }
    return @($items | Where-Object { -not $_.PSIsContainer } | ForEach-Object {
        $relative = $_.FullName.Substring($rootFull.Length).Replace('\','/')
        '{0}|{1}' -f $relative,(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    } | Sort-Object)
}
function Assert-NoReparseWithin([string]$Path, [string]$Boundary) {
    $full = [IO.Path]::GetFullPath($Path)
    $base = [IO.Path]::GetFullPath($Boundary).TrimEnd('\','/')
    if (-not $full.StartsWith($base + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw "Path escaped artifacts: $full" }
    $cursor = $full
    while ($true) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Refusing to follow a reparse point: $cursor" }
        if ($cursor -eq $base) { break }
        $cursor = Split-Path -Parent $cursor
    }
}

if (Test-Path -LiteralPath $destination) {
    if (-not (Test-Path -LiteralPath $destination -PathType Container)) { throw "Source materials destination exists and is not a directory: $destination" }
    $sourceHashes = Get-TreeHashes $sourceRoot
    $destinationHashes = Get-TreeHashes $destination
    if (($sourceHashes -join "`n") -cne ($destinationHashes -join "`n")) { throw "Existing source materials differ; refusing to overwrite: $destination" }
} else {
    $parent = Split-Path -Parent $destination
    $null = New-Item -ItemType Directory -Path $parent -Force
    Assert-NoReparseWithin $parent $artifactsRoot
    $stage = Join-Path $parent ('.native-source-copy-' + [guid]::NewGuid().ToString('N'))
    Assert-NoReparseWithin $stage $artifactsRoot
    $null = New-Item -ItemType Directory -Path $stage
    try {
        foreach ($item in Get-ChildItem -LiteralPath $sourceRoot -Force -Recurse) {
            $relative = $item.FullName.Substring(([IO.Path]::GetFullPath($sourceRoot).TrimEnd('\') + '\').Length)
            $target = Join-Path $stage $relative
            if ($item.PSIsContainer) { $null = New-Item -ItemType Directory -Path $target -Force }
            else { $null = New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force; Copy-Item -LiteralPath $item.FullName -Destination $target }
        }
        if (((Get-TreeHashes $sourceRoot) -join "`n") -cne ((Get-TreeHashes $stage) -join "`n")) { throw 'Copied source materials failed the post-copy hash comparison.' }
        if (Test-Path -LiteralPath $destination) { throw "Source materials destination appeared during copy; refusing to overwrite: $destination" }
        Move-Item -LiteralPath $stage -Destination $destination
        $stage = $null
    } catch {
        throw
    } finally {
        if ($stage -and (Test-Path -LiteralPath $stage)) {
            Assert-NoReparseWithin $stage $artifactsRoot
            $stageItems = @(Get-ChildItem -LiteralPath $stage -Force -Recurse)
            if (@($stageItems | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -gt 0) { throw 'Refusing to remove source-copy staging tree containing a reparse point.' }
            Remove-Item -LiteralPath $stage -Recurse -Force
        }
    }
}
Write-Host "Verified native media corresponding source materials at $destination"
