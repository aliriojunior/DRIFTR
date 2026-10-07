using System.Net;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using PokeQuad.Config;
using PokeQuad.Models;
using PokeQuad.Services;

string root = Path.Combine(Path.GetTempPath(), $"DRIFTR-tests-{Guid.NewGuid():N}");
string currentRoot = Path.Combine(root, "current");
string legacyRoot = Path.Combine(root, "legacy");
Environment.SetEnvironmentVariable("DRIFTR_DATA_ROOT", currentRoot);
Environment.SetEnvironmentVariable("DRIFTR_LEGACY_DATA_ROOT", legacyRoot);

try
{
    TestLicensingApiConfiguration();
    TestMemoryDiagnosticsConfiguration(Path.Combine(root, "memory-options"));
    TestBrowserSessionDiagnosticsConfiguration(Path.Combine(root, "session-diagnostics"));
    TestSessionRepairPolicy();
    TestSemanticVersionsAndReleaseSelection();
    TestUpdateStatePersistence(Path.Combine(root, "update-state"));
    await TestUpdateNetworkBehaviorAsync();
    await TestUpdateStartupIndependenceAsync();
    TestMemoryGrowthAnalysis();
    TestMemoryDiagnosticsLogger(Path.Combine(root, "memory-logger"));
    TestEntitlements();
    TestWorkspaceLayouts();
    TestFlexibleThreeSessionLayouts(Path.Combine(root, "three-layout-settings"));
    TestDuoOrientationPersistence(Path.Combine(root, "settings"));
    TestPopOutStateAndLayouts();
    TestDetachedWindowPersistence(Path.Combine(root, "detached-settings"));
    TestDeviceIdentity(Path.Combine(root, "identity"));
    TestProtectedSession(Path.Combine(root, "session"));
    await TestApiContractAsync();
    await TestBillingCheckoutContractAsync();
    TestBillingPresentationAndUrlValidation();
    await TestCheckoutAndAuthoritativeEntitlementRefreshAsync(Path.Combine(root, "billing-refresh"));
    await TestLoginStoresRefreshSessionAsync(Path.Combine(root, "login"));
    await TestAdaptiveProactiveWindowsAsync(Path.Combine(root, "adaptive-window"));
    await TestProactiveRefreshAndSingleFlightAsync(Path.Combine(root, "single-flight"));
    await TestUnauthorizedRetryAndRetryLimitAsync(Path.Combine(root, "retry"));
    await TestPermanentRefreshFailuresAsync(Path.Combine(root, "permanent"));
    await TestNetworkFailurePreservesSessionAsync(Path.Combine(root, "network"));
    await TestSignOutFlowsAsync(Path.Combine(root, "logout"));
    await TestDeactivationPreservesIdentityAndProfilesAsync(Path.Combine(root, "deactivate"));
    await TestEntitlementsAfterRefreshAsync(Path.Combine(root, "entitlements"));
    TestMigration(legacyRoot, currentRoot);
    Console.WriteLine("All DRIFTR tests passed.");
    return 0;
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, true);
}

static void TestSemanticVersionsAndReleaseSelection()
{
    SemanticVersion Parse(string value)
    {
        Assert(SemanticVersion.TryParse(value, out SemanticVersion? parsed), $"Semantic version {value} parses.");
        return parsed!;
    }
    bool HasUpdate(string current, string remote, bool remotePrerelease = false) =>
        UpdateChecker.SelectUpdate(Parse(current),
            [new PublishedRelease($"v{remote}", false, remotePrerelease,
                new Uri($"https://github.com/{AppConfig.UpdateOwner}/{AppConfig.UpdateRepository}/releases/tag/v{remote}"))]) is not null;

    Assert(HasUpdate("1.1.0-rc.3", "1.1.0-rc.5", true), "RC.3 detects RC.5 as an update.");
    Assert(HasUpdate("1.1.0-rc.5", "1.1.0"), "RC.5 detects the matching stable release as an update.");
    Assert(!HasUpdate("1.1.0", "1.1.0-rc.6", true), "Stable 1.1.0 ignores prerelease RC.6.");
    Assert(HasUpdate("1.1.0", "1.1.1"), "Stable 1.1.0 detects 1.1.1.");
    Assert(HasUpdate("1.1.9", "1.2.0"), "1.1.9 detects 1.2.0.");
    Assert(HasUpdate("1.9.0", "1.10.0"), "Semantic comparison orders 1.10.0 after 1.9.0.");
    Assert(HasUpdate("1.10.0", "2.0.0"), "1.10.0 detects 2.0.0.");
    Assert(!HasUpdate("1.1.0", "1.1.0"), "The same version is not an update.");
    Assert(!HasUpdate("1.1.1", "1.1.0"), "An older remote version is not an update.");

    SemanticVersion current = Parse("1.1.0-rc.5");
    Uri official = new("https://github.com/aliriojunior/DRIFTR/releases/tag/v1.1.0-rc.6");
    Assert(UpdateChecker.SelectUpdate(current, [new PublishedRelease("v1.1.0-rc.6", true, true, official)]) is null,
        "Draft releases are ignored.");
    Assert(UpdateChecker.SelectUpdate(current, [new PublishedRelease("newest", false, false, official)]) is null,
        "Malformed release tags are ignored.");
    Assert(UpdateChecker.SelectUpdate(current,
        [new PublishedRelease("v1.1.0-rc.6", false, true, new Uri("https://evil.example/releases/tag/v1.1.0-rc.6"))]) is null,
        "A release with an unexpected HTML URL is rejected.");
    Assert(UpdateChecker.IsOfficialReleaseUrl(official) &&
           !UpdateChecker.IsOfficialReleaseUrl(new Uri("http://github.com/aliriojunior/DRIFTR/releases/tag/v1")) &&
           !UpdateChecker.IsOfficialReleaseUrl(new Uri("https://github.com/another/DRIFTR/releases/tag/v1")),
        "Release URL validation requires HTTPS and the configured official repository.");
    DateTimeOffset now = DateTimeOffset.UtcNow;
    Assert(UpdateChecker.ShouldCheckAutomatically(null, now) &&
           !UpdateChecker.ShouldCheckAutomatically(now.AddHours(-23), now) &&
           UpdateChecker.ShouldCheckAutomatically(now.AddHours(-24), now),
        "Automatic checks are limited to once per 24 hours.");
    SemanticVersion next = Parse("1.1.0-rc.6");
    Assert(!UpdateChecker.ShouldNotifyAutomatically("1.1.0-rc.6", next) &&
           UpdateChecker.ShouldNotifyAutomatically("1.1.0-rc.5", next),
        "Automatic notifications suppress the same version but allow a newer version.");
}

static void TestUpdateStatePersistence(string path)
{
    var service = new SettingsService(path);
    DateTimeOffset checkedAt = DateTimeOffset.UtcNow;
    service.Save(new AppSettings { LastUpdateCheckUtc = checkedAt, LastNotifiedVersion = "1.1.0-rc.6" });
    AppSettings loaded = service.Load();
    Assert(loaded.LastUpdateCheckUtc == checkedAt && loaded.LastNotifiedVersion == "1.1.0-rc.6",
        "Minimal update-check timestamp and last-notified version persist in settings.");
}

