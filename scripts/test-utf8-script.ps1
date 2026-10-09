[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) ('artifacts/validation/utf8-helper-' + [guid]::NewGuid().ToString('N')))
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$helperPath = Join-Path $PSScriptRoot 'invoke-utf8-script.ps1'
$pwshPath = Join-Path $PSHOME 'pwsh.exe'
if (-not (Test-Path -LiteralPath $helperPath -PathType Leaf)) { throw "UTF-8 helper not found: $helperPath" }
if (-not (Test-Path -LiteralPath $pwshPath -PathType Leaf)) { throw "PowerShell executable not found: $pwshPath" }

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
$validationRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts/validation')).TrimEnd('\', '/') + '\'
if (-not $resolvedOutput.StartsWith($validationRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Output directory must be under $validationRoot"
}
if (Test-Path -LiteralPath $resolvedOutput) { throw "Output directory already exists: $resolvedOutput" }
$null = New-Item -ItemType Directory -Path $resolvedOutput

$fixturePath = Join-Path $resolvedOutput 'parameter-fixture.ps1'
$noParameterPath = Join-Path $resolvedOutput 'no-parameter-fixture.ps1'
$nativeExitPath = Join-Path $resolvedOutput 'native-exit-fixture.ps1'
$throwPath = Join-Path $resolvedOutput 'throw-fixture.ps1'
$markerPath = Join-Path $resolvedOutput 'must-not-exist.marker'

@'
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Release','Debug')][string]$Configuration,
    [Parameter(Mandatory)][string]$Version,
    [switch]$WithoutFfmpeg,
    [switch]$SkipInstaller
)
Write-Output "中文 stdout：参数绑定完成"
[Console]::Error.WriteLine("中文 stderr：参数绑定完成")
Write-Output (ConvertTo-Json -Compress ([ordered]@{
    Configuration = $Configuration
    Version = $Version
    WithoutFfmpeg = [bool]$WithoutFfmpeg
    SkipInstaller = [bool]$SkipInstaller
}))
'@ | Set-Content -LiteralPath $fixturePath -Encoding utf8

@'
[CmdletBinding()]
param()
Write-Output 'no-parameters-ok'
'@ | Set-Content -LiteralPath $noParameterPath -Encoding utf8

@'
[CmdletBinding()]
param()
cmd.exe /d /c exit 23
Write-Output 'native-command-returned'
'@ | Set-Content -LiteralPath $nativeExitPath -Encoding utf8

@'
[CmdletBinding()]
param()
throw '中文故意失败'
'@ | Set-Content -LiteralPath $throwPath -Encoding utf8

