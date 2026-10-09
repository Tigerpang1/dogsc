param([Parameter(Mandatory=$true)][string]$Base)
$ErrorActionPreference = 'Stop'
$tone = Join-Path $Base 'frametrail-capture-lowlevel-tone.wav'
if (-not (Test-Path -LiteralPath $tone -PathType Leaf)) { throw "Missing low volume tone: $tone" }

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class QueueProbeNative {
  [DllImport("winmm.dll", CharSet=CharSet.Unicode, SetLastError=true)]
  public static extern bool PlaySound(string sound, IntPtr module, uint flags);
}
'@

$service = $null
$handle = $null
$result = $null
$tonePlaying = $false
$trackStopped = $false
$runId = [Guid]::NewGuid().ToString('N')
$output = Join-Path $Base "audio-pause-queue-$runId.wav"
$csv = Join-Path $Base "audio-pause-queue-$runId.csv"
$log = Join-Path $Base "audio-pause-queue-$runId.json"
$watch = [Diagnostics.Stopwatch]::StartNew()
try {
  foreach ($file in Get-ChildItem -LiteralPath $Base -Filter '*.dll') { try { [Reflection.Assembly]::LoadFrom($file.FullName) | Out-Null } catch {} }
  $asm = [Reflection.Assembly]::LoadFrom((Join-Path $Base 'FrameTrail.Capture.dll'))
  $serviceType = $asm.GetType('FrameTrail.Capture.CaptureTrackService')
  $service = [Activator]::CreateInstance($serviceType)
  $flags = [Reflection.BindingFlags]'Instance,NonPublic'
  $sessionField = $serviceType.GetField('_sessions', $flags)
  $devices = $serviceType.GetMethod('EnumerateAudioDevicesAsync').Invoke($service, @([Threading.CancellationToken]::None)).GetAwaiter().GetResult()
  $kindType = $asm.GetType('FrameTrail.Capture.AudioDeviceKind')
  $loopbackKind = [Enum]::Parse($kindType, 'SystemLoopback')
  $device = $devices | Where-Object { $_.Kind -eq $loopbackKind -and $_.IsDefault } | Select-Object -First 1
  if ($null -eq $device) { throw 'No default loopback device.' }
  $requestType = $asm.GetType('FrameTrail.Capture.AudioTrackRequest')
  $requestArgs = [Array]::CreateInstance([object], 3)
  $requestArgs.SetValue([string]$output, 0)
  $requestArgs.SetValue($loopbackKind, 1)
  $requestArgs.SetValue([string]$device.Id, 2)
  $request = $requestType.GetConstructors()[0].Invoke($requestArgs)
  $handle = $serviceType.GetMethod('StartAudioTrackAsync').Invoke($service, @($request, [Threading.CancellationToken]::None)).GetAwaiter().GetResult()
  $session = $sessionField.GetValue($service).Values | Select-Object -First 1
  $queueProperty = $session.GetType().GetProperty('AudioPackets', $flags)
  $pausedField = $session.GetType().GetField('AudioPaused', $flags)
  [IO.File]::WriteAllText($csv, "utc,elapsed_seconds,phase,paused,queue_packets,private_mib`n")
  $process = [Diagnostics.Process]::GetCurrentProcess()
  [QueueProbeNative]::PlaySound($tone, [IntPtr]::Zero, [uint32]0x0002000B) | Out-Null
  $tonePlaying = $true

  function Sample([string]$Phase) {
    $q = $queueProperty.GetValue($session).Count
    $p = [bool]$pausedField.GetValue($session)
    $process.Refresh()
    $mib = $process.PrivateMemorySize64 / 1MB
    $line = '{0},{1:F3},{2},{3},{4},{5:F1}' -f [DateTimeOffset]::UtcNow.ToString('O'), $watch.Elapsed.TotalSeconds, $Phase, $p, $q, $mib
    [IO.File]::AppendAllText($csv, $line + "`n")
  }

  foreach ($phase in @(@('active-before', 10), @('paused', 10), @('active-after', 10))) {
    if ($phase[0] -eq 'paused') {
      $serviceType.GetMethod('PauseTrackAsync').Invoke($service, @($handle.TrackId, [Threading.CancellationToken]::None)).GetAwaiter().GetResult()
    } elseif ($phase[0] -eq 'active-after') {
      $serviceType.GetMethod('ResumeTrackAsync').Invoke($service, @($handle.TrackId, [Threading.CancellationToken]::None)).GetAwaiter().GetResult()
    }
    $end = $watch.Elapsed.TotalSeconds + [int]$phase[1]
    while ($watch.Elapsed.TotalSeconds -lt $end) { Sample $phase[0]; Start-Sleep -Milliseconds 100 }
  }
}
finally {
  if ($tonePlaying) { [QueueProbeNative]::PlaySound($null, [IntPtr]::Zero, [uint32]1) | Out-Null }
  if ($null -ne $service -and $null -ne $handle -and -not $trackStopped) {
    try {
      $result = $serviceType.GetMethod('StopTrackAsync').Invoke($service, @($handle.TrackId, [Threading.CancellationToken]::None)).GetAwaiter().GetResult()
      $trackStopped = $true
    } catch { Write-Warning "Could not stop probe track during cleanup: $($_.Exception.Message)" }
  }
  if ($null -ne $service) { try { $service.DisposeAsync().AsTask().GetAwaiter().GetResult() } catch {} }
}

if ($null -eq $result) { throw 'The recording did not produce a stop result.' }
$summary = [ordered]@{ Device=$device.Name; OutputPath=$output; StartStopwatchTicks=$handle.StartStopwatchTicks; StopStopwatchTicks=$result.StopStopwatchTicks; Frequency=$handle.StopwatchFrequency; PausedIntervals=$result.PausedIntervals; Warnings=$result.Warnings; ProbeDurationSeconds=$watch.Elapsed.TotalSeconds; QueueCsv=$csv }
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $log -Encoding utf8
$summary | ConvertTo-Json -Depth 6
