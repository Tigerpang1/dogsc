[CmdletBinding()]
param([ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.2.2', [switch]$SkipInstallation, [switch]$InteractiveDesktop, [switch]$ContentOnly)

$ErrorActionPreference = 'Stop'
if (-not $ContentOnly -and -not $InteractiveDesktop) {
    throw '包运行验收会打开窗口、播放声音并操作真实桌面。请安排主机交互时段后显式传入 -InteractiveDesktop；本次未启动应用或安装程序。'
}
$repo = Split-Path -Parent $PSScriptRoot
$release = Join-Path $repo "artifacts/release/FrameTrail-$Version-win-x64"
$validation = Join-Path $repo "artifacts/validation/package-$Version"
$null = New-Item -ItemType Directory -Path $validation -Force
$report = [ordered]@{ Version=$Version; Scope=$(if ($ContentOnly) { 'content-only' } elseif ($SkipInstallation) { 'portable-only' } else { 'interactive-package' }); StartedUtc=[DateTime]::UtcNow.ToString('O'); Passed=$false; Checks=@(); Limitations=@('本机 Windows 11 x64；不代替没有运行库的干净虚拟机。') }
if ($ContentOnly) { $report.Limitations += '仅后台检查包内容与哈希，未执行播放、录制或安装卸载，不替代交互运行验收。' }
else { $report.Limitations += '应用运行验证隔离 PATH 与 DOTNET_ROOT，使用当前真实桌面。' }
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
function Assert-PortableArchive([string]$ArchivePath, [string]$DirectoryPath) {
    # Inspect the archive itself so a stale ZIP cannot inherit a newer directory's result.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $directoryRoot = [IO.Path]::GetFullPath($DirectoryPath).TrimEnd('\') + '\'
    $expected = @{}
    foreach ($file in Get-ChildItem -LiteralPath $DirectoryPath -Recurse -File) {
        $relative = $file.FullName.Substring($directoryRoot.Length).Replace('\', '/')
        $expected.Add($relative, $file)
    }
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $seen = @{}
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName.EndsWith('/')) { continue }
            $name = $entry.FullName.Replace('\', '/')
            if ($seen.ContainsKey($name) -or -not $expected.ContainsKey($name)) { throw "ZIP包含重复或非预期文件：$name" }
            $seen.Add($name, $true)
            $file = $expected[$name]
            if ($entry.Length -ne $file.Length) { throw "ZIP文件长度与发布目录不一致：$name" }
            $stream = $entry.Open()
            $algorithm = [Security.Cryptography.SHA256]::Create()
            try { $entryHash = [Convert]::ToHexString($algorithm.ComputeHash($stream)) }
            finally { $algorithm.Dispose(); $stream.Dispose() }
            if ($entryHash -ne (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) { throw "ZIP文件内容与发布目录不一致：$name" }
        }
        if ($seen.Count -ne $expected.Count) { throw 'ZIP缺少发布目录中的文件。' }
        return $seen.Count
    } finally { $archive.Dispose() }
}
try {
    $exe = Join-Path $release 'app/FrameTrail.exe'
    $sample = Join-Path $release 'samples/FrameTrail-Demo/FrameTrail-Demo.frametrail'
    foreach ($path in @($exe, $sample, (Join-Path $release 'app/coreclr.dll'), (Join-Path $release 'app/hostfxr.dll'), (Join-Path $release 'app/hostpolicy.dll'), (Join-Path $release 'app/FrameTrail.runtimeconfig.json'), (Join-Path $release 'app/tools/ffmpeg/ffmpeg.exe'), (Join-Path $release 'app/tools/ffmpeg/ffprobe.exe'), (Join-Path $release 'LICENSE'), (Join-Path $release 'licenses/DogSC-MIT.txt'), (Join-Path $release 'licenses/DogSCWindows-MIT.txt'))) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "发行包缺少必要文件：$path" }
    }
    $report.PortableArchiveFilesVerified = Assert-PortableArchive (Join-Path $repo "artifacts/release/FrameTrail-$Version-win-x64.zip") $release
    $report.Checks += [ordered]@{ Name='主程序、自包含运行库、原创样例与许可证齐备，ZIP逐文件内容一致'; Passed=$true }
    if (-not $ContentOnly) {
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
    }
    $report.Artifacts = @(foreach ($artifactName in @("FrameTrail-$Version-win-x64.zip", "FrameTrail-Setup-$Version-win-x64.exe")) {
        $artifact = Get-Item -LiteralPath (Join-Path $repo "artifacts/release/$artifactName")
        [ordered]@{ Name=$artifact.Name; Bytes=$artifact.Length; SHA256=(Get-FileHash -LiteralPath $artifact.FullName -Algorithm SHA256).Hash }
    })
    $report.BuildInfo = Get-Content -LiteralPath (Join-Path $release 'BUILD-INFO.txt') -Raw
    $report.Passed = $true
} catch { $report.Error = $_.Exception.ToString(); throw }
finally {
    $report.CompletedUtc = [DateTime]::UtcNow.ToString('O')
    $resultName = if ($ContentOnly) { 'content-result.json' } else { 'result.json' }
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $validation $resultName) -Encoding utf8
}