static async Task TestUpdateNetworkBehaviorAsync()
{
    const string stableJson = "[{\"tag_name\":\"v1.1.1\",\"draft\":false,\"prerelease\":false,\"html_url\":\"https://github.com/aliriojunior/DRIFTR/releases/tag/v1.1.1\"}]";
    var successHandler = new UpdateHttpHandler((_, _) => Task.FromResult(UpdateJsonResponse(HttpStatusCode.OK, stableJson)));
    using (var provider = new GitHubReleaseMetadataProvider(new HttpClient(successHandler), TimeSpan.FromSeconds(1)))
    {
        IReadOnlyList<PublishedRelease> releases = await provider.GetReleasesAsync(CancellationToken.None);
        Assert(releases.Count == 1 && releases[0].TagName == "v1.1.1", "GitHub release metadata parses successfully.");
        HttpRequestMessage captured = successHandler.LastRequest!;
        Assert(captured.RequestUri == AppConfig.UpdateReleasesApiUri && captured.Headers.Authorization is null,
            "Update requests use only the configured public releases endpoint without authentication.");
        Assert(captured.Headers.Accept.Any(value => value.MediaType == "application/vnd.github+json") &&
               captured.Headers.UserAgent.ToString() == "DRIFTR/1.1.0" &&
               captured.Headers.Contains("X-GitHub-Api-Version"),
            "Update requests send the required GitHub REST headers.");
    }

    async Task<UpdateCheckStatus> StatusFor(HttpStatusCode status, string body = "[]")
    {
        using var provider = new GitHubReleaseMetadataProvider(
            new HttpClient(new UpdateHttpHandler((_, _) => Task.FromResult(UpdateJsonResponse(status, body)))), TimeSpan.FromSeconds(1));
        return (await new UpdateChecker(provider).CheckAsync()).Status;
    }
    Assert(await StatusFor(HttpStatusCode.Forbidden) == UpdateCheckStatus.Failed, "GitHub HTTP 403 is a non-crashing check failure.");
    Assert(await StatusFor(HttpStatusCode.NotFound) == UpdateCheckStatus.Failed, "GitHub HTTP 404 is a non-crashing check failure.");
    Assert(await StatusFor(HttpStatusCode.InternalServerError) == UpdateCheckStatus.Failed, "GitHub HTTP 500 is a non-crashing check failure.");
    using (var offlineProvider = new GitHubReleaseMetadataProvider(
               new HttpClient(new UpdateHttpHandler((_, _) => throw new HttpRequestException("offline"))), TimeSpan.FromSeconds(1)))
        Assert((await new UpdateChecker(offlineProvider).CheckAsync()).Status == UpdateCheckStatus.Failed,
            "An offline network failure does not crash the update checker.");
    Assert(await StatusFor(HttpStatusCode.OK, "not-json") == UpdateCheckStatus.Failed, "Malformed release JSON is a non-crashing check failure.");
    Assert(await StatusFor(HttpStatusCode.OK) == UpdateCheckStatus.UpToDate, "An empty release list reports up to date.");
    Assert(await StatusFor(HttpStatusCode.OK,
        "[{\"tag_name\":\"v9.0.0\",\"draft\":true,\"prerelease\":false,\"html_url\":\"https://github.com/aliriojunior/DRIFTR/releases/tag/v9.0.0\"}]") == UpdateCheckStatus.UpToDate,
        "A draft-only release list reports up to date.");

    using (var timeoutProvider = new GitHubReleaseMetadataProvider(
               new HttpClient(new UpdateHttpHandler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return UpdateJsonResponse(HttpStatusCode.OK, "[]"); })),
               TimeSpan.FromMilliseconds(25)))
        Assert((await new UpdateChecker(timeoutProvider).CheckAsync()).Status == UpdateCheckStatus.Failed,
            "A timed-out update request is a non-crashing check failure.");

    using var cancellationProvider = new GitHubReleaseMetadataProvider(
        new HttpClient(new UpdateHttpHandler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return UpdateJsonResponse(HttpStatusCode.OK, "[]"); })),
        TimeSpan.FromSeconds(5));
    using var cancellation = new CancellationTokenSource(25);
    await AssertThrowsAsync<OperationCanceledException>(() => new UpdateChecker(cancellationProvider).CheckAsync(cancellation.Token));
    Console.WriteLine("PASS: Caller cancellation stops the update request.");
}

static async Task TestUpdateStartupIndependenceAsync()
{
    using var provider = new GitHubReleaseMetadataProvider(
        new HttpClient(new UpdateHttpHandler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return UpdateJsonResponse(HttpStatusCode.OK, "[]"); })),
        TimeSpan.FromSeconds(5));
    using var cancellation = new CancellationTokenSource();
    var stopwatch = Stopwatch.StartNew();
    Task<UpdateCheckResult> pending = new UpdateChecker(provider).CheckAsync(cancellation.Token);
    stopwatch.Stop();
    Assert(stopwatch.Elapsed < TimeSpan.FromMilliseconds(250) && !pending.IsCompleted,
        "A slow update provider does not synchronously delay usable startup work.");
    cancellation.Cancel();
    await AssertThrowsAsync<OperationCanceledException>(() => pending);
}

static HttpResponseMessage UpdateJsonResponse(HttpStatusCode status, string json) => new(status)
{
    Content = new StringContent(json, Encoding.UTF8, "application/json")
};

static void TestEntitlements()
{
    Assert(EntitlementPolicy.NormalizeMaxSessions(1) == 1, "Free entitlement normalizes to one.");
    Assert(EntitlementPolicy.NormalizeMaxSessions(4) == 4, "Pro entitlement normalizes to four.");
    Assert(!EntitlementPolicy.CanUseAccount(2, 1), "Free cannot access account two.");
    Assert(!EntitlementPolicy.CanUseLayout(LayoutMode.Quad, 1), "Free cannot use Quad.");
    Assert(EntitlementPolicy.CanUseLayout(LayoutMode.Duo, 4), "Pro can use Duo.");
    Assert(EntitlementPolicy.EnforceLayout(LayoutMode.Quad, 1) == LayoutMode.Solo, "Free layout falls back to Solo.");
    Assert(Enumerable.Range(2, 3).All(account => !EntitlementPolicy.CanUseAccount(account, 1)),
        "Free cannot enable Accounts 2-4.");
    var refreshedState = new PopOutSessionState(1);
    refreshedState.UpdateMaxSessions(4);
    Assert(refreshedState.DockedAccounts.Count == 4 && refreshedState.CanDetach(4),
        "A refreshed Pro entitlement exposes all four session slots immediately.");
}

static void TestLicensingApiConfiguration()
{
    Assert(AppConfig.ResolveLicensingApiBaseUri(null) == new Uri("https://driftr-licensing-api.onrender.com/"),
        "Production licensing URL is the default.");
    Assert(AppConfig.ResolveLicensingApiBaseUri("https://license.example.com") == new Uri("https://license.example.com/"),
        "A production override must use HTTPS.");
    AssertThrows<InvalidOperationException>(() =>
        AppConfig.ResolveLicensingApiBaseUri("http://127.0.0.1:8000", allowInsecureLoopback: false));
    AssertThrows<InvalidOperationException>(() =>
        AppConfig.ResolveLicensingApiBaseUri("http://license.example.com", allowInsecureLoopback: false));
#if DEBUG
    Assert(AppConfig.ResolveLicensingApiBaseUri("http://127.0.0.1:8000") == new Uri("http://127.0.0.1:8000/"),
        "A Debug build enables its loopback development override.");
#else
    AssertThrows<InvalidOperationException>(() => AppConfig.ResolveLicensingApiBaseUri("http://127.0.0.1:8000"));
#endif
    Assert(AppConfig.ResolveLicensingApiBaseUri("http://127.0.0.1:8000", allowInsecureLoopback: true) ==
           new Uri("http://127.0.0.1:8000/"),
        "An explicitly enabled development build can use an HTTP loopback endpoint.");
    Assert(AppConfig.ResolveLicensingApiBaseUri("http://localhost:8000", allowInsecureLoopback: true) ==
           new Uri("http://localhost:8000/"),
        "An explicitly enabled development build can use localhost.");
    AssertThrows<InvalidOperationException>(() =>
        AppConfig.ResolveLicensingApiBaseUri("http://license.example.com", allowInsecureLoopback: true));
    AssertThrows<InvalidOperationException>(() => AppConfig.ResolveLicensingApiBaseUri("not-a-url"));
    Assert(AppConfig.ResolveLicensingApiBaseUri("https://example.com///").AbsoluteUri == "https://example.com/",
        "Licensing URL trailing slash is normalized.");
}

