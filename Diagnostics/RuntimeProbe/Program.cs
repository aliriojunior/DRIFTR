using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using PokeQuad;
using PokeQuad.Config;
using PokeQuad.Controls;
using PokeQuad.Models;

internal static class Program
{
    private static int _assertions;
    private static int _exitCode;
    private static readonly List<string> Results = [];

    [STAThread]
    private static int Main(string[] args)
    {
        bool navigationStress = args.Any(value => value.Equals("--navigation", StringComparison.OrdinalIgnoreCase));
        bool diagnosticsOff = args.Any(value => value.Equals("--diagnostics-off", StringComparison.OrdinalIgnoreCase));
        bool diagnosticsSmoke = args.Any(value => value.Equals("--diagnostics-smoke", StringComparison.OrdinalIgnoreCase));
        bool sessionProfileDiagnostics = args.Any(value => value.Equals("--session-profile-diagnostics", StringComparison.OrdinalIgnoreCase));
        bool repairSession = args.Any(value => value.Equals("--repair-session", StringComparison.OrdinalIgnoreCase));
        bool freeEntitlement = args.Any(value => value.Equals("--free", StringComparison.OrdinalIgnoreCase)) ||
                               diagnosticsOff || diagnosticsSmoke || sessionProfileDiagnostics;
        bool sessionRestart = args.Any(value => value.Equals("--session-restart", StringComparison.OrdinalIgnoreCase));
        bool skipNavigation = args.Any(value => value.Equals("--skip-navigation", StringComparison.OrdinalIgnoreCase));
        string scenario = diagnosticsOff ? "runtime-diagnostics-off"
            : diagnosticsSmoke ? "runtime-diagnostics-smoke"
            : sessionProfileDiagnostics ? "runtime-session-profile-diagnostics"
            : repairSession ? freeEntitlement ? "runtime-repair-session-free" : "runtime-repair-session-pro"
            : sessionRestart
            ? freeEntitlement ? "runtime-session-restart-free" : "runtime-session-restart-pro"
            : freeEntitlement
            ? "runtime-free"
            : navigationStress ? "runtime-navigation-popout" : "runtime-idle-popout";
        string projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string runRoot = Path.Combine(projectRoot, "Diagnostics", "Runs", $"{DateTime.Now:yyyyMMdd-HHmmss}-{scenario}");
        string dataRoot = Path.Combine(runRoot, "Data");
        string logRoot = Path.Combine(runRoot, "MemoryLogs");
        Directory.CreateDirectory(runRoot);
        using CacheFixtureServer? cacheFixture = repairSession ? new CacheFixtureServer() : null;
        Environment.SetEnvironmentVariable("DRIFTR_DATA_ROOT", dataRoot);
        Environment.SetEnvironmentVariable("DRIFTR_RUNTIME_PROBE", "1");
        Environment.SetEnvironmentVariable("DRIFTR_LEGACY_DATA_ROOT", Path.Combine(runRoot, "LegacyData"));
        Environment.SetEnvironmentVariable("DRIFTR_MEMORY_DIAGNOSTICS", diagnosticsOff ? null : "1");
        Environment.SetEnvironmentVariable("DRIFTR_MEMORY_DIAGNOSTICS_INTERVAL_SECONDS", "1");
        Environment.SetEnvironmentVariable("DRIFTR_MEMORY_DIAGNOSTICS_ROOT", logRoot);
        Environment.SetEnvironmentVariable("DRIFTR_MEMORY_DIAGNOSTICS_SCENARIO", scenario);
        Environment.SetEnvironmentVariable("DRIFTR_MEMORY_DIAGNOSTICS_SKIP_NAVIGATION", skipNavigation ? "1" : null);
        string sessionDiagnosticsRoot = Path.Combine(runRoot, "SessionLogs");
        Environment.SetEnvironmentVariable("DRIFTR_SESSION_DIAGNOSTICS", sessionProfileDiagnostics ? "1" : null);
        Environment.SetEnvironmentVariable("DRIFTR_SESSION_DIAGNOSTICS_ACCOUNT", sessionProfileDiagnostics ? "1" : null);
        Environment.SetEnvironmentVariable("DRIFTR_SESSION_DIAGNOSTICS_ROOT", sessionProfileDiagnostics ? sessionDiagnosticsRoot : null);
        string page = sessionProfileDiagnostics ? "session-errors.html" : navigationStress ? "navigation-a.html" : "idle.html";
        Environment.SetEnvironmentVariable("DRIFTR_MEMORY_DIAGNOSTICS_URL", diagnosticsOff ? null : repairSession
            ? cacheFixture!.HomeUrl
            : sessionRestart ? AppConfig.DefaultUrl
            : new Uri(Path.Combine(projectRoot, "Diagnostics", "TestPages", page)).AbsoluteUri);

        var app = new App();
        app.InitializeComponent();
        var session = new LicensingSession(
            "isolated-memory-probe@invalid.example",
            new LicenseValidationResponse(freeEntitlement ? "free" : "pro", "active", freeEntitlement ? 1 : 4, 1, null, true, true));
        var window = new MainWindow(session);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        window.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                if (diagnosticsOff)
                    await RunDiagnosticsOffProbeAsync(window, closed, logRoot);
                else if (diagnosticsSmoke)
                    await RunDiagnosticsSmokeProbeAsync(window, closed, logRoot);
                else if (sessionProfileDiagnostics)
                    await RunSessionProfileDiagnosticsProbeAsync(window, closed, sessionDiagnosticsRoot);
                else if (repairSession)
                    await RunRepairSessionProbeAsync(window, closed, freeEntitlement, dataRoot, cacheFixture!);
                else if (sessionRestart)
                    await RunSessionRestartProbeAsync(window, closed, freeEntitlement, dataRoot, logRoot);
                else
                    await RunProbeAsync(window, closed, navigationStress, freeEntitlement, logRoot);
            }
            catch (Exception exception)
            {
                _exitCode = 1;
                Results.Add($"FAIL: {exception}");
                Console.Error.WriteLine(exception);
                if (window.IsLoaded) window.Close();
            }
            finally
            {
                await Task.Delay(1000);
                var report = new
                {
                    schema = "driftr-memory-runtime-probe-v1",
                    scenario,
                    assertions = _assertions,
                    passed = _exitCode == 0,
                    results = Results,
                    isolated_data_root = dataRoot,
                    memory_log_root = logRoot
                };
                File.WriteAllText(Path.Combine(runRoot, "runtime-probe-result.json"),
                    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Runtime probe completed with {_assertions} assertions. Run: {runRoot}");
                window.Dispatcher.InvokeShutdown();
            }
        }, DispatcherPriority.ContextIdle);

        Dispatcher.Run();
        return _exitCode;
    }

    private static async Task RunDiagnosticsOffProbeAsync(
        MainWindow window, TaskCompletionSource closed, string logRoot)
    {
        BrowserPanel panel = (BrowserPanel)window.FindName("Account1Panel");
        await WaitUntilAsync(() => panel.IsBrowserInitialized, TimeSpan.FromSeconds(90),
            "Diagnostics-off Account 1 did not initialize.");
        await WaitUntilAsync(() => panel.CaptureMemoryDiagnostics(false).Processes.Count > 0,
            TimeSpan.FromSeconds(30), "Diagnostics-off process snapshot was unavailable.");
        Button restart = (Button)window.FindName("RestartSessionButton");
        Check(restart.Visibility == Visibility.Collapsed,
            "Diagnostics OFF hides Restart Session from normal users.");
        Button options = (Button)panel.FindName("OptionsButton");
        Check(options.IsEnabled && options.ContextMenu?.Items.OfType<MenuItem>()
                  .Any(item => string.Equals(item.Header?.ToString(), "REPAIR SESSION", StringComparison.Ordinal)) == true,
            "Diagnostics OFF keeps the normal Repair Session action available for the initialized account.");
        Check(!Directory.Exists(logRoot) || Directory.GetFiles(logRoot).Length == 0,
            "Diagnostics OFF creates no memory or restart diagnostic logs.");
        Check(!((BrowserPanel)window.FindName("Account2Panel")).IsBrowserInitialized,
            "Diagnostics OFF preserves FREE locked-session initialization behavior.");
        int[] pids = panel.CaptureMemoryDiagnostics(false).Processes.Select(process => process.ProcessId).ToArray();
        window.Close();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(45));
        await Task.Delay(3000);
        Check(pids.All(HasExited), "Diagnostics-off shutdown leaves no isolated WebView2 process running.");
    }

    private static async Task RunDiagnosticsSmokeProbeAsync(
        MainWindow window, TaskCompletionSource closed, string logRoot)
    {
        BrowserPanel panel = (BrowserPanel)window.FindName("Account1Panel");
        await WaitUntilAsync(() => panel.IsBrowserInitialized, TimeSpan.FromSeconds(90),
            "Diagnostics smoke Account 1 did not initialize.");
        await WaitUntilAsync(() => Directory.Exists(logRoot) && Directory.GetFiles(logRoot, "memory-*.jsonl").Length == 1,
            TimeSpan.FromSeconds(15), "Memory diagnostics did not create a sample log.");
        Button restart = (Button)window.FindName("RestartSessionButton");
        Check(restart.Visibility == Visibility.Visible,
            "Diagnostics ON exposes the diagnostic-only Restart Session control.");
        string sample = Directory.GetFiles(logRoot, "memory-*.jsonl").Single();
        string content = File.ReadAllText(sample);
        Check(content.Contains("driftr-memory-sample-v1", StringComparison.Ordinal) &&
              !content.Contains("token", StringComparison.OrdinalIgnoreCase) &&
              !content.Contains("password", StringComparison.OrdinalIgnoreCase),
            "Diagnostics ON writes versioned memory samples without token or password fields.");
        int[] pids = panel.CaptureMemoryDiagnostics(false).Processes.Select(process => process.ProcessId).ToArray();
        window.Close();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(45));
        await Task.Delay(3000);
        Check(pids.All(HasExited), "Diagnostics smoke shutdown leaves no isolated WebView2 process running.");
    }

    private static async Task RunSessionProfileDiagnosticsProbeAsync(
        MainWindow window, TaskCompletionSource closed, string diagnosticsRoot)
    {
        BrowserPanel panel = (BrowserPanel)window.FindName("Account1Panel");
        await WaitUntilAsync(() => panel.IsBrowserInitialized, TimeSpan.FromSeconds(90),
            "Session diagnostic Account 1 did not initialize.");
        await panel.WaitForFirstNavigationAsync(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => Directory.Exists(diagnosticsRoot) &&
                                   Directory.GetFiles(diagnosticsRoot, "browser-session-*.jsonl").Length == 1,
            TimeSpan.FromSeconds(15), "Session diagnostic log was not created.");

        AccountMemorySnapshot before = panel.CaptureMemoryDiagnostics(false);
        string sourceBefore = panel.CaptureSafeNavigationTarget() ?? string.Empty;
        Button tools = (Button)window.FindName("ToolbarButton");
        RaiseClick(tools);
        await Task.Delay(250);
        AccountMemorySnapshot toolsOff = panel.CaptureMemoryDiagnostics(false);
        Check(tools.Content?.ToString() == "TOOLS: OFF" &&
              before.BrowserPanelIdentity == toolsOff.BrowserPanelIdentity &&
              before.BrowserControlIdentity == toolsOff.BrowserControlIdentity &&
              before.EnvironmentIdentity == toolsOff.EnvironmentIdentity &&
              panel.CaptureSafeNavigationTarget() == sourceBefore,
            "TOOLS OFF changes only WPF toolbar visibility; page, WebView, and environment identities remain unchanged.");
        RaiseClick(tools);

        string logPath = Directory.GetFiles(diagnosticsRoot, "browser-session-*.jsonl").Single();
        await WaitUntilAsync(() =>
        {
            string content = File.ReadAllText(logPath);
            return content.Contains("javascript-exception", StringComparison.Ordinal) &&
                   content.Contains("console-error", StringComparison.Ordinal) &&
                   content.Contains("resource-failed", StringComparison.Ordinal) &&
                   content.Contains("synthetic uncaught exception", StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(20), "Expected JavaScript/console/resource diagnostic events were not captured.");
        string log = File.ReadAllText(logPath);
        Check(log.Contains("driftr-browser-session-diagnostic-v1", StringComparison.Ordinal) &&
              log.Contains("synthetic uncaught exception", StringComparison.Ordinal) &&
              log.Contains("[object Event]", StringComparison.Ordinal),
            "Opt-in diagnostics capture the underlying exception and console error text.");
        Check(!log.Contains("session-diagnostic-secret", StringComparison.Ordinal) &&
              !log.Contains("session-diagnostic-token", StringComparison.Ordinal) &&
              !log.Contains("C:/PokeQuad", StringComparison.OrdinalIgnoreCase) &&
              !log.Contains("requestHeaders", StringComparison.OrdinalIgnoreCase) &&
              !log.Contains("responseBody", StringComparison.OrdinalIgnoreCase),
            "Opt-in diagnostics redact fixture credentials and omit headers and bodies.");

        int[] pids = panel.CaptureMemoryDiagnostics(false).Processes.Select(process => process.ProcessId).ToArray();
        window.Close();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(45));
        await Task.Delay(3000);
        Check(pids.All(HasExited), "Session diagnostic shutdown leaves no isolated WebView2 process running.");
    }

    private static async Task RunRepairSessionProbeAsync(
        MainWindow window,
        TaskCompletionSource closed,
        bool freeEntitlement,
        string dataRoot,
        CacheFixtureServer cacheFixture)
    {
        int expectedSessions = freeEntitlement ? 1 : 4;
        BrowserPanel[] Panels() => Enumerable.Range(1, 4)
            .Select(account => (BrowserPanel)window.FindName($"Account{account}Panel"))
            .ToArray();
        WebView2 WebView(int account) => (WebView2)Panels()[account - 1].FindName("Browser");

        await WaitUntilAsync(() => Panels().Take(expectedSessions).All(panel => panel.IsBrowserInitialized),
            TimeSpan.FromSeconds(90), "Repair probe WebViews did not initialize.");
        await WaitUntilAsync(() => cacheFixture.CacheResourceRequests >= expectedSessions,
            TimeSpan.FromSeconds(30), "Initial cacheable resources were not requested.");
        await WaitUntilAsync(() => Panels().Take(expectedSessions).All(panel =>
        {
            AccountMemorySnapshot snapshot = panel.CaptureMemoryDiagnostics(false);
            return snapshot.BrowserProcessId.HasValue && snapshot.CoreEventHandlerCount == 4 &&
                   snapshot.WpfEventHandlerCount == 3;
        }), TimeSpan.FromSeconds(30), "Repair probe process snapshots were unavailable.");

        string[] sentinels = Enumerable.Range(1, 4).Select(account =>
        {
            string path = Path.Combine(dataRoot, "Profiles", $"Account{account}", "repair-persistent-sentinel.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, SHA256.HashData(Encoding.UTF8.GetBytes($"account-{account}-repair-sentinel")));
            return path;
        }).ToArray();
        string[] hashesBefore = sentinels.Select(HashFile).ToArray();

        foreach (int account in Enumerable.Range(1, expectedSessions))
        {
            await WebView(account).ExecuteScriptAsync(
                "document.cookie='repair_fixture_cookie=present; Max-Age=3600; Path=/; SameSite=Lax';" +
                "localStorage.setItem('repair_fixture_storage','present'); true;");
        }

        async Task<bool> PersistentStatePresentAsync(int account)
        {
            await WaitUntilAsync(() => WebView(account).CoreWebView2 is not null, TimeSpan.FromSeconds(15),
                $"Account {account} CoreWebView2 was unavailable for persistent-state verification.");
            string value = await WebView(account).ExecuteScriptAsync(
                "document.cookie.includes('repair_fixture_cookie=present') && " +
                "localStorage.getItem('repair_fixture_storage') === 'present'");
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        if (freeEntitlement)
        {
            bool lockedRejected = false;
            try { await window.RepairSessionAsync(2); }
            catch (InvalidOperationException) { lockedRejected = true; }
            Check(lockedRejected, "FREE cannot repair locked Account 2.");
            int cacheRequestsBefore = cacheFixture.CacheResourceRequests;
            SessionRepairResult result = await window.RepairSessionAsync(1);
            await WaitUntilAsync(() => cacheFixture.CacheResourceRequests > cacheRequestsBefore,
                TimeSpan.FromSeconds(15), "FREE repair did not refetch the cacheable resource.");
            Check(result.DiskCacheCleared && result.ProfilePathBefore == result.ProfilePathAfter &&
                  result.Shutdown.AllCapturedProcessesExited,
                "FREE repairs Account 1, clears DiskCache, preserves its profile path, and naturally stops old processes.");
            Check(await PersistentStatePresentAsync(1),
                "FREE repair preserves the isolated cookie and LocalStorage fixtures.");
            Check(hashesBefore.SequenceEqual(sentinels.Select(HashFile)),
                "FREE repair preserves all four isolated persistent sentinels.");
            int[] pids = Panels()[0].CaptureMemoryDiagnostics(false).Processes.Select(process => process.ProcessId).ToArray();
            window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(45));
            await Task.Delay(3000);
            Check(pids.All(HasExited), "FREE repair probe shutdown leaves no isolated WebView2 process running.");
            return;
        }

        var oldProcessIds = new HashSet<int>();
        foreach (int account in Enumerable.Range(1, 4))
        {
            AccountMemorySnapshot[] before = Panels().Select(panel => panel.CaptureMemoryDiagnostics(false)).ToArray();
            int cacheRequestsBefore = cacheFixture.CacheResourceRequests;
            SessionRepairResult result = await window.RepairSessionAsync(account);
            oldProcessIds.UnionWith(result.Shutdown.CapturedProcessIds);
            await WaitUntilAsync(() => cacheFixture.CacheResourceRequests > cacheRequestsBefore,
                TimeSpan.FromSeconds(15), $"Account {account} repair did not refetch the cacheable resource.");
            AccountMemorySnapshot[] after = Panels().Select(panel => panel.CaptureMemoryDiagnostics(false)).ToArray();
            Check(result.DiskCacheCleared && result.ProfilePathBefore == result.ProfilePathAfter &&
                  result.Shutdown.AllCapturedProcessesExited && !SameObjectIdentity(before[account - 1], after[account - 1]),
                $"Account {account} repair clears DiskCache, preserves its profile, replaces only its browser, and naturally stops old processes.");
            Check(before.Where((_, index) => index != account - 1)
                    .Zip(after.Where((_, index) => index != account - 1))
                    .All(pair => SameIdentity(pair.First, pair.Second)),
                $"Repairing Account {account} leaves the other three browser and environment identities unchanged.");
            Check(await PersistentStatePresentAsync(account),
                $"Account {account} repair preserves its isolated cookie and LocalStorage fixtures.");
        }
        foreach (int account in Enumerable.Range(1, 4))
            Check(await PersistentStatePresentAsync(account),
                $"Account {account} remains operational after the sequential Account1-4 repair sequence.");

        RaiseClick((Button)Panels()[3].FindName("PopOutButton"));
        await WaitUntilAsync(() => DetachedWindows().Any(item => item.AccountNumber == 4), TimeSpan.FromSeconds(10),
            "Account 4 did not detach for the docked flexible-layout repair test.");
        RaiseClick((Button)window.FindName("Account3Button"));
        RaiseClick((Button)window.FindName("ThreeLayoutButton"));
        string threeLayoutBefore = ((Button)window.FindName("ThreeLayoutButton")).Content?.ToString() ?? string.Empty;
        (int Row, int Column, int RowSpan, int ColumnSpan)[] placementsBefore = Panels().Take(3)
            .Select(panel => (Grid.GetRow(panel), Grid.GetColumn(panel), Grid.GetRowSpan(panel), Grid.GetColumnSpan(panel)))
            .ToArray();
        AccountMemorySnapshot detachedFourBefore = Panels()[3].CaptureMemoryDiagnostics(true);
        SessionRepairResult dockedThreeResult = await window.RepairSessionAsync(3);
        oldProcessIds.UnionWith(dockedThreeResult.Shutdown.CapturedProcessIds);
        (int Row, int Column, int RowSpan, int ColumnSpan)[] placementsAfter = Panels().Take(3)
            .Select(panel => (Grid.GetRow(panel), Grid.GetColumn(panel), Grid.GetRowSpan(panel), Grid.GetColumnSpan(panel)))
            .ToArray();
        Check(threeLayoutBefore == ((Button)window.FindName("ThreeLayoutButton")).Content?.ToString() &&
              placementsBefore.SequenceEqual(placementsAfter) && await PersistentStatePresentAsync(3),
            "Repairing docked Account 3 preserves the active flexible three-session layout and persistent storage.");
        Check(SameIdentity(detachedFourBefore, Panels()[3].CaptureMemoryDiagnostics(true)),
            "Repairing docked Account 3 leaves detached Account 4 unchanged.");
        RaiseClick((Button)DetachedWindows().Single(item => item.AccountNumber == 4).FindName("DockBackButton"));
        await WaitUntilAsync(() => DetachedWindows().All(item => item.AccountNumber != 4), TimeSpan.FromSeconds(10),
            "Account 4 did not dock after the flexible-layout repair test.");

        RaiseClick((Button)Panels()[2].FindName("PopOutButton"));
        await WaitUntilAsync(() => DetachedWindows().Any(item => item.AccountNumber == 3), TimeSpan.FromSeconds(10),
            "Account 3 did not detach for repair.");
        DetachedBrowserWindow detachedBefore = DetachedWindows().Single(item => item.AccountNumber == 3);
        Rect boundsBefore = detachedBefore.RestoreBounds;
        AccountMemorySnapshot[] detachedOthersBefore = Panels().Select(panel => panel.CaptureMemoryDiagnostics(panel.AccountNumber == 3)).ToArray();
        SessionRepairResult detachedResult = await window.RepairSessionAsync(3);
        oldProcessIds.UnionWith(detachedResult.Shutdown.CapturedProcessIds);
        DetachedBrowserWindow detachedAfter = DetachedWindows().Single(item => item.AccountNumber == 3);
        Check(detachedResult.WasDetached && !ReferenceEquals(detachedAfter, detachedBefore) &&
              ReferenceEquals(detachedAfter.Panel, Panels()[2]) && detachedAfter.RestoreBounds == boundsBefore,
            "Detached repair safely replaces the detached host while preserving bounds, state, and account ownership.");
        Check(detachedOthersBefore.Where((_, index) => index != 2)
                .Zip(Panels().Where((_, index) => index != 2).Select(panel => panel.CaptureMemoryDiagnostics(false)))
                .All(pair => SameIdentity(pair.First, pair.Second)),
            "Detached Account 3 repair leaves all other account identities unchanged.");
        Check(await PersistentStatePresentAsync(3),
            "Detached Account 3 repair preserves its cookie and LocalStorage fixtures.");
        RaiseClick((Button)detachedAfter.FindName("DockBackButton"));
        await WaitUntilAsync(() => DetachedWindows().All(item => item.AccountNumber != 3), TimeSpan.FromSeconds(10),
            "Repaired detached Account 3 did not dock back.");
        Check(await PersistentStatePresentAsync(3),
            "Docking repaired Account 3 back preserves a usable browser and persistent storage.");

        for (int cycle = 1; cycle <= 5; cycle++)
        {
            AccountMemorySnapshot[] before = Panels().Select(panel => panel.CaptureMemoryDiagnostics(false)).ToArray();
            Task<SessionRepairResult> repairTask = window.RepairSessionAsync(2);
            if (cycle == 1)
            {
                bool concurrentRejected = false;
                try { await window.RepairSessionAsync(3); }
                catch (InvalidOperationException) { concurrentRejected = true; }
                Check(concurrentRejected, "A concurrent second Repair Session operation is rejected.");
            }
            SessionRepairResult result = await repairTask;
            oldProcessIds.UnionWith(result.Shutdown.CapturedProcessIds);
            AccountMemorySnapshot[] after = Panels().Select(panel => panel.CaptureMemoryDiagnostics(false)).ToArray();
            Check(result.DiskCacheCleared && result.Shutdown.AllCapturedProcessesExited &&
                  !SameObjectIdentity(before[1], after[1]),
                $"Account 2 repair stress cycle {cycle} clears cache, replaces the target, and terminates old processes.");
            Check(before.Where((_, index) => index != 1).Zip(after.Where((_, index) => index != 1))
                      .All(pair => SameIdentity(pair.First, pair.Second)) &&
                  after.All(snapshot => snapshot.CoreEventHandlerCount == 4 && snapshot.WpfEventHandlerCount == 3),
                $"Account 2 repair stress cycle {cycle} preserves other identities and stable handler counts.");
            Check(await PersistentStatePresentAsync(2),
                $"Account 2 repair stress cycle {cycle} preserves cookie and LocalStorage fixtures.");
        }

        Check(hashesBefore.SequenceEqual(sentinels.Select(HashFile)),
            "All selected and non-selected profile sentinels retain identical SHA-256 hashes after repairs.");
        // Windows may reuse a numeric PID for a later, valid replacement environment during this
        // rapid stress sequence. Each repair already verifies its captured process group exited;
        // exclude PIDs now owned by the live replacement browsers from this aggregate PID check.
        HashSet<int> currentProcessIds = Panels()
            .SelectMany(panel => panel.CaptureMemoryDiagnostics(false).Processes)
            .Select(process => process.ProcessId)
            .ToHashSet();
        Check(oldProcessIds.Except(currentProcessIds).All(HasExited),
            "No retired environment process remains after sequential and repeated repairs.");

        foreach (string buttonName in new[] { "SoloButton", "DuoButton", "QuadButton" })
            RaiseClick((Button)window.FindName(buttonName));
        Check(Panels().All(panel => panel.IsBrowserInitialized),
            "Solo, Duo, Quad, and replacement panel references remain valid after repairs.");

        int[] livePids = Panels().SelectMany(panel => panel.CaptureMemoryDiagnostics(false).Processes)
            .Select(process => process.ProcessId).Distinct().ToArray();
        window.Close();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(60));
        await Task.Delay(3000);
        Check(livePids.All(HasExited), "PRO repair probe shutdown leaves no isolated WebView2 process running.");
    }

    private static async Task RunSessionRestartProbeAsync(
        MainWindow window,
        TaskCompletionSource closed,
        bool freeEntitlement,
        string dataRoot,
        string logRoot)
    {
        int expectedSessions = freeEntitlement ? 1 : 4;
        BrowserPanel[] InitialPanels() => Enumerable.Range(1, 4)
            .Select(account => (BrowserPanel)window.FindName($"Account{account}Panel"))
            .ToArray();

        await WaitUntilAsync(() => InitialPanels().Take(expectedSessions).All(panel => panel.IsBrowserInitialized),
            TimeSpan.FromSeconds(90), "Diagnostic restart WebViews did not initialize.");
        await WaitUntilAsync(() => InitialPanels().Take(expectedSessions)
                .All(panel =>
                {
                    AccountMemorySnapshot snapshot = panel.CaptureMemoryDiagnostics(false);
                    return snapshot.Processes.Count > 0 && snapshot.BrowserProcessId.HasValue &&
                           snapshot.CoreEventHandlerCount == 4 && snapshot.WpfEventHandlerCount == 3;
                }),
            TimeSpan.FromSeconds(30), "Diagnostic restart process snapshots were unavailable.");

        string[] sentinels = Enumerable.Range(1, 4).Select(account =>
        {
            string path = Path.Combine(dataRoot, "Profiles", $"Account{account}", "restart-sentinel.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"account-{account}-persistent-sentinel")));
            return path;
        }).ToArray();
        string[] hashesBefore = sentinels.Select(HashFile).ToArray();

        if (freeEntitlement)
        {
            bool lockedRejected = false;
            try { await window.DiagnosticRestartSessionAsync(2); }
            catch (InvalidOperationException) { lockedRejected = true; }
            Check(lockedRejected, "FREE cannot restart locked Account 2.");
            SessionRestartResult freeRestart = await window.DiagnosticRestartSessionAsync(1);
            Check(freeRestart.Shutdown.AllCapturedProcessesExited,
                "FREE can restart initialized Account 1 and its old process group exits naturally.");
            Check(hashesBefore.SequenceEqual(sentinels.Select(HashFile)),
                "FREE restart preserves every isolated profile sentinel.");
            int[] allPids = freeRestart.AfterPageLoad.Processes.Select(process => process.ProcessId).ToArray();
            window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(45));
            await Task.Delay(3000);
            Check(allPids.All(HasExited), "FREE diagnostic shutdown leaves zero isolated WebView2 processes.");
            return;
        }

        var allOldProcessIds = new HashSet<int>();
        foreach (int account in Enumerable.Range(1, 4))
        {
            AccountMemorySnapshot[] before = InitialPanels().Select(panel => panel.CaptureMemoryDiagnostics(false)).ToArray();
            SessionRestartResult result = await window.DiagnosticRestartSessionAsync(account);
            allOldProcessIds.UnionWith(result.Shutdown.CapturedProcessIds);
            AccountMemorySnapshot[] after = InitialPanels().Select(panel => panel.CaptureMemoryDiagnostics(false)).ToArray();
            Check(result.ProfilePathBefore == result.ProfilePathAfter &&
                  result.Shutdown.AllCapturedProcessesExited,
                $"Account {account} docked restart preserves its profile and naturally terminates its old process group.");
            Check(!SameObjectIdentity(before[account - 1], after[account - 1]),
                $"Account {account} receives new panel, WebView, and environment identities; browser-process outcome is recorded.");
            Check(before.Where((_, index) => index != account - 1)
                    .Zip(after.Where((_, index) => index != account - 1))
                    .All(pair => SameIdentity(pair.First, pair.Second)),
                $"Restarting Account {account} leaves the other three identities unchanged.");
            Check(after.All(snapshot => snapshot.CoreEventHandlerCount == 4 && snapshot.WpfEventHandlerCount == 3),
                $"Handler registration counts remain exactly 4 Core and 3 WPF after Account {account} restart.");
        }

        RaiseClick((Button)InitialPanels()[2].FindName("PopOutButton"));
        await WaitUntilAsync(() => DetachedWindows().Any(item => item.AccountNumber == 3), TimeSpan.FromSeconds(10),
            "Account 3 did not detach for restart.");
        DetachedBrowserWindow detachedBefore = DetachedWindows().Single(item => item.AccountNumber == 3);
        Rect boundsBefore = detachedBefore.RestoreBounds;
        SessionRestartResult detachedResult = await window.DiagnosticRestartSessionAsync(3);
        BrowserPanel detachedPanel = InitialPanels()[2];
        Check(detachedResult.WasDetached && DetachedWindows().Single(item => item.AccountNumber == 3) == detachedBefore &&
              ReferenceEquals(detachedBefore.Panel, detachedPanel),
            "Detached Account 3 restart replaces only its window content without closing or docking the window.");
        Check(detachedBefore.RestoreBounds == boundsBefore,
            "Detached restart preserves the detached window bounds and state.");
        RaiseClick((Button)detachedBefore.FindName("DockBackButton"));
        await WaitUntilAsync(() => DetachedWindows().All(item => item.AccountNumber != 3), TimeSpan.FromSeconds(10),
            "Restarted detached Account 3 did not dock back.");

        for (int cycle = 1; cycle <= 5; cycle++)
        {
            AccountMemorySnapshot[] before = InitialPanels().Select(panel => panel.CaptureMemoryDiagnostics(false)).ToArray();
            SessionRestartResult result = await window.DiagnosticRestartSessionAsync(2);
            allOldProcessIds.UnionWith(result.Shutdown.CapturedProcessIds);
            AccountMemorySnapshot[] after = InitialPanels().Select(panel => panel.CaptureMemoryDiagnostics(false)).ToArray();
            Check(!SameObjectIdentity(before[1], after[1]) && result.Shutdown.AllCapturedProcessesExited,
                $"Account 2 restart stress cycle {cycle} replaces the target and terminates its old processes.");
            Check(before.Where((_, index) => index != 1).Zip(after.Where((_, index) => index != 1))
                    .All(pair => SameIdentity(pair.First, pair.Second)) &&
                  after.All(snapshot => snapshot.CoreEventHandlerCount == 4 && snapshot.WpfEventHandlerCount == 3),
                $"Account 2 stress cycle {cycle} preserves other identities and stable handler counts.");
        }

        Check(hashesBefore.SequenceEqual(sentinels.Select(HashFile)),
            "All selected and non-selected isolated profile sentinels retain identical SHA-256 hashes.");
        Check(allOldProcessIds.All(HasExited), "No old environment process remains after repeated restart cycles.");
        Check(Directory.GetFiles(logRoot, "session-restarts-*.jsonl").Length == 1 &&
              File.ReadLines(Directory.GetFiles(logRoot, "session-restarts-*.jsonl").Single())
                  .Any(line => line.Contains("after-page-load", StringComparison.Ordinal) ||
                               line.Contains("navigation-failed", StringComparison.Ordinal)),
            "Restart diagnostics emit staged JSONL evidence.");

        foreach (string buttonName in new[] { "SoloButton", "DuoButton", "QuadButton" }) RaiseClick((Button)window.FindName(buttonName));
        Check(InitialPanels().All(panel => panel.IsBrowserInitialized),
            "Solo, Duo, and Quad remain valid after session replacements.");

        int[] liveProcessIds = InitialPanels().SelectMany(panel => panel.CaptureMemoryDiagnostics(false).Processes)
            .Select(process => process.ProcessId).Distinct().ToArray();
        window.Close();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(60));
        await Task.Delay(3000);
        Check(liveProcessIds.All(HasExited), "PRO diagnostic shutdown leaves zero isolated WebView2 processes.");
    }

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static async Task RunProbeAsync(
        MainWindow window,
        TaskCompletionSource closed,
        bool navigationStress,
        bool freeEntitlement,
        string logRoot)
    {
        BrowserPanel[] panels = Enumerable.Range(1, 4)
            .Select(account => (BrowserPanel)window.FindName($"Account{account}Panel"))
            .ToArray();
        int expectedSessions = freeEntitlement ? 1 : 4;
        await WaitUntilAsync(() => panels.Take(expectedSessions).All(panel => panel.IsBrowserInitialized), TimeSpan.FromSeconds(90),
            $"The expected {expectedSessions} WebViews did not initialize.");
        await Task.Delay(TimeSpan.FromSeconds(3));
        await WaitUntilAsync(() => panels.Take(expectedSessions).All(panel => panel.CaptureMemoryDiagnostics(false).Processes.Count > 0),
            TimeSpan.FromSeconds(30), "WebView2 process snapshots were unavailable.");

        AccountMemorySnapshot[] before = panels.Select(panel => panel.CaptureMemoryDiagnostics(false)).ToArray();
        AccountMemorySnapshot[] activeBefore = before.Take(expectedSessions).ToArray();
        Check(activeBefore.All(item => item.CoreEventHandlerCount == 4),
            "Each initialized account has exactly four CoreWebView2 event registrations.");
        Check(activeBefore.Select(item => item.BrowserControlIdentity).Distinct().Count() == expectedSessions &&
              activeBefore.Select(item => item.BrowserPanelIdentity).Distinct().Count() == expectedSessions &&
              activeBefore.Select(item => item.EnvironmentIdentity).Distinct().Count() == expectedSessions,
            $"The {expectedSessions} entitled accounts use stable, distinct panels, WebView controls, and environments.");
        Check(activeBefore.All(item => item.BrowserProcessId.HasValue),
            "Every initialized account exposes a supported browser process ID.");
        if (freeEntitlement)
        {
            Check(before[0].IsInitialized && before.Skip(1).All(item => !item.IsInitialized && item.Processes.Count == 0),
                "FREE initializes only Account 1 and creates no WebView2 processes for Accounts 2-4.");
            RaiseClick((Button)panels[0].FindName("PopOutButton"));
            await WaitUntilAsync(() => DetachedWindows().Count == 1, TimeSpan.FromSeconds(10), "FREE Account 1 did not detach.");
            DetachedWindows().Single().Close();
            await WaitUntilAsync(() => DetachedWindows().Count == 0, TimeSpan.FromSeconds(10), "FREE Account 1 did not dock on X.");
            Check(SameIdentity(before[0], panels[0].CaptureMemoryDiagnostics(false)),
                "FREE Account 1 pop-out/dock preserves the same browser session.");
            int[] freeProcessIds = before[0].Processes.Select(item => item.ProcessId).Distinct().ToArray();
            window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(45));
            await Task.Delay(3000);
            Check(freeProcessIds.All(HasExited), "FREE clean shutdown leaves no sampled WebView2 process running.");
            return;
        }

        if (navigationStress)
        {
            await Task.Delay(TimeSpan.FromSeconds(16));
            AccountMemorySnapshot[] afterNavigation = panels.Select(panel => panel.CaptureMemoryDiagnostics(false)).ToArray();
            Check(SameIdentity(before, afterNavigation),
                "Controlled local navigation cycles preserve browser controls, environments, and browser processes.");
        }

        RaiseClick((Button)panels[3].FindName("PopOutButton"));
        await WaitUntilAsync(() => DetachedWindows().Count == 1, TimeSpan.FromSeconds(10), "Account 4 did not detach.");
        AccountMemorySnapshot detachedFour = panels[3].CaptureMemoryDiagnostics(true);
        Check(SameIdentity(before[3], detachedFour) && detachedFour.CoreEventHandlerCount == 4,
            "Account 4 pop-out transfers the same browser/environment without adding handlers.");

        Button threeLayoutButton = (Button)window.FindName("ThreeLayoutButton");
        Check(HasPlacement(panels[0], 0, 0, 2, 1),
            "Three Large Left places the selected Account 1 on the full left side.");
        await Task.Delay(1100);
        RaiseClick(threeLayoutButton);
        Check(HasPlacement(panels[0], 0, 1, 2, 1),
            "Three Large Right places Account 1 on the full right side.");
        await Task.Delay(1100);
        RaiseClick(threeLayoutButton);
        Check(HasPlacement(panels[0], 0, 0, 1, 2),
            "Three Large Top places Account 1 across the full top.");
        await Task.Delay(1100);
        RaiseClick(threeLayoutButton);
        Check(HasPlacement(panels[0], 1, 0, 1, 2),
            "Three Large Bottom places Account 1 across the full bottom.");
        await Task.Delay(1100);
        RaiseClick((Button)window.FindName("Account3Button"));
        Check(HasPlacement(panels[2], 1, 0, 1, 2),
            "The existing Account 3 button selects Account 3 as the large docked browser.");
        RaiseClick(threeLayoutButton);
        Check(new[] { panels[0], panels[1], panels[2] }.Select(Grid.GetColumn).SequenceEqual([0, 1, 2]),
            "Three Columns creates three equal proportional columns.");
        await Task.Delay(1100);
        RaiseClick(threeLayoutButton);
        Check(new[] { panels[0], panels[1], panels[2] }.Select(Grid.GetRow).SequenceEqual([0, 1, 2]),
            "Three Rows creates three equal proportional rows.");
        await Task.Delay(1100);
        foreach (int unused in Enumerable.Range(0, 4)) RaiseClick(threeLayoutButton);
        Check(HasPlacement(panels[2], 1, 0, 1, 2),
            "Repeated layout cycling returns to Large Bottom while preserving Account 3 as large.");
        Check(SameIdentity(detachedFour, panels[3].CaptureMemoryDiagnostics(true)),
            "Switching every three-session layout leaves the detached Account 4 browser unaffected.");
        RaiseClick((Button)window.FindName("Account4Button"));
        await Task.Delay(250);
        Check(DetachedWindows().Count == 1 && SameIdentity(detachedFour, panels[3].CaptureMemoryDiagnostics(true)),
            "Selecting detached Account 4 focuses its window without docking or changing browser identity.");
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        window.UpdateLayout();
        Check(panels.Take(3).All(panel => panel.ActualWidth > 0 && panel.ActualHeight > 0) &&
              ((Grid)window.FindName("BrowserGrid")).ActualWidth > 0,
            "Three-session layouts remain bounded and usable at the minimum main-window size.");

        RaiseClick((Button)panels[1].FindName("PopOutButton"));
        await WaitUntilAsync(() => DetachedWindows().Count == 2, TimeSpan.FromSeconds(10), "Account 2 did not detach.");
        Check(HasPlacement(panels[0], 0, 0, 2, 1) && HasPlacement(panels[2], 0, 1, 2, 1),
            "Three-to-two transition uses Side by Side Duo orientation.");
        RaiseClick((Button)window.FindName("DuoOrientationButton"));
        Check(HasPlacement(panels[0], 0, 0, 1, 2) && HasPlacement(panels[2], 1, 0, 1, 2),
            "Two docked browsers switch to Stacked orientation.");
        DetachedBrowserWindow detachedTwo = DetachedWindows().Single(item => item.AccountNumber == 2);
        RaiseClick((Button)detachedTwo.FindName("DockBackButton"));
        await WaitUntilAsync(() => DetachedWindows().Count == 1, TimeSpan.FromSeconds(10), "Account 2 did not dock back.");
        Check(HasPlacement(panels[2], 1, 0, 1, 2),
            "Two-to-three transition restores Large Bottom and the preferred large account.");
        RaiseClick((Button)window.FindName("RestoreAllButton"));
        await WaitUntilAsync(() => DetachedWindows().Count == 0, TimeSpan.FromSeconds(10), "Account 4 did not restore.");
        Check(new[] { panels[0], panels[1], panels[2], panels[3] }
                .Select(panel => (Grid.GetRow(panel), Grid.GetColumn(panel)))
                .SequenceEqual([(0, 0), (0, 1), (1, 0), (1, 1)]),
            "Restoring the fourth account returns the main workspace to Quad.");

        RaiseClick((Button)panels[3].FindName("PopOutButton"));
        await WaitUntilAsync(() => DetachedWindows().Count == 1, TimeSpan.FromSeconds(10), "Account 4 did not detach for close-X test.");
        DetachedWindows().Single().Close();
        await WaitUntilAsync(() => DetachedWindows().Count == 0, TimeSpan.FromSeconds(10),
            "Closing Account 4 with X did not dock it.");

        for (int cycle = 0; cycle < 5; cycle++)
        {
            RaiseClick((Button)panels[0].FindName("PopOutButton"));
            await WaitUntilAsync(() => DetachedWindows().Count == 1, TimeSpan.FromSeconds(10),
                "Account 1 repeated pop-out failed.");
            RaiseClick((Button)DetachedWindows().Single().FindName("DockBackButton"));
            await WaitUntilAsync(() => DetachedWindows().Count == 0, TimeSpan.FromSeconds(10),
                "Account 1 repeated dock-back failed.");
        }
        Check(SameIdentity(before[0], panels[0].CaptureMemoryDiagnostics(false)) &&
              panels[0].CaptureMemoryDiagnostics(false).CoreEventHandlerCount == 4,
            "Five Account 1 pop-out/dock cycles preserve identity and handler count.");

        foreach (string buttonName in new[] { "SoloButton", "DuoButton", "QuadButton", "DuoButton", "SoloButton" })
        {
            RaiseClick((Button)window.FindName(buttonName));
            await Task.Delay(250);
        }
        Check(SameIdentity(before, panels.Select(panel => panel.CaptureMemoryDiagnostics(false)).ToArray()),
            "SOLO/DUO/QUAD layout transitions do not recreate WebViews or environments.");

        foreach (BrowserPanel panel in panels)
        {
            RaiseClick((Button)panel.FindName("PopOutButton"));
            await Task.Delay(250);
        }
        await WaitUntilAsync(() => DetachedWindows().Count == 4, TimeSpan.FromSeconds(10),
            "Four detached windows were not created.");
        Check(DetachedWindows().Select(item => item.AccountNumber).Order().SequenceEqual([1, 2, 3, 4]),
            "Four detached windows retain unique account ownership.");
        RaiseClick((Button)window.FindName("RestoreAllButton"));
        await WaitUntilAsync(() => DetachedWindows().Count == 0, TimeSpan.FromSeconds(10),
            "RESTORE ALL did not dock every account.");
        AccountMemorySnapshot[] restored = panels.Select(panel => panel.CaptureMemoryDiagnostics(false)).ToArray();
        Check(SameIdentity(before, restored) && restored.All(item => item.CoreEventHandlerCount == 4),
            "RESTORE ALL preserves all browser identities and event-registration counts.");

        int[] processIds = before.SelectMany(item => item.Processes).Select(item => item.ProcessId).Distinct().ToArray();
        window.Close();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(45));
        await Task.Delay(3000);
        Check(processIds.All(HasExited), "Clean shutdown leaves none of the sampled WebView2 processes running.");
        string samplePath = Directory.GetFiles(logRoot, "*.jsonl").Single();
        string[] loggedLayouts = File.ReadLines(samplePath)
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("Layout").GetString() ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        string[] expectedLayouts =
        [
            "ThreeLargeLeft", "ThreeLargeRight", "ThreeLargeTop", "ThreeLargeBottom", "ThreeColumns", "ThreeRows"
        ];
        Check(expectedLayouts.All(loggedLayouts.Contains),
            "Memory diagnostics record every flexible three-session layout name.");
    }

    private static List<DetachedBrowserWindow> DetachedWindows() =>
        Application.Current.Windows.OfType<DetachedBrowserWindow>().Where(window => window.IsLoaded).ToList();

    private static bool SameIdentity(IReadOnlyList<AccountMemorySnapshot> expected, IReadOnlyList<AccountMemorySnapshot> actual) =>
        expected.Count == actual.Count && expected.Zip(actual).All(pair => SameIdentity(pair.First, pair.Second));

    private static bool SameIdentity(AccountMemorySnapshot expected, AccountMemorySnapshot actual) =>
        expected.AccountNumber == actual.AccountNumber &&
        expected.BrowserPanelIdentity == actual.BrowserPanelIdentity &&
        expected.BrowserControlIdentity == actual.BrowserControlIdentity &&
        expected.EnvironmentIdentity == actual.EnvironmentIdentity &&
        expected.BrowserProcessId == actual.BrowserProcessId;

    private static bool SameObjectIdentity(AccountMemorySnapshot expected, AccountMemorySnapshot actual) =>
        expected.AccountNumber == actual.AccountNumber &&
        expected.BrowserPanelIdentity == actual.BrowserPanelIdentity &&
        expected.BrowserControlIdentity == actual.BrowserControlIdentity &&
        expected.EnvironmentIdentity == actual.EnvironmentIdentity;

    private static bool HasPlacement(BrowserPanel panel, int row, int column, int rowSpan, int columnSpan) =>
        Grid.GetRow(panel) == row && Grid.GetColumn(panel) == column &&
        Grid.GetRowSpan(panel) == rowSpan && Grid.GetColumnSpan(panel) == columnSpan;

    private static bool HasExited(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static void RaiseClick(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string failure)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed >= timeout) throw new TimeoutException(failure);
            await Task.Delay(100);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _assertions++;
        string result = $"PASS: {message}";
        Results.Add(result);
        Console.WriteLine(result);
    }

    private sealed class CacheFixtureServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _acceptLoop;
        private int _cacheResourceRequests;

        public CacheFixtureServer()
        {
            _listener.Start();
            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            HomeUrl = $"http://127.0.0.1:{port}/index.html";
            _acceptLoop = AcceptLoopAsync();
        }

        public string HomeUrl { get; }
        public int CacheResourceRequests => Volatile.Read(ref _cacheResourceRequests);

        public void Dispose()
        {
            _cancellation.Cancel();
            _listener.Stop();
            try { _acceptLoop.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException) { }
            _cancellation.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                try
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
                    _ = HandleClientAsync(client);
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) when (_cancellation.IsCancellationRequested) { return; }
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            using (NetworkStream stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true))
            {
                string? requestLine = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(requestLine)) return;
                string path = requestLine.Split(' ').ElementAtOrDefault(1) ?? "/";
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }

                byte[] body;
                string contentType;
                string cacheControl;
                string status = "200 OK";
                if (path.StartsWith("/cached-resource.js", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _cacheResourceRequests);
                    body = Encoding.UTF8.GetBytes("window.repairFixtureResourceLoaded = true;");
                    contentType = "application/javascript; charset=utf-8";
                    cacheControl = "public, max-age=3600, immutable";
                }
                else if (path.StartsWith("/index.html", StringComparison.Ordinal) || path == "/")
                {
                    body = Encoding.UTF8.GetBytes(
                        "<!doctype html><meta charset='utf-8'><title>DRIFTR repair cache fixture</title>" +
                        "<main>isolated repair cache fixture</main><script src='/cached-resource.js'></script>");
                    contentType = "text/html; charset=utf-8";
                    cacheControl = "no-store";
                }
                else
                {
                    status = "404 Not Found";
                    body = [];
                    contentType = "text/plain";
                    cacheControl = "no-store";
                }

                string headers = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\n" +
                                 $"Content-Length: {body.Length}\r\nCache-Control: {cacheControl}\r\n" +
                                 "Connection: close\r\n\r\n";
                byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
                await stream.WriteAsync(headerBytes, _cancellation.Token);
                await stream.WriteAsync(body, _cancellation.Token);
            }
        }
    }
}
