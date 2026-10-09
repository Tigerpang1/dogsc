[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$IncludeCaptureProbe
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$coreTests = Join-Path $repoRoot 'tests/FrameTrail.Core.Tests/FrameTrail.Core.Tests.csproj'
$captureProbe = Join-Path $repoRoot 'tests/FrameTrail.Capture.Probe/FrameTrail.Capture.Probe.csproj'

if (-not (Test-Path -LiteralPath $coreTests -PathType Leaf)) {
    throw "测试项目不存在：$coreTests"
}

Push-Location $repoRoot
try {
    & dotnet run --project $coreTests --configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Core 测试程序失败，退出码 $LASTEXITCODE" }

    if ($IncludeCaptureProbe) {
        if (-not (Test-Path -LiteralPath $captureProbe -PathType Leaf)) {
            throw "采集探针项目不存在：$captureProbe"
        }
        & dotnet build $captureProbe --configuration $Configuration -p:Platform=x64
        if ($LASTEXITCODE -ne 0) { throw "采集探针构建失败，退出码 $LASTEXITCODE" }
        Write-Host '采集探针仅完成构建；脚本不会启动录制、访问设备或请求权限。'
    }
}
finally {
    Pop-Location
}