static void TestMemoryDiagnosticsConfiguration(string path)
{
    string? previousEnabled = Environment.GetEnvironmentVariable("DRIFTR_MEMORY_DIAGNOSTICS");
    string? previousInterval = Environment.GetEnvironmentVariable("DRIFTR_MEMORY_DIAGNOSTICS_INTERVAL_SECONDS");
    string? previousRoot = Environment.GetEnvironmentVariable("DRIFTR_MEMORY_DIAGNOSTICS_ROOT");
    string? previousScenario = Environment.GetEnvironmentVariable("DRIFTR_MEMORY_DIAGNOSTICS_SCENARIO");
    try
    {
        Environment.SetEnvironmentVariable("DRIFTR_MEMORY_DIAGNOSTICS", null);
        Environment.SetEnvironmentVariable("DRIFTR_MEMORY_DIAGNOSTICS_INTERVAL_SECONDS", null);
        Environment.SetEnvironmentVariable("DRIFTR_MEMORY_DIAGNOSTICS_ROOT", path);
        Environment.SetEnvironmentVariable("DRIFTR_MEMORY_DIAGNOSTICS_SCENARIO", null);
        MemoryDiagnosticsOptions disabled = MemoryDiagnosticsOptions.FromEnvironment();
        Assert(!disabled.Enabled && disabled.SamplingInterval == TimeSpan.FromSeconds(60),
            "Memory diagnostics are disabled by default with a 60-second interval.");

        Environment.SetEnvironmentVariable("DRIFTR_MEMORY_DIAGNOSTICS", "1");
        Environment.SetEnvironmentVariable("DRIFTR_MEMORY_DIAGNOSTICS_INTERVAL_SECONDS", "15");
        Environment.SetEnvironmentVariable("DRIFTR_MEMORY_DIAGNOSTICS_SCENARIO", "layout-stress");
        MemoryDiagnosticsOptions enabled = MemoryDiagnosticsOptions.FromEnvironment();
        Assert(enabled.Enabled && enabled.SamplingInterval == TimeSpan.FromSeconds(15) &&
               enabled.OutputDirectory == Path.GetFullPath(path) && enabled.Scenario == "layout-stress",
            "Memory diagnostics honor explicit interval, isolated output root, and scenario label.");

        Environment.SetEnvironmentVariable("DRIFTR_MEMORY_DIAGNOSTICS_INTERVAL_SECONDS", "0");
        Assert(MemoryDiagnosticsOptions.FromEnvironment().SamplingInterval == TimeSpan.FromSeconds(1),
            "Memory diagnostic sampling interval is safely clamped.");
        Assert(AppConfig.ResolveBrowserHomeUrl(null, "file:///unsafe-override.html") == AppConfig.DefaultUrl,
            "Browser URL override is ignored when diagnostics are disabled.");
        Assert(AppConfig.ResolveBrowserHomeUrl("1", "file:///controlled-test.html").StartsWith("file:", StringComparison.Ordinal),
            "Diagnostics can use a controlled local test page.");
        AssertThrows<InvalidOperationException>(() => AppConfig.ResolveBrowserHomeUrl("1", "javascript:alert(1)"));
    }
    finally
    {
        Environment.SetEnvironmentVariable("DRIFTR_MEMORY_DIAGNOSTICS", previousEnabled);
        Environment.SetEnvironmentVariable("DRIFTR_MEMORY_DIAGNOSTICS_INTERVAL_SECONDS", previousInterval);
        Environment.SetEnvironmentVariable("DRIFTR_MEMORY_DIAGNOSTICS_ROOT", previousRoot);
        Environment.SetEnvironmentVariable("DRIFTR_MEMORY_DIAGNOSTICS_SCENARIO", previousScenario);
    }
}

static void TestMemoryGrowthAnalysis()
{
    MemoryScopeSummary plateau = MemoryGrowthAnalyzer.Summarize("plateau",
        new (double, long)[] { (0, 100), (120, 104), (240, 102), (360, 103), (480, 101), (600, 102) });
    Assert(plateau.Classification == "Stable plateau" && plateau.PeakBytes == 104,
        "Memory summary identifies a stable plateau and retains the peak.");

    MemoryScopeSummary warmup = MemoryGrowthAnalyzer.Summarize("warmup",
        new (double, long)[] { (0, 100), (120, 140), (240, 150), (360, 151), (480, 150), (600, 151) });
    Assert(warmup.Classification == "Initial warm-up then plateau",
        "Memory summary distinguishes initial warm-up from continuing growth.");

    MemoryScopeSummary growth = MemoryGrowthAnalyzer.Summarize("growth",
        new (double, long)[] { (0, 100), (120, 115), (240, 130), (360, 145), (480, 160), (600, 175) });
    Assert(growth.Classification == "Sustained growth signal" && growth.GrowthBytes == 75,
        "Memory summary labels monotonic growth as a diagnostic signal rather than proof of a leak.");

    MemoryScopeSummary shortRun = MemoryGrowthAnalyzer.Summarize("short",
        new (double, long)[] { (0, 100), (60, 150) });
    Assert(shortRun.Classification == "Inconclusive" && shortRun.AverageGrowthBytesPerHour == 3000,
        "Short runs remain inconclusive while reporting average hourly growth.");
}

static void TestBrowserSessionDiagnosticsConfiguration(string path)
{
    string? previousEnabled = Environment.GetEnvironmentVariable("DRIFTR_SESSION_DIAGNOSTICS");
    string? previousAccount = Environment.GetEnvironmentVariable("DRIFTR_SESSION_DIAGNOSTICS_ACCOUNT");
    string? previousRoot = Environment.GetEnvironmentVariable("DRIFTR_SESSION_DIAGNOSTICS_ROOT");
    try
    {
        Environment.SetEnvironmentVariable("DRIFTR_SESSION_DIAGNOSTICS", null);
        Environment.SetEnvironmentVariable("DRIFTR_SESSION_DIAGNOSTICS_ACCOUNT", "3");
        Environment.SetEnvironmentVariable("DRIFTR_SESSION_DIAGNOSTICS_ROOT", path);
        BrowserSessionDiagnosticsOptions disabled = BrowserSessionDiagnosticsOptions.FromEnvironment();
        Assert(!disabled.Enabled && !disabled.AppliesTo(3) && !Directory.Exists(path),
            "Browser-session diagnostics are disabled by default and create no output directory.");

        Environment.SetEnvironmentVariable("DRIFTR_SESSION_DIAGNOSTICS", "1");
        BrowserSessionDiagnosticsOptions enabled = BrowserSessionDiagnosticsOptions.FromEnvironment();
        Assert(enabled.Enabled && enabled.AppliesTo(3) && !enabled.AppliesTo(2) &&
               enabled.OutputDirectory == Path.GetFullPath(path),
            "Browser-session diagnostics require an explicit valid account and isolated output root.");

        Environment.SetEnvironmentVariable("DRIFTR_SESSION_DIAGNOSTICS_ACCOUNT", "all");
        Assert(!BrowserSessionDiagnosticsOptions.FromEnvironment().Enabled,
            "Browser-session diagnostics reject missing or non-numeric all-session capture.");

        string sanitized = BrowserSessionDiagnostics.SanitizeDiagnosticText(
            "failed password=hunter2 access_token=abcdef bearer:secret user@example.com eyJabcdefghijk.abcdefghijklmnop.qrstuvwxyz123 at file:///C:/private/path/page.html")!;
        Assert(!sanitized.Contains("hunter2", StringComparison.Ordinal) &&
               !sanitized.Contains("abcdef", StringComparison.Ordinal) &&
               !sanitized.Contains("user@example.com", StringComparison.Ordinal) &&
               !sanitized.Contains("eyJabcdefghijk", StringComparison.Ordinal) &&
               !sanitized.Contains("C:/private/path", StringComparison.OrdinalIgnoreCase) &&
               sanitized.Contains("page.html", StringComparison.Ordinal),
            "Browser-session diagnostic text redacts credentials and reduces source URLs to origin plus filename.");
    }
    finally
    {
        Environment.SetEnvironmentVariable("DRIFTR_SESSION_DIAGNOSTICS", previousEnabled);
        Environment.SetEnvironmentVariable("DRIFTR_SESSION_DIAGNOSTICS_ACCOUNT", previousAccount);
        Environment.SetEnvironmentVariable("DRIFTR_SESSION_DIAGNOSTICS_ROOT", previousRoot);
    }
}

static void TestSessionRepairPolicy()
{
    Assert(SessionRepairPolicy.BrowsingDataToClear == Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds.DiskCache,
        "Repair Session clears exactly the supported WebView2 DiskCache category.");
    var persistentKinds = Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds.Cookies |
                          Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds.LocalStorage |
                          Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds.IndexedDb |
                          Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds.AllDomStorage |
                          Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds.AllSite |
                          Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds.AllProfile |
                          Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds.PasswordAutosave |
                          Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds.Settings |
                          Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds.ServiceWorkers |
                          Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds.CacheStorage;
    Assert((SessionRepairPolicy.BrowsingDataToClear & persistentKinds) == 0,
        "Repair Session excludes cookies, DOM storage, profile settings, service workers, and cache storage.");
    Assert(SessionRepairPolicy.ConfirmationTitle(3) == "Repair Account 3?",
        "Repair confirmation identifies the selected account.");
    Assert(SessionRepairPolicy.ConfirmationMessage.Contains("other DRIFTR sessions will not be affected", StringComparison.Ordinal) &&
           SessionRepairPolicy.ConfirmationMessage.Contains("may require you to sign in again", StringComparison.Ordinal),
        "Repair confirmation explains isolation and does not guarantee login preservation.");
}

