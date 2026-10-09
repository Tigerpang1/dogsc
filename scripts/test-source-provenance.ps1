[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'source-provenance.ps1')
$repoRoot = Split-Path -Parent $PSScriptRoot
$output = Join-Path $repoRoot ('artifacts/validation/source-provenance-policy-' + [guid]::NewGuid().ToString('N'))
$parentRepository = Join-Path $output 'unrelated-parent'
$null = New-Item -ItemType Directory -Path $parentRepository -Force
& git -C $parentRepository init --quiet
if ($LASTEXITCODE -ne 0) { throw 'Cannot initialize isolated policy fixture.' }
[IO.File]::WriteAllText((Join-Path $parentRepository 'parent.txt'), 'Unrelated parent repository fixture.')
& git -C $parentRepository add parent.txt
& git -C $parentRepository -c user.name=FrameTrailPolicyTest -c user.email=policy-test@example.invalid commit --quiet -m 'Isolated fixture'
if ($LASTEXITCODE -ne 0) { throw 'Cannot commit isolated policy fixture.' }
$upstream = 'e207d11b111c5e58152238fb64115b2009850547'
$archiveCommit = '1111111111111111111111111111111111111111'
$extracted = Join-Path $parentRepository 'extracted/FrameTrail'
$null = New-Item -ItemType Directory -Path $extracted -Force
$metadataPath = Join-Path $extracted 'SOURCE-METADATA.json'
$metadata = @{ formatVersion=1; frameTrailCommit=$archiveCommit; upstreamDogScCommit=$upstream; worktreeStateAtArchive='clean' }
$metadata | ConvertTo-Json | Set-Content -LiteralPath $metadataPath -Encoding utf8
$checks = [Collections.Generic.List[object]]::new()

function Require([bool]$Condition, [string]$Name) {
    $checks.Add([pscustomobject]@{ Name=$Name; Passed=$Condition })
    if (-not $Condition) { throw "Source provenance policy regression: $Name" }
}
function Require-Rejection([scriptblock]$Action, [string]$Name) {
    $rejected = $false
    try { $null = & $Action } catch { $rejected = $true }
    Require $rejected $Name
}

try {
    $actual = Get-FrameTrailSourceProvenance -RepositoryRoot $repoRoot -UpstreamBaseSha $upstream
    Require ($actual.Head -match '^[0-9a-f]{40}$' -and $actual.Kind -in @('git checkout','source archive')) 'current source has a valid independent identity'
    $nested = Get-FrameTrailSourceProvenance -RepositoryRoot $extracted -UpstreamBaseSha $upstream
    Require ($nested.Kind -eq 'source archive' -and $nested.Head -eq $archiveCommit) 'an archive inside another Git repository retains archive identity'
    Require-Rejection { Get-FrameTrailSourceProvenance -RepositoryRoot $extracted -UpstreamBaseSha $upstream -RequireGitCheckout } 'source packaging rejects an archive even under a parent Git repository'
    $withoutMetadata = Join-Path $parentRepository 'no-metadata'
    $null = New-Item -ItemType Directory -Path $withoutMetadata
    Require-Rejection { Get-FrameTrailSourceProvenance -RepositoryRoot $withoutMetadata -UpstreamBaseSha $upstream } 'a directory cannot inherit its parent Git identity'
    $metadata.worktreeStateAtArchive='dirty'
    $metadata | ConvertTo-Json | Set-Content -LiteralPath $metadataPath -Encoding utf8
    Require-Rejection { Get-FrameTrailSourceProvenance -RepositoryRoot $extracted -UpstreamBaseSha $upstream } 'dirty archive metadata is rejected'
    $metadata.worktreeStateAtArchive='clean'
    $metadata.upstreamDogScCommit='2222222222222222222222222222222222222222'
    $metadata | ConvertTo-Json | Set-Content -LiteralPath $metadataPath -Encoding utf8
    Require-Rejection { Get-FrameTrailSourceProvenance -RepositoryRoot $extracted -UpstreamBaseSha $upstream } 'wrong upstream metadata is rejected'
    $result = @{ Passed=$true; Scope='Source identity policy only; isolated synthetic Git and archive fixtures'; Checks=@($checks) }
}
catch {
    $result = @{ Passed=$false; Checks=@($checks); Error=$_.Exception.ToString() }
    throw
}
finally { $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding utf8 }
Write-Host "PASS: $($checks.Count) source identity checks; $output"
