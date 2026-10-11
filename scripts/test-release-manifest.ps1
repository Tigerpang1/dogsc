[CmdletBinding()]
param()

# Exercise the release gate with isolated, synthetic reports. No application or installer runs.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$output = Join-Path $repoRoot ('artifacts/validation/release-manifest-policy-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $output
$version = '0.2.17'
$head = '1111111111111111111111111111111111111111'
$upstream = 'e207d11b111c5e58152238fb64115b2009850547'
$checks = [Collections.Generic.List[object]]::new()
$pwsh = Join-Path $PSHOME 'pwsh.exe'

function Test-Case([string]$Name, [bool]$Candidate, [bool]$ExpectPass, [scriptblock]$Mutate, [bool]$PublicBeta = $false) {
    $caseRoot = Join-Path $output $Name
    $scripts = Join-Path $caseRoot 'scripts'
    $release = Join-Path $caseRoot 'artifacts/release'
    $validation = Join-Path $caseRoot "artifacts/validation/package-$version"
    foreach ($directory in @($scripts, $release, $validation)) { $null = New-Item -ItemType Directory -Path $directory -Force }
    $script = Join-Path $scripts 'write-release-manifest.ps1'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'write-release-manifest.ps1') -Destination $script
    $artifacts = @()
    foreach ($artifactName in @("DogSC-$version-win-x64.zip", "DogSC-Setup-$version-win-x64.exe", "DogSC-$version-source.zip")) {
        $path = Join-Path $release $artifactName
        [IO.File]::WriteAllText($path, 'Synthetic release-gate fixture; not an executable or delivery package.')
        $artifacts += @{ Name=$artifactName; SHA256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
    }
    $required = @('portable-archive-content', 'preferences-snapshot', 'portable-playback', 'portable-workflow', 'installed-playback', 'installed-uninstall', 'preferences-restored', 'portable-archive-unchanged')
    if ($PublicBeta) {
        $required += 'native-media-distribution'
        $nativeRoot = Join-Path $caseRoot 'third_party/ffmpeg'
        $null = New-Item -ItemType Directory -Path $nativeRoot -Force
        $native = @{ schemaVersion=1; version='8.1.2' }
        foreach ($entry in @(@{ key='archive'; file='DogSC-FFmpeg-8.1.2-win-x64.zip' }, @{ key='sourceArchive'; file='DogSC-FFmpeg-8.1.2-sources.zip' })) {
            $nativePath = Join-Path $release $entry.file
            [IO.File]::WriteAllText($nativePath, 'Synthetic native distribution fixture.')
            $native[$entry.key] = @{ file=$entry.file; sha256=(Get-FileHash -LiteralPath $nativePath -Algorithm SHA256).Hash.ToLowerInvariant() }
            $artifacts += @{ Name=$entry.file; SHA256=$native[$entry.key].sha256 }
        }
        $native | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $nativeRoot 'manifest.json') -Encoding utf8
    }
    $package = @{
        Passed=$true; Scope='interactive-package'; Version=$version; CompletedUtc=[DateTime]::UtcNow.ToString('o')
        Checks=@($required | ForEach-Object { @{ Id=$_; Passed=$true } })
        Artifacts=$artifacts
        BuildInfo="版本：$version`nFrameTrail 源码提交：$head`nDogSC 上游审计基线：$upstream`n打包时工作区：clean`n"
    }
    $source = @{
        status='passed'; sourceZipSha256=$artifacts[2].SHA256.ToLowerInvariant(); completedUtc=[DateTime]::UtcNow.ToString('o')
        sourceMetadata=@{ frameTrailCommit=$head; upstreamDogScCommit=$upstream }
    }
    if ($PublicBeta) {
        $nativeCheck = @($package.Checks | Where-Object Id -eq 'native-media-distribution')[0]
        $nativeCheck.ArchiveSha256 = $native.archive.sha256
        $nativeCheck.SourceArchiveSha256 = $native.sourceArchive.sha256
    }
    if ($Candidate) { $package.Scope='content-only'; $package.Checks=@(@{ Id='portable-archive-content'; Passed=$true }) }
    & $Mutate $package $source $release
    $package | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $validation 'result.json') -Encoding utf8
    $package | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $validation 'content-result.json') -Encoding utf8
    $source | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $validation 'source-validation.json') -Encoding utf8
    $arguments = @('-NoProfile', '-NonInteractive', '-File', $script, '-Version', $version)
    if ($Candidate) { $arguments += '-Candidate' }
    if ($PublicBeta) { $arguments += '-PublicBeta' }
    & $pwsh @arguments *> (Join-Path $caseRoot 'result.log')
    $exit = $LASTEXITCODE
    $manifest = Join-Path $release $(if ($Candidate) { "DogSC-$version-candidate-manifest.json" } else { 'DELIVERY-MANIFEST.json' })
    $created = Test-Path -LiteralPath $manifest
    $passed = if ($ExpectPass) { $exit -eq 0 -and $created } else { $exit -ne 0 -and -not $created }
    if ($passed -and $ExpectPass) {
        $actual = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
        $expectedStatus = if ($Candidate) { 'candidate-awaiting-interactive-validation' } elseif ($PublicBeta) { 'validated-public-beta' } else { 'validated-local-delivery' }
        $expectedCount = if ($PublicBeta) { 5 } else { 3 }
        $passed = $actual.status -eq $expectedStatus -and $actual.artifacts.Count -eq $expectedCount -and
            $actual.productName -eq 'DogSC Windows 社区移植' -and
            $actual.upstreamRepository -eq 'https://github.com/laogou717/dogsc' -and
            $actual.artifacts[0].file -eq "DogSC-$version-win-x64.zip" -and
            $actual.artifacts[1].file -eq "DogSC-Setup-$version-win-x64.exe" -and
            $actual.artifacts[2].file -eq "DogSC-$version-source.zip"
    }
    $checks.Add([pscustomobject]@{ Name=$Name; Passed=$passed; ExpectedPass=$ExpectPass; ExitCode=$exit; ManifestCreated=$created })
    if (-not $passed) { throw "Release manifest policy regression: $Name. See $caseRoot" }
}