static void TestMemoryDiagnosticsLogger(string path)
{
    var disabledOptions = new MemoryDiagnosticsOptions(false, TimeSpan.FromSeconds(60), path, "disabled-test");
    MemoryDiagnosticsService? disabled = MemoryDiagnosticsService.TryCreate(
        Dispatcher.CurrentDispatcher, () => [], () => "Solo", disabledOptions);
    Assert(disabled is null && !Directory.Exists(path),
        "Disabled diagnostics create no timer, service, or output directory.");

    var process = new WebViewProcessMemorySnapshot(43210, "Renderer", 20_000_000, 18_000_000);
    var account = new AccountMemorySnapshot(1, true, false, true, 51, 101, 201, 43210, 4,
        "test environment", [process]);
    var enabledOptions = new MemoryDiagnosticsOptions(true, TimeSpan.FromSeconds(60), path, "logger-test");
    using MemoryDiagnosticsService logger = MemoryDiagnosticsService.TryCreate(
        Dispatcher.CurrentDispatcher, () => [account], () => "Solo", enabledOptions)!;
    logger.Start();
    logger.CaptureSample();
    logger.Stop();

    string[] lines = File.ReadAllLines(logger.SamplePath);
    Assert(lines.Length >= 2 && lines.All(line => line.Contains("driftr-memory-sample-v1", StringComparison.Ordinal)),
        "Enabled diagnostics write versioned JSONL samples.");
    string summary = File.ReadAllText(logger.SummaryPath);
    Assert(summary.Contains("driftr-memory-summary-v1", StringComparison.Ordinal) &&
           summary.Contains("WebView2 total", StringComparison.Ordinal) &&
           summary.Contains("Account 1 environment", StringComparison.Ordinal),
        "Diagnostics write host, WebView2-total, and per-environment summaries.");
    Assert(!summary.Contains("token", StringComparison.OrdinalIgnoreCase) &&
           !string.Join('\n', lines).Contains("url", StringComparison.OrdinalIgnoreCase),
        "Memory diagnostic output contains no token or browser URL fields.");
}

static void TestWorkspaceLayouts()
{
    Assert(new AppSettings().DuoOrientation == nameof(DuoOrientation.Horizontal),
        "Duo defaults to Side by Side.");

    IReadOnlyList<PanelPlacement> horizontal = WorkspaceLayoutPolicy.Duo(1, 4, DuoOrientation.Horizontal);
    Assert(horizontal[0] == new PanelPlacement(true, 0, 0, 2, 1) &&
           horizontal[3] == new PanelPlacement(true, 0, 1, 2, 1),
        "Pro Duo Side by Side uses equal left and right columns.");

    IReadOnlyList<PanelPlacement> vertical = WorkspaceLayoutPolicy.Duo(1, 4, DuoOrientation.Vertical);
    Assert(vertical[0] == new PanelPlacement(true, 0, 0, 1, 2) &&
           vertical[3] == new PanelPlacement(true, 1, 0, 1, 2),
        "Pro Duo Stacked uses equal top and bottom rows.");

    int[] horizontalPair = horizontal.Select((placement, index) => (placement, index))
        .Where(item => item.placement.Visible).Select(item => item.index + 1).ToArray();
    int[] verticalPair = vertical.Select((placement, index) => (placement, index))
        .Where(item => item.placement.Visible).Select(item => item.index + 1).ToArray();
    Assert(horizontalPair.SequenceEqual(verticalPair) && horizontalPair.SequenceEqual([1, 4]),
        "Selected Duo account pair survives orientation changes.");

    IReadOnlyList<PanelPlacement> solo = WorkspaceLayoutPolicy.Solo(3);
    Assert(solo.Count(item => item.Visible) == 1 && solo[2] == new PanelPlacement(true, 0, 0, 2, 2),
        "Solo remains one full-workspace panel.");

    IReadOnlyList<PanelPlacement> quad = WorkspaceLayoutPolicy.Quad();
    Assert(quad.SequenceEqual(new[]
    {
        new PanelPlacement(true, 0, 0, 1, 1),
        new PanelPlacement(true, 0, 1, 1, 1),
        new PanelPlacement(true, 1, 0, 1, 1),
        new PanelPlacement(true, 1, 1, 1, 1)
    }), "Quad remains a 2x2 layout.");
}

static void TestFlexibleThreeSessionLayouts(string settingsPath)
{
    Assert(new AppSettings().ThreeSessionLayout == nameof(ThreeSessionLayout.ThreeLargeLeft) &&
           new AppSettings().ThreeLargeAccount == 1,
        "Three-session layout defaults to Large Left with Account 1 preferred.");

    int[] docked = [1, 2, 4];
    IReadOnlyList<PanelPlacement> left = WorkspaceLayoutPolicy.Three(docked, 4, ThreeSessionLayout.ThreeLargeLeft);
    Assert(left[3] == new PanelPlacement(true, 0, 0, 2, 1) &&
           left[0] == new PanelPlacement(true, 0, 1, 1, 1) &&
           left[1] == new PanelPlacement(true, 1, 1, 1, 1),
        "Three Large Left uses the chosen account on the full left side.");

    IReadOnlyList<PanelPlacement> right = WorkspaceLayoutPolicy.Three(docked, 4, ThreeSessionLayout.ThreeLargeRight);
    Assert(right[3] == new PanelPlacement(true, 0, 1, 2, 1) &&
           right[0] == new PanelPlacement(true, 0, 0, 1, 1) &&
           right[1] == new PanelPlacement(true, 1, 0, 1, 1),
        "Three Large Right uses the chosen account on the full right side.");

    IReadOnlyList<PanelPlacement> top = WorkspaceLayoutPolicy.Three(docked, 4, ThreeSessionLayout.ThreeLargeTop);
    Assert(top[3] == new PanelPlacement(true, 0, 0, 1, 2) &&
           top[0] == new PanelPlacement(true, 1, 0, 1, 1) &&
           top[1] == new PanelPlacement(true, 1, 1, 1, 1),
        "Three Large Top uses the chosen account across the full top.");

    IReadOnlyList<PanelPlacement> bottom = WorkspaceLayoutPolicy.Three(docked, 4, ThreeSessionLayout.ThreeLargeBottom);
    Assert(bottom[3] == new PanelPlacement(true, 1, 0, 1, 2) &&
           bottom[0] == new PanelPlacement(true, 0, 0, 1, 1) &&
           bottom[1] == new PanelPlacement(true, 0, 1, 1, 1),
        "Three Large Bottom uses the chosen account across the full bottom.");

    IReadOnlyList<PanelPlacement> columns = WorkspaceLayoutPolicy.Three(docked, 4, ThreeSessionLayout.ThreeColumns);
    Assert(columns[0].Column == 0 && columns[1].Column == 1 && columns[3].Column == 2 &&
           columns.Where(item => item.Visible).All(item => item.Row == 0 && item.RowSpan == 1),
        "Three Columns preserves docked-account order in equal columns.");

    IReadOnlyList<PanelPlacement> rows = WorkspaceLayoutPolicy.Three(docked, 4, ThreeSessionLayout.ThreeRows);
    Assert(rows[0].Row == 0 && rows[1].Row == 1 && rows[3].Row == 2 &&
           rows.Where(item => item.Visible).All(item => item.Column == 0 && item.ColumnSpan == 1),
        "Three Rows preserves docked-account order in equal rows.");

    foreach (int detached in Enumerable.Range(1, 4))
    {
        int[] remaining = Enumerable.Range(1, 4).Where(account => account != detached).ToArray();
        int large = remaining[^1];
        IReadOnlyList<PanelPlacement> placements = WorkspaceLayoutPolicy.Three(
            remaining, large, ThreeSessionLayout.ThreeLargeLeft);
        Assert(!placements[detached - 1].Visible && placements[large - 1] == new PanelPlacement(true, 0, 0, 2, 1) &&
               placements.Count(item => item.Visible) == 3,
            $"Three-session layout handles Account {detached} detached without changing account identity.");
    }

    Assert(WorkspaceLayoutPolicy.Three([1, 3, 4], 3, ThreeSessionLayout.ThreeLargeRight)[2] ==
           new PanelPlacement(true, 0, 1, 2, 1),
        "An arbitrary docked account can be selected as the large browser.");
    Assert(WorkspaceLayoutPolicy.Three([2, 3, 4], 1, ThreeSessionLayout.ThreeLargeLeft)[1] ==
           new PanelPlacement(true, 0, 0, 2, 1),
        "A detached preferred large account safely falls back to the first docked account.");

    var transitions = new PopOutSessionState(4);
    Assert(transitions.DockedAccounts.Count == 4 && transitions.Detach(4) && transitions.DockedAccounts.Count == 3,
        "Four-to-three transition exposes exactly three docked accounts.");
    Assert(transitions.Detach(3) && transitions.DockedAccounts.SequenceEqual([1, 2]) &&
           WorkspaceLayoutPolicy.Docked(transitions.DockedAccounts, DuoOrientation.Vertical)[1].Row == 1,
        "Three-to-two transition restores the preferred Duo orientation.");
    Assert(transitions.Dock(3) && transitions.DockedAccounts.SequenceEqual([1, 2, 3]) &&
           WorkspaceLayoutPolicy.Docked(transitions.DockedAccounts, DuoOrientation.Horizontal,
               ThreeSessionLayout.ThreeLargeBottom, 3)[2].Row == 1,
        "Two-to-three transition restores the preferred three-session layout.");
    Assert(transitions.Dock(4) && WorkspaceLayoutPolicy.Docked(transitions.DockedAccounts, DuoOrientation.Horizontal)
               .SequenceEqual(WorkspaceLayoutPolicy.Quad()),
        "Three-to-four transition returns to Quad.");

    var fullCycle = new PopOutSessionState(4);
    foreach (int account in Enumerable.Range(1, 4)) fullCycle.Detach(account);
    Assert(fullCycle.DockedAccounts.Count == 0, "Four-to-three-to-two-to-one-to-zero transition remains recoverable.");
    fullCycle.RestoreAll();
    Assert(fullCycle.DockedAccounts.SequenceEqual([1, 2, 3, 4]), "Restore All returns a zero-docked state to four accounts.");

    Assert(Enum.GetValues<ThreeSessionLayout>().All(layout =>
            WorkspaceLayoutPolicy.Three([1, 2, 4], 2, layout).Count(item => item.Visible) == 3),
        "Repeated switching across every three-session layout keeps exactly three panels visible.");
    Assert(docked.All(large => WorkspaceLayoutPolicy.Three(docked, large, ThreeSessionLayout.ThreeLargeTop)[large - 1]
            == new PanelPlacement(true, 0, 0, 1, 2)),
        "Repeated large-account switching assigns the requested docked account without profile swapping.");

    var service = new SettingsService(settingsPath);
    service.Save(new AppSettings
    {
        DuoOrientation = nameof(DuoOrientation.Vertical),
        ThreeSessionLayout = nameof(ThreeSessionLayout.ThreeLargeBottom),
        ThreeLargeAccount = 4
    });
    AppSettings loaded = service.Load();
    Assert(loaded.DuoOrientation == nameof(DuoOrientation.Vertical) &&
           WorkspaceLayoutPolicy.ParseThreeSessionLayout(loaded.ThreeSessionLayout) == ThreeSessionLayout.ThreeLargeBottom &&
           loaded.ThreeLargeAccount == 4,
        "Duo and three-session preferences persist together.");
    Assert(WorkspaceLayoutPolicy.ParseThreeSessionLayout("future-unknown-value") == ThreeSessionLayout.ThreeLargeLeft,
        "Unknown three-session settings safely fall back to Large Left.");
}

