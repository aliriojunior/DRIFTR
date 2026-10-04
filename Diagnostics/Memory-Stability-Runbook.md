# DRIFTR memory stability runbook

Memory diagnostics are opt-in and should be run with isolated data. They never require profile deletion, forced garbage collection, renderer termination, or automatic reload of the production target.

## Build and launch

```powershell
cd C:\PokeQuad
dotnet build -c Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Diagnostics\Start-MemoryStabilityRun.ps1 -Scenario IdleBaseline -DurationMinutes 30
```

The launcher creates `Diagnostics\Runs\<timestamp>-<scenario>\Data`, so it does not read or write the normal DRIFTR or RyVex data roots. Use only an approved test login. If a non-production licensing fixture is available, pass `-LicensingApiUrl http://127.0.0.1:8000` (or its approved URL). Credentials are never command-line parameters or diagnostic fields.

Diagnostics can also be enabled manually:

```powershell
$env:DRIFTR_MEMORY_DIAGNOSTICS='1'
$env:DRIFTR_MEMORY_DIAGNOSTICS_INTERVAL_SECONDS='60'
$env:DRIFTR_DATA_ROOT='C:\Temp\DRIFTR-memory-test\Data'
$env:DRIFTR_LEGACY_DATA_ROOT='C:\Temp\DRIFTR-memory-test\LegacyData'
dotnet run -c Release
```

Disable them by closing that shell or running:

```powershell
Remove-Item Env:\DRIFTR_MEMORY_DIAGNOSTICS
```

## Scenarios

- `IdleBaseline`: four entitled sessions use the included static local page. Do not interact after all four say READY.
- `ApplicationPage`: the normal configured target is used. Authentication is manual; never place credentials in the harness.
- `NavigationStress`: the two included local pages navigate between one another every five seconds. No production website is reloaded.
- `LayoutStress`: once four test sessions are READY, repeat SOLO → DUO horizontal → QUAD → DUO stacked → SOLO. A practical cadence is one transition every 10 seconds for 20 rounds.
- `PopOutStress`: pop out and dock back Accounts 1–4 in sequence for 20 rounds. Close several detached windows with X and use RESTORE ALL periodically.
- `FourDetached`: detach all four, wait one minute, restore all, and repeat 10 times.
- `SignOutLifecycle`: with isolated test authentication, sign out and sign back in five times. Do not create accounts or use personal credentials.

For layout and pop-out runs, record the browser process IDs, browser-control identities, environment identities, and `CoreEventHandlerCount` before and after. Stable identities and a handler count of four show that the existing panel was transferred without another initialization. A change after an actual browser-process failure/restart must be interpreted separately.

## Long runs

Use the same machine, app build, sample interval, and page state when comparing runs:

```powershell
# 2 hours
.\Diagnostics\Start-MemoryStabilityRun.ps1 -Scenario IdleBaseline -DurationMinutes 120

# 8 hours
.\Diagnostics\Start-MemoryStabilityRun.ps1 -Scenario IdleBaseline -DurationMinutes 480

# 24 hours
.\Diagnostics\Start-MemoryStabilityRun.ps1 -Scenario ApplicationPage -DurationMinutes 1440
```

Run the 2-hour lightweight baseline before the 2-hour application-page comparison. For the target-site run, note the memory immediately before and 5–10 minutes after one normal page reload. A substantial renderer/environment reduction after reload, with stable host memory and the same Browser control/environment identity, favors page/renderer state accumulation over a retained DRIFTR control graph.

Each run produces JSONL samples plus a `*-summary.json`. The summary contains bytes and MB for start, current, peak, growth, growth percentage, average growth per hour, elapsed time, and a descriptive classification. “Sustained growth signal” is deliberately not called a leak; confirm it across longer comparable runs and inspect host versus WebView2 scopes.
