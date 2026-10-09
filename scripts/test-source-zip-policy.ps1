[CmdletBinding()]
param()

# Isolated ZIP-structure fixtures only. This test never extracts or builds a source package.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'source-zip-policy.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$runId = [guid]::NewGuid().ToString('N')
$tempBoundary = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\', '/') + '\'
$tempRoot = Join-Path $env:TEMP "FrameTrail-source-zip-policy-$runId"
$outputRoot = Join-Path $repoRoot "artifacts/validation/source-zip-policy-$runId"
$checks = [Collections.Generic.List[object]]::new()
$result = $null

function New-FixtureZip([string]$Path, [string[]]$Entries) {
    $archive = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $Entries) {
            $entry = $archive.CreateEntry($name)
            if (-not $name.EndsWith('/')) {
                $stream = $entry.Open()
                try {
                    $bytes = [Text.Encoding]::UTF8.GetBytes("fixture:$name")
                    $stream.Write($bytes, 0, $bytes.Length)
                } finally { $stream.Dispose() }
            }
        }
    } finally { $archive.Dispose() }
}

function Invoke-FixtureCase([string]$Name, [string[]]$Entries, [bool]$ShouldPass) {
    $zipPath = Join-Path $tempRoot "$Name.zip"
    New-FixtureZip $zipPath $Entries
    $errorText = $null
    $summary = $null
    try { $summary = Assert-FrameTrailSourceZipLayout -SourceZipPath $zipPath -ExtractionRoot (Join-Path $tempRoot "$Name-extract") }
    catch { $errorText = $_.Exception.Message }
    $passed = if ($ShouldPass) { $null -eq $errorText -and $summary.FileCount -eq 2 } else { $null -ne $errorText }
    $checks.Add([pscustomobject]@{ Name=$Name; Passed=$passed; ExpectedPass=$ShouldPass; Error=$errorText; Summary=$summary })
    if (-not $passed) { throw "源码 ZIP 结构策略回归失败：$Name；$errorText" }
}

try {
    $null = New-Item -ItemType Directory -Path $tempRoot
    $null = New-Item -ItemType Directory -Path $outputRoot
    Invoke-FixtureCase 'valid-root-with-explicit-directories' @(
        'FrameTrail/', 'FrameTrail/docs/', 'FrameTrail/SOURCE-METADATA.json', 'FrameTrail/docs/readme.md'
    ) $true
    Invoke-FixtureCase 'reject-sibling-file' @(
        'FrameTrail/SOURCE-METADATA.json', 'FrameTrail/README.md', 'unverified.txt'
    ) $false
    Invoke-FixtureCase 'reject-case-changed-root' @(
        'frametrail/SOURCE-METADATA.json', 'FrameTrail/README.md'
    ) $false
    Invoke-FixtureCase 'reject-case-insensitive-duplicate' @(
        'FrameTrail/SOURCE-METADATA.json', 'FrameTrail/Assets/icon.png', 'FrameTrail/assets/ICON.PNG'
    ) $false
    Invoke-FixtureCase 'reject-file-directory-same-path' @(
        'FrameTrail/SOURCE-METADATA.json', 'FrameTrail/assets', 'FrameTrail/assets/'
    ) $false
    Invoke-FixtureCase 'reject-file-ancestor-conflict' @(
        'FrameTrail/SOURCE-METADATA.json', 'FrameTrail/assets', 'FrameTrail/assets/icon.png'
    ) $false
    Invoke-FixtureCase 'reject-path-traversal' @(
        'FrameTrail/SOURCE-METADATA.json', 'FrameTrail/../outside.txt'
    ) $false
    Invoke-FixtureCase 'reject-ambiguous-empty-component' @(
        'FrameTrail/SOURCE-METADATA.json', 'FrameTrail//README.md'
    ) $false
    Invoke-FixtureCase 'reject-missing-metadata' @(
        'FrameTrail/README.md', 'FrameTrail/docs/readme.md'
    ) $false
    Invoke-FixtureCase 'reject-trailing-dot' @(
        'FrameTrail/SOURCE-METADATA.json', 'FrameTrail/assets/icon.'
    ) $false
    Invoke-FixtureCase 'reject-trailing-space' @(
        'FrameTrail/SOURCE-METADATA.json', 'FrameTrail/assets/icon '
    ) $false
    Invoke-FixtureCase 'reject-control-character' @(
        'FrameTrail/SOURCE-METADATA.json', ("FrameTrail/assets/icon`u{0001}.png")
    ) $false
    Invoke-FixtureCase 'reject-invalid-windows-character' @(
        'FrameTrail/SOURCE-METADATA.json', 'FrameTrail/assets/icon?.png'
    ) $false
    Invoke-FixtureCase 'reject-reserved-con-extension' @(
        'FrameTrail/SOURCE-METADATA.json', 'FrameTrail/assets/CON.txt'
    ) $false
    Invoke-FixtureCase 'reject-reserved-com-extension' @(
        'FrameTrail/SOURCE-METADATA.json', 'FrameTrail/assets/COM9.log'
    ) $false
    Invoke-FixtureCase 'reject-reserved-lpt-extension' @(
        'FrameTrail/SOURCE-METADATA.json', 'FrameTrail/assets/LPT1.dat'
    ) $false
    Invoke-FixtureCase 'reject-metadata-directory' @(
        'FrameTrail/SOURCE-METADATA.json/', 'FrameTrail/README.md'
    ) $false

    $result = [ordered]@{
        SchemaVersion = 1
        Passed = $true
        Scope = 'Isolated synthetic ZIP structure policy tests; no extraction, application, installer, device, or build.'
        Cases = @($checks)
    }
}
catch {
    $result = [ordered]@{
        SchemaVersion = 1
        Passed = $false
        Scope = 'Isolated synthetic ZIP structure policy tests'
        Cases = @($checks)
        Error = $_.Exception.ToString()
    }
    throw
}
finally {
    $resultPath = Join-Path $outputRoot 'result.json'
    if (Test-Path -LiteralPath $outputRoot) {
        $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resultPath -Encoding utf8
    }
    if (Test-Path -LiteralPath $tempRoot) {
        $fullTemp = [IO.Path]::GetFullPath($tempRoot)
        if (-not $fullTemp.StartsWith($tempBoundary, [StringComparison]::OrdinalIgnoreCase)) {
            throw "拒绝清理TEMP之外的ZIP测试暂存：$fullTemp"
        }
        Remove-Item -LiteralPath $fullTemp -Recurse -Force
    }
}

Write-Host "PASS: $($checks.Count) 源码 ZIP 结构策略合成用例；$outputRoot"
