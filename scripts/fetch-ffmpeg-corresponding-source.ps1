[CmdletBinding()]
param(
    [string]$FfmpegDirectory,
    [Parameter(Mandatory = $true)]
    [string]$Destination
)

$ErrorActionPreference = 'Stop'

if (-not $FfmpegDirectory) {
    $packageRoots = Get-ChildItem -LiteralPath (Join-Path $env:LOCALAPPDATA 'Microsoft/WinGet/Packages') `
        -Directory -Filter 'Gyan.FFmpeg_*' -ErrorAction SilentlyContinue
    foreach ($packageRoot in $packageRoots) {
        $candidate = Get-ChildItem -LiteralPath $packageRoot.FullName -Directory -Filter 'ffmpeg-8.1.1-full_build' |
            Select-Object -First 1
        if ($candidate) { $FfmpegDirectory = $candidate.FullName; break }
    }
}

if (-not $FfmpegDirectory) { throw '找不到本机 Gyan FFmpeg 8.1.1 full build。' }
$ffmpegExe = Join-Path $FfmpegDirectory 'bin/ffmpeg.exe'
$ffprobeExe = Join-Path $FfmpegDirectory 'bin/ffprobe.exe'
$readme = Join-Path $FfmpegDirectory 'README.txt'
$license = Join-Path $FfmpegDirectory 'LICENSE'
foreach ($required in @($ffmpegExe, $ffprobeExe, $readme, $license)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "FFmpeg 发行文件缺失：$required" }
}

$readmeText = Get-Content -LiteralPath $readme -Raw
if ($readmeText -notmatch '(?m)^Source Code:\s*https://github\.com/FFmpeg/FFmpeg/commit/([0-9a-f]{7,40})\s*$') {
    throw 'Gyan README 中没有可识别的 FFmpeg 精确源码提交。'
}
$shortCommit = $Matches[1]
$destinationPath = [System.IO.Path]::GetFullPath($Destination)
$null = New-Item -ItemType Directory -Path $destinationPath -Force

$ffmpegVersion = & $ffmpegExe -version 2>&1 | Out-String
if ($LASTEXITCODE -ne 0 -or $ffmpegVersion -notmatch 'ffmpeg version 8\.1\.1-full_build-www\.gyan\.dev') {
    throw 'FFmpeg 版本与预期的 Gyan 8.1.1 full build 不一致。'
}
$buildConfiguration = & $ffmpegExe -buildconf 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { throw '读取 ffmpeg -buildconf 失败。' }

$archivePath = Join-Path $destinationPath "FFmpeg-$shortCommit-source.zip"
$archiveUri = "https://codeload.github.com/FFmpeg/FFmpeg/zip/$shortCommit"
Invoke-WebRequest -Uri $archiveUri -OutFile $archivePath
Copy-Item -LiteralPath $readme -Destination (Join-Path $destinationPath 'Gyan-README.txt') -Force
Copy-Item -LiteralPath $license -Destination (Join-Path $destinationPath 'FFmpeg-GPLv3.txt') -Force
$ffmpegVersion | Set-Content -LiteralPath (Join-Path $destinationPath 'ffmpeg-version.txt') -Encoding utf8
$buildConfiguration | Set-Content -LiteralPath (Join-Path $destinationPath 'ffmpeg-buildconf.txt') -Encoding utf8

$binaryEntries = @()
foreach ($binary in @($ffmpegExe, $ffprobeExe)) {
    $hash = Get-FileHash -LiteralPath $binary -Algorithm SHA256
    $binaryEntries += "- $([System.IO.Path]::GetFileName($binary)): SHA-256 $($hash.Hash)"
}
$sourceHash = Get-FileHash -LiteralPath $archivePath -Algorithm SHA256
$manifest = @"
# FFmpeg 本地验证构建源材料

- Build: Gyan FFmpeg 8.1.1 full static GPLv3 build
- FFmpeg source commit prefix: $shortCommit (as published in the Gyan build README)
- Source archive: $(Split-Path -Leaf $archivePath), SHA-256 $($sourceHash.Hash)
- Binary files:
$($binaryEntries -join "`n")
- Build configuration: `Gyan-README.txt` and `ffmpeg-buildconf.txt`
- GPL text: `FFmpeg-GPLv3.txt`

下载地址按 Gyan README 发布的 commit 前缀固定到 FFmpeg 核心源码；源压缩包内顶层目录和 GitHub commit 页面可用于复核该前缀。该压缩包只包含 FFmpeg 核心仓库源代码。Gyan README 列出的静态外部依赖的精确源码及构建脚本/补丁未由此脚本自动收集。此目录不能单独视为完整公开发行对应源代码材料。
"@
$manifest | Set-Content -LiteralPath (Join-Path $destinationPath 'SOURCE-MANIFEST.md') -Encoding utf8
Write-Output $destinationPath