function Invoke-Helper {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$TargetScript,
        [Parameter(Mandatory)][System.Collections.IDictionary]$TargetParameters
    )

    $json = ConvertTo-Json -InputObject $TargetParameters -Compress -Depth 8
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $pwshPath
    $startInfo.WorkingDirectory = $repoRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $startInfo.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    foreach ($argument in @('-NoProfile','-NonInteractive','-File',$helperPath,'-ScriptPath',$TargetScript,'-ParametersJson',$json)) {
        $null = $startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { throw "Could not start UTF-8 helper test: $Name" }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(30000)) {
        $process.Kill($true)
        throw "UTF-8 helper test timed out: $Name"
    }
    [System.Threading.Tasks.Task]::WaitAll(@($stdoutTask,$stderrTask))
    $stdout = $stdoutTask.Result
    $stderr = $stderrTask.Result
    $exitCode = $process.ExitCode
    $process.Dispose()

    [System.IO.File]::WriteAllText((Join-Path $resolvedOutput "$Name.stdout.log"), $stdout, [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText((Join-Path $resolvedOutput "$Name.stderr.log"), $stderr, [System.Text.UTF8Encoding]::new($false))
    return [pscustomobject]@{ ExitCode = $exitCode; Stdout = $stdout; Stderr = $stderr }
}

$codePageBefore = (& chcp.com) -join ' '
$specialValue = 'space ''quote'' 中文 literal $(Set-Content -LiteralPath ''{0}'' -Value ''executed'')' -f $markerPath
$parameterResult = Invoke-Helper -Name 'parameters' -TargetScript $fixturePath -TargetParameters ([ordered]@{
    Configuration = 'Release'
    Version = $specialValue
    WithoutFfmpeg = $true
    SkipInstaller = $false
})
$noParameterResult = Invoke-Helper -Name 'no-parameters' -TargetScript $noParameterPath -TargetParameters ([ordered]@{})
$nativeResult = Invoke-Helper -Name 'native-exit' -TargetScript $nativeExitPath -TargetParameters ([ordered]@{})
$throwResult = Invoke-Helper -Name 'powershell-throw' -TargetScript $throwPath -TargetParameters ([ordered]@{})
$codePageAfter = (& chcp.com) -join ' '

$replacements = @{}
foreach ($entry in @(
    @{ Name='parameters'; Result=$parameterResult },
    @{ Name='no-parameters'; Result=$noParameterResult },
    @{ Name='native-exit'; Result=$nativeResult },
    @{ Name='powershell-throw'; Result=$throwResult }
)) {
    $replacements[$entry.Name] = ([regex]::Matches($entry.Result.Stdout + $entry.Result.Stderr, [string][char]0xFFFD)).Count
}

$parameterLine = ($parameterResult.Stdout -split "`r?`n" | Where-Object { $_.StartsWith('{') } | Select-Object -First 1)
$bound = if ($parameterLine) { $parameterLine | ConvertFrom-Json } else { $null }
$summary = [ordered]@{
    helperPath = $helperPath
    targetParameterBinding = [ordered]@{
        exitCode = $parameterResult.ExitCode
        configuration = $bound.Configuration
        versionExact = ($bound.Version -ceq $specialValue)
        versionValue = $bound.Version
        withoutFfmpeg = $bound.WithoutFfmpeg
        skipInstaller = $bound.SkipInstaller
        markerCreated = Test-Path -LiteralPath $markerPath
        stdoutContainsChinese = $parameterResult.Stdout.Contains('中文 stdout')
        stderrContainsChinese = $parameterResult.Stderr.Contains('中文 stderr')
    }
    noParameters = [ordered]@{ exitCode = $noParameterResult.ExitCode; outputPresent = $noParameterResult.Stdout.Contains('no-parameters-ok') }
    nativeNonzero = [ordered]@{ exitCode = $nativeResult.ExitCode; expectedExitCode = 23 }
    powershellThrow = [ordered]@{ exitCode = $throwResult.ExitCode; stderrContainsChinese = $throwResult.Stderr.Contains('中文故意失败') }
    replacementCharacterCounts = $replacements
    parentCodePageBefore = $codePageBefore
    parentCodePageAfter = $codePageAfter
    parentCodePageUnchanged = ($codePageBefore -ceq $codePageAfter)
}
$passed = $parameterResult.ExitCode -eq 0 -and
    $bound.Configuration -ceq 'Release' -and
    $bound.Version -ceq $specialValue -and
    $bound.WithoutFfmpeg -eq $true -and
    $bound.SkipInstaller -eq $false -and
    -not (Test-Path -LiteralPath $markerPath) -and
    $parameterResult.Stdout.Contains('中文 stdout') -and
    $parameterResult.Stderr.Contains('中文 stderr') -and
    $noParameterResult.ExitCode -eq 0 -and
    $noParameterResult.Stdout.Contains('no-parameters-ok') -and
    $nativeResult.ExitCode -eq 23 -and
    $throwResult.ExitCode -ne 0 -and
    $throwResult.Stderr.Contains('中文故意失败') -and
    (@($replacements.Values | Where-Object { $_ -ne 0 }).Count -eq 0) -and
    ($codePageBefore -ceq $codePageAfter)

$summary.Passed = $passed
$summaryPath = Join-Path $resolvedOutput 'summary.json'
[System.IO.File]::WriteAllText($summaryPath, ($summary | ConvertTo-Json -Depth 8), [System.Text.UTF8Encoding]::new($false))

Write-Output "UTF-8 helper regression: $(if ($passed) {'PASS'} else {'FAIL'})"
Write-Output "Summary: $summaryPath"
if (-not $passed) { exit 1 }
