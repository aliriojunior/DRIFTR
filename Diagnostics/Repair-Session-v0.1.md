# DRIFTR Repair Session v0.1

## Scope

Repair Session operates on one initialized, entitled account. It replaces that account's `BrowserPanel`, WebView2 control, and `CoreWebView2Environment` while retaining the same `Profiles\AccountN` user-data directory.

## Data clearing policy

The replacement WebView calls:

```csharp
CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache)
```

`DiskCache` is the only browsing-data flag used. DRIFTR does not manually delete Chromium files or directories.

Repair v0.1 does not intentionally clear cookies, password data, Local Storage, IndexedDB, DOM storage, CacheStorage, service workers, downloads, permissions, settings, history, or the profile directory. Service-worker and CacheStorage repair are deferred because broadening the clear operation is not justified by the current evidence.

## Lifecycle

1. Reject locked, uninitialized, or concurrent targets.
2. Disable layout/session-lifecycle controls and target interaction.
3. Preserve the selected account, layout state, profile path, and detached bounds/state.
4. Remove and unsubscribe only the selected panel.
5. Dispose it and wait up to 20 seconds for captured environment processes to exit naturally.
6. Stop on timeout; never kill Edge/WebView2 processes and never broaden the data clear.
7. Create the replacement with the same account number and profile directory.
8. Clear `DiskCache` before navigating to DRIFTR Home.
9. Restore the panel to its docked layout or a fresh detached host with the previous bounds/state.
10. Verify target identities changed, other account identities did not, and the profile path is identical.

Detached replacements initialize transparently in the main visual tree before moving to a new detached host. This matches normal Pop Out ownership and keeps subsequent Dock Back safe.

## Failure behavior

Once the old WebView has been disposed it cannot be rolled back. If shutdown fails, DRIFTR does not create a competing environment on the same profile; it shows a disabled failure panel. If cache clearing or initialization fails, the replacement displays a failure state. No fallback deletes profile data or clears broader categories.

## Isolated validation

The runtime probe uses an isolated `DRIFTR_DATA_ROOT`, per-account persistent sentinels, a loopback HTTP resource with a long cache lifetime, and synthetic cookie/LocalStorage markers. A post-repair resource refetch verifies the HTTP disk cache was cleared. Marker reads and SHA-256 comparisons verify persistent fixtures were preserved.

Synthetic fixtures validate mechanics only. They do not prove that Repair Session resolves the real website's `ERROR: [OBJECT EVENT]` state.
