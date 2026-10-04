[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$scriptPath = Join-Path $PSScriptRoot 'DRIFTR.iss'
$payloadRoot = Join-Path $projectRoot 'artifacts\DRIFTR-1.1.0-rc.5-win-x64'

$expectedPayloadDigest = 'AF05A928295091A6FCC12FC3D37D6893A359FF678DE2295E67809091D2EFE601'
$expectedHashes = [ordered]@{
    'artifacts\DRIFTR-1.1.0-rc.4-win-x64.zip' = '7FC6BCB4CF77B64BBDAB1D925248033F79473F627382632057758E7AF0028EF1'
    'artifacts\DRIFTR-1.1.0-rc.3-win-x64.zip' = 'A638A2DEC92D55041B1DA5CC3C3595E9763C850E55FD9FA51D0D1671BC845C9F'
    'artifacts\DRIFTR-1.1.0-rc.2-win-x64.zip' = '04BAD9C5B693B76B5B4DD9CA7894B287AC97F64FE3E5D36767EF7FDD0C2998B5'
    'artifacts\RyVex-1.0.1-win-x64.zip' = '1F96CBA2C9DE93962FD9B10161312365447B00FEB27138EE37D8953FE3132690'
    'artifacts\installer\DRIFTR-Setup-1.1.0-rc.3.exe' = 'A37F96BBF8C5C4B7E93DA3978998A5F6B700B51BF0318706FB086276AD795AC6'
    'artifacts\installer\DRIFTR-Setup-1.1.0-rc.4.exe' = '6F3B7694CCF99496A2B09BB0D7DF9BBBD9058D46CBC58E4D40ED278212CFCB07'
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        throw $Message
    }
}

function Get-PayloadDigest([string]$Root) {
    $resolvedRoot = (Resolve-Path -LiteralPath $Root).Path
    $lines = Get-ChildItem -LiteralPath $resolvedRoot -File -Recurse |
        ForEach-Object {
            $relative = $_.FullName.Substring($resolvedRoot.Length + 1).Replace('\', '/')
            $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash
            "$relative|$hash"
        } |
        Sort-Object

    $bytes = [Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '')
    }
    finally {
        $sha.Dispose()
    }
}

Assert-True (Test-Path -LiteralPath $scriptPath) 'Installer definition is missing.'
Assert-True (Test-Path -LiteralPath $payloadRoot -PathType Container) 'rc.5 payload directory is missing.'

foreach ($entry in $expectedHashes.GetEnumerator()) {
    $path = Join-Path $projectRoot $entry.Key
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Frozen artifact is missing: $($entry.Key)"
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash
    Assert-True ($actual -eq $entry.Value) "Frozen artifact hash mismatch: $($entry.Key)"
}

$payloadFiles = @(Get-ChildItem -LiteralPath $payloadRoot -File -Recurse)
Assert-True ($payloadFiles.Count -eq 472) "Unexpected rc.5 payload file count: $($payloadFiles.Count)"
Assert-True ((Get-PayloadDigest $payloadRoot) -eq $expectedPayloadDigest) 'Frozen rc.5 payload directory has changed.'

$exePath = Join-Path $payloadRoot 'DRIFTR.exe'
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($exePath)
Assert-True ($version.ProductVersion -eq '1.1.0-rc.5') "Unexpected DRIFTR product version: $($version.ProductVersion)"

$definition = Get-Content -LiteralPath $scriptPath -Raw
Assert-True ($definition -match 'AppId=\{\{7B167009-F34A-4CAD-829E-47310861AA48\}') 'Stable AppId is missing or changed.'
Assert-True ($definition -match 'PrivilegesRequired=lowest') 'Installer is not configured per-user.'
Assert-True ($definition -match 'DefaultDirName=\{localappdata\}\\Programs\\DRIFTR') 'Default installation directory is incorrect.'
Assert-True ($definition -notmatch '(?im)^\s*\[UninstallDelete\]') 'An UninstallDelete section is not permitted.'
Assert-True ($definition -notmatch '(?im)^\s*\[InstallDelete\]') 'An InstallDelete section is not permitted.'
Assert-True ($definition -notmatch '(?i)\\DRIFTR\\Profiles|\\RyVex') 'Installer must not reference browser or legacy user-data paths.'
Assert-True ($definition -match 'CloseApplicationsFilter=\{#MyAppExeName\}') 'Running-app handling is missing.'

Write-Host "PASS: Stable AppId is configured."
Write-Host "PASS: Per-user install path is configured."
Write-Host "PASS: Complete rc.5 payload is referenced and unchanged."
Write-Host "PASS: No destructive install/uninstall sections exist."
Write-Host "PASS: DRIFTR-only running-application handling is configured."
Write-Host "PASS: Frozen rc.4/rc.3 installers plus rc.4, rc.3, rc.2, and RyVex ZIP hashes match."
