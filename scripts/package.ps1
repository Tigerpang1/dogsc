[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.2.3',
    [ValidatePattern('^[A-Za-z0-9_.-]+\.exe$')]
    [string]$ApplicationExeName = 'FrameTrail.exe',
    [switch]$WithoutFfmpeg,
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src/FrameTrail.App/FrameTrail.App.csproj'
$licenseRoot = Join-Path $repoRoot 'licenses'
$releaseRoot = Join-Path $repoRoot 'artifacts/release'
$publishRoot = Join-Path $releaseRoot "FrameTrail-$Version-win-x64"
$portableRoot = Join-Path $publishRoot 'app'
$publishStaging = Join-Path $env:TEMP "FrameTrail-publish-$Version-$PID-$([guid]::NewGuid().ToString('N'))"
$licenseOutput = Join-Path $publishRoot 'licenses'
$ffmpegOutput = Join-Path $portableRoot 'tools/ffmpeg'
$zipPath = Join-Path $releaseRoot "FrameTrail-$Version-win-x64.zip"
$installerPath = Join-Path $releaseRoot "FrameTrail-Setup-$Version-win-x64.exe"
$upstreamBaseSha = 'e207d11b111c5e58152238fb64115b2009850547'
. (Join-Path $PSScriptRoot 'source-provenance.ps1')
$null = Get-FrameTrailSourceProvenance -RepositoryRoot $repoRoot -UpstreamBaseSha $upstreamBaseSha

if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw "应用项目尚未集成：$project"
}
if (-not (Test-Path -LiteralPath $licenseRoot -PathType Container)) { throw "许可证目录缺失：$licenseRoot" }

