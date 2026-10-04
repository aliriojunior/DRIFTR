using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PokeQuad.Config;
using PokeQuad.Models;

namespace PokeQuad.Services;

public sealed class LicensingApiClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public LicensingApiClient(HttpClient? httpClient = null, Uri? baseUri = null)
    {
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        _httpClient.BaseAddress = baseUri ?? AppConfig.LicensingApiBaseUri;
    }

    public async Task RegisterAsync(string email, string password, CancellationToken cancellationToken = default) =>
        await SendAsync<object>(HttpMethod.Post, "auth/register", new RegisterRequest(email, password), null, cancellationToken);

    public Task<TokenResponse> LoginAsync(
        string email,
        string password,
        string deviceId,
        CancellationToken cancellationToken = default) =>
        SendAsync<TokenResponse>(
            HttpMethod.Post,
            "auth/login",
            new LoginRequest(email, password, deviceId),
            null,
            cancellationToken);

    public Task<TokenResponse> RefreshAsync(
        string refreshToken,
        string deviceId,
        CancellationToken cancellationToken = default) =>
        SendAsync<TokenResponse>(
            HttpMethod.Post,
            "auth/refresh",
            new RefreshTokenRequest(refreshToken, deviceId),
            null,
            cancellationToken);

    public Task<SessionRevocationResponse> LogoutAsync(
        string accessToken,
        CancellationToken cancellationToken = default) =>
        SendAsync<SessionRevocationResponse>(HttpMethod.Post, "auth/logout", null, accessToken, cancellationToken);

    public Task<SessionRevocationResponse> LogoutAllAsync(
        string accessToken,
        CancellationToken cancellationToken = default) =>
        SendAsync<SessionRevocationResponse>(HttpMethod.Post, "auth/logout-all", null, accessToken, cancellationToken);

    public Task<LicenseResponse> GetLicenseAsync(string token, CancellationToken cancellationToken = default) =>
        SendAsync<LicenseResponse>(HttpMethod.Get, "license/me", null, token, cancellationToken);

    public Task<BillingCheckoutResponse> CreateBillingCheckoutAsync(
        string token,
        string plan,
        CancellationToken cancellationToken = default)
    {
        if (!BillingPolicy.IsSupportedPlan(plan))
        {
            throw new ArgumentOutOfRangeException(nameof(plan), "Unsupported DRIFTR billing plan.");
        }

        return SendAsync<BillingCheckoutResponse>(
            HttpMethod.Post,
            "billing/checkout",
            new BillingCheckoutRequest(plan),
            token,
            cancellationToken);
    }

    public Task<DeviceResponse> ActivateDeviceAsync(
        string token,
        DeviceIdentity identity,
        CancellationToken cancellationToken = default) =>
        SendAsync<DeviceResponse>(
            HttpMethod.Post,
            "devices/activate",
            new DeviceActivationRequest(identity.DeviceId, identity.DeviceName),
            token,
            cancellationToken);

    public Task<LicenseValidationResponse> ValidateLicenseAsync(
        string token,
        string deviceId,
        CancellationToken cancellationToken = default) =>
        SendAsync<LicenseValidationResponse>(
            HttpMethod.Post,
            "license/validate",
            new LicenseValidationRequest(deviceId),
            token,
            cancellationToken);

    public Task<DeviceResponse> DeactivateDeviceAsync(
        string token,
        string deviceId,
        CancellationToken cancellationToken = default) =>
        SendAsync<DeviceResponse>(
            HttpMethod.Post,
            "devices/deactivate",
            new DeviceDeactivationRequest(deviceId),
            token,
            cancellationToken);

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string path,
        object? payload,
        string? token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload);
        }

        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        try
        {
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw await CreateExceptionAsync(response, cancellationToken);
            }

            if (typeof(T) == typeof(object))
            {
                return (T)new object();
            }

            T? result = await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
            return result ?? throw new LicensingException(
                LicensingErrorKind.Unknown,
                "The licensing server returned an incomplete response.");
        }
        catch (LicensingException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LicensingException(
                LicensingErrorKind.Network,
                "The licensing server did not respond in time. Check your connection and try again.");
        }
        catch (HttpRequestException)
        {
            throw new LicensingException(
                LicensingErrorKind.Network,
                "DRIFTR could not connect to the licensing server. Check your connection and try again.");
        }
    }

    public static LicensingException MapError(HttpStatusCode statusCode, string? code, string? message)
    {
        string safeMessage = string.IsNullOrWhiteSpace(message)
            ? "The licensing request could not be completed."
            : message;
        LicensingErrorKind kind = code switch
        {
            "DEVICE_LIMIT_REACHED" => LicensingErrorKind.DeviceLimit,
            "INVALID_REFRESH_TOKEN" or "REFRESH_TOKEN_REUSED" or "REFRESH_TOKEN_REVOKED" or
                "REFRESH_TOKEN_EXPIRED" => LicensingErrorKind.SessionExpired,
            "INVALID_CREDENTIALS" or "INVALID_TOKEN" => LicensingErrorKind.Authentication,
            "LICENSE_EXPIRED" or "LICENSE_CANCELLED" or "LICENSE_REVOKED" or "LICENSE_NOT_FOUND" =>
                LicensingErrorKind.LicenseUnavailable,
            _ when statusCode == HttpStatusCode.Unauthorized => LicensingErrorKind.Authentication,
            _ when statusCode == HttpStatusCode.Forbidden => LicensingErrorKind.LicenseUnavailable,
            _ when statusCode == HttpStatusCode.UnprocessableEntity => LicensingErrorKind.Validation,
            _ => LicensingErrorKind.Unknown
        };

        string userMessage = kind switch
        {
            LicensingErrorKind.DeviceLimit =>
                "This license is already active on another device. Deactivate that device before continuing.",
            LicensingErrorKind.Authentication => "Your email, password, or saved sign-in has expired or is invalid.",
            LicensingErrorKind.SessionExpired => "Your secure sign-in session has expired. Please sign in again.",
            LicensingErrorKind.LicenseUnavailable => safeMessage,
            LicensingErrorKind.Validation => "Please check the information you entered and try again.",
            _ => safeMessage
        };

        return new LicensingException(kind, userMessage, (int)statusCode, code);
    }

    private static async Task<LicensingException> CreateExceptionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        string? code = null;
        string? message = null;
        try
        {
            using JsonDocument json = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            if (json.RootElement.TryGetProperty("detail", out JsonElement detail))
            {
                if (detail.ValueKind == JsonValueKind.Object)
                {
                    if (detail.TryGetProperty("code", out JsonElement codeElement))
                    {
                        code = codeElement.GetString();
                    }

                    if (detail.TryGetProperty("message", out JsonElement messageElement))
                    {
                        message = messageElement.GetString();
                    }
                }
                else if (detail.ValueKind == JsonValueKind.String)
                {
                    message = detail.GetString();
                }
            }
        }
        catch (JsonException)
        {
        }

        return MapError(response.StatusCode, code, message);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}
