[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$IncludeCaptureProbe,
    [switch]$IncludeMediaProbe,
    [switch]$IncludeVisualProbe,
    [switch]$IncludeReleaseProbe,
    [switch]$IncludeIntegration
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
    & dotnet run --project (Join-Path $repoRoot 'tests/FrameTrail.DogScImport.Tests/FrameTrail.DogScImport.Tests.csproj') --configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw "DogSC 只读导入测试失败，退出码 $LASTEXITCODE" }
    if ($IncludeMediaProbe) {
        & dotnet run --project (Join-Path $repoRoot 'tests/FrameTrail.Render.Probe/FrameTrail.Render.Probe.csproj') --configuration $Configuration
        if ($LASTEXITCODE -ne 0) { throw "共享GPU/媒体测试失败，退出码 $LASTEXITCODE" }
        & dotnet run --no-build --project (Join-Path $repoRoot 'tests/FrameTrail.Render.Probe/FrameTrail.Render.Probe.csproj') --configuration $Configuration -- --timeline-assets
        if ($LASTEXITCODE -ne 0) { throw "时间线缩略图与波形测试失败，退出码 $LASTEXITCODE" }
        & dotnet run --no-build --project (Join-Path $repoRoot 'tests/FrameTrail.Render.Probe/FrameTrail.Render.Probe.csproj') --configuration $Configuration -- --dogsc-camera-import
        if ($LASTEXITCODE -ne 0) { throw "DogSC 摄像头素材比例与保存失败回归失败，退出码 $LASTEXITCODE" }
        & dotnet run --project (Join-Path $repoRoot 'tests/FrameTrail.AudioOverrides.Probe/FrameTrail.AudioOverrides.Probe.csproj') --configuration $Configuration -- (Join-Path $repoRoot ('artifacts/validation/audio-overrides-' + (Get-Date -Format 'yyyyMMdd-HHmmss')))
        if ($LASTEXITCODE -ne 0) { throw "逐片段音量与辅助媒体时钟测试失败，退出码 $LASTEXITCODE" }
        & dotnet run --no-build --project (Join-Path $repoRoot 'tests/FrameTrail.AudioOverrides.Probe/FrameTrail.AudioOverrides.Probe.csproj') --configuration $Configuration -- --source-rate-probe (Join-Path $repoRoot ('artifacts/validation/source-rate-' + [guid]::NewGuid().ToString('N')))
        if ($LASTEXITCODE -ne 0) { throw "辅助音频短时/长时时钟滤镜回归失败，退出码 $LASTEXITCODE" }
    }
    if ($IncludeIntegration) {
        & dotnet run --project (Join-Path $repoRoot 'tests/FrameTrail.Integration/FrameTrail.Integration.csproj') --configuration $Configuration
        if ($LASTEXITCODE -ne 0) { throw "合成媒体集成测试失败，退出码 $LASTEXITCODE" }
        & dotnet run --no-build --project (Join-Path $repoRoot 'tests/FrameTrail.Integration/FrameTrail.Integration.csproj') --configuration $Configuration -- --sync
        if ($LASTEXITCODE -ne 0) { throw "1080p60 音画同步集成测试失败，退出码 $LASTEXITCODE" }
    }

    if ($IncludeVisualProbe) {
        $visualOutput = Join-Path $repoRoot ('artifacts/validation/theme-offscreen-' + [guid]::NewGuid().ToString('N'))
        & dotnet run --project (Join-Path $repoRoot 'tests/FrameTrail.App.VisualProbe/FrameTrail.App.VisualProbe.csproj') --configuration $Configuration -- $visualOutput
        if ($LASTEXITCODE -ne 0) { throw "离屏主题文字与时间线像素检查失败，退出码 $LASTEXITCODE" }
        Write-Host 'WPF离屏检查完成，未创建桌面窗口或访问采集设备；真实交互仍须单独验收。'
    }

    if ($IncludeCaptureProbe) {
        if (-not (Test-Path -LiteralPath $captureProbe -PathType Leaf)) {
            throw "采集探针项目不存在：$captureProbe"
        }
        & dotnet build $captureProbe --configuration $Configuration -p:Platform=x64
        if ($LASTEXITCODE -ne 0) { throw "采集探针构建失败，退出码 $LASTEXITCODE" }
        foreach ($pureProbe in @('--audio-packet-clock-test', '--audio-qpc-jitter-diagnosis-test', '--audio-packet-diagnostics-test', '--track-dispose-race-test', '--failure-lifecycle-probe', '--playback-options-test', '--playback-session-volume-test', '--microphone-clock-segment-test')) {
            & dotnet run --no-build --project $captureProbe --configuration $Configuration -p:Platform=x64 -- $pureProbe
            if ($LASTEXITCODE -ne 0) { throw "采集纯逻辑探针 $pureProbe 失败，退出码 $LASTEXITCODE" }
        }
        Write-Host '采集探针构建和纯逻辑检查通过；脚本不会启动录制、访问设备或请求权限。'
    }
    if ($IncludeReleaseProbe) {
        & (Join-Path $PSScriptRoot 'test-release-manifest.ps1')
        & (Join-Path $PSScriptRoot 'test-source-provenance.ps1')
        & (Join-Path $PSScriptRoot 'test-source-zip-policy.ps1')
        & (Join-Path $PSScriptRoot 'test-utf8-script.ps1')
        if ($LASTEXITCODE -ne 0) { throw "UTF-8 子脚本回归失败，退出码 $LASTEXITCODE" }
        Write-Host '发行清单、源码来源与源码 ZIP 结构规则检查完成；使用隔离合成数据，未运行安装器或应用。'
    }
}
finally {
    Pop-Location
}