static void TestDuoOrientationPersistence(string path)
{
    var settings = new AppSettings { DuoOrientation = nameof(DuoOrientation.Vertical) };
    var service = new SettingsService(path);
    service.Save(settings);
    Assert(service.Load().DuoOrientation == nameof(DuoOrientation.Vertical),
        "Duo orientation persists in local settings.");
}

static void TestPopOutStateAndLayouts()
{
    var free = new PopOutSessionState(1);
    Assert(free.Detach(1) && !free.Detach(2), "FREE can pop out only entitled Account 1.");

    var pro = new PopOutSessionState(4);
    Assert(pro.Detach(4) && !pro.Detach(4) && pro.DetachedAccounts.Count == 1,
        "An account cannot be detached twice.");
    Assert(pro.Dock(4) && pro.Detach(4) && pro.Dock(4),
        "Repeated detach and dock transitions remain consistent.");
    pro.Detach(2);
    pro.Detach(3);
    pro.RestoreAll();
    Assert(pro.DetachedAccounts.Count == 0 && pro.DockedAccounts.SequenceEqual([1, 2, 3, 4]),
        "Restore All docks every entitled account.");

    Assert(AppConfig.GetProfileDirectory(3).EndsWith(Path.Combine("Profiles", "Account3"), StringComparison.Ordinal),
        "Pop-out state does not change account profile mapping.");

    IReadOnlyList<PanelPlacement> four = WorkspaceLayoutPolicy.Docked([1, 2, 3, 4], DuoOrientation.Horizontal);
    Assert(four.SequenceEqual(WorkspaceLayoutPolicy.Quad()), "Four docked accounts remain a 2x2 layout.");

    IReadOnlyList<PanelPlacement> three = WorkspaceLayoutPolicy.Docked([1, 2, 3], DuoOrientation.Horizontal);
    Assert(three[0] == new PanelPlacement(true, 0, 0, 2, 1) &&
           three[1] == new PanelPlacement(true, 0, 1, 1, 1) &&
           three[2] == new PanelPlacement(true, 1, 1, 1, 1),
        "Three docked accounts default to Large Left.");

    IReadOnlyList<PanelPlacement> twoHorizontal = WorkspaceLayoutPolicy.Docked([1, 4], DuoOrientation.Horizontal);
    IReadOnlyList<PanelPlacement> twoVertical = WorkspaceLayoutPolicy.Docked([1, 4], DuoOrientation.Vertical);
    Assert(twoHorizontal[0].ColumnSpan == 1 && twoHorizontal[3].Column == 1,
        "Two docked accounts respect Side by Side orientation.");
    Assert(twoVertical[0].RowSpan == 1 && twoVertical[3].Row == 1,
        "Two docked accounts respect Stacked orientation.");

    IReadOnlyList<PanelPlacement> one = WorkspaceLayoutPolicy.Docked([3], DuoOrientation.Horizontal);
    Assert(one[2] == new PanelPlacement(true, 0, 0, 2, 2) && one.Count(item => item.Visible) == 1,
        "One docked account fills the workspace.");
    Assert(WorkspaceLayoutPolicy.Docked([], DuoOrientation.Horizontal).All(item => !item.Visible),
        "Zero docked accounts produce an explicit empty workspace state.");

    var downgraded = new PopOutSessionState(1);
    downgraded.Restore([1, 2, 3, 4]);
    Assert(downgraded.DetachedAccounts.SequenceEqual([1]),
        "FREE restoration rejects detached Accounts 2-4 after downgrade.");
}

static void TestDetachedWindowPersistence(string path)
{
    var service = new SettingsService(path);
    service.Save(new AppSettings
    {
        DetachedAccounts = [2, 4],
        DetachedWindows =
        [
            new DetachedWindowSettings { AccountNumber = 2, Left = 100, Top = 120, Width = 900, Height = 650 },
            new DetachedWindowSettings { AccountNumber = 4, Left = 2100, Top = 100, Width = 800, Height = 600, IsMaximized = true }
        ]
    });
    AppSettings loaded = service.Load();
    Assert(loaded.DetachedAccounts.SequenceEqual([2, 4]) && loaded.DetachedWindows.Length == 2,
        "Detached account identities and window bounds persist locally.");

    DisplayBounds[] displays = [new DisplayBounds(0, 0, 1920, 1080)];
    DetachedWindowSettings recovered = WindowBoundsPolicy.Normalize(
        new DetachedWindowSettings { AccountNumber = 4, Left = 5000, Top = 5000, Width = 900, Height = 650 },
        4,
        displays);
    Assert(recovered.Left == 60 && recovered.Top == 60,
        "Off-screen detached bounds recover to a visible display.");

    DetachedWindowSettings visible = WindowBoundsPolicy.Normalize(
        new DetachedWindowSettings { AccountNumber = 2, Left = 100, Top = 120, Width = 900, Height = 650 },
        2,
        displays);
    Assert(visible.Left == 100 && visible.Top == 120 && visible.Width == 900,
        "Visible detached bounds are preserved.");
}

static void TestDeviceIdentity(string path)
{
    var service = new DeviceIdentityService(path);
    DeviceIdentity first = service.GetOrCreate();
    DeviceIdentity second = service.GetOrCreate();
    Assert(first.DeviceId == second.DeviceId, "Device ID persists.");
    Assert(Guid.TryParseExact(first.DeviceId, "N", out _), "Device ID is an opaque GUID.");
}

