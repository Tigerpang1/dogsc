[CmdletBinding()]
param(
    [string]$MSBuildPath,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw '请使用 PowerShell 7 或更高版本运行此脚本。' }

$repoRoot = Split-Path -Parent $PSScriptRoot
$dependencyRoot = Join-Path $repoRoot 'third_party/ScreenRecorderLib'
$archivePath = Join-Path $dependencyRoot 'ScreenRecorderLib-v7.0.1-source.zip'
$patchPath = Join-Path $dependencyRoot 'first-frame-wgc.patch'
$licensePath = Join-Path $dependencyRoot 'LICENSE'
$expected = [ordered]@{
    ArchiveSha256 = '31435BB4F5D159D5D943AE5E31BD4DE6A9C5E8476AFA9691E7DC8E51A5FA27E0'
    PatchSha256 = '4FB260DCADD8FDE129A06D4380AE19ECA7A3EA985FEDE4D19E430DAA7C2C7C3F'
    LicenseSha256 = '9AAD4239954F3F4B1BAE22011CA72E00FE5AD9218068CD44375A1A5306F7CF32'
    OriginalSourceSha256 = '17AECF2A85D49BAF5848AC97FD315A5FE311B6F8A8ADF64ED63FDE10EEB0BA1A'
    PatchedSourceNormalizedSha256 = '643F7AEE810E5C57467DF3620808F72576EBDB05DB466D0F77F9218E3669D61D'
}
$requiredToolset = '14.44.35207'
$requiredSdk = '10.0.26100.0'

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Get-NormalizedSourceSha256([string]$Path) {
    $text = [IO.File]::ReadAllText($Path)
    $text = $text.Replace("`r`n", "`n")
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($text)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return [Convert]::ToHexString($sha.ComputeHash($bytes))
    }
    finally {
        $sha.Dispose()
    }
}

function Resolve-MSBuild([string]$RequestedPath) {
    if ($RequestedPath) {
        $resolved = (Resolve-Path -LiteralPath $RequestedPath).Path
        if ((Split-Path -Leaf $resolved) -ine 'MSBuild.exe') { throw 'MSBuildPath must point to MSBuild.exe.' }
        return $resolved
    }

    $candidates = @(
        (Join-Path ${env:ProgramFiles} 'Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/MSBuild.exe'),
        (Join-Path ${env:ProgramFiles} 'Microsoft Visual Studio/2022/Professional/MSBuild/Current/Bin/MSBuild.exe'),
        (Join-Path ${env:ProgramFiles} 'Microsoft Visual Studio/2022/Enterprise/MSBuild/Current/Bin/MSBuild.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/MSBuild.exe')
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return (Resolve-Path -LiteralPath $candidate).Path }
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (Test-Path -LiteralPath $vswhere -PathType Leaf) {
        $installations = & $vswhere -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        foreach ($installation in $installations) {
            $candidate = Join-Path $installation 'MSBuild/Current/Bin/MSBuild.exe'
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { return (Resolve-Path -LiteralPath $candidate).Path }
        }
    }
    throw '未找到 Visual Studio 2022 MSBuild。请安装 C++ x64/x86 工具，或通过 -MSBuildPath 指定 MSBuild.exe。'
}

function Assert-Hash([string]$Path, [string]$ExpectedHash, [string]$Label) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw "缺少 $Label：$Path" }
    $actual = Get-Sha256 $Path
    if ($actual -ne $ExpectedHash) { throw "$Label SHA-256 不匹配。期望 $ExpectedHash，实际 $actual。" }
}

if (!(Test-Path -LiteralPath $archivePath -PathType Leaf) -or !(Test-Path -LiteralPath $patchPath -PathType Leaf) -or !(Test-Path -LiteralPath $licensePath -PathType Leaf)) {
    throw 'ScreenRecorderLib 源码 ZIP、补丁或许可证文件缺失。'
}
Assert-Hash $archivePath $expected.ArchiveSha256 '上游源码 ZIP'
Assert-Hash $patchPath $expected.PatchSha256 'WGC 补丁'
Assert-Hash $licensePath $expected.LicenseSha256 '上游 MIT 许可证'

