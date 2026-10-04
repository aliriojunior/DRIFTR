[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$definition = Join-Path $PSScriptRoot 'DRIFTR.iss'
$output = Join-Path $projectRoot 'artifacts\installer\DRIFTR-Setup-1.1.0-rc.5.exe'

& (Join-Path $PSScriptRoot 'Test-InstallerDefinition.ps1')

$command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
$candidates = @(
    if ($command) { $command.Source }
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
    'C:\Program Files\Inno Setup 6\ISCC.exe'
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }

$compiler = $candidates | Select-Object -First 1
if (-not $compiler) {
    throw 'Inno Setup 6 is required. Install it from https://jrsoftware.org/isdl.php and rerun this script.'
}

if (Test-Path -LiteralPath $output) {
    throw "Refusing to overwrite existing installer artifact: $output"
}

& $compiler $definition
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path -LiteralPath $output -PathType Leaf)) {
    throw "Inno Setup reported success but did not create: $output"
}

$hash = Get-FileHash -Algorithm SHA256 -LiteralPath $output
Write-Host "Installer: $output"
Write-Host "SHA-256:  $($hash.Hash)"
