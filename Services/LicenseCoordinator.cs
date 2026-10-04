using System.IO;
using PokeQuad.Models;

namespace PokeQuad.Services;

public sealed class LicenseCoordinator
{
    private static readonly TimeSpan RefreshSafetyWindow = TimeSpan.FromSeconds(60);
    private readonly LicensingApiClient _apiClient;
    private readonly DeviceIdentityService _deviceIdentityService;
    private readonly AuthSessionService _authSessionService;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _stateLock = new();
    private StoredAuthSession? _currentAuth;

    public LicenseCoordinator(
        LicensingApiClient apiClient,
        DeviceIdentityService deviceIdentityService,
        AuthSessionService authSessionService,
        TimeProvider? timeProvider = null)
    {
        _apiClient = apiClient;
        _deviceIdentityService = deviceIdentityService;
        _authSessionService = authSessionService;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<LicensingSession?> TryResumeAsync(CancellationToken cancellationToken = default)
    {
        StoredAuthSession? stored = _authSessionService.Load();
        if (stored is null)
        {
            return null;
        }

        SetCurrent(stored);
        try
        {
            return await EstablishAsync(stored.Email, cancellationToken);
        }
        catch (LicensingException exception) when (
            exception.Kind is LicensingErrorKind.Authentication or LicensingErrorKind.SessionExpired)
        {
            ClearLocalAuthentication();
            throw SessionExpired(exception.Code);
        }
    }

    public async Task<LicensingSession> SignInAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        DeviceIdentity identity = _deviceIdentityService.GetOrCreate();
        TokenResponse tokens = await _apiClient.LoginAsync(
            email,
            password,
            identity.DeviceId,
            cancellationToken);
        StoredAuthSession stored = CreateStoredSession(email.Trim(), tokens);
        SetCurrent(stored);

        try
        {
            LicensingSession session = await EstablishAsync(stored.Email, cancellationToken, allowProactiveRefresh: false);
            try
            {
                PersistCurrent(stored);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                ClearLocalAuthentication();
                throw new LicensingException(
                    LicensingErrorKind.SessionExpired,
                    "DRIFTR could not securely save the sign-in. Please try again.");
            }
            return session;
        }
        catch
        {
            SetCurrent(null);
            throw;
        }
    }

