[CmdletBinding()]
param(
    [ValidateSet('IdleBaseline', 'ApplicationPage', 'NavigationStress', 'LayoutStress', 'PopOutStress', 'FourDetached', 'SignOutLifecycle')]
    [string]$Scenario = 'IdleBaseline',
    [ValidateRange(0, 10080)]
    [int]$DurationMinutes = 30,
    [ValidateRange(1, 3600)]
    [int]$SampleIntervalSeconds = 60,
    [string]$AppPath = (Join-Path $PSScriptRoot '..\bin\Release\net8.0-windows\DRIFTR.exe'),
    [string]$LicensingApiUrl
)

$ErrorActionPreference = 'Stop'
$resolvedApp = (Resolve-Path -LiteralPath $AppPath).Path
$runId = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $Scenario
$runRoot = Join-Path $PSScriptRoot (Join-Path 'Runs' $runId)
$dataRoot = Join-Path $runRoot 'Data'
$legacyRoot = Join-Path $runRoot 'LegacyData'
$logRoot = Join-Path $runRoot 'MemoryLogs'
New-Item -ItemType Directory -Path $dataRoot, $legacyRoot, $logRoot -Force | Out-Null

$startInfo = [System.Diagnostics.ProcessStartInfo]::new($resolvedApp)
$startInfo.UseShellExecute = $false
$startInfo.Environment['DRIFTR_DATA_ROOT'] = $dataRoot
$startInfo.Environment['DRIFTR_LEGACY_DATA_ROOT'] = $legacyRoot
$startInfo.Environment['DRIFTR_MEMORY_DIAGNOSTICS'] = '1'
$startInfo.Environment['DRIFTR_MEMORY_DIAGNOSTICS_INTERVAL_SECONDS'] = [string]$SampleIntervalSeconds
$startInfo.Environment['DRIFTR_MEMORY_DIAGNOSTICS_ROOT'] = $logRoot
$startInfo.Environment['DRIFTR_MEMORY_DIAGNOSTICS_SCENARIO'] = $Scenario
if ($LicensingApiUrl) { $startInfo.Environment['DRIFTR_LICENSE_API_URL'] = $LicensingApiUrl }

$testPages = Join-Path $PSScriptRoot 'TestPages'
if ($Scenario -eq 'IdleBaseline') {
    $startInfo.Environment['DRIFTR_MEMORY_DIAGNOSTICS_URL'] = ([uri](Join-Path $testPages 'idle.html')).AbsoluteUri
}
elseif ($Scenario -eq 'NavigationStress') {
    $startInfo.Environment['DRIFTR_MEMORY_DIAGNOSTICS_URL'] = ([uri](Join-Path $testPages 'navigation-a.html')).AbsoluteUri
}

[ordered]@{
    schema = 'driftr-memory-run-v1'
    run_id = $runId
    scenario = $Scenario
    started_utc = [DateTimeOffset]::UtcNow
    duration_minutes = $DurationMinutes
    sample_interval_seconds = $SampleIntervalSeconds
    app_path = $resolvedApp
    isolated_data_root = $dataRoot
    memory_log_root = $logRoot
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'run-manifest.json') -Encoding UTF8

Write-Host "Starting isolated $Scenario run: $runId"
Write-Host "Data: $dataRoot"
Write-Host "Logs: $logRoot"
Write-Host 'Use only an approved test login. No credentials are recorded by this harness.'
Write-Host 'Follow Diagnostics\Memory-Stability-Runbook.md for the scenario actions.'
$process = [System.Diagnostics.Process]::Start($startInfo)
if (-not $process) { throw 'DRIFTR did not start.' }

if ($DurationMinutes -eq 0) {
    $process.WaitForExit()
    exit $process.ExitCode
}

$deadline = [DateTimeOffset]::UtcNow.AddMinutes($DurationMinutes)
while (-not $process.HasExited -and [DateTimeOffset]::UtcNow -lt $deadline) {
    Start-Sleep -Seconds 30
    $process.Refresh()
}

if (-not $process.HasExited) {
    [void]$process.CloseMainWindow()
    if (-not $process.WaitForExit(30000)) {
        throw 'DRIFTR did not close within 30 seconds. It was left running; close it normally so the final summary is written.'
    }
}

Write-Host "Run finished. Review $logRoot"
