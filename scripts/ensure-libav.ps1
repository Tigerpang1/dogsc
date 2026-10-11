[CmdletBinding()]
param([string]$ArchivePath, [switch]$Offline)

$arguments = @{}
if ($ArchivePath) { $arguments.ArchivePath = $ArchivePath }
if ($Offline) { $arguments.Offline = $true }
& (Join-Path $PSScriptRoot 'ensure-native-media.ps1') @arguments
