# DRIFTR object-lifetime baseline audit

## Architecture

- Four `BrowserPanel` controls are declared once in `MainWindow.xaml`; entitlement decides which initialize. Each owns one WebView2 control.
- `BrowserEnvironmentService.CreateForAccountAsync` creates one persistent `CoreWebView2Environment` per initialized account, each with `Profiles\AccountN`. Up to four environments therefore exist for PRO; FREE initializes only Account 1.
- `BrowserPanel.InitializeBrowserAsync` is guarded by initialized/initializing/disposed flags. It subscribes four CoreWebView2 events after `EnsureCoreWebView2Async` and navigates once.
- MainWindow keeps the four panels in a fixed array for its lifetime. Pop-out removes the same panel from the main grid and places it in a `DetachedBrowserWindow`; docking reverses that operation. It does not construct a panel, WebView2, or environment.
- Detached windows are held in a dictionary only while detached. Docking removes the entry, unsubscribes `DockBackRequested`, releases the panel, and closes the window. Shutdown unsubscribes and closes every detached window before browser disposal.
- Each panel removes its Loaded, mouse, keyboard, NavigationStarting, NavigationCompleted, HistoryChanged, and ProcessFailed handlers and disposes WebView2. The temporary BrowserProcessExited shutdown handler is removed in `finally`.

## Timers, tasks, and roots

The release path had no DispatcherTimer, Threading.Timer, cancellation-token source, recurring async loop, static panel/window collection, or WebMessageReceived subscription. Browser initialization and orderly shutdown are the only notable asynchronous panel operations. Diagnostics add one DispatcherTimer only while explicitly enabled; `MemoryDiagnosticsService.Stop` stops it and removes its Tick handler before the main window closes.

## Findings to validate with measurements

| Location | Observation | Severity | Status |
|---|---|---:|---|
| `BrowserPanel.InitializeBrowserAsync` | If an exception occurs after the four CoreWebView2 subscriptions but before `_isInitialized` becomes true, retry could subscribe them again and disposal's `_isInitialized` guard could skip unsubscription. | Medium | Suspected edge path; not observed. Diagnostic handler counts make it visible. Do not redesign without reproduction. |
| `App.CloseForAuthenticationAsync` | A one-shot anonymous `Closed` handler captures a completion source. The closed window owns the delegate and becomes collectible after `Application.MainWindow` is replaced. | Low | No rooted retention identified. |
| `AuthWindow` / `RenameAccountDialog` | Loaded handlers use lambdas owned by their own window. | Low | Self-contained lifetime; no external publisher found. |
| `MainWindow` panel events | Panels retain MainWindow through seven handlers, while MainWindow retains panels. | Low | A collectible object graph, not a leak by itself; no static root found. |
| WebView2 profiles/environments | Four separate environments carry a real multi-process memory cost. | Expected | Required for account/session isolation; do not combine profiles in this phase. |

No leak is proven by this code audit. The diagnostic comparison between the static local page and the target site is required before attributing long-session growth to DRIFTR, Chromium, or page state.
