using PokeQuad.Models;
using PokeQuad.Services;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: DRIFTR.LiveProbe <isolated-data-root>");
    return 2;
}

string dataRoot = Path.GetFullPath(args[0]);
Environment.SetEnvironmentVariable("DRIFTR_DATA_ROOT", dataRoot);
string email = $"refresh-concurrency-{Guid.NewGuid():N}@example.com";
const string password = "Integration-only-password-9!";

using var api = new LicensingApiClient(baseUri: new Uri("http://127.0.0.1:8000"));
var store = new AuthSessionService(dataRoot);
var identity = new DeviceIdentityService(dataRoot);
var initial = new LicenseCoordinator(api, identity, store);

LicensingSession registered = await initial.RegisterAsync(email, password);
StoredAuthSession saved = store.Load()
    ?? throw new InvalidOperationException("Registration did not persist the refresh-capable session.");

DateTimeOffset now = DateTimeOffset.UtcNow;
store.Save(saved with
{
    AccessIssuedAt = now.AddMinutes(-2),
    AccessExpiresAt = now.AddMinutes(-1)
});

var resumedCoordinator = new LicenseCoordinator(api, identity, store);
LicensingSession?[] resumed = await Task.WhenAll(
    Enumerable.Range(0, 4).Select(_ => resumedCoordinator.TryResumeAsync()));
StoredAuthSession replacement = store.Load()
    ?? throw new InvalidOperationException("Concurrent resume did not retain the replacement session.");

bool allAuthenticated = resumed.All(session => session is not null);
bool allFree = resumed.All(session => session?.License.MaxSessions == 1);
bool replacementAdvanced = replacement.AccessExpiresAt > now && replacement.AccessIssuedAt >= now;

Console.WriteLine($"REGISTERED={registered.License.Valid}");
Console.WriteLine($"CONCURRENT_CALLERS={resumed.Length}");
Console.WriteLine($"ALL_AUTHENTICATED={allAuthenticated}");
Console.WriteLine($"ALL_FREE={allFree}");
Console.WriteLine($"REPLACEMENT_PERSISTED={replacementAdvanced}");

if (!allAuthenticated || !allFree || !replacementAdvanced)
{
    return 1;
}

bool serverConfirmed = await resumedCoordinator.SignOutAsync();
bool logoutRefreshBlocked = await RefreshIsBlockedAsync(api, replacement, identity.GetOrCreate());
Console.WriteLine($"LOGOUT_CONFIRMED={serverConfirmed}");
Console.WriteLine($"LOCAL_SESSION_CLEARED={store.Load() is null}");
Console.WriteLine($"LOGOUT_REFRESH_BLOCKED={logoutRefreshBlocked}");

bool logoutAllRefreshBlocked = await VerifyRevocationAsync(
    Path.Combine(dataRoot, "logout-all"),
    "logout-all",
    coordinator => coordinator.SignOutEverywhereAsync());
bool deactivationRefreshBlocked = await VerifyRevocationAsync(
    Path.Combine(dataRoot, "deactivation"),
    "deactivation",
    coordinator => coordinator.DeactivateAsync());
Console.WriteLine($"LOGOUT_ALL_REFRESH_BLOCKED={logoutAllRefreshBlocked}");
Console.WriteLine($"DEACTIVATION_REFRESH_BLOCKED={deactivationRefreshBlocked}");

return serverConfirmed && store.Load() is null && logoutRefreshBlocked &&
    logoutAllRefreshBlocked && deactivationRefreshBlocked ? 0 : 1;

static async Task<bool> VerifyRevocationAsync(
    string dataRoot,
    string scenario,
    Func<LicenseCoordinator, Task> revoke)
{
    using var api = new LicensingApiClient(baseUri: new Uri("http://127.0.0.1:8000"));
    var store = new AuthSessionService(dataRoot);
    var identityService = new DeviceIdentityService(dataRoot);
    var coordinator = new LicenseCoordinator(api, identityService, store);
    string email = $"refresh-{scenario}-{Guid.NewGuid():N}@example.com";
    await coordinator.RegisterAsync(email, "Integration-only-password-9!");
    StoredAuthSession session = store.Load()
        ?? throw new InvalidOperationException($"{scenario} registration did not persist a session.");
    DeviceIdentity identity = identityService.GetOrCreate();
    await revoke(coordinator);
    return store.Load() is null && await RefreshIsBlockedAsync(api, session, identity);
}

static async Task<bool> RefreshIsBlockedAsync(
    LicensingApiClient api,
    StoredAuthSession session,
    DeviceIdentity identity)
{
    if (string.IsNullOrWhiteSpace(session.RefreshToken))
    {
        return false;
    }

    try
    {
        _ = await api.RefreshAsync(session.RefreshToken, identity.DeviceId);
        return false;
    }
    catch (LicensingException exception)
    {
        return exception.Kind == LicensingErrorKind.SessionExpired;
    }
}
