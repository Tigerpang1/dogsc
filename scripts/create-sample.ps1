param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "..\artifacts\sample\FrameTrail-Demo")
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
dotnet run --project (Join-Path $repoRoot "tools\FrameTrail.SampleGenerator\FrameTrail.SampleGenerator.csproj") -c Release -- $outputPath
if ($LASTEXITCODE -ne 0) { throw "样例生成失败，退出码 $LASTEXITCODE" }
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/sample-project.md') -Destination (Join-Path $outputPath 'README.md') -Force
