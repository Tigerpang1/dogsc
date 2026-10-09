[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ScriptPath,
    [Parameter(Mandatory)][string]$ArgumentsJson
)

# This bootstrap runs in the hidden child process, never in the caller's console.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding
$scriptArguments = @(ConvertFrom-Json -InputObject $ArgumentsJson | ForEach-Object { [string]$_ })
if (-not (Test-Path -LiteralPath $ScriptPath -PathType Leaf)) { throw "Script does not exist: $ScriptPath" }
$global:LASTEXITCODE = 0
& $ScriptPath @scriptArguments
exit $global:LASTEXITCODE
