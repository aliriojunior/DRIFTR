# DRIFTR installer

This directory defines the per-user Windows installer for DRIFTR 1.1.0-rc.5.

## Permanent application identity

The DRIFTR Inno Setup AppId is:

```text
{7B167009-F34A-4CAD-829E-47310861AA48}
```

Every future DRIFTR installer must reuse this exact AppId. Changing it would create a separate installed product instead of upgrading the existing installation.

## Build

Install Inno Setup 6 from the official download page:

https://jrsoftware.org/isdl.php

Then run from the project root:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Installer\Build-Installer.ps1
```

The build helper validates the rc.5 payload and all frozen release hashes before invoking `ISCC.exe`. It refuses to overwrite an existing installer artifact. Successful compilation produces:

```text
artifacts\installer\DRIFTR-Setup-1.1.0-rc.5.exe
```

## Installation and upgrades

The installer uses `PrivilegesRequired=lowest` and defaults to:

```text
%LOCALAPPDATA%\Programs\DRIFTR
```

It installs the complete self-contained rc.5 win-x64 application payload, creates a Start Menu shortcut, and offers an unchecked optional Desktop shortcut. The completion page offers to launch DRIFTR.

The stable AppId, previous-directory/task reuse, and fixed installation path permit future in-place upgrades. Inno Setup Restart Manager integration checks only `DRIFTR.exe` and asks for it to be closed before replacing files. It does not target unrelated WebView2 processes.

## User-data preservation

Normal install, upgrade, repair, and uninstall operate only on installer-owned files under the application directory and installer-created shortcuts. The definition intentionally has no `InstallDelete`, `UninstallDelete`, user-data `Files`, or user-data `Dirs` entries.

DRIFTR user data is stored separately under `%LOCALAPPDATA%\DRIFTR`; normal uninstall preserves profiles, website sessions, DPAPI authentication state, device identity, settings, and window-layout preferences. Legacy `%LOCALAPPDATA%\RyVex` data is also outside installer scope and remains untouched.

Run the non-destructive definition check at any time:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Installer\Test-InstallerDefinition.ps1
```

## WebView2

The application payload includes the Microsoft WebView2 SDK assemblies and native loader, but not the Microsoft Edge WebView2 Evergreen Runtime. Supported systems must have the Evergreen Runtime installed. If it is missing, DRIFTR catches `WebView2RuntimeNotFoundException`, displays an in-application explanation, and offers Retry after the runtime is installed. This installer does not download or bundle a runtime.

## Signing readiness

This installer and the rc.5 application binaries are currently unsigned, so Windows SmartScreen may show an unknown-publisher or low-reputation warning.

For a future signed release:

1. Sign `DRIFTR.exe` and any organization-owned binaries before freezing the publish payload.
2. Configure an Inno Setup `SignTool` command or sign the completed setup executable after compilation.

Keep certificate private keys and passwords outside the repository and scripts. Never modify an already frozen release payload merely to add a signature.