$git = Get-Command git -ErrorAction SilentlyContinue
if (!$git) { throw '需要本机 Git 来应用已固定的 unified diff；无需网络访问。' }
$msbuild = Resolve-MSBuild $MSBuildPath
$vsRoot = (Resolve-Path (Join-Path (Split-Path -Parent $msbuild) '../../..')).Path
$atlHeader = Get-ChildItem -LiteralPath (Join-Path $vsRoot 'VC/Tools/MSVC') -Directory -ErrorAction SilentlyContinue |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'atlmfc/include/atlbase.h') } |
    Where-Object { $_.Name -eq $requiredToolset } |
    Select-Object -First 1
if (!$atlHeader) { throw "需要 Visual Studio 2022 v143/MSVC $requiredToolset，并安装 ATL 头文件。当前实例：$vsRoot" }

$frameworkPack = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8'
if (!(Test-Path -LiteralPath $frameworkPack -PathType Container)) { throw '缺少 .NET Framework 4.8 Developer Pack。' }
$sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/Include'
$sdkHeader = Join-Path $sdkRoot "$requiredSdk/um/windows.h"
$sdkCaptureHeader = Join-Path $sdkRoot "$requiredSdk/winrt/Windows.Graphics.Capture.h"
if (!(Test-Path -LiteralPath $sdkHeader -PathType Leaf) -or !(Test-Path -LiteralPath $sdkCaptureHeader -PathType Leaf)) {
    throw "缺少 Windows SDK $requiredSdk（包括 WinRT Graphics Capture headers）。"
}

