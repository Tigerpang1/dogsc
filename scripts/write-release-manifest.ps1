[CmdletBinding()]
param([ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.2.2', [switch]$Candidate)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$release = Join-Path $repo 'artifacts/release'
$validation = Join-Path $repo "artifacts/validation/package-$Version"
$packageResultName = if ($Candidate) { 'content-result.json' } else { 'result.json' }
$package = Get-Content -LiteralPath (Join-Path $validation $packageResultName) -Raw | ConvertFrom-Json
$source = Get-Content -LiteralPath (Join-Path $validation 'source-validation.json') -Raw | ConvertFrom-Json
if (-not $package.Passed -or $source.status -ne 'passed') { throw '发行材料必须先完成对应范围的包验收与源码归档构建验收。' }
if ($package.Version -ne $Version) { throw '包验证版本不匹配。' }
if ($Candidate) {
    if ($package.Scope -ne 'content-only') { throw '候选清单需要对应的后台包内容验证。' }
} elseif ($package.Scope -ne 'interactive-package' -or $package.Checks.Count -ne 5) {
    throw '完整交付清单需要包含安装卸载的五项交互包验收；后台内容检查和跳过安装的结果不能代替。'
}
$entries = @()
foreach ($name in @("FrameTrail-$Version-win-x64.zip", "FrameTrail-Setup-$Version-win-x64.exe", "FrameTrail-$Version-source.zip")) {
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
    version=$Version; generatedUtc=[DateTime]::UtcNow.ToString('O'); purpose='本地交付与验收，未公开发布'
    status=$(if ($Candidate) { 'candidate-awaiting-interactive-validation' } else { 'validated-local-delivery' })
    frameTrailCommit=$head; upstreamDogScCommit=$source.sourceMetadata.upstreamDogScCommit
    packageValidationScope=$package.Scope; packageValidatedUtc=$package.CompletedUtc; sourceValidatedUtc=$source.completedUtc
    artifacts=$entries
    limitations=@('本机Windows 11 x64验证，不等同于干净虚拟机。','原Mac真实工程、物理摄像头/麦克风与多显示器矩阵未验收。','短片50ms同步目标未稳定通过；新QPC长测后段静音原因未定位，不能认定30分钟同步通过；静态逐像素一致未认证。','未签名，FFmpeg静态依赖的完整公开分发材料仍待核实。')
}
if ($Candidate) { $manifest.limitations += '当前候选仅完成后台包内容检查；最终全量UIA、发行包实际播放录制和安装卸载待主机交互时段验证。' }
$manifestJson = $manifest | ConvertTo-Json -Depth 6
$checksumLines = $entries | ForEach-Object { "$($_.sha256)  $($_.file)" }
$versionPrefix = if ($Candidate) { "FrameTrail-$Version-candidate" } else { "FrameTrail-$Version" }
$manifestJson | Set-Content -LiteralPath (Join-Path $release "$versionPrefix-manifest.json") -Encoding utf8
$checksumLines | Set-Content -LiteralPath (Join-Path $release "$versionPrefix-SHA256SUMS.txt") -Encoding ascii
if ($Candidate) { Write-Host (Join-Path $release "$versionPrefix-manifest.json"); exit 0 }
$manifestJson | Set-Content -LiteralPath (Join-Path $release 'DELIVERY-MANIFEST.json') -Encoding utf8
$checksumLines | Set-Content -LiteralPath (Join-Path $release 'SHA256SUMS.txt') -Encoding ascii
Write-Host (Join-Path $release 'DELIVERY-MANIFEST.json')