try {
    Test-Case 'candidate-content-only' $true $true { }
    Test-Case 'full-required-checks' $false $true { }
    Test-Case 'additional-passed-check' $false $true { param($p) $p.Checks += @{ Id='additional-evidence'; Passed=$true } }
    Test-Case 'candidate-cannot-be-final' $false $false { param($p) $p.Scope='content-only' }
    Test-Case 'portable-cannot-be-final' $false $false { param($p) $p.Scope='portable-only' }
    Test-Case 'missing-uninstall' $false $false { param($p) $p.Checks=@($p.Checks | Where-Object Id -ne 'installed-uninstall') }
    Test-Case 'missing-archive-unchanged' $false $false { param($p) $p.Checks=@($p.Checks | Where-Object Id -ne 'portable-archive-unchanged') }
    Test-Case 'duplicate-restore' $false $false { param($p) $p.Checks += @{ Id='preferences-restored'; Passed=$true } }
    Test-Case 'failed-restore' $false $false { param($p) ($p.Checks | Where-Object Id -eq 'preferences-restored').Passed=$false }
    Test-Case 'string-is-not-a-boolean' $false $false { param($p) $p.Passed='true' }
    Test-Case 'additional-failed-check' $false $false { param($p) $p.Checks += @{ Id='additional-evidence'; Passed=$false } }
    Test-Case 'source-test-failure' $false $false { param($p,$s) $s.status='failed' }
    Test-Case 'source-commit-mismatch' $false $false { param($p,$s) $s.sourceMetadata.frameTrailCommit='2222222222222222222222222222222222222222' }
    Test-Case 'dirty-build' $false $false { param($p) $p.BuildInfo=$p.BuildInfo.Replace('clean','dirty') }
    Test-Case 'wrong-version' $false $false { param($p) $p.Version='0.2.2' }
    Test-Case 'portable-changed-after-validation' $false $false { param($p,$s,$r) [IO.File]::AppendAllText((Join-Path $r "DogSC-$version-win-x64.zip"), 'changed') }
    Test-Case 'source-changed-after-validation' $true $false { param($p,$s,$r) [IO.File]::AppendAllText((Join-Path $r "DogSC-$version-source.zip"), 'changed') }
    Test-Case 'public-beta-required-checks' $false $true { } $true
    Test-Case 'public-beta-missing-native-check' $false $false { param($p) $p.Checks=@($p.Checks | Where-Object Id -ne 'native-media-distribution') } $true
    Test-Case 'public-beta-modified-native-source' $false $false { param($p,$s,$r) [IO.File]::AppendAllText((Join-Path $r 'DogSC-FFmpeg-8.1.2-sources.zip'), 'changed') } $true
    Test-Case 'public-beta-native-changed-since-validation' $false $false { param($p) (@($p.Checks | Where-Object Id -eq 'native-media-distribution')[0]).ArchiveSha256=('0' * 64) } $true
    Test-Case 'public-beta-source-changed-since-validation' $false $false { param($p) (@($p.Checks | Where-Object Id -eq 'native-media-distribution')[0]).SourceArchiveSha256=('0' * 64) } $true
    Test-Case 'public-beta-native-wrong-filename-version' $false $false {
        param($p,$s,$r)
        $manifestPath = Join-Path (Split-Path -Parent (Split-Path -Parent $r)) 'third_party/ffmpeg/manifest.json'
        $nativeFixture = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $oldName = $nativeFixture.archive.file
        $newName = 'DogSC-FFmpeg-9.9-win-x64.zip'
        Copy-Item -LiteralPath (Join-Path $r $oldName) -Destination (Join-Path $r $newName)
        ($p.Artifacts | Where-Object Name -eq $oldName).Name = $newName
        $nativeFixture.archive.file = $newName
        $nativeFixture | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    } $true
    Test-Case 'public-beta-native-wrong-asset-role' $false $false {
        param($p,$s,$r)
        $manifestPath = Join-Path (Split-Path -Parent (Split-Path -Parent $r)) 'third_party/ffmpeg/manifest.json'
        $nativeFixture = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $binary = $nativeFixture.archive
        $nativeFixture.archive = $nativeFixture.sourceArchive
        $nativeFixture.sourceArchive = $binary
        $nativeFixture | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    } $true
    Test-Case 'public-beta-cannot-be-candidate' $true $false { } $true
    $result = @{ Passed=$true; Scope='Isolated synthetic release-manifest policy tests; no actual delivery or installer validation'; Checks=@($checks) }
}
catch {
    $result = @{ Passed=$false; Scope='Isolated synthetic release-manifest policy tests'; Checks=@($checks); Error=$_.Exception.ToString() }
    throw
}
finally { $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding utf8 }
Write-Host "PASS: $($checks.Count) release-manifest policy checks; $output"