static void TestProtectedSession(string path)
{
    const string token = "access-token-that-must-not-be-plaintext";
    const string refresh = "refresh-token-that-must-not-be-plaintext";
    var service = new AuthSessionService(path);
    service.Save(new StoredAuthSession(
        "test@example.com",
        token,
        refresh,
        DateTimeOffset.UtcNow.AddMinutes(15),
        DateTimeOffset.UtcNow.AddDays(30)));
    StoredAuthSession? loaded = service.Load();
    Assert(loaded?.AccessToken == token, "Protected session round-trips.");
    Assert(loaded?.RefreshToken == refresh, "Protected refresh token round-trips.");
    byte[] persisted = File.ReadAllBytes(Path.Combine(path, "Licensing", "session.dat"));
    Assert(!Encoding.UTF8.GetString(persisted).Contains(token, StringComparison.Ordinal), "Token is not stored as plaintext.");
    Assert(!Encoding.UTF8.GetString(persisted).Contains(refresh, StringComparison.Ordinal), "Refresh token is DPAPI protected.");
    Assert(Directory.GetFiles(Path.Combine(path, "Licensing"), "*.tmp").Length == 0, "Atomic session save leaves no temporary file.");
    service.Clear();
    Assert(service.Load() is null, "Sign-out clears the saved token.");
}

static async Task TestApiContractAsync()
{
    var handler = new RecordingHandler();
    using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
    using var api = new LicensingApiClient(client);
    await api.ActivateDeviceAsync("token", new DeviceIdentity("opaque", "desktop"));
    using JsonDocument json = JsonDocument.Parse(handler.Body!);
    Assert(json.RootElement.GetProperty("device_id").GetString() == "opaque", "API sends snake_case device_id.");
    Assert(handler.Authorization == "Bearer token", "API sends bearer authorization.");
    Assert(LicensingApiClient.MapError(HttpStatusCode.Conflict, "DEVICE_LIMIT_REACHED", null).Kind == LicensingErrorKind.DeviceLimit,
        "Device-limit errors are mapped.");
}

static async Task TestBillingCheckoutContractAsync()
{
    foreach (string plan in new[] { BillingPolicy.ProMonthly, BillingPolicy.ProAnnual })
    {
        var handler = new ScenarioHandler(request => Task.FromResult(JsonResponse(
            $"{{\"provider\":\"mercado_pago\",\"checkout_session_id\":\"checkout-123\",\"checkout_url\":\"https://checkout.example/session\",\"plan\":\"{plan}\",\"currency\":\"BRL\",\"amount_minor\":{(plan == BillingPolicy.ProMonthly ? 990 : 9900)},\"billing_interval\":\"{(plan == BillingPolicy.ProMonthly ? "month" : "year")}\"}}")));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        using var api = new LicensingApiClient(http);
        BillingCheckoutResponse response = await api.CreateBillingCheckoutAsync("billing-token", plan);
        RequestSnapshot request = handler.Requests.Single();
        using JsonDocument payload = JsonDocument.Parse(request.Body!);
        Assert(request.Method == "POST" && request.Path == "/billing/checkout" &&
               request.Authorization == "Bearer billing-token" && payload.RootElement.GetProperty("plan").GetString() == plan,
            $"{plan} checkout serializes the authenticated backend request.");
        Assert(response.Provider == "mercado_pago" && response.CheckoutSessionId == "checkout-123" &&
               response.CheckoutUrl == "https://checkout.example/session" && response.Plan == plan &&
               response.Currency == "BRL" && response.AmountMinor == (plan == BillingPolicy.ProMonthly ? 990 : 9900),
            $"{plan} checkout response parses into the typed billing model.");
    }
}

static void TestBillingPresentationAndUrlValidation()
{
    var free = new LicenseValidationResponse("FREE", "ACTIVE", 1, 1, null, true, true);
    var monthly = new LicenseValidationResponse(BillingPolicy.ProMonthly, "ACTIVE", 4, 1, null, true, true);
    var annual = new LicenseValidationResponse(BillingPolicy.ProAnnual, "ACTIVE", 4, 1, null, true, true);
    Assert(BillingPolicy.ShouldShowUpgrade(free) && BillingPolicy.GetPlanLabel(free) == "DRIFTR FREE · 1 SESSION",
        "Free entitlement exposes Upgrade and the Free plan label.");
    Assert(!BillingPolicy.ShouldShowUpgrade(monthly) && BillingPolicy.GetPlanLabel(monthly) == "DRIFTR PRO · MONTHLY",
        "Active monthly Pro hides misleading Upgrade and shows Monthly.");
    Assert(!BillingPolicy.ShouldShowUpgrade(annual) && BillingPolicy.GetPlanLabel(annual) == "DRIFTR PRO · ANNUAL",
        "Active annual Pro hides misleading Upgrade and shows Annual.");
    Assert(BillingPolicy.RequireSecureCheckoutUri("https://checkout.example/session").Scheme == Uri.UriSchemeHttps,
        "An absolute HTTPS checkout URL is accepted.");
    _ = AssertThrows<LicensingException>(() => BillingPolicy.RequireSecureCheckoutUri(null));
    _ = AssertThrows<LicensingException>(() => BillingPolicy.RequireSecureCheckoutUri("not-a-url"));
    _ = AssertThrows<LicensingException>(() => BillingPolicy.RequireSecureCheckoutUri("http://checkout.example/session"));
    Console.WriteLine("PASS: Missing, malformed, and non-HTTPS checkout URLs are rejected.");
}

static async Task TestCheckoutAndAuthoritativeEntitlementRefreshAsync(string path)
{
    var store = new AuthSessionService(path);
    store.Save(FreshSession("access", "refresh"));
    int maxSessions = 1;
    string plan = "FREE";
    var handler = new ScenarioHandler(request => Task.FromResult(request.Path switch
    {
        "/billing/checkout" => JsonResponse("{\"provider\":\"mercado_pago\",\"checkout_session_id\":\"checkout-123\",\"checkout_url\":\"https://checkout.example/session\",\"plan\":\"PRO_MONTHLY\",\"currency\":\"BRL\",\"amount_minor\":990,\"billing_interval\":\"month\"}"),
        "/license/validate" => JsonResponse($"{{\"plan\":\"{plan}\",\"status\":\"ACTIVE\",\"max_sessions\":{maxSessions},\"max_devices\":1,\"expires_at\":null,\"valid\":true,\"device_authorized\":true}}"),
        _ => StandardResponse(request)
    }));
    using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
    using var api = new LicensingApiClient(http);
    var coordinator = new LicenseCoordinator(api, new DeviceIdentityService(path), store);
    LicensingSession initial = (await coordinator.TryResumeAsync())!;
    int validationsBeforeCheckout = handler.Count("/license/validate");
    _ = await coordinator.CreateBillingCheckoutAsync(BillingPolicy.ProMonthly);
    Assert(initial.License.MaxSessions == 1 && handler.Count("/license/validate") == validationsBeforeCheckout,
        "Checkout creation alone does not grant Pro or mutate entitlement.");

    LicensingSession stillFree = await coordinator.RefreshEntitlementAsync();
    Assert(stillFree.License.MaxSessions == 1 && EntitlementPolicy.NormalizeMaxSessions(stillFree.License.MaxSessions) == 1,
        "Free remains Free while the backend still reports Free.");

    maxSessions = 4;
    plan = BillingPolicy.ProMonthly;
    LicensingSession confirmed = await coordinator.RefreshEntitlementAsync();
    Assert(confirmed.License.Plan == BillingPolicy.ProMonthly &&
           EntitlementPolicy.NormalizeMaxSessions(confirmed.License.MaxSessions) == 4,
        "Authoritative backend refresh grants Pro and updates the four-session limit.");
}

static async Task TestLoginStoresRefreshSessionAsync(string path)
{
    var handler = new ScenarioHandler(async request =>
    {
        if (request.Path == "/auth/login")
        {
            await Task.Yield();
            return JsonResponse(TokenJson("access-login", "refresh-login"));
        }
        return StandardResponse(request);
    });
    using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
    using var api = new LicensingApiClient(http);
    var store = new AuthSessionService(path);
    var coordinator = new LicenseCoordinator(api, new DeviceIdentityService(path), store);
    LicensingSession session = await coordinator.SignInAsync("user@example.com", "never-persist-this");
    StoredAuthSession? saved = store.Load();
    Assert(session.Email == "user@example.com" && saved?.RefreshToken == "refresh-login", "Login stores a refresh-capable session.");
    RequestSnapshot login = handler.Requests.Single(item => item.Path == "/auth/login");
    using JsonDocument payload = JsonDocument.Parse(login.Body!);
    Assert(payload.RootElement.GetProperty("device_id").GetString() is { Length: 32 }, "Login sends the persistent device ID.");
    byte[] protectedFile = File.ReadAllBytes(Path.Combine(path, "Licensing", "session.dat"));
    string persistedText = Encoding.UTF8.GetString(protectedFile);
    Assert(!persistedText.Contains("never-persist-this", StringComparison.Ordinal), "Password is never persisted.");
    Assert(!persistedText.Contains("access-login", StringComparison.Ordinal) && !persistedText.Contains("refresh-login", StringComparison.Ordinal),
        "Both login tokens are DPAPI protected.");
}

