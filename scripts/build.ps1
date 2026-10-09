[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src/FrameTrail.App/FrameTrail.App.csproj'

if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw "应用项目尚未集成：$project"
}

$arguments = @('build', $project, '--configuration', $Configuration, '-p:Platform=x64')
if ($NoRestore) { $arguments += '--no-restore' }

Push-Location $repoRoot
try {
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet build 失败，退出码 $LASTEXITCODE" }
}
finally {
    Pop-Location
}
