using System.IO;

namespace PokeQuad.Config;

public static class AppConfig
{
    // Change this one value to update the Home destination for all four accounts.
    public const string DefaultUrl = "https://poke.idleworld.online/";
    public const string DefaultLicensingApiUrl = "https://driftr-licensing-api.onrender.com/";
    public const string UpdateOwner = "aliriojunior";
    public const string UpdateRepository = "DRIFTR";
    public const string GitHubApiVersion = "2026-03-10";
    public static Uri UpdateReleasesApiUri { get; } =
        new($"https://api.github.com/repos/{UpdateOwner}/{UpdateRepository}/releases?per_page=30");
    public static TimeSpan AutomaticUpdateCheckInterval { get; } = TimeSpan.FromHours(24);

    public static string BrowserHomeUrl { get; } = ResolveBrowserHomeUrl(
        Environment.GetEnvironmentVariable("DRIFTR_MEMORY_DIAGNOSTICS"),
        Environment.GetEnvironmentVariable("DRIFTR_MEMORY_DIAGNOSTICS_URL"));

    public static bool SkipInitialNavigationForDiagnostics { get; } =
        string.Equals(Environment.GetEnvironmentVariable("DRIFTR_MEMORY_DIAGNOSTICS")?.Trim(), "1", StringComparison.Ordinal) &&
        string.Equals(Environment.GetEnvironmentVariable("DRIFTR_MEMORY_DIAGNOSTICS_SKIP_NAVIGATION")?.Trim(), "1", StringComparison.Ordinal);

    public static string LegacyAppDataDirectory { get; } = ResolveDirectory(
        "DRIFTR_LEGACY_DATA_ROOT",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RyVex"));

    public static string AppDataDirectory { get; } = ResolveAppDataDirectory();

    public static Uri LicensingApiBaseUri { get; } = ResolveLicensingApiBaseUri(
        Environment.GetEnvironmentVariable("DRIFTR_LICENSE_API_URL"));

    public static string ProfilesDirectory { get; } = Path.Combine(AppDataDirectory, "Profiles");

    public static string GetProfileDirectory(int accountNumber) =>
        Path.Combine(ProfilesDirectory, $"Account{accountNumber}");

    private static string ResolveAppDataDirectory()
    {
        return ResolveDirectory(
            "DRIFTR_DATA_ROOT",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DRIFTR"));
    }

    private static string ResolveDirectory(string variableName, string defaultPath)
    {
        string? configured = Environment.GetEnvironmentVariable(variableName);
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? defaultPath : configured);
    }

#if DEBUG
    internal const bool AllowInsecureLoopbackLicensingApi = true;
#else
    internal const bool AllowInsecureLoopbackLicensingApi = false;
#endif

    public static Uri ResolveLicensingApiBaseUri(string? configured) =>
        ResolveLicensingApiBaseUri(configured, AllowInsecureLoopbackLicensingApi);

    public static Uri ResolveLicensingApiBaseUri(string? configured, bool allowInsecureLoopback)
    {
        string value = configured ?? DefaultLicensingApiUrl;
        if (!Uri.TryCreate(value.TrimEnd('/') + "/", UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("DRIFTR_LICENSE_API_URL must be an absolute HTTPS URL.");
        }

        if (uri.Scheme == Uri.UriSchemeHttp && (!allowInsecureLoopback || !uri.IsLoopback))
            throw new InvalidOperationException(
                "DRIFTR_LICENSE_API_URL must use HTTPS; HTTP is allowed only for loopback development endpoints in Debug builds.");

        return uri;
    }

    public static string ResolveBrowserHomeUrl(string? diagnosticsEnabled, string? configured)
    {
        if (!string.Equals(diagnosticsEnabled?.Trim(), "1", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(configured))
            return DefaultUrl;

        if (!Uri.TryCreate(configured, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https" or "file"))
            throw new InvalidOperationException("DRIFTR_MEMORY_DIAGNOSTICS_URL must be an absolute HTTP, HTTPS, or file URL.");

        return uri.AbsoluteUri;
    }
}
