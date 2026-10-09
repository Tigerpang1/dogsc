[CmdletBinding()]
param([ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.2.0')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$release = Join-Path $repo 'artifacts/release'
$validation = Join-Path $repo "artifacts/validation/package-$Version"
$package = Get-Content -LiteralPath (Join-Path $validation 'result.json') -Raw | ConvertFrom-Json
$source = Get-Content -LiteralPath (Join-Path $validation 'source-validation.json') -Raw | ConvertFrom-Json
if (-not $package.Passed -or $source.status -ne 'passed') { throw '发行材料必须先完成包运行验收与源码归档构建验收。' }
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
if (-not $package.BuildInfo.Contains($head)) { throw '程序包与源码包的提交不一致。' }
$manifest = [ordered]@{
    version=$Version; generatedUtc=[DateTime]::UtcNow.ToString('O'); purpose='本地交付与验收，未公开发布'
    frameTrailCommit=$head; upstreamDogScCommit=$source.sourceMetadata.upstreamDogScCommit
    packageValidatedUtc=$package.CompletedUtc; sourceValidatedUtc=$source.completedUtc
    artifacts=$entries
    limitations=@('本机Windows 11 x64验证，不等同于干净虚拟机。','原Mac真实工程、物理摄像头/麦克风与多显示器矩阵未验收。','短片50ms同步目标未稳定通过；静态逐像素一致未认证。','未签名，FFmpeg静态依赖的完整公开分发材料仍待核实。')
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $release 'DELIVERY-MANIFEST.json') -Encoding utf8
$entries | ForEach-Object { "$($_.sha256)  $($_.file)" } | Set-Content -LiteralPath (Join-Path $release 'SHA256SUMS.txt') -Encoding ascii
Write-Host (Join-Path $release 'DELIVERY-MANIFEST.json')
