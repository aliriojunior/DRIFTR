[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$scriptPath = Join-Path $PSScriptRoot 'DRIFTR.iss'
$publishProfilePath = Join-Path $projectRoot 'Properties\PublishProfiles\DRIFTR-win-x64.pubxml'
$payloadRoot = Join-Path $projectRoot 'artifacts\DRIFTR-1.1.0-win-x64'

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        throw $Message
    }
}

Assert-True (Test-Path -LiteralPath $scriptPath -PathType Leaf) 'Installer definition is missing.'
Assert-True (Test-Path -LiteralPath $publishProfilePath -PathType Leaf) 'Final publish profile is missing.'
Assert-True (Test-Path -LiteralPath $payloadRoot -PathType Container) `
    'Final 1.1.0 payload is missing. Publish with DRIFTR-win-x64 before running this validator.'

$profile = Get-Content -LiteralPath $publishProfilePath -Raw
Assert-True ($profile -match '<Configuration>Release</Configuration>') 'Final publish profile must use Release.'
Assert-True ($profile -match '<RuntimeIdentifier>win-x64</RuntimeIdentifier>') 'Final publish profile must target win-x64.'
Assert-True ($profile -match '<SelfContained>true</SelfContained>') 'Final publish profile must be self-contained.'
Assert-True ($profile -match '<PublishDir>\$\(MSBuildProjectDirectory\)\\artifacts\\DRIFTR-1\.1\.0-win-x64\\</PublishDir>') `
    'Final publish profile has an unexpected output directory.'

$definition = Get-Content -LiteralPath $scriptPath -Raw
Assert-True ($definition -match '(?m)^#define MyAppVersion "1\.1\.0"\r?$') 'Installer version is not 1.1.0.'
Assert-True ($definition -match '(?m)^#define MyPayloadDir "\.\.\\artifacts\\DRIFTR-1\.1\.0-win-x64"\r?$') `
    'Installer does not reference the final 1.1.0 payload.'
Assert-True ($definition -match '(?m)^OutputBaseFilename=DRIFTR-Setup-1\.1\.0\r?$') 'Installer output filename is incorrect.'
Assert-True ($definition -match 'AppId=\{\{7B167009-F34A-4CAD-829E-47310861AA48\}') 'Stable AppId is missing or changed.'
Assert-True ($definition -match '(?m)^PrivilegesRequired=lowest\r?$') 'Installer is not configured per-user.'
Assert-True ($definition -match '(?m)^DefaultDirName=\{localappdata\}\\Programs\\DRIFTR\r?$') `
    'Default installation directory is incorrect.'
Assert-True ($definition -notmatch '(?im)^\s*\[UninstallDelete\]') 'An UninstallDelete section is not permitted.'
Assert-True ($definition -notmatch '(?im)^\s*\[InstallDelete\]') 'An InstallDelete section is not permitted.'
Assert-True ($definition -notmatch '(?i)\\DRIFTR\\Profiles|\\RyVex') `
    'Installer must not reference browser or legacy user-data paths.'
Assert-True ($definition -match '(?m)^CloseApplicationsFilter=\{#MyAppExeName\}\r?$') `
    'DRIFTR-only running-application handling is missing.'

$payloadFiles = @(Get-ChildItem -LiteralPath $payloadRoot -File -Recurse)
Assert-True ($payloadFiles.Count -gt 0) 'Final payload is empty.'

$requiredFiles = @(
    'DRIFTR.exe',
    'DRIFTR.dll',
    'DRIFTR.deps.json',
    'DRIFTR.runtimeconfig.json',
    'coreclr.dll',
    'hostfxr.dll',
    'hostpolicy.dll',
    'Microsoft.Web.WebView2.Core.dll',
    'Microsoft.Web.WebView2.Wpf.dll',
    'runtimes\win-x64\native\WebView2Loader.dll'
)
foreach ($relativePath in $requiredFiles) {
    Assert-True (Test-Path -LiteralPath (Join-Path $payloadRoot $relativePath) -PathType Leaf) `
        "Required payload file is missing: $relativePath"
}

$pdbFiles = @($payloadFiles | Where-Object Extension -eq '.pdb')
Assert-True ($pdbFiles.Count -eq 0) "Payload contains PDB files: $($pdbFiles.Name -join ', ')"

$unexpectedData = @($payloadFiles | Where-Object {
    $relative = $_.FullName.Substring($payloadRoot.Length + 1).Replace('\', '/')
    $relative -match '(?i)(^|/)(Profiles?|Diagnostics?|Sessions?)(/|$)' -or
    $_.Name -match '(?i)^(settings\.json|session\.dat|memory-.*\.(csv|jsonl|log))$'
})
Assert-True ($unexpectedData.Count -eq 0) `
    "Payload contains profile, session, or diagnostic data: $($unexpectedData.Name -join ', ')"

$exePath = Join-Path $payloadRoot 'DRIFTR.exe'
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($exePath)
$productVersion = ($version.ProductVersion -split '\+')[0]
Assert-True ($productVersion -eq '1.1.0') "Unexpected DRIFTR product version: $($version.ProductVersion)"
Assert-True ($version.FileVersion -eq '1.1.0.0') "Unexpected DRIFTR file version: $($version.FileVersion)"
Assert-True ((Get-Item -LiteralPath $exePath).Length -gt 0) 'DRIFTR.exe is empty.'

Write-Host 'PASS: Final publish profile is Release, self-contained, win-x64, and uses the 1.1.0 output directory.'
Write-Host "PASS: Final payload contains $($payloadFiles.Count) files, including DRIFTR, .NET runtime, and WebView2 components."
Write-Host 'PASS: DRIFTR product/file versions are 1.1.0/1.1.0.0 and no PDB is present.'
Write-Host 'PASS: Payload contains no profile, session, or diagnostic data.'
Write-Host 'PASS: Installer version, output, stable AppId, per-user scope, and data-preservation rules are valid.'
