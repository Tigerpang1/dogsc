[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceZip,
    [ValidateRange(60, 7200)]
    [int]$TestTimeoutSeconds = 3600,
    [ValidateRange(60, 7200)]
    [int]$BuildTimeoutSeconds = 1800,
    [ValidateRange(60, 7200)]
    [int]$PackageTimeoutSeconds = 3600
)

$ErrorActionPreference = 'Stop'
$workspaceRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$sourceZipPath = [System.IO.Path]::GetFullPath($SourceZip)
$resultDirectory = Join-Path $workspaceRoot 'artifacts/validation/package-0.2.0'
$resultPath = Join-Path $resultDirectory 'source-validation.json'
$upstreamCommit = 'e207d11b111c5e58152238fb64115b2009850547'
$runId = [guid]::NewGuid().ToString('N')
$tempBoundary = [System.IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
$tempRoot = Join-Path $env:TEMP "FrameTrail-source-package-validation-$runId"
$logsDirectory = Join-Path $resultDirectory "source-validation-logs-$runId"
$run = [ordered]@{
    schemaVersion = 1
    runId = $runId
    startedUtc = [DateTime]::UtcNow.ToString('o')
    completedUtc = $null
    status = 'running'
    sourceZip = $sourceZipPath
    sourceZipSha256 = $null
    validationTempDirectory = $tempRoot
    extractedDirectory = Join-Path $tempRoot 'extract/FrameTrail'
    workspaceRoot = $workspaceRoot
    logsDirectory = $logsDirectory
    sourceMetadata = $null
    noGitMetadataVerified = $false
    steps = @()
    buildInfo = $null
    error = $null
}

function Assert-TempPath([string]$Path) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($tempBoundary, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to use a temporary path outside TEMP: $fullPath"
    }
    $workspaceBoundary = $workspaceRoot.TrimEnd('\') + '\'
    if ($fullPath.StartsWith($workspaceBoundary, [System.StringComparison]::OrdinalIgnoreCase) -or
        $workspaceRoot.StartsWith($fullPath.TrimEnd('\') + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Temporary validation directory must be outside the workspace and its Git ancestor: $fullPath"
    }
}

function Invoke-LoggedProcess(
    [string]$Name,
    [string]$FileName,
    [string[]]$Arguments,
    [string]$WorkingDirectory,
    [int]$TimeoutSeconds
) {
    $stdoutPath = Join-Path $logsDirectory "$Name.stdout.log"
    $stderrPath = Join-Path $logsDirectory "$Name.stderr.log"
    $record = [ordered]@{
        name = $Name
        executable = $FileName
        arguments = @($Arguments)
        workingDirectory = $WorkingDirectory
        timeoutSeconds = $TimeoutSeconds
        startedUtc = [DateTime]::UtcNow.ToString('o')
        completedUtc = $null
        exitCode = $null
        timedOut = $false
        stdoutLog = $stdoutPath
        stderrLog = $stderrPath
        error = $null
    }
    $process = $null
    try {
        $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $FileName
        $startInfo.WorkingDirectory = $WorkingDirectory
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        foreach ($argument in $Arguments) { $null = $startInfo.ArgumentList.Add($argument) }

        $process = [System.Diagnostics.Process]::new()
        $process.StartInfo = $startInfo
        if (-not $process.Start()) { throw "Failed to start process: $FileName" }

        # Read both redirected streams immediately so neither child pipe can block the process.
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $record.timedOut = $true
            try { $process.Kill($true) } catch { }
            $process.WaitForExit()
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        [System.IO.File]::WriteAllText($stdoutPath, $stdout, [System.Text.UTF8Encoding]::new($false))
        [System.IO.File]::WriteAllText($stderrPath, $stderr, [System.Text.UTF8Encoding]::new($false))
        if (-not $record.timedOut) { $record.exitCode = $process.ExitCode }
    }
    catch {
        $record.error = $_.Exception.Message
        [System.IO.File]::WriteAllText($stderrPath, $record.error, [System.Text.UTF8Encoding]::new($false))
    }
    finally {
        if ($process) { $process.Dispose() }
        $record.completedUtc = [DateTime]::UtcNow.ToString('o')
    }
    $run.steps += [pscustomobject]$record
    return [pscustomobject]$record
}

try {
    $null = New-Item -ItemType Directory -Path $resultDirectory -Force
    $null = New-Item -ItemType Directory -Path $logsDirectory -Force
    Assert-TempPath $tempRoot
    if (-not (Test-Path -LiteralPath $sourceZipPath -PathType Leaf)) { throw "Source ZIP does not exist: $sourceZipPath" }
    $run.sourceZipSha256 = (Get-FileHash -LiteralPath $sourceZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if (Test-Path -LiteralPath $tempRoot) { throw "UUID temporary directory already exists: $tempRoot" }
    $null = New-Item -ItemType Directory -Path $tempRoot
    $archiveRoot = Join-Path $tempRoot 'extract'
    $null = New-Item -ItemType Directory -Path $archiveRoot

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($sourceZipPath)
    try {
        $destinationBoundary = [System.IO.Path]::GetFullPath($archiveRoot).TrimEnd('\') + '\'
        foreach ($entry in $archive.Entries) {
            $entryName = $entry.FullName.Replace('/', '\')
            if ([System.IO.Path]::IsPathRooted($entryName) -or $entryName -match '(^|\\)\.\.(\\|$)' -or $entryName.Contains(':')) {
                throw "Unsafe ZIP entry path: $($entry.FullName)"
            }
            $target = [System.IO.Path]::GetFullPath((Join-Path $archiveRoot $entryName))
            if (-not $target.StartsWith($destinationBoundary, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "ZIP entry escapes extraction root: $($entry.FullName)"
            }
        }
    }
    finally { $archive.Dispose() }
    Expand-Archive -LiteralPath $sourceZipPath -DestinationPath $archiveRoot

    $gitMetadata = @(Get-ChildItem -LiteralPath $archiveRoot -Force -Recurse -ErrorAction Stop |
        Where-Object { $_.Name -eq '.git' })
    if ($gitMetadata.Count -gt 0) { throw "Source archive contains Git metadata: $($gitMetadata[0].FullName)" }
    $run.noGitMetadataVerified = $true

    $repoRoot = Join-Path $archiveRoot 'FrameTrail'
    $metadataPath = Join-Path $repoRoot 'SOURCE-METADATA.json'
    if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) { throw "Missing SOURCE-METADATA.json: $metadataPath" }
    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    if ($metadata.formatVersion -ne 1 -or
        [string]$metadata.frameTrailCommit -notmatch '^[0-9a-f]{40}$' -or
        [string]$metadata.upstreamDogScCommit -ne $upstreamCommit -or
        [string]$metadata.worktreeStateAtArchive -ne 'clean') {
        throw 'SOURCE-METADATA.json has an unsupported format, commit, upstream base, or non-clean archive state.'
    }
    $run.sourceMetadata = $metadata

    $pwshPath = Join-Path $PSHOME 'pwsh.exe'
    if (-not (Test-Path -LiteralPath $pwshPath -PathType Leaf)) { throw "PowerShell executable not found: $pwshPath" }
    $testScript = Join-Path $repoRoot 'scripts/test.ps1'
    $testResult = Invoke-LoggedProcess -Name 'tests' -FileName $pwshPath `
        -Arguments @('-NoProfile', '-NonInteractive', '-File', $testScript, '-Configuration', 'Release') `
        -WorkingDirectory $repoRoot -TimeoutSeconds $TestTimeoutSeconds

    $appProject = Join-Path $repoRoot 'src/FrameTrail.App/FrameTrail.App.csproj'
    $buildResult = Invoke-LoggedProcess -Name 'app-build' -FileName 'dotnet' `
        -Arguments @('build', $appProject, '--configuration', 'Release') `
        -WorkingDirectory $repoRoot -TimeoutSeconds $BuildTimeoutSeconds

    $packageScript = Join-Path $repoRoot 'scripts/package.ps1'
    $packageResult = Invoke-LoggedProcess -Name 'package-without-ffmpeg' -FileName $pwshPath `
        -Arguments @('-NoProfile', '-NonInteractive', '-File', $packageScript, '-Configuration', 'Release', '-Version', '0.2.0', '-WithoutFfmpeg', '-SkipInstaller') `
        -WorkingDirectory $repoRoot -TimeoutSeconds $PackageTimeoutSeconds

    $buildInfoPath = Join-Path $repoRoot 'artifacts/release/FrameTrail-0.2.0-win-x64/BUILD-INFO.txt'
    if (Test-Path -LiteralPath $buildInfoPath -PathType Leaf) {
        $buildInfo = Get-Content -LiteralPath $buildInfoPath -Raw
        $sourceKindMatch = [regex]::Match($buildInfo, '(?m)^源码类型：(.+)$')
        $commitMatch = [regex]::Match($buildInfo, '(?m)^FrameTrail 源码提交：([0-9a-f]{40})$')
        $upstreamMatch = [regex]::Match($buildInfo, '(?m)^DogSC 上游审计基线：([0-9a-f]{40})$')
        $run.buildInfo = [ordered]@{
            path = $buildInfoPath
            sourceKind = if ($sourceKindMatch.Success) { $sourceKindMatch.Groups[1].Value.Trim() } else { $null }
            frameTrailCommit = if ($commitMatch.Success) { $commitMatch.Groups[1].Value } else { $null }
            upstreamDogScCommit = if ($upstreamMatch.Success) { $upstreamMatch.Groups[1].Value } else { $null }
            sourceKindCorrect = $sourceKindMatch.Success -and $sourceKindMatch.Groups[1].Value.Trim() -eq 'source archive'
            frameTrailCommitCorrect = $commitMatch.Success -and $commitMatch.Groups[1].Value -eq [string]$metadata.frameTrailCommit
            upstreamCommitCorrect = $upstreamMatch.Success -and $upstreamMatch.Groups[1].Value -eq $upstreamCommit
        }
    }
    else {
        $run.buildInfo = [ordered]@{ path = $buildInfoPath; error = 'BUILD-INFO.txt was not produced.' }
    }

    $stepResults = @($testResult, $buildResult, $packageResult)
    $stepsPassed = $stepResults.Count -eq 3 -and @($stepResults | Where-Object { $_.timedOut -or $_.exitCode -ne 0 -or $_.error }).Count -eq 0
    $buildInfoPassed = $run.buildInfo.sourceKindCorrect -and $run.buildInfo.frameTrailCommitCorrect -and $run.buildInfo.upstreamCommitCorrect
    $run.status = if ($stepsPassed -and $buildInfoPassed) { 'passed' } else { 'failed' }
}
catch {
    $run.status = 'failed'
    $run.error = $_.Exception.Message
}
finally {
    $run.completedUtc = [DateTime]::UtcNow.ToString('o')
    $json = $run | ConvertTo-Json -Depth 12
    [System.IO.File]::WriteAllText($resultPath, $json, [System.Text.UTF8Encoding]::new($false))
}

Write-Host "Source package validation: $($run.status)"
Write-Host "Result: $resultPath"
Write-Host "Extracted source retained at: $tempRoot"
Write-Host "Logs retained at: $logsDirectory"
if ($run.status -ne 'passed') { exit 1 }
