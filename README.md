# DRIFTR 1.1.0

**One Window. Every World.**

DRIFTR is a Windows desktop client for one to four independent, manually controlled browser sessions. The DRIFTR Licensing API provides authentication, device activation, and server-authoritative entitlements. DRIFTR does not automate browser or gameplay activity.

The licensing API defaults to `https://driftr-licensing-api.onrender.com/`. `DRIFTR_LICENSE_API_URL` may select another HTTPS endpoint. Debug builds additionally permit explicit HTTP loopback endpoints such as `http://127.0.0.1:8000`; Release builds reject all insecure HTTP licensing endpoints. No credentials or administrative secrets are embedded in the client.

## Build and test

```powershell
dotnet restore
dotnet build -c Release
dotnet run -c Release --project Tests\DRIFTR.Tests.csproj
dotnet run
```

Requirements are Windows 10/11 x64, Microsoft Edge WebView2 Runtime, and a reachable licensing API. Framework-dependent development builds also require .NET 8 Desktop Runtime.

## Secure authentication

Login sends the persistent opaque installation `device_id`. The email, short-lived access token, rotating refresh token, and expiration metadata are stored together at:

```text
%LOCALAPPDATA%\DRIFTR\Licensing\session.dat
```

The entire file is protected with Windows DPAPI `CurrentUser`. Passwords are never persisted. Replacement access and refresh tokens are serialized together, DPAPI-protected, written to a temporary file with write-through and disk flush, then atomically replace the prior session file.

Authenticated licensing calls proactively refresh near known access-token expiry. The safety window is the smaller of 60 seconds and one fifth of the issued access-token lifetime, so the normal 15-minute token uses 60 seconds while deliberately short development tokens do not rotate repeatedly. A shared asynchronous gate permits only one refresh request; waiting callers reuse the replacement access token. An `INVALID_TOKEN` response triggers that same refresh path and retries the original request once.

Permanent refresh failures clear licensing authentication and return to Sign In. Network failures preserve the stored refresh credentials but do not fabricate offline access. Server validation remains required for entitlements.

Normal Sign Out attempts `/auth/logout` before clearing local authentication, and still signs out locally if the server is unavailable. Sign Out Everywhere calls `/auth/logout-all`. Device deactivation calls `/devices/deactivate`. These actions preserve the device identity, settings, browser profiles, cookies, and website sessions.

There remains a small unavoidable failure window if the server rotates a token and the process terminates before the replacement file becomes durable. In that case the user must sign in again; DRIFTR never retries the consumed token.

## Entitlements and layouts

- `max_sessions = 1`: Free; Account1 and Solo only. Accounts 2-4 do not initialize WebView2.
- `max_sessions = 4`: Pro; Account1-4 plus Solo, Duo, and Quad. Duo can be switched between `SIDE BY SIDE` and `STACKED`; the selected account pair and orientation preference persist locally.

`/license/validate` is authoritative after login or refresh. A Pro-to-Free change falls back to Solo Account1 while leaving Account2-4 profile data intact. Switching layouts repositions initialized controls and does not recreate WebViews.

- `Ctrl+1` through `Ctrl+4`: entitled account in Solo
- `Ctrl+S`, `Ctrl+D`, `Ctrl+Q`: Solo, Duo, Quad
- `Ctrl+T`: toggle browser toolbars
- `Ctrl+Plus`, `Ctrl+Minus`, `Ctrl+0`: zoom
- `F5`: refresh selected account
- `F11`: fullscreen

## Pop-out windows and multiple monitors

Each entitled account has a compact `POP OUT` action. It transfers that account's existing browser panel and WebView2 control from the main workspace to an independent DRIFTR window on the same UI dispatcher. It does not create a second browser or profile. The detached window can be moved, resized, minimized, maximized, or placed on another monitor.

Use `DOCK BACK`, or close a detached window with its X button, to return that same browser to the main workspace. `RESTORE ALL` docks every detached account. When no accounts remain docked, the main window shows a recovery view with `RESTORE ALL` instead of a blank workspace.

The main workspace reflows according to the docked account count: four use the Quad 2x2 grid, three use one full-width upper panel and two lower panels, two use the saved Duo Side-by-Side or Stacked orientation, and one fills the workspace. Solo, Duo, and Quad selections never duplicate a detached browser; selecting a detached account activates its window.

Detached account numbers, normal window bounds, maximized state, and Duo orientation are persisted. Startup restores only windows permitted by the current server entitlement. Saved bounds are checked against current monitor work areas; an off-screen window is moved to a visible fallback area. A Pro-to-Free downgrade retires Accounts 2-4 from the active UI without deleting their profile directories.

Closing the main window closes all detached windows as part of application shutdown. Closing an individual detached window docks it instead. Window transfers preserve the live WebView2 when supported; there is intentionally no fallback that silently recreates a failed detached session.

## Browser data and migration

```text
%LOCALAPPDATA%\DRIFTR\Profiles\Account1
%LOCALAPPDATA%\DRIFTR\Profiles\Account2
%LOCALAPPDATA%\DRIFTR\Profiles\Account3
%LOCALAPPDATA%\DRIFTR\Profiles\Account4
```

On first launch, if DRIFTR has no populated profile directory and `%LOCALAPPDATA%\RyVex` exists, DRIFTR copies RyVex profiles and compatible settings. Migration never deletes RyVex data, overwrites populated DRIFTR profiles, or copies licensing state.

For isolated testing:

```powershell
$env:DRIFTR_DATA_ROOT = 'C:\isolated\DRIFTR'
$env:DRIFTR_LEGACY_DATA_ROOT = 'C:\isolated\RyVex'
```

## Final publish

```powershell
dotnet publish -p:PublishProfile=DRIFTR-win-x64
```

Output: `artifacts\DRIFTR-1.1.0-win-x64\DRIFTR.exe`.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Installer\Test-InstallerDefinition.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Installer\Build-Installer.ps1
```

The final profile is Release, self-contained, and targets Windows x64. The installer remains per-user and preserves DRIFTR and legacy RyVex user data during upgrades and uninstall.
