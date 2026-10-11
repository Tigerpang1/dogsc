[CmdletBinding()]
param([ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.2.17', [switch]$Candidate)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$release = Join-Path $repo 'artifacts/release'
$validation = Join-Path $repo "artifacts/validation/package-$Version"
$packageResultName = if ($Candidate) { 'content-result.json' } else { 'result.json' }
$package = Get-Content -LiteralPath (Join-Path $validation $packageResultName) -Raw | ConvertFrom-Json
$source = Get-Content -LiteralPath (Join-Path $validation 'source-validation.json') -Raw | ConvertFrom-Json
if ($package.Passed -isnot [bool] -or -not $package.Passed -or $source.status -ne 'passed') { throw '发行材料必须先完成对应范围的包验收与源码归档构建验收。' }
if ($package.Version -ne $Version) { throw '包验证版本不匹配。' }
if ($Candidate) {
    if ($package.Scope -ne 'content-only') { throw '候选清单需要对应的后台包内容验证。' }
} elseif ($package.Scope -ne 'interactive-package') {
    throw '完整交付清单需要包含安装卸载的交互包验收；后台内容检查和跳过安装的结果不能代替。'
}
$requiredChecks = @('portable-archive-content')
if (-not $Candidate) {
    $requiredChecks += @('preferences-snapshot', 'portable-playback', 'portable-workflow', 'installed-playback', 'installed-uninstall', 'preferences-restored', 'portable-archive-unchanged')
}
foreach ($id in $requiredChecks) {
    $matches = @($package.Checks | Where-Object Id -eq $id)
    if ($matches.Count -ne 1 -or $matches[0].Passed -isnot [bool] -or -not $matches[0].Passed) {
        throw "包验收缺少、重复或未通过必要检查：$id"
    }
}
if (@($package.Checks | Where-Object { $_.Passed -isnot [bool] -or -not $_.Passed }).Count -gt 0) {
    throw '包验收仍包含失败或无效的检查。'
}
$entries = @()
foreach ($name in @("DogSC-$Version-win-x64.zip", "DogSC-Setup-$Version-win-x64.exe", "DogSC-$Version-source.zip")) {
    $path = Join-Path $release $name
    $file = Get-Item -LiteralPath $path
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($name.EndsWith('-source.zip')) {
        if ($hash -ne $source.sourceZipSha256) { throw "源码包与已验证哈希不一致：$name" }
    } else {
        $verified = @($package.Artifacts | Where-Object Name -eq $name)
        if ($verified.Count -ne 1 -or $hash -ne $verified[0].SHA256.ToLowerInvariant()) { throw "程序包与已验证哈希不一致：$name" }
    }
    $entries += [ordered]@{ file=$name; bytes=$file.Length; sha256=$hash }
}
$head = [string]$source.sourceMetadata.frameTrailCommit
$upstream = [string]$source.sourceMetadata.upstreamDogScCommit
if ($head -notmatch '^[0-9a-f]{40}$' -or $upstream -ne 'e207d11b111c5e58152238fb64115b2009850547') { throw '源码提交或固定上游基线无效。' }
if ($package.BuildInfo -notmatch "(?m)^版本：$([regex]::Escape($Version))\r?$" -or
    $package.BuildInfo -notmatch "(?m)^FrameTrail 源码提交：$head\r?$" -or
    $package.BuildInfo -notmatch "(?m)^DogSC 上游审计基线：$upstream\r?$" -or
    $package.BuildInfo -notmatch '(?m)^打包时工作区：clean\r?$') { throw '程序包版本、提交、上游基线或干净工作区标记不匹配。' }
$manifest = [ordered]@{
    productName='DogSC Windows 社区移植'; upstreamRepository='https://github.com/laogou717/dogsc'
    version=$Version; generatedUtc=[DateTime]::UtcNow.ToString('O'); purpose='本地交付与验收，未公开发布'
    status=$(if ($Candidate) { 'candidate-awaiting-interactive-validation' } else { 'validated-local-delivery' })
    frameTrailCommit=$head; upstreamDogScCommit=$source.sourceMetadata.upstreamDogScCommit
    packageValidationScope=$package.Scope; packageValidatedUtc=$package.CompletedUtc; sourceValidatedUtc=$source.completedUtc
    packageValidationCheckIds=@($package.Checks | ForEach-Object Id)
    artifacts=$entries
    limitations=@('本机Windows 11 x64验证，不等同于干净虚拟机。','原Mac真实工程、物理摄像头与多显示器矩阵未验收；麦克风已有含暂停的实采证据，物理拔插及多设备同时录制尚未覆盖。','采集与音画同步的局部测试不替代完整发行包工作流；具体样本、数值及版本边界见docs/capture-validation.md。','窗口首帧补丁已通过静态/动态对照及遮挡、移动、缩放、关闭回归；其他驱动和多显示器仍需覆盖。','DPI、浅深主题与浮层的同包实机证据及人工审阅范围见docs/validation-report.md；混合DPI、多显示器和系统高对比模式尚未验收。','未签名，FFmpeg静态依赖的完整公开分发材料仍待核实。')
}
if ($Candidate) { $manifest.limitations += '当前候选仅完成后台包内容检查；最终全量UIA、发行包实际播放录制和安装卸载待主机交互时段验证。' }
$manifestJson = $manifest | ConvertTo-Json -Depth 6
$checksumLines = $entries | ForEach-Object { "$($_.sha256)  $($_.file)" }
$versionPrefix = if ($Candidate) { "DogSC-$Version-candidate" } else { "DogSC-$Version" }
$manifestJson | Set-Content -LiteralPath (Join-Path $release "$versionPrefix-manifest.json") -Encoding utf8
$checksumLines | Set-Content -LiteralPath (Join-Path $release "$versionPrefix-SHA256SUMS.txt") -Encoding ascii
if ($Candidate) { Write-Host (Join-Path $release "$versionPrefix-manifest.json"); exit 0 }
$manifestJson | Set-Content -LiteralPath (Join-Path $release 'DELIVERY-MANIFEST.json') -Encoding utf8
$checksumLines | Set-Content -LiteralPath (Join-Path $release 'SHA256SUMS.txt') -Encoding ascii
Write-Host (Join-Path $release 'DELIVERY-MANIFEST.json')
