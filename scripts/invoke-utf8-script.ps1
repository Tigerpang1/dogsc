[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ScriptPath,
    [Parameter(Mandatory)][string]$ParametersJson
)

# This bootstrap runs in the hidden child process, never in the caller's console.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding
$scriptParameters = ConvertFrom-Json -InputObject $ParametersJson -AsHashtable
if ($scriptParameters -isnot [System.Collections.IDictionary]) { throw 'Script parameters must be a JSON object.' }
if (-not (Test-Path -LiteralPath $ScriptPath -PathType Leaf)) { throw "Script does not exist: $ScriptPath" }
$global:LASTEXITCODE = 0
& $ScriptPath @scriptParameters
exit $global:LASTEXITCODE
