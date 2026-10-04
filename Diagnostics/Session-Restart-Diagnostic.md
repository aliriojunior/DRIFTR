# DRIFTR single-session restart diagnostic

This is a development-only experiment. It is not a production feature and never restarts a session automatically.

## Safety boundary

- The control exists only when `DRIFTR_MEMORY_DIAGNOSTICS=1`.
- Only the selected, entitled account is detached, disposed, and recreated.
- The replacement uses the same `DRIFTR_DATA_ROOT\Profiles\AccountN` directory. Nothing deletes or clears profile data.
- Website tokens, credentials, session storage, licensing state, and backend APIs are not read or migrated.
- Old WebView2 processes are observed through `GetProcessInfos()` and `BrowserProcessExited`; they are never killed.
- Recreation stops if the captured old process group does not terminate within 20 seconds.
- Non-target panel, WebView, environment, and available browser-process identities are checked after recreation.

## Launch the diagnostic artifact

From PowerShell, use a new isolated data directory (do not point `DRIFTR_DATA_ROOT` at a real DRIFTR profile):

```powershell
$env:DRIFTR_MEMORY_DIAGNOSTICS = '1'
$env:DRIFTR_DATA_ROOT = 'C:\PokeQuad\Diagnostics\ManualData\session-restart'
$env:DRIFTR_MEMORY_DIAGNOSTICS_ROOT = 'C:\PokeQuad\Diagnostics\ManualData\session-restart\MemoryLogs'
$env:DRIFTR_MEMORY_DIAGNOSTICS_INTERVAL_SECONDS = '60'
$env:DRIFTR_MEMORY_DIAGNOSTICS_SCENARIO = 'aged-four-session-restart'
& 'C:\PokeQuad\artifacts\DRIFTR-1.1.0-session-restart-diagnostic-win-x64\DRIFTR.exe'
```

Do not add `DRIFTR_MEMORY_DIAGNOSTICS_SKIP_NAVIGATION`; that switch exists only for the isolated runtime harness.

## Future aged-session experiment

1. Launch the diagnostic artifact with the variables above and sign in to DRIFTR normally.
2. Open four normal website sessions and leave them running for the intended aging period.
3. Preserve all other activity and capture the current memory log plus Task Manager observations.
4. Select one high-memory account. Record its account number, docked/detached state, and whether it is signed in.
5. Click **RESTART SESSION**, verify the account number in the confirmation, then confirm.
6. Do not reload, navigate, dock, or pop out the other three accounts.
7. Wait for the old process group result and the replacement page to stabilize.
8. Retain the `session-restarts-*.jsonl` file. It contains before, disposal, creation, page-load, one-minute, and five-minute stages.
9. Compare the selected account's working set/private memory before disposal and after warm-up. Also verify the other accounts kept their identities and browser PIDs.
10. Record whether the website remained authenticated. A new WebView2 environment does not preserve `sessionStorage`, so a new website login may be required.

Only after those measurements can the experiment answer whether a full environment restart reclaims substantially more memory than page reload.
