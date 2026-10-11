[CmdletBinding()]
param(
    [switch]$Offline,
    [string]$PackageDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$sourceRoot = Join-Path $artifactsRoot 'research/libav-source-materials'
$readmeSource = Join-Path $repoRoot 'licenses/FFmpeg-libav-CORRESPONDING-SOURCE.md'
$ffmpegSourceName = 'FFmpeg-9b6c8969e05b4f0b29f0f85cd501be6b3e582e6b-source.zip'
$ffmpegSourceUrl = 'https://codeload.github.com/FFmpeg/FFmpeg/zip/9b6c8969e05b4f0b29f0f85cd501be6b3e582e6b'
$ffmpegSourceSha256 = '696dc2da356a4d92f8a5d113e426e105896b66915ea54fe1724dd9029ff66fc0'
$buildSourceName = 'BtbN-FFmpeg-Builds-a99e8230eae00d1cee38f23076a7a1f55cd984e2.zip'
$buildSourceUrl = 'https://codeload.github.com/BtbN/FFmpeg-Builds/zip/a99e8230eae00d1cee38f23076a7a1f55cd984e2'
$buildSourceSha256 = '32bd407c060e53737d5db008e75f52e5b8d63adfe98490be3c044fa5e3628ccd'

function Ensure-PinnedArchive([string]$Name, [string]$Url, [string]$ExpectedSha256) {
    $destination = Join-Path $sourceRoot $Name
    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        $actual = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $ExpectedSha256) { throw "Existing source archive hash mismatch: $Name (expected $ExpectedSha256, got $actual)" }
        return
    }
    if ($Offline) { throw "Pinned source archive is missing in offline mode: $destination" }

    $temporary = Join-Path $sourceRoot ('.download-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        Invoke-WebRequest -Uri $Url -OutFile $temporary
        $actual = (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $ExpectedSha256) { throw "Downloaded source archive hash mismatch: $Name (expected $ExpectedSha256, got $actual)" }
        if (Test-Path -LiteralPath $destination) { throw "Source archive destination appeared during download; refusing to overwrite: $destination" }
        Move-Item -LiteralPath $temporary -Destination $destination
    } finally {
        $temporaryFull = [IO.Path]::GetFullPath($temporary)
        if ($temporaryFull.StartsWith([IO.Path]::GetFullPath($sourceRoot).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $temporaryFull)) {
            Remove-Item -LiteralPath $temporaryFull -Force
        }
    }
}

New-Item -ItemType Directory -Force -Path $sourceRoot | Out-Null
Ensure-PinnedArchive -Name $ffmpegSourceName -Url $ffmpegSourceUrl -ExpectedSha256 $ffmpegSourceSha256
Ensure-PinnedArchive -Name $buildSourceName -Url $buildSourceUrl -ExpectedSha256 $buildSourceSha256

$readmeDestination = Join-Path $sourceRoot 'README.md'
if (Test-Path -LiteralPath $readmeDestination -PathType Leaf) {
    $expectedReadmeHash = (Get-FileHash -LiteralPath $readmeSource -Algorithm SHA256).Hash
    $actualReadmeHash = (Get-FileHash -LiteralPath $readmeDestination -Algorithm SHA256).Hash
    if ($expectedReadmeHash -ne $actualReadmeHash) { throw "Existing source material README differs from tracked source: $readmeDestination" }
} else {
    Copy-Item -LiteralPath $readmeSource -Destination $readmeDestination
}

$expectedNames = @($ffmpegSourceName, $buildSourceName, 'README.md') | Sort-Object
$actualNames = @(Get-ChildItem -LiteralPath $sourceRoot -File | Select-Object -ExpandProperty Name | Sort-Object)
if (Compare-Object -ReferenceObject $expectedNames -DifferenceObject $actualNames) {
    throw "Source material directory has unexpected or missing files: $sourceRoot"
}

if ($PackageDirectory) {
    $packageFull = [IO.Path]::GetFullPath($PackageDirectory)
    if (-not $packageFull.StartsWith($artifactsRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "PackageDirectory must be within artifacts: $packageFull"
    }
    $destination = Join-Path $packageFull 'source-materials'
    if (Test-Path -LiteralPath $destination) {
        $sourceFiles = @(Get-ChildItem -LiteralPath $sourceRoot -File -Recurse | ForEach-Object { '{0}|{1}' -f $_.FullName.Substring($sourceRoot.Length).TrimStart('\'), (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } | Sort-Object)
        $destinationFiles = @(Get-ChildItem -LiteralPath $destination -File -Recurse | ForEach-Object { '{0}|{1}' -f $_.FullName.Substring($destination.Length).TrimStart('\'), (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } | Sort-Object)
        if (($sourceFiles -join "`n") -cne ($destinationFiles -join "`n")) {
            throw "Existing package source-materials differs; refusing to overwrite: $destination"
        }
    } else {
        New-Item -ItemType Directory -Force -Path $packageFull | Out-Null
        Copy-Item -LiteralPath $sourceRoot -Destination $destination -Recurse
    }
}

Write-Host "Verified corresponding source materials at $sourceRoot"
