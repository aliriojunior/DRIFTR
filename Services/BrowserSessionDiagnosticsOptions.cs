using System.IO;
using PokeQuad.Config;

namespace PokeQuad.Services;

public sealed record BrowserSessionDiagnosticsOptions(bool Enabled, int AccountNumber, string OutputDirectory)
{
    public static BrowserSessionDiagnosticsOptions FromEnvironment()
    {
        bool requested = string.Equals(
            Environment.GetEnvironmentVariable("DRIFTR_SESSION_DIAGNOSTICS")?.Trim(),
            "1",
            StringComparison.Ordinal);
        bool validAccount = int.TryParse(
            Environment.GetEnvironmentVariable("DRIFTR_SESSION_DIAGNOSTICS_ACCOUNT"),
            out int accountNumber) && accountNumber is >= 1 and <= 4;
        string? configuredRoot = Environment.GetEnvironmentVariable("DRIFTR_SESSION_DIAGNOSTICS_ROOT");
        string outputDirectory = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(AppConfig.AppDataDirectory, "Diagnostics", "BrowserSession")
            : Path.GetFullPath(configuredRoot);

        // Requiring both the explicit flag and a valid account avoids accidental all-session capture.
        return new BrowserSessionDiagnosticsOptions(requested && validAccount, accountNumber, outputDirectory);
    }

    public bool AppliesTo(int accountNumber) => Enabled && accountNumber == AccountNumber;
}
