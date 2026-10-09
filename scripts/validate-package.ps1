[CmdletBinding()]
param([ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.2.1', [switch]$SkipInstallation)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$release = Join-Path $repo "artifacts/release/FrameTrail-$Version-win-x64"
$validation = Join-Path $repo "artifacts/validation/package-$Version"
$null = New-Item -ItemType Directory -Path $validation -Force
$report = [ordered]@{ Version=$Version; StartedUtc=[DateTime]::UtcNow.ToString('O'); Passed=$false; Checks=@(); Limitations=@('本机 Windows 11 x64，隔离 PATH 与 DOTNET_ROOT；不代替没有运行库的干净虚拟机。') }
function Run-OwnedProcess([string]$Exe, [string[]]$Arguments, [bool]$Isolate = $false, [int]$TimeoutSeconds = 180) {
    $start = [Diagnostics.ProcessStartInfo]::new($Exe)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WorkingDirectory = Split-Path -Parent $Exe
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    if ($Isolate) {
        $start.Environment['PATH'] = "$env:WINDIR\System32;$env:WINDIR"
        $start.Environment['DOTNET_ROOT'] = Join-Path $validation 'absent-dotnet'
        $start.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
        $start.Environment.Remove('FRAMETRAIL_FFMPEG_DIR') | Out-Null
        $start.Environment.Remove('FRAMETRAIL_FFPROBE') | Out-Null
    }
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            throw "验证进程超时，已停止本次启动的进程树：$Exe"
        }
        if ($process.ExitCode -ne 0) { throw "验证进程失败 ($($process.ExitCode))：$Exe" }
    } finally { $process.Dispose() }
}
function Check-Report([string]$Path, [string]$Name) {
    $data = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if (-not $data.Passed) { throw "$Name 未通过：$($data.Error)" }
    $report.Checks += [ordered]@{ Name=$Name; Passed=$true; Report=$Path }
}
try {
    $exe = Join-Path $release 'app/FrameTrail.exe'
    $sample = Join-Path $release 'samples/FrameTrail-Demo/FrameTrail-Demo.frametrail'
    foreach ($path in @($exe, $sample, (Join-Path $release 'app/tools/ffmpeg/ffmpeg.exe'), (Join-Path $release 'app/tools/ffmpeg/ffprobe.exe'), (Join-Path $release 'LICENSE'), (Join-Path $release 'licenses/DogSC-MIT.txt'), (Join-Path $release 'licenses/DogSCWindows-MIT.txt'))) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "发行包缺少必要文件：$path" }
    }
    $report.Checks += [ordered]@{ Name='主程序、原创样例与许可证齐备'; Passed=$true }
    $playback = Join-Path $validation 'portable-playback.json'
    Run-OwnedProcess $exe @('--open', $sample, '--playback-smoke', $playback) $true
    Check-Report $playback '隔离开发环境后的便携版有声播放与暂停恢复'
    $workflow = Join-Path $validation 'portable-workflow'
    Run-OwnedProcess $exe @('--workflow-smoke', $workflow, '--with-pause', '--with-input-stimulus', '--record-seconds', '6') $true
    Check-Report (Join-Path $workflow 'workflow-result.json') '便携版真实录制、暂停、编辑、重开及导出'
    if (-not $SkipInstallation) {
        $registry = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{9EB9A39E-9EF9-4E70-9D2B-E081ECBC1A68}_is1'
        if (Test-Path -LiteralPath $registry) { throw '存在已安装的 FrameTrail；为保留用户安装，本次跳过自动覆盖。' }
        $install = Join-Path $validation 'installed'
        if (Test-Path -LiteralPath $install) { throw "安装验证目录已存在，请使用新的版本或手动检查：$install" }
        $installer = Join-Path $repo "artifacts/release/FrameTrail-Setup-$Version-win-x64.exe"
        Run-OwnedProcess $installer @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/DIR=$install", "/LOG=$(Join-Path $validation 'install.log')") $false 300
        $registration = Get-ItemProperty -LiteralPath $registry
        $registeredPath = [IO.Path]::GetFullPath($registration.InstallLocation).TrimEnd('\')
        if ($registeredPath -ne [IO.Path]::GetFullPath($install).TrimEnd('\')) { throw "安装注册路径不匹配：$registeredPath" }
        $installedExe = Join-Path $install 'FrameTrail.exe'
        $installedSample = Join-Path $install 'samples/FrameTrail-Demo/FrameTrail-Demo.frametrail'
        $installedReport = Join-Path $validation 'installed-playback.json'
        Run-OwnedProcess $installedExe @('--open', $installedSample, '--playback-smoke', $installedReport) $true
        Check-Report $installedReport 'per-user 安装版样例打开及有声播放'
        $uninstaller = Join-Path $install 'unins000.exe'
        Run-OwnedProcess $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$(Join-Path $validation 'uninstall.log')") $false 300
        if ((Test-Path -LiteralPath $registry) -or (Test-Path -LiteralPath $installedExe)) { throw '卸载后仍残留应用主程序或安装注册项。' }
        $report.Checks += [ordered]@{ Name='卸载清除本次安装程序与 HKCU 注册项'; Passed=$true }
    }
    $report.Artifacts = @(Get-ChildItem -LiteralPath (Join-Path $repo 'artifacts/release') -File | Where-Object { $_.Name -in @("FrameTrail-$Version-win-x64.zip", "FrameTrail-Setup-$Version-win-x64.exe") } | ForEach-Object { [ordered]@{ Name=$_.Name; Bytes=$_.Length; SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
    $report.BuildInfo = Get-Content -LiteralPath (Join-Path $release 'BUILD-INFO.txt') -Raw
    $report.Passed = $true
} catch { $report.Error = $_.Exception.ToString(); throw }
finally {
    $report.CompletedUtc = [DateTime]::UtcNow.ToString('O')
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $validation 'result.json') -Encoding utf8
}