$null = New-Item -ItemType Directory -Path $releaseRoot -Force
$releaseBoundary = [System.IO.Path]::GetFullPath($releaseRoot).TrimEnd('\') + '\'
function Remove-GeneratedReleaseOutput([string]$Path) {
    $absolutePath = [System.IO.Path]::GetFullPath($Path)
    if (-not $absolutePath.StartsWith($releaseBoundary, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝删除发布目录以外的路径：$absolutePath"
    }
    if (Test-Path -LiteralPath $absolutePath) {
        $item = Get-Item -LiteralPath $absolutePath -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "拒绝删除链接或重解析点：$absolutePath"
        }
        Remove-Item -LiteralPath $absolutePath -Recurse -Force
    }
}
Remove-GeneratedReleaseOutput $publishRoot
Remove-GeneratedReleaseOutput $zipPath
Remove-GeneratedReleaseOutput $installerPath
Remove-GeneratedReleaseOutput (Join-Path $releaseRoot 'ffmpeg-corresponding-source')
$null = New-Item -ItemType Directory -Path $portableRoot -Force
if ($publishStaging.Contains("'")) { throw "系统临时目录含有撇号，无法规避 .NET SDK 发布路径限制：$publishStaging" }

try {
    $null = New-Item -ItemType Directory -Path $publishStaging -Force
    Push-Location $repoRoot
    try {
        & dotnet publish $project --configuration $Configuration --runtime win-x64 --self-contained true `
            -p:Platform=x64 -p:PublishSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=false `
            -p:Version=$Version -p:AssemblyVersion=$Version -p:FileVersion=$Version `
            --output $publishStaging
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出码 $LASTEXITCODE" }
    }
    finally {
        Pop-Location
    }
    Copy-Item -Path (Join-Path $publishStaging '*') -Destination $portableRoot -Recurse -Force
}
finally {
    if (Test-Path -LiteralPath $publishStaging -PathType Container) {
        $stagingAbsolute = [System.IO.Path]::GetFullPath($publishStaging)
        $tempAbsolute = [System.IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
        if (-not $stagingAbsolute.StartsWith($tempAbsolute, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "拒绝清理系统临时目录之外的发布暂存：$stagingAbsolute"
        }
        Remove-Item -LiteralPath $stagingAbsolute -Recurse -Force
    }
}

$applicationExe = Join-Path $portableRoot $ApplicationExeName
if (-not (Test-Path -LiteralPath $applicationExe -PathType Leaf)) {
    throw "发布目录中未找到应用主程序 $ApplicationExeName。检查 App 项目的 AssemblyName 后再运行 package.ps1。"
}
Copy-Item -LiteralPath $licenseRoot -Destination $licenseOutput -Recurse -Force
foreach ($name in @('README.md', 'LICENSE')) {
    $sourceFile = Join-Path $repoRoot $name
    if (-not (Test-Path -LiteralPath $sourceFile -PathType Leaf)) { throw "发行材料缺失：$sourceFile" }
    Copy-Item -LiteralPath $sourceFile -Destination $publishRoot -Force
}
$docsSource = Join-Path $repoRoot 'docs'
if (-not (Test-Path -LiteralPath $docsSource -PathType Container)) { throw "文档目录缺失：$docsSource" }
Copy-Item -LiteralPath $docsSource -Destination $publishRoot -Recurse -Force

$sampleStaging = Join-Path $env:TEMP ("FrameTrail-sample-$Version-$PID-" + [guid]::NewGuid().ToString('N'))
$sampleNames = @('FrameTrail-Demo.frametrail', 'FrameTrail-Demo.mp4', 'synthetic-screen.mp4', 'synthetic-camera.mp4', 'independent-tone.wav', 'demo-badge.png', 'preview.png', 'README.md')
try {
    if (Test-Path -LiteralPath $sampleStaging) { throw "样例暂存目录已存在：$sampleStaging" }
    & (Join-Path $PSScriptRoot 'create-sample.ps1') -OutputDirectory $sampleStaging
    if ($LASTEXITCODE -ne 0) { throw '原创样例生成失败。' }
    $sampleDestination = Join-Path $publishRoot 'samples/FrameTrail-Demo'
    $null = New-Item -ItemType Directory -Path $sampleDestination -Force
    foreach ($name in $sampleNames) {
        $sampleFile = Join-Path $sampleStaging $name
        if (-not (Test-Path -LiteralPath $sampleFile -PathType Leaf)) { throw "生成的演示缺少文件：$name" }
        Copy-Item -LiteralPath $sampleFile -Destination $sampleDestination
    }
}
finally {
    $sampleAbsolute = [IO.Path]::GetFullPath($sampleStaging)
    $tempBoundary = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
    if (-not $sampleAbsolute.StartsWith($tempBoundary, [StringComparison]::OrdinalIgnoreCase)) { throw "拒绝清理TEMP之外的样例暂存：$sampleAbsolute" }
    if (Test-Path -LiteralPath $sampleAbsolute) {
        if (((Get-Item -LiteralPath $sampleAbsolute -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw '拒绝清理样例重解析点。' }
        Remove-Item -LiteralPath $sampleAbsolute -Recurse -Force
    }
}

$sourceProvenance = Get-FrameTrailSourceProvenance -RepositoryRoot $repoRoot -UpstreamBaseSha $upstreamBaseSha
$gitHead = $sourceProvenance.Head
$sourceKind = $sourceProvenance.Kind
$workingTreeState = $sourceProvenance.WorktreeState

$ffmpegSourceMaterial = Join-Path $releaseRoot 'ffmpeg-corresponding-source'
if (-not $WithoutFfmpeg) {
    $ffmpegCandidates = Get-ChildItem -LiteralPath (Join-Path $env:LOCALAPPDATA 'Microsoft/WinGet/Packages') `
        -Directory -Filter 'Gyan.FFmpeg_*' -ErrorAction SilentlyContinue |
        ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Directory -Filter 'ffmpeg-8.1.1-full_build' -ErrorAction SilentlyContinue } |
        Select-Object -First 1
    if (-not $ffmpegCandidates) { throw '本机找不到 Gyan FFmpeg 8.1.1 full build；安装或使用 -WithoutFfmpeg。' }
    $ffmpegDirectory = $ffmpegCandidates.FullName
    $null = New-Item -ItemType Directory -Path $ffmpegOutput -Force
    Copy-Item -LiteralPath (Join-Path $ffmpegDirectory 'bin/ffmpeg.exe') -Destination $ffmpegOutput -Force
    Copy-Item -LiteralPath (Join-Path $ffmpegDirectory 'bin/ffprobe.exe') -Destination $ffmpegOutput -Force
    if (Test-Path -LiteralPath (Join-Path $ffmpegDirectory 'presets') -PathType Container) {
        Copy-Item -LiteralPath (Join-Path $ffmpegDirectory 'presets') -Destination $ffmpegOutput -Recurse -Force
    }
    & (Join-Path $PSScriptRoot 'fetch-ffmpeg-corresponding-source.ps1') `
        -FfmpegDirectory $ffmpegDirectory -Destination $ffmpegSourceMaterial | Out-Null
    $ffmpegSourceArchive = Get-ChildItem -LiteralPath $ffmpegSourceMaterial -Filter '*-source.zip' -File | Select-Object -First 1
    if (-not $ffmpegSourceArchive) { throw '未生成 FFmpeg 对应源代码压缩包。' }
    $ffmpegAuditDestination = Join-Path $licenseOutput 'ffmpeg-corresponding-source'
    Copy-Item -LiteralPath $ffmpegSourceMaterial -Destination $ffmpegAuditDestination -Recurse -Force
    $ffmpegVersionPath = Join-Path $ffmpegSourceMaterial 'ffmpeg-version.txt'
    Get-Content -LiteralPath $ffmpegVersionPath -Raw |
        Set-Content -LiteralPath (Join-Path $portableRoot 'ffmpeg-version.txt') -Encoding utf8
}
else {
    if (Test-Path -LiteralPath $ffmpegOutput) { Remove-Item -LiteralPath $ffmpegOutput -Recurse -Force }
}

$vcRuntimeNotice = @'
FrameTrail 录制功能通过 ScreenRecorderLib 依赖 Microsoft Visual C++ 2015-2022 Redistributable (x64)。
本安装程序不会安装或更新系统运行库。如果录制无法启动，请从微软官方页面手动安装 x64 版本：
https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist
'@
$vcRuntimeNotice | Set-Content -LiteralPath (Join-Path $publishRoot 'VC-RUNTIME-REQUIREMENT.txt') -Encoding utf8

$buildInfo = @"
产品：FrameTrail
版本：$Version
目标：win-x64 自包含便携发布目录
配置：$Configuration
打包用途：本地验证
源码类型：$sourceKind
包含 FFmpeg：$(-not $WithoutFfmpeg)
FrameTrail 源码提交：$gitHead
DogSC 上游审计基线：$upstreamBaseSha
打包时工作区：$workingTreeState
"@
$buildInfo | Set-Content -LiteralPath (Join-Path $publishRoot 'BUILD-INFO.txt') -Encoding utf8

Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal

if (-not $SkipInstaller) {
    $compiler = & (Join-Path $PSScriptRoot 'ensure-inno-setup.ps1')
    if ($LASTEXITCODE -ne 0 -or -not $compiler) { throw '无法取得 Inno Setup 编译器。' }
    $iss = Join-Path $repoRoot 'installer/FrameTrail.iss'
    if (-not (Test-Path -LiteralPath $iss -PathType Leaf)) { throw "安装器脚本不存在：$iss" }
    & $compiler $iss "/DAppVersion=$Version" "/DAppExeName=$ApplicationExeName" "/DReleaseDir=$publishRoot" `
        "/O$releaseRoot" "/FFrameTrail-Setup-$Version-win-x64"
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
        throw 'Inno Setup 安装包编译失败。'
    }
}

Write-Host "便携目录：$publishRoot"
Write-Host "ZIP：$zipPath"
if (Test-Path -LiteralPath $installerPath) { Write-Host "安装包：$installerPath" }
Write-Warning '此包包含用于本地验证的 Gyan FFmpeg GPLv3 构建，不得作为公开发行包；公开发行需完成完整对应源代码与许可证审核。'