static async Task TestProactiveRefreshAndSingleFlightAsync(string path)
{
    var store = new AuthSessionService(path);
    store.Save(TimedSession("access-old", "refresh-old", TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5)));
    int refreshCalls = 0;
    var handler = new ScenarioHandler(async request =>
    {
        if (request.Path == "/auth/refresh")
        {
            Interlocked.Increment(ref refreshCalls);
            await Task.Delay(150);
            return JsonResponse(TokenJson("access-new", "refresh-new", 60));
        }
        return StandardResponse(request);
    });
    using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
    using var api = new LicensingApiClient(http);
    var coordinator = new LicenseCoordinator(api, new DeviceIdentityService(path), store);
    LicensingSession?[] sessions = await Task.WhenAll(
        coordinator.TryResumeAsync(),
        coordinator.TryResumeAsync())!;
    StoredAuthSession? saved = store.Load();
    Assert(refreshCalls == 1, "Simultaneous callers cause exactly one refresh request.");
    Assert(saved?.AccessToken == "access-new" && saved.RefreshToken == "refresh-new", "Successful refresh atomically replaces both tokens.");
    Assert(handler.Requests.Where(IsProtectedRequest).All(item => item.Authorization == "Bearer access-new"),
        "Waiting callers reuse the replacement access token.");
    Assert(sessions.All(item => item?.License.MaxSessions == 1), "Concurrent refreshed callers receive validated entitlements.");
}