if (!$OutputDirectory) {
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssZ')
    $OutputDirectory = Join-Path $repoRoot "artifacts/validation/screenrecorderlib-rebuild-$stamp-$([guid]::NewGuid().ToString('N').Substring(0,8))"
}
$outputFull = [IO.Path]::GetFullPath($OutputDirectory)
$validationRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts/validation'))
$validationPrefix = $validationRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (!$outputFull.StartsWith($validationPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw '输出目录必须位于仓库 artifacts/validation/ 下。'
}
if (Test-Path -LiteralPath $outputFull) { throw "输出目录已存在；不会覆盖：$outputFull" }
New-Item -ItemType Directory -Path $outputFull | Out-Null

$workRoot = Join-Path $env:TEMP ('frametrail-srl-7.0.1-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workRoot | Out-Null
$sourceRoot = Join-Path $workRoot 'ScreenRecorderLib-7.0.1'
$buildLogPath = Join-Path $outputFull 'build.log'
$reportPath = Join-Path $outputFull 'rebuild-result.json'
$report = [ordered]@{
    schemaVersion = 1
    component = 'ScreenRecorderLib'
    version = '7.0.1'
    sourceTag = 'v7.0.1'
    sourceCommit = '646383234187efd3854e6b1069612b80c3ab9981'
    archiveSha256 = Get-Sha256 $archivePath
    patchSha256 = Get-Sha256 $patchPath
    licenseSha256 = Get-Sha256 $licensePath
    toolchain = [ordered]@{
        msbuildPath = $msbuild
        msbuildVersion = $null
        visualStudioInstance = $vsRoot
        msvcToolset = $requiredToolset
        windowsSdk = $requiredSdk
        target = 'Release x64; managed C++/CLI wrapper .NET Framework 4.8'
    }
    outputDirectory = $outputFull
    temporarySourceDirectory = $workRoot
    buildSourceDirectory = $null
    buildExitCode = $null
    outputDll = $null
    outputDllSha256 = $null
    result = 'started'
}

try {
    $versionText = & $msbuild -version -nologo 2>&1
    if ($LASTEXITCODE -ne 0) { throw '无法读取 MSBuild 版本。' }
    $report.toolchain.msbuildVersion = ($versionText | Select-Object -Last 1).ToString().Trim()

    Expand-Archive -LiteralPath $archivePath -DestinationPath $workRoot
    if (!(Test-Path -LiteralPath $sourceRoot -PathType Container)) { throw '源码 ZIP 根目录与预期不符。' }
    $upstreamFile = Join-Path $sourceRoot 'ScreenRecorderLibNative/WindowsGraphicsCapture.cpp'
    Assert-Hash $upstreamFile $expected.OriginalSourceSha256 '上游 WGC 源文件'

    & $git.Source -C $sourceRoot apply --check $patchPath
    if ($LASTEXITCODE -ne 0) { throw 'WGC 补丁未能通过 git apply --check。' }
    & $git.Source -C $sourceRoot apply $patchPath
    if ($LASTEXITCODE -ne 0) { throw 'WGC 补丁应用失败。' }
    $patchedHash = Get-NormalizedSourceSha256 $upstreamFile
    if ($patchedHash -ne $expected.PatchedSourceNormalizedSha256) { throw "补丁后规范化源文件哈希不匹配：$patchedHash" }

    $buildSourceParent = Join-Path $outputFull 'source'
    New-Item -ItemType Directory -Path $buildSourceParent | Out-Null
    Copy-Item -LiteralPath $sourceRoot -Destination $buildSourceParent -Recurse
    $sourceRoot = Join-Path $buildSourceParent 'ScreenRecorderLib-7.0.1'
    $report.buildSourceDirectory = $sourceRoot

    $project = Join-Path $sourceRoot 'ScreenRecorderLib/ScreenRecorderLib.vcxproj'
    if (!(Test-Path -LiteralPath $project -PathType Leaf)) { throw '缺少 ScreenRecorderLib C++/CLI 项目文件。' }
    $buildArgs = @(
        $project,
        '/m:1',
        '/t:Build',
        '/p:Configuration=Release',
        '/p:Platform=x64',
        "/p:VCToolsVersion=$requiredToolset",
        "/p:WindowsTargetPlatformVersion=$requiredSdk",
        '/v:minimal',
        '/nologo'
    )
    $buildOutput = & $msbuild @buildArgs 2>&1
    $buildExit = $LASTEXITCODE
    $report.buildExitCode = $buildExit
    $buildOutput | Out-File -LiteralPath $buildLogPath -Encoding utf8
    if ($buildExit -ne 0) { throw "MSBuild 失败，退出码 $buildExit。日志：$buildLogPath；源码保留在：$workRoot" }

    $matches = @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -Filter 'ScreenRecorderLib.dll' |
        Where-Object { $_.FullName -match '[\\/]bin[\\/]x64[\\/]Release[\\/]ScreenRecorderLib\.dll$' })
    if ($matches.Count -ne 1) { throw "预期找到一个 Release x64 ScreenRecorderLib.dll，实际找到 $($matches.Count) 个。" }
    $builtDll = $matches[0].FullName
    $destinationDll = Join-Path $outputFull 'ScreenRecorderLib.dll'
    Copy-Item -LiteralPath $builtDll -Destination $destinationDll
    $report.outputDll = $destinationDll
    $report.outputDllSha256 = Get-Sha256 $destinationDll
    $report.result = 'success'
    $report.note = '源码和补丁固定；PE 哈希记录为本次实际构建值，不保证跨工具链或环境逐字节一致。完整构建源码保留在输出目录 source/ 下。'
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8
    Write-Output "ScreenRecorderLib 7.0.1 构建完成。DLL SHA-256：$($report.outputDllSha256)"
    Write-Output "结果：$reportPath"
    Write-Output "MSBuild 日志：$buildLogPath"
}
catch {
    $report.result = 'failed'
    $report.error = $_.Exception.Message
    $report.buildLog = if (Test-Path -LiteralPath $buildLogPath) { $buildLogPath } else { $null }
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8
    throw
}
