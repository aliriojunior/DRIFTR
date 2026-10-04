using System.Text.Json.Serialization;

namespace PokeQuad.Models;

public sealed record RegisterRequest(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("password")] string Password);
public sealed record LoginRequest(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("device_id")] string DeviceId);
public sealed record RefreshTokenRequest(
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("device_id")] string DeviceId);
public sealed record DeviceActivationRequest(
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("device_name")] string DeviceName);
public sealed record DeviceDeactivationRequest(
    [property: JsonPropertyName("device_id")] string DeviceId);
public sealed record LicenseValidationRequest(
    [property: JsonPropertyName("device_id")] string DeviceId);
public sealed record BillingCheckoutRequest(
    [property: JsonPropertyName("plan")] string Plan);

public sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("access_expires_in")] int? AccessExpiresIn,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("refresh_expires_at")] DateTimeOffset? RefreshExpiresAt);

public sealed record SessionRevocationResponse(
    [property: JsonPropertyName("revoked")] bool Revoked,
    [property: JsonPropertyName("revoked_sessions")] int RevokedSessions);

public sealed record LicenseResponse(
    [property: JsonPropertyName("plan")] string Plan,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("max_sessions")] int MaxSessions,
    [property: JsonPropertyName("max_devices")] int MaxDevices,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt);

public sealed record LicenseValidationResponse(
    [property: JsonPropertyName("plan")] string Plan,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("max_sessions")] int MaxSessions,
    [property: JsonPropertyName("max_devices")] int MaxDevices,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("valid")] bool Valid,
    [property: JsonPropertyName("device_authorized")] bool DeviceAuthorized);

public sealed record BillingCheckoutResponse(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("checkout_session_id")] string CheckoutSessionId,
    [property: JsonPropertyName("checkout_url")] string CheckoutUrl,
    [property: JsonPropertyName("plan")] string Plan,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("amount_minor")] long AmountMinor,
    [property: JsonPropertyName("billing_interval")] string BillingInterval);

public sealed record DeviceResponse(
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("device_name")] string DeviceName,
    [property: JsonPropertyName("is_active")] bool IsActive);

public sealed record StoredAuthSession(
    string Email,
    string AccessToken,
    string? RefreshToken = null,
    DateTimeOffset? AccessExpiresAt = null,
    DateTimeOffset? RefreshExpiresAt = null,
    DateTimeOffset? AccessIssuedAt = null);
public sealed record DeviceIdentity(string DeviceId, string DeviceName);
public sealed record LicensingSession(string Email, LicenseValidationResponse License)
{
    public bool IsPro => License.MaxSessions > 1;
}

public enum LicensingErrorKind
{
    Authentication,
    SessionExpired,
    DeviceLimit,
    LicenseUnavailable,
    Network,
    Validation,
    Unknown
}

public sealed class LicensingException(
    LicensingErrorKind kind,
    string userMessage,
    int? statusCode = null,
    string? code = null) : Exception(userMessage)
{
    public LicensingErrorKind Kind { get; } = kind;
    public string UserMessage { get; } = userMessage;
    public int? StatusCode { get; } = statusCode;
    public string? Code { get; } = code;
}