static async Task TestAdaptiveProactiveWindowsAsync(string path)
{
    (string Name, TimeSpan Lifetime, TimeSpan Remaining, int ExpectedRefreshes)[] cases =
    [
        ("normal-not-near", TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(5), 0),
        ("normal-near", TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(45), 1),
        ("short-not-near", TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(30), 0),
        ("short-near", TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5), 1)
    ];

    foreach ((string name, TimeSpan lifetime, TimeSpan remaining, int expectedRefreshes) in cases)
    {
        string casePath = Path.Combine(path, name);
        var store = new AuthSessionService(casePath);
        store.Save(TimedSession("access-old", "refresh-old", lifetime, remaining));
        int refreshCalls = 0;
        var handler = new ScenarioHandler(request =>
        {
            if (request.Path == "/auth/refresh")
            {
                Interlocked.Increment(ref refreshCalls);
                return Task.FromResult(JsonResponse(TokenJson(
                    "access-new",
                    "refresh-new",
                    (int)lifetime.TotalSeconds)));
            }
            return Task.FromResult(StandardResponse(request));
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        using var api = new LicensingApiClient(http);
        var coordinator = new LicenseCoordinator(api, new DeviceIdentityService(casePath), store);
        _ = await coordinator.TryResumeAsync();
        Assert(refreshCalls == expectedRefreshes,
            $"Adaptive refresh window handles {name} without unnecessary rotations.");
    }
}

static async Task TestUnauthorizedRetryAndRetryLimitAsync(string path)
{
    string successPath = Path.Combine(path, "success");
    var store = new AuthSessionService(successPath);
    store.Save(FreshSession("access-old", "refresh-old"));
    var handler = new ScenarioHandler(request => Task.FromResult(request.Path switch
    {
        "/license/me" when request.Authorization == "Bearer access-old" => ErrorResponse(HttpStatusCode.Unauthorized, "INVALID_TOKEN"),
        "/auth/refresh" => JsonResponse(TokenJson("access-new", "refresh-new")),
        _ => StandardResponse(request)
    }));
    using (var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") })
    using (var api = new LicensingApiClient(http))
    {
        var coordinator = new LicenseCoordinator(api, new DeviceIdentityService(successPath), store);
        _ = await coordinator.TryResumeAsync();
    }
    Assert(handler.Count("/license/me") == 2 && handler.Count("/auth/refresh") == 1,
        "An unauthorized API request refreshes and retries the original request once.");

    string failurePath = Path.Combine(path, "retry-limit");
    var failureStore = new AuthSessionService(failurePath);
    failureStore.Save(FreshSession("access-old", "refresh-old"));
    var failureHandler = new ScenarioHandler(request => Task.FromResult(request.Path switch
    {
        "/license/me" => ErrorResponse(HttpStatusCode.Unauthorized, "INVALID_TOKEN"),
        "/auth/refresh" => JsonResponse(TokenJson("access-new", "refresh-new")),
        _ => StandardResponse(request)
    }));
    using var failureHttp = new HttpClient(failureHandler) { BaseAddress = new Uri("http://localhost/") };
    using var failureApi = new LicensingApiClient(failureHttp);
    var failureCoordinator = new LicenseCoordinator(
        failureApi,
        new DeviceIdentityService(failurePath),
        failureStore);
    await AssertThrowsAsync<LicensingException>(() => failureCoordinator.TryResumeAsync());
    Assert(failureHandler.Count("/license/me") == 2 && failureHandler.Count("/auth/refresh") == 1,
        "Authentication retry cannot loop indefinitely.");
}

static async Task TestPermanentRefreshFailuresAsync(string path)
{
    string[] codes = ["INVALID_REFRESH_TOKEN", "REFRESH_TOKEN_REUSED", "REFRESH_TOKEN_REVOKED", "REFRESH_TOKEN_EXPIRED"];
    foreach (string code in codes)
    {
        string casePath = Path.Combine(path, code);
        var store = new AuthSessionService(casePath);
        store.Save(ExpiredSession("access-old", "refresh-old"));
        var handler = new ScenarioHandler(request => Task.FromResult(
            request.Path == "/auth/refresh"
                ? ErrorResponse(HttpStatusCode.Unauthorized, code)
                : StandardResponse(request)));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        using var api = new LicensingApiClient(http);
        var coordinator = new LicenseCoordinator(api, new DeviceIdentityService(casePath), store);
        LicensingException exception = await AssertThrowsAsync<LicensingException>(() => coordinator.TryResumeAsync());
        Assert(exception.Kind == LicensingErrorKind.SessionExpired && store.Load() is null,
            $"{code} clears authentication and requires Sign In.");
    }
}

static async Task TestNetworkFailurePreservesSessionAsync(string path)
{
    var store = new AuthSessionService(path);
    store.Save(ExpiredSession("access-old", "refresh-old"));
    var handler = new ScenarioHandler(request => request.Path == "/auth/refresh"
        ? Task.FromException<HttpResponseMessage>(new HttpRequestException("offline"))
        : Task.FromResult(StandardResponse(request)));
    using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
    using var api = new LicensingApiClient(http);
    var coordinator = new LicenseCoordinator(api, new DeviceIdentityService(path), store);
    LicensingException exception = await AssertThrowsAsync<LicensingException>(() => coordinator.TryResumeAsync());
    Assert(exception.Kind == LicensingErrorKind.Network && store.Load()?.RefreshToken == "refresh-old",
        "Network refresh failure preserves valid refresh credentials.");
}

static async Task TestSignOutFlowsAsync(string path)
{
    string normalPath = Path.Combine(path, "normal");
    var normalStore = new AuthSessionService(normalPath);
    normalStore.Save(FreshSession("access", "refresh"));
    var normalHandler = new ScenarioHandler(request => Task.FromResult(StandardResponse(request)));
    using (var http = new HttpClient(normalHandler) { BaseAddress = new Uri("http://localhost/") })
    using (var api = new LicensingApiClient(http))
    {
        var coordinator = new LicenseCoordinator(api, new DeviceIdentityService(normalPath), normalStore);
        bool confirmed = await coordinator.SignOutAsync();
        Assert(confirmed && normalHandler.Count("/auth/logout") == 1 && normalStore.Load() is null,
            "Normal Sign Out calls logout and clears local authentication.");
    }

    string offlinePath = Path.Combine(path, "offline");
    var offlineStore = new AuthSessionService(offlinePath);
    offlineStore.Save(FreshSession("access", "refresh"));
    var offlineHandler = new ScenarioHandler(request => request.Path == "/auth/logout"
        ? Task.FromException<HttpResponseMessage>(new HttpRequestException("offline"))
        : Task.FromResult(StandardResponse(request)));
    using (var http = new HttpClient(offlineHandler) { BaseAddress = new Uri("http://localhost/") })
    using (var api = new LicensingApiClient(http))
    {
        var coordinator = new LicenseCoordinator(api, new DeviceIdentityService(offlinePath), offlineStore);
        bool confirmed = await coordinator.SignOutAsync();
        Assert(!confirmed && offlineStore.Load() is null, "Offline Sign Out still clears local authentication.");
    }

    string allPath = Path.Combine(path, "all");
    var allStore = new AuthSessionService(allPath);
    allStore.Save(FreshSession("access", "refresh"));
    var allHandler = new ScenarioHandler(request => Task.FromResult(StandardResponse(request)));
    using var allHttp = new HttpClient(allHandler) { BaseAddress = new Uri("http://localhost/") };
    using var allApi = new LicensingApiClient(allHttp);
    var allCoordinator = new LicenseCoordinator(allApi, new DeviceIdentityService(allPath), allStore);
    _ = await allCoordinator.TryResumeAsync();
    await allCoordinator.SignOutEverywhereAsync();
    Assert(allHandler.Count("/auth/logout-all") == 1 && allStore.Load() is null, "Sign Out Everywhere revokes and clears authentication.");
}

static async Task TestDeactivationPreservesIdentityAndProfilesAsync(string path)
{
    var store = new AuthSessionService(path);
    store.Save(FreshSession("access", "refresh"));
    var identityService = new DeviceIdentityService(path);
    string deviceId = identityService.GetOrCreate().DeviceId;
    string profile = Path.Combine(path, "Profiles", "Account2");
    Directory.CreateDirectory(profile);
    File.WriteAllText(Path.Combine(profile, "marker.txt"), "preserve");
    var handler = new ScenarioHandler(request => Task.FromResult(StandardResponse(request)));
    using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
    using var api = new LicensingApiClient(http);
    var coordinator = new LicenseCoordinator(api, identityService, store);
    _ = await coordinator.TryResumeAsync();
    await coordinator.DeactivateAsync();
    Assert(store.Load() is null && identityService.GetOrCreate().DeviceId == deviceId,
        "Device deactivation clears auth but preserves device identity.");
    Assert(File.ReadAllText(Path.Combine(profile, "marker.txt")) == "preserve", "Authentication clearing does not change browser profiles.");
}

static async Task TestEntitlementsAfterRefreshAsync(string path)
{
    foreach ((string name, int maxSessions) in new[] { ("free", 1), ("pro", 4), ("downgrade", 1) })
    {
        string casePath = Path.Combine(path, name);
        var store = new AuthSessionService(casePath);
        store.Save(ExpiredSession("access-old", "refresh-old"));
        var handler = new ScenarioHandler(request => Task.FromResult(request.Path switch
        {
            "/auth/refresh" => JsonResponse(TokenJson("access-new", "refresh-new")),
            "/license/validate" => JsonResponse(LicenseJson(maxSessions)),
            _ => StandardResponse(request)
        }));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        using var api = new LicensingApiClient(http);
        var coordinator = new LicenseCoordinator(api, new DeviceIdentityService(casePath), store);
        LicensingSession session = (await coordinator.TryResumeAsync())!;
        Assert(session.License.MaxSessions == maxSessions,
            name == "pro" ? "Pro remains four sessions after refresh." : name == "downgrade" ? "Pro-to-Free downgrade is respected after refresh." : "Free remains one session after refresh.");
    }
}

static StoredAuthSession ExpiredSession(string access, string refresh) => new(
    "user@example.com", access, refresh, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(30));

static StoredAuthSession FreshSession(string access, string refresh) => new(
    "user@example.com", access, refresh, DateTimeOffset.UtcNow.AddMinutes(10), DateTimeOffset.UtcNow.AddDays(30));

static StoredAuthSession TimedSession(
    string access,
    string refresh,
    TimeSpan lifetime,
    TimeSpan remaining)
{
    DateTimeOffset now = DateTimeOffset.UtcNow;
    DateTimeOffset expires = now.Add(remaining);
    return new StoredAuthSession(
        "user@example.com",
        access,
        refresh,
        expires,
        now.AddDays(30),
        expires.Subtract(lifetime));
}

static bool IsProtectedRequest(RequestSnapshot request) =>
    request.Path is "/license/me" or "/devices/activate" or "/license/validate";

static HttpResponseMessage StandardResponse(RequestSnapshot request) => request.Path switch
{
    "/license/me" => JsonResponse("{\"plan\":\"FREE\",\"status\":\"ACTIVE\",\"max_sessions\":1,\"max_devices\":1,\"expires_at\":null}"),
    "/license/validate" => JsonResponse(LicenseJson(1)),
    "/devices/activate" => JsonResponse("{\"device_id\":\"device\",\"device_name\":\"PC\",\"is_active\":true}"),
    "/devices/deactivate" => JsonResponse("{\"device_id\":\"device\",\"device_name\":\"PC\",\"is_active\":false}"),
    "/auth/logout" or "/auth/logout-all" => JsonResponse("{\"revoked\":true,\"revoked_sessions\":1}"),
    _ => JsonResponse("{}")
};

static string TokenJson(string access, string refresh, int accessExpiresIn = 900) =>
    $"{{\"access_token\":\"{access}\",\"refresh_token\":\"{refresh}\",\"token_type\":\"bearer\",\"expires_in\":{accessExpiresIn},\"access_expires_in\":{accessExpiresIn},\"refresh_expires_at\":\"{DateTimeOffset.UtcNow.AddDays(30):O}\"}}";

static string LicenseJson(int maxSessions) =>
    $"{{\"plan\":\"{(maxSessions == 4 ? "PRO_MONTHLY" : "FREE")}\",\"status\":\"ACTIVE\",\"max_sessions\":{maxSessions},\"max_devices\":1,\"expires_at\":null,\"valid\":true,\"device_authorized\":true}}";

static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
{
    Content = new StringContent(json, Encoding.UTF8, "application/json")
};

static HttpResponseMessage ErrorResponse(HttpStatusCode status, string code) =>
    JsonResponse($"{{\"detail\":{{\"code\":\"{code}\",\"message\":\"Request rejected\"}}}}", status);

static async Task<TException> AssertThrowsAsync<TException>(Func<Task> action) where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException exception)
    {
        return exception;
    }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static TException AssertThrows<TException>(Action action) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException exception)
    {
        Console.WriteLine("PASS: Invalid licensing URLs are rejected.");
        return exception;
    }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static void TestMigration(string legacyRoot, string currentRoot)
{
    string legacyProfile = Path.Combine(legacyRoot, "Profiles", "Account1");
    Directory.CreateDirectory(legacyProfile);
    File.WriteAllText(Path.Combine(legacyProfile, "marker.txt"), "legacy");
    File.WriteAllText(Path.Combine(legacyRoot, "settings.json"), "{}");
    DataMigrationService.MigrateLegacyDataIfNeeded();
    string currentMarker = Path.Combine(currentRoot, "Profiles", "Account1", "marker.txt");
    Assert(File.ReadAllText(currentMarker) == "legacy", "Legacy profile is copied.");
    File.WriteAllText(currentMarker, "current");
    File.WriteAllText(Path.Combine(legacyProfile, "marker.txt"), "changed legacy");
    DataMigrationService.MigrateLegacyDataIfNeeded();
    Assert(File.ReadAllText(currentMarker) == "current", "Existing DRIFTR data is never overwritten.");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine($"PASS: {message}");
}

sealed class UpdateHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return responder(request, cancellationToken);
    }
}

sealed class RecordingHandler : HttpMessageHandler
{
    public string? Body { get; private set; }
    public string? Authorization { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Authorization = request.Headers.Authorization?.ToString();
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"device_id\":\"opaque\",\"device_name\":\"desktop\",\"is_active\":true}", Encoding.UTF8, "application/json")
        };
    }
}

sealed record RequestSnapshot(string Method, string Path, string? Authorization, string? Body);

sealed class ScenarioHandler(Func<RequestSnapshot, Task<HttpResponseMessage>> responder) : HttpMessageHandler
{
    private readonly object _lock = new();
    private readonly List<RequestSnapshot> _requests = [];

    public IReadOnlyList<RequestSnapshot> Requests
    {
        get
        {
            lock (_lock)
            {
                return _requests.ToArray();
            }
        }
    }

    public int Count(string path)
    {
        lock (_lock)
        {
            return _requests.Count(item => item.Path == path);
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        var snapshot = new RequestSnapshot(
            request.Method.Method,
            request.RequestUri?.AbsolutePath ?? string.Empty,
            request.Headers.Authorization?.ToString(),
            body);
        lock (_lock)
        {
            _requests.Add(snapshot);
        }
        return await responder(snapshot);
    }
}
