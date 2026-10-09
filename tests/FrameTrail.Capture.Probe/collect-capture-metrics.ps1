param(
    [Parameter(Mandatory=$true)][int]$ProcessId,
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [Parameter(Mandatory=$true)][string]$MediaPath,
    [int]$IntervalSeconds = 10,
    [switch]$Append
)
$ErrorActionPreference = 'Stop'
$writeHeader = -not $Append -or -not (Test-Path -LiteralPath $OutputPath) -or (Get-Item -LiteralPath $OutputPath).Length -eq 0
$csv = [System.IO.StreamWriter]::new($OutputPath, [bool]$Append, [System.Text.UTF8Encoding]::new($false))
try {
    if ($writeHeader) {
        $csv.WriteLine('utc,elapsed_seconds,cpu_seconds,private_bytes,working_set_bytes,available_memory_bytes,media_bytes')
        $csv.Flush()
    }
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    while ($clock.Elapsed.TotalSeconds -lt 1810) {
        $p = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
        if ($null -eq $p) { break }
        $p.Refresh()
        $os = Get-CimInstance Win32_OperatingSystem
        $available = [int64]$os.FreePhysicalMemory * 1024
        $size = if (Test-Path -LiteralPath $MediaPath) { (Get-Item -LiteralPath $MediaPath).Length } else { 0 }
        $line = '{0},{1:F3},{2:F3},{3},{4},{5},{6}' -f [DateTimeOffset]::UtcNow.ToString('O'), $clock.Elapsed.TotalSeconds, $p.TotalProcessorTime.TotalSeconds, $p.PrivateMemorySize64, $p.WorkingSet64, $available, $size
        $csv.WriteLine($line)
        $csv.Flush()
        Start-Sleep -Seconds $IntervalSeconds
    }
} finally {
    $csv.Dispose()
}