    public async Task<LicensingSession> RegisterAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        await _apiClient.RegisterAsync(email, password, cancellationToken);
        return await SignInAsync(email, password, cancellationToken);
    }

    public async Task<bool> SignOutAsync(CancellationToken cancellationToken = default)
    {
        bool serverConfirmed = true;
        try
        {
            StoredAuthSession? stored = GetCurrent() ?? _authSessionService.Load();
            if (stored is not null)
            {
                SetCurrent(stored);
                _ = await ExecuteAuthenticatedAsync(
                    (token, ct) => _apiClient.LogoutAsync(token, ct),
                    cancellationToken);
            }
        }
        catch (LicensingException)
        {
            serverConfirmed = false;
        }
        finally
        {
            ClearLocalAuthentication();
        }

        return serverConfirmed;
    }

    public async Task SignOutEverywhereAsync(CancellationToken cancellationToken = default)
    {
        _ = await ExecuteAuthenticatedAsync(
            (token, ct) => _apiClient.LogoutAllAsync(token, ct),
            cancellationToken);
        ClearLocalAuthentication();
    }

    public async Task DeactivateAsync(CancellationToken cancellationToken = default)
    {
        DeviceIdentity identity = _deviceIdentityService.GetOrCreate();
        _ = await ExecuteAuthenticatedAsync(
            (token, ct) => _apiClient.DeactivateDeviceAsync(token, identity.DeviceId, ct),
            cancellationToken);
        ClearLocalAuthentication();
    }

    public Task<BillingCheckoutResponse> CreateBillingCheckoutAsync(
        string plan,
        CancellationToken cancellationToken = default) =>
        ExecuteAuthenticatedAsync(
            (token, ct) => _apiClient.CreateBillingCheckoutAsync(token, plan, ct),
            cancellationToken);

    public Task<LicensingSession> RefreshEntitlementAsync(CancellationToken cancellationToken = default)
    {
        StoredAuthSession current = GetCurrent() ?? throw SessionExpired();
        return EstablishAsync(current.Email, cancellationToken);
    }

    private async Task<LicensingSession> EstablishAsync(
        string email,
        CancellationToken cancellationToken,
        bool allowProactiveRefresh = true)
    {
        _ = await ExecuteAuthenticatedAsync(
            (token, ct) => _apiClient.GetLicenseAsync(token, ct),
            cancellationToken,
            allowProactiveRefresh);
        DeviceIdentity identity = _deviceIdentityService.GetOrCreate();
        _ = await ExecuteAuthenticatedAsync(
            (token, ct) => _apiClient.ActivateDeviceAsync(token, identity, ct),
            cancellationToken,
            allowProactiveRefresh);
        LicenseValidationResponse license = await ExecuteAuthenticatedAsync(
            (token, ct) => _apiClient.ValidateLicenseAsync(token, identity.DeviceId, ct),
            cancellationToken,
            allowProactiveRefresh);
        return new LicensingSession(email, license);
    }

    private async Task<T> ExecuteAuthenticatedAsync<T>(
        Func<string, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken,
        bool allowProactiveRefresh = true)
    {
        StoredAuthSession current = GetCurrent() ?? throw SessionExpired();
        if (allowProactiveRefresh && NeedsProactiveRefresh(current))
        {
            current = await RefreshSingleFlightAsync(current.AccessToken, cancellationToken);
        }

        try
        {
            return await operation(current.AccessToken, cancellationToken);
        }
        catch (LicensingException exception) when (ShouldRefresh(exception))
        {
            StoredAuthSession refreshed = await RefreshSingleFlightAsync(
                current.AccessToken,
                cancellationToken);
            try
            {
                return await operation(refreshed.AccessToken, cancellationToken);
            }
            catch (LicensingException retryException) when (ShouldRefresh(retryException))
            {
                ClearLocalAuthentication();
                throw SessionExpired(retryException.Code);
            }
        }
    }

    private async Task<StoredAuthSession> RefreshSingleFlightAsync(
        string observedAccessToken,
        CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            StoredAuthSession current = GetCurrent() ?? throw SessionExpired();
            if (!string.Equals(current.AccessToken, observedAccessToken, StringComparison.Ordinal))
            {
                return current;
            }

            if (string.IsNullOrWhiteSpace(current.RefreshToken))
            {
                ClearLocalAuthentication();
                throw SessionExpired();
            }

            DeviceIdentity identity = _deviceIdentityService.GetOrCreate();
            TokenResponse replacement;
            try
            {
                replacement = await _apiClient.RefreshAsync(
                    current.RefreshToken,
                    identity.DeviceId,
                    cancellationToken);
            }
            catch (LicensingException exception) when (exception.Kind == LicensingErrorKind.SessionExpired)
            {
                ClearLocalAuthentication();
                throw SessionExpired(exception.Code);
            }

            StoredAuthSession updated = CreateStoredSession(current.Email, replacement);
            try
            {
                PersistCurrent(updated);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                ClearLocalAuthentication();
                throw new LicensingException(
                    LicensingErrorKind.SessionExpired,
                    "DRIFTR could not securely save the renewed sign-in. Please sign in again.");
            }

            SetCurrent(updated);
            return updated;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private StoredAuthSession CreateStoredSession(string email, TokenResponse response)
    {
        if (string.IsNullOrWhiteSpace(response.AccessToken) || string.IsNullOrWhiteSpace(response.RefreshToken))
        {
            throw new LicensingException(
                LicensingErrorKind.SessionExpired,
                "The licensing server did not provide a refresh-capable sign-in session.");
        }

        int lifetimeSeconds = response.AccessExpiresIn ?? response.ExpiresIn;
        DateTimeOffset issuedAt = _timeProvider.GetUtcNow();
        return new StoredAuthSession(
            email,
            response.AccessToken,
            response.RefreshToken,
            issuedAt.AddSeconds(Math.Max(0, lifetimeSeconds)),
            response.RefreshExpiresAt,
            issuedAt);
    }

    private bool NeedsProactiveRefresh(StoredAuthSession session)
    {
        if (session.AccessExpiresAt is not { } expiresAt)
        {
            return false;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (expiresAt <= now)
        {
            return true;
        }

        TimeSpan safetyWindow = RefreshSafetyWindow;
        if (session.AccessIssuedAt is { } issuedAt && expiresAt > issuedAt)
        {
            TimeSpan lifetime = expiresAt - issuedAt;
            TimeSpan proportionalWindow = TimeSpan.FromTicks(lifetime.Ticks / 5);
            safetyWindow = proportionalWindow < RefreshSafetyWindow
                ? proportionalWindow
                : RefreshSafetyWindow;
        }

        return expiresAt <= now.Add(safetyWindow);
    }

    private static bool ShouldRefresh(LicensingException exception) =>
        exception.Kind == LicensingErrorKind.Authentication &&
        string.Equals(exception.Code, "INVALID_TOKEN", StringComparison.Ordinal);

    private void PersistCurrent(StoredAuthSession session) => _authSessionService.Save(session);

    private StoredAuthSession? GetCurrent()
    {
        lock (_stateLock)
        {
            return _currentAuth;
        }
    }

    private void SetCurrent(StoredAuthSession? session)
    {
        lock (_stateLock)
        {
            _currentAuth = session;
        }
    }

    private void ClearLocalAuthentication()
    {
        SetCurrent(null);
        _authSessionService.Clear();
    }

    private static LicensingException SessionExpired(string? code = null) => new(
        LicensingErrorKind.SessionExpired,
        "Your secure sign-in session has expired. Please sign in again.",
        code: code);
}
