using PokeQuad.Models;

namespace PokeQuad.Services;

public static class BillingPolicy
{
    public const string ProMonthly = "PRO_MONTHLY";
    public const string ProAnnual = "PRO_ANNUAL";

    public static bool IsSupportedPlan(string? plan) =>
        string.Equals(plan, ProMonthly, StringComparison.Ordinal) ||
        string.Equals(plan, ProAnnual, StringComparison.Ordinal);

    public static bool ShouldShowUpgrade(LicenseValidationResponse license) =>
        EntitlementPolicy.NormalizeMaxSessions(license.MaxSessions) == 1;

    public static string GetPlanLabel(LicenseValidationResponse license)
    {
        if (EntitlementPolicy.NormalizeMaxSessions(license.MaxSessions) == 1)
        {
            return "DRIFTR FREE · 1 SESSION";
        }

        return (license.Plan ?? string.Empty).ToUpperInvariant() switch
        {
            ProMonthly => "DRIFTR PRO · MONTHLY",
            ProAnnual => "DRIFTR PRO · ANNUAL",
            _ => "DRIFTR PRO · 4 SESSIONS"
        };
    }

    public static Uri RequireSecureCheckoutUri(string? checkoutUrl)
    {
        if (!Uri.TryCreate(checkoutUrl, UriKind.Absolute, out Uri? uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new LicensingException(
                LicensingErrorKind.Validation,
                "The secure checkout link was unavailable. Please try again.");
        }

        return uri;
    }
}
