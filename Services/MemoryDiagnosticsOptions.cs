using System.IO;
using PokeQuad.Config;

namespace PokeQuad.Services;

public sealed record MemoryDiagnosticsOptions(bool Enabled, TimeSpan SamplingInterval, string OutputDirectory, string Scenario)
{
    public const int DefaultIntervalSeconds = 60;
    public const int MinimumIntervalSeconds = 1;
    public const int MaximumIntervalSeconds = 3600;

    public static MemoryDiagnosticsOptions FromEnvironment()
    {
        bool enabled = string.Equals(
            Environment.GetEnvironmentVariable("DRIFTR_MEMORY_DIAGNOSTICS")?.Trim(),
            "1",
            StringComparison.Ordinal);

        int seconds = DefaultIntervalSeconds;
        if (int.TryParse(Environment.GetEnvironmentVariable("DRIFTR_MEMORY_DIAGNOSTICS_INTERVAL_SECONDS"), out int configured))
        {
            seconds = Math.Clamp(configured, MinimumIntervalSeconds, MaximumIntervalSeconds);
        }

        string? configuredRoot = Environment.GetEnvironmentVariable("DRIFTR_MEMORY_DIAGNOSTICS_ROOT");
        string outputDirectory = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(AppConfig.AppDataDirectory, "Diagnostics", "Memory")
            : Path.GetFullPath(configuredRoot);
        string? scenario = Environment.GetEnvironmentVariable("DRIFTR_MEMORY_DIAGNOSTICS_SCENARIO")?.Trim();
        if (string.IsNullOrWhiteSpace(scenario)) scenario = "manual";

        return new MemoryDiagnosticsOptions(enabled, TimeSpan.FromSeconds(seconds), outputDirectory, scenario);
    }
}
