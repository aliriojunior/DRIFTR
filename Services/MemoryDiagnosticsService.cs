using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using PokeQuad.Models;

namespace PokeQuad.Services;

public sealed class MemoryDiagnosticsService : IDisposable
{
    private const string SampleSchema = "driftr-memory-sample-v1";
    private const string SummarySchema = "driftr-memory-summary-v1";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private static readonly JsonSerializerOptions SummaryJsonOptions = new() { WriteIndented = true };
    private readonly MemoryDiagnosticsOptions _options;
    private readonly Func<IReadOnlyList<AccountMemorySnapshot>> _accountSnapshotProvider;
    private readonly Func<string> _layoutProvider;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _elapsed = new();
    private readonly List<MemoryDiagnosticSample> _samples = [];
    private readonly string _sessionId = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}";
    private readonly string _samplePath;
    private readonly string _summaryPath;
    private bool _started;
    private bool _disposed;
    private bool _sampling;

    private MemoryDiagnosticsService(
        MemoryDiagnosticsOptions options,
        Dispatcher dispatcher,
        Func<IReadOnlyList<AccountMemorySnapshot>> accountSnapshotProvider,
        Func<string> layoutProvider)
    {
        _options = options;
        _accountSnapshotProvider = accountSnapshotProvider;
        _layoutProvider = layoutProvider;
        _samplePath = Path.Combine(options.OutputDirectory, $"memory-{_sessionId}.jsonl");
        _summaryPath = Path.Combine(options.OutputDirectory, $"memory-{_sessionId}-summary.json");
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = options.SamplingInterval
        };
        _timer.Tick += Timer_Tick;
    }

    public string SamplePath => _samplePath;
    public string SummaryPath => _summaryPath;

    public static MemoryDiagnosticsService? TryCreate(
        Dispatcher dispatcher,
        Func<IReadOnlyList<AccountMemorySnapshot>> accountSnapshotProvider,
        Func<string> layoutProvider,
        MemoryDiagnosticsOptions? options = null)
    {
        MemoryDiagnosticsOptions resolved = options ?? MemoryDiagnosticsOptions.FromEnvironment();
        return resolved.Enabled
            ? new MemoryDiagnosticsService(resolved, dispatcher, accountSnapshotProvider, layoutProvider)
            : null;
    }

    public void Start()
    {
        if (_started || _disposed) return;
        Directory.CreateDirectory(_options.OutputDirectory);
        _started = true;
        _elapsed.Start();
        CaptureSample();
        _timer.Start();
    }

    public void CaptureSample()
    {
        if (!_started || _disposed || _sampling) return;
        _sampling = true;
        try
        {
            IReadOnlyList<AccountMemorySnapshot> accounts = _accountSnapshotProvider();
            using Process hostProcess = Process.GetCurrentProcess();
            hostProcess.Refresh();
            GCMemoryInfo gcInfo = GC.GetGCMemoryInfo();
            var host = new HostMemorySnapshot(
                hostProcess.Id,
                hostProcess.WorkingSet64,
                hostProcess.PrivateMemorySize64,
                GC.GetTotalMemory(false),
                gcInfo.HeapSizeBytes,
                gcInfo.TotalCommittedBytes,
                GC.GetTotalAllocatedBytes(false),
                GC.CollectionCount(0),
                GC.CollectionCount(1),
                GC.CollectionCount(2));

            WebViewProcessMemorySnapshot[] uniqueProcesses = accounts
                .SelectMany(account => account.Processes)
                .GroupBy(process => process.ProcessId)
                .Select(group => group.First())
                .ToArray();
            var sample = new MemoryDiagnosticSample(
                SampleSchema,
                _sessionId,
                DateTimeOffset.UtcNow,
                _elapsed.Elapsed.TotalSeconds,
                _options.Scenario,
                _layoutProvider(),
                host,
                uniqueProcesses.Sum(process => process.WorkingSetBytes ?? 0),
                uniqueProcesses.Sum(process => process.PrivateMemoryBytes ?? 0),
                uniqueProcesses.Length,
                accounts);

            _samples.Add(sample);
            File.AppendAllText(_samplePath, JsonSerializer.Serialize(sample, JsonOptions) + Environment.NewLine);
            if (_samples.Count % 10 == 0) WriteSummary();
        }
        catch (IOException)
        {
            // Diagnostics must never interfere with browser operation.
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (InvalidOperationException)
        {
            // A WebView process may exit while its snapshot is being collected.
        }
        finally
        {
            _sampling = false;
        }
    }

    public void Stop()
    {
        if (_disposed) return;
        _timer.Stop();
        if (_started)
        {
            CaptureSample();
            _elapsed.Stop();
            WriteSummary();
        }
        _timer.Tick -= Timer_Tick;
        _disposed = true;
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    private void Timer_Tick(object? sender, EventArgs e) => CaptureSample();

    private void WriteSummary()
    {
        if (_samples.Count == 0) return;
        var workingSet = new List<MemoryScopeSummary>
        {
            MemoryGrowthAnalyzer.Summarize("DRIFTR host", _samples.Select(sample => (sample.ElapsedSeconds, sample.Host.WorkingSetBytes)).ToArray()),
            MemoryGrowthAnalyzer.Summarize("WebView2 total", _samples.Select(sample => (sample.ElapsedSeconds, sample.WebViewWorkingSetBytes)).ToArray())
        };
        var privateMemory = new List<MemoryScopeSummary>
        {
            MemoryGrowthAnalyzer.Summarize("DRIFTR host", _samples.Select(sample => (sample.ElapsedSeconds, sample.Host.PrivateMemoryBytes)).ToArray()),
            MemoryGrowthAnalyzer.Summarize("WebView2 total", _samples.Select(sample => (sample.ElapsedSeconds, sample.WebViewPrivateMemoryBytes)).ToArray())
        };

        foreach (int account in _samples.SelectMany(sample => sample.Accounts).Select(item => item.AccountNumber).Distinct().Order())
        {
            (double ElapsedSeconds, long Bytes)[] accountWorkingSet = _samples.Select(sample =>
            {
                AccountMemorySnapshot? item = sample.Accounts.FirstOrDefault(value => value.AccountNumber == account);
                return (sample.ElapsedSeconds, item?.Processes.Sum(process => process.WorkingSetBytes ?? 0) ?? 0);
            }).ToArray();
            (double ElapsedSeconds, long Bytes)[] accountPrivate = _samples.Select(sample =>
            {
                AccountMemorySnapshot? item = sample.Accounts.FirstOrDefault(value => value.AccountNumber == account);
                return (sample.ElapsedSeconds, item?.Processes.Sum(process => process.PrivateMemoryBytes ?? 0) ?? 0);
            }).ToArray();
            workingSet.Add(MemoryGrowthAnalyzer.Summarize($"Account {account} environment", accountWorkingSet));
            privateMemory.Add(MemoryGrowthAnalyzer.Summarize($"Account {account} environment", accountPrivate));
        }

        var summary = new MemoryDiagnosticSummary(
            SummarySchema,
            _sessionId,
            DateTimeOffset.UtcNow,
            _samples[^1].ElapsedSeconds,
            "Growth classifications are descriptive signals, not proof of a memory leak. Compare lightweight-page and target-site runs.",
            workingSet,
            privateMemory);
        try
        {
            string temporaryPath = _summaryPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(summary, SummaryJsonOptions));
            File.Move(temporaryPath, _summaryPath, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
