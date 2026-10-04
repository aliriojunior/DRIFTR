using System.Diagnostics;
using System.IO;
using System.Text.Json;
using PokeQuad.Models;

namespace PokeQuad.Services;

public sealed class SessionRestartDiagnosticsLogger
{
    private const string Schema = "driftr-session-restart-v1";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly string _path;
    private readonly object _gate = new();

    public SessionRestartDiagnosticsLogger(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        _path = System.IO.Path.Combine(outputDirectory, $"session-restarts-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}.jsonl");
    }

    public string Path => _path;

    public void Write(
        Guid restartId,
        string stage,
        int accountNumber,
        string layout,
        bool isDetached,
        string profilePath,
        AccountMemorySnapshot? account,
        IReadOnlyList<int> oldProcessIds,
        IReadOnlyList<int> remainingOldProcessIds,
        long? reclaimedWorkingSetBytes = null,
        double? reclaimedWorkingSetPercent = null,
        string? error = null)
    {
        try
        {
            var value = new SessionRestartDiagnosticEvent(
                Schema,
                restartId,
                stage,
                DateTimeOffset.UtcNow,
                accountNumber,
                layout,
                isDetached,
                profilePath,
                CaptureHost(),
                account,
                oldProcessIds,
                remainingOldProcessIds,
                reclaimedWorkingSetBytes,
                reclaimedWorkingSetPercent,
                error);
            string line = JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine;
            lock (_gate) File.AppendAllText(_path, line);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Diagnostics must never disrupt a browser session.
        }
    }

    private static HostMemorySnapshot CaptureHost()
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        GCMemoryInfo gc = GC.GetGCMemoryInfo();
        return new HostMemorySnapshot(
            process.Id,
            process.WorkingSet64,
            process.PrivateMemorySize64,
            GC.GetTotalMemory(false),
            gc.HeapSizeBytes,
            gc.TotalCommittedBytes,
            GC.GetTotalAllocatedBytes(false),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2));
    }
}
