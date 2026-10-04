namespace PokeQuad.Models;

public sealed record WebViewProcessMemorySnapshot(
    int ProcessId,
    string Kind,
    long? WorkingSetBytes,
    long? PrivateMemoryBytes);

public sealed record AccountMemorySnapshot(
    int AccountNumber,
    bool IsInitialized,
    bool IsDetached,
    bool IsVisible,
    int BrowserPanelIdentity,
    int BrowserControlIdentity,
    int? EnvironmentIdentity,
    int? BrowserProcessId,
    int CoreEventHandlerCount,
    string Attribution,
    IReadOnlyList<WebViewProcessMemorySnapshot> Processes,
    int WpfEventHandlerCount = 0);

public sealed record HostMemorySnapshot(
    int ProcessId,
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    long ManagedMemoryBytes,
    long ManagedHeapBytes,
    long ManagedCommittedBytes,
    long TotalAllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections);

public sealed record MemoryDiagnosticSample(
    string Schema,
    string SessionId,
    DateTimeOffset TimestampUtc,
    double ElapsedSeconds,
    string Scenario,
    string Layout,
    HostMemorySnapshot Host,
    long WebViewWorkingSetBytes,
    long WebViewPrivateMemoryBytes,
    int UniqueWebViewProcessCount,
    IReadOnlyList<AccountMemorySnapshot> Accounts);

public sealed record MemoryScopeSummary(
    string Scope,
    int SampleCount,
    double ElapsedHours,
    long StartBytes,
    long CurrentBytes,
    long PeakBytes,
    long GrowthBytes,
    double? GrowthPercent,
    double? AverageGrowthBytesPerHour,
    string Classification,
    string ClassificationBasis)
{
    public double StartMegabytes => StartBytes / 1048576d;
    public double CurrentMegabytes => CurrentBytes / 1048576d;
    public double PeakMegabytes => PeakBytes / 1048576d;
    public double GrowthMegabytes => GrowthBytes / 1048576d;
    public double? AverageGrowthMegabytesPerHour => AverageGrowthBytesPerHour / 1048576d;
}

public sealed record MemoryDiagnosticSummary(
    string Schema,
    string SessionId,
    DateTimeOffset GeneratedUtc,
    double ElapsedSeconds,
    string InterpretationWarning,
    IReadOnlyList<MemoryScopeSummary> WorkingSet,
    IReadOnlyList<MemoryScopeSummary> PrivateMemory);

public sealed record BrowserShutdownResult(
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    bool BrowserProcessExitObserved,
    bool AllCapturedProcessesExited,
    IReadOnlyList<int> CapturedProcessIds,
    IReadOnlyList<int> RemainingProcessIds,
    string? Error);

public sealed record SessionRestartResult(
    Guid RestartId,
    int AccountNumber,
    bool WasDetached,
    string ProfilePathBefore,
    string ProfilePathAfter,
    AccountMemorySnapshot Before,
    AccountMemorySnapshot AfterCreation,
    AccountMemorySnapshot AfterPageLoad,
    BrowserShutdownResult Shutdown,
    bool Succeeded,
    string? Error)
{
    public long BeforeWorkingSetBytes => Before.Processes.Sum(process => process.WorkingSetBytes ?? 0);
    public long AfterWorkingSetBytes => AfterPageLoad.Processes.Sum(process => process.WorkingSetBytes ?? 0);
    public long ReclaimedWorkingSetBytes => BeforeWorkingSetBytes - AfterWorkingSetBytes;
    public double? ReclaimedWorkingSetPercent => BeforeWorkingSetBytes > 0
        ? ReclaimedWorkingSetBytes * 100d / BeforeWorkingSetBytes
        : null;
}

public sealed record SessionRepairResult(
    Guid RepairId,
    int AccountNumber,
    bool WasDetached,
    string ProfilePathBefore,
    string ProfilePathAfter,
    AccountMemorySnapshot Before,
    AccountMemorySnapshot After,
    BrowserShutdownResult Shutdown,
    bool DiskCacheCleared,
    bool NavigationCompleted,
    string? Error);

public sealed record SessionRestartDiagnosticEvent(
    string Schema,
    Guid RestartId,
    string Stage,
    DateTimeOffset TimestampUtc,
    int AccountNumber,
    string Layout,
    bool IsDetached,
    string ProfilePath,
    HostMemorySnapshot Host,
    AccountMemorySnapshot? Account,
    IReadOnlyList<int> OldProcessIds,
    IReadOnlyList<int> RemainingOldProcessIds,
    long? ReclaimedWorkingSetBytes,
    double? ReclaimedWorkingSetPercent,
    string? Error);
