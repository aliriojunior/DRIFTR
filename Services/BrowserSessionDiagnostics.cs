using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;

namespace PokeQuad.Services;

public sealed partial class BrowserSessionDiagnostics : IDisposable
{
    private const string Schema = "driftr-browser-session-diagnostic-v1";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly CoreWebView2 _webView;
    private readonly int _accountNumber;
    private readonly string _logPath;
    private readonly object _writeLock = new();
    private CoreWebView2DevToolsProtocolEventReceiver? _exceptionReceiver;
    private CoreWebView2DevToolsProtocolEventReceiver? _consoleReceiver;
    private CoreWebView2DevToolsProtocolEventReceiver? _logReceiver;
    private CoreWebView2DevToolsProtocolEventReceiver? _networkFailureReceiver;
    private bool _disposed;

    private BrowserSessionDiagnostics(CoreWebView2 webView, int accountNumber, string outputDirectory)
    {
        _webView = webView;
        _accountNumber = accountNumber;
        Directory.CreateDirectory(outputDirectory);
        _logPath = Path.Combine(outputDirectory,
            $"browser-session-account{accountNumber}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}.jsonl");
    }

    public string LogPath => _logPath;

    public static async Task<BrowserSessionDiagnostics?> TryStartAsync(
        CoreWebView2 webView,
        int accountNumber,
        BrowserSessionDiagnosticsOptions? options = null)
    {
        BrowserSessionDiagnosticsOptions resolved = options ?? BrowserSessionDiagnosticsOptions.FromEnvironment();
        if (!resolved.AppliesTo(accountNumber)) return null;

        BrowserSessionDiagnostics? diagnostics = null;
        try
        {
            diagnostics = new BrowserSessionDiagnostics(webView, accountNumber, resolved.OutputDirectory);
            await diagnostics.StartAsync();
            return diagnostics;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException)
        {
            diagnostics?.Dispose();
            return null;
        }
    }

    public void RecordNavigationStarting(string uri, ulong navigationId, bool isRedirected)
    {
        Write("navigation-starting", new
        {
            navigation_id = navigationId,
            redirected = isRedirected,
            location = SafeLocation(uri)
        });
    }

    public void RecordNavigationCompleted(CoreWebView2NavigationCompletedEventArgs args)
    {
        Write("navigation-completed", new
        {
            navigation_id = args.NavigationId,
            success = args.IsSuccess,
            web_error = args.IsSuccess ? null : args.WebErrorStatus.ToString(),
            http_status = args.HttpStatusCode == 0 ? (int?)null : args.HttpStatusCode
        });
    }

    public void RecordProcessFailed(CoreWebView2ProcessFailedEventArgs args)
    {
        Write("process-failed", new
        {
            kind = args.ProcessFailedKind.ToString(),
            reason = args.Reason.ToString(),
            exit_code = args.ExitCode,
            description = SanitizeDiagnosticText(args.ProcessDescription),
            source_module = string.IsNullOrWhiteSpace(args.FailureSourceModulePath)
                ? null
                : Path.GetFileName(args.FailureSourceModulePath),
            affected_frames = args.FrameInfosForFailedProcess?.Count ?? 0
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_exceptionReceiver is not null)
            _exceptionReceiver.DevToolsProtocolEventReceived -= ExceptionReceived;
        if (_consoleReceiver is not null)
            _consoleReceiver.DevToolsProtocolEventReceived -= ConsoleReceived;
        if (_logReceiver is not null)
            _logReceiver.DevToolsProtocolEventReceived -= LogReceived;
        if (_networkFailureReceiver is not null)
            _networkFailureReceiver.DevToolsProtocolEventReceived -= NetworkFailureReceived;
    }

    public static string? SanitizeDiagnosticText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string sanitized = CredentialAssignmentRegex().Replace(value, "$1=[redacted]");
        sanitized = JwtRegex().Replace(sanitized, "[redacted-token]");
        sanitized = LongSecretRegex().Replace(sanitized, "[redacted-value]");
        sanitized = EmailRegex().Replace(sanitized, "[redacted-email]");
        sanitized = UrlRegex().Replace(sanitized, match => SafeLocationText(match.Value));
        sanitized = sanitized.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return sanitized.Length <= 1000 ? sanitized : sanitized[..1000] + "…";
    }

    private Task StartAsync()
    {
        _exceptionReceiver = _webView.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown");
        _consoleReceiver = _webView.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled");
        _logReceiver = _webView.GetDevToolsProtocolEventReceiver("Log.entryAdded");
        _networkFailureReceiver = _webView.GetDevToolsProtocolEventReceiver("Network.loadingFailed");
        _exceptionReceiver.DevToolsProtocolEventReceived += ExceptionReceived;
        _consoleReceiver.DevToolsProtocolEventReceived += ConsoleReceived;
        _logReceiver.DevToolsProtocolEventReceived += LogReceived;
        _networkFailureReceiver.DevToolsProtocolEventReceived += NetworkFailureReceived;
        Write("diagnostics-started", new
        {
            page_modification = false,
            captures = new[] { "runtime-exceptions", "console-errors", "browser-log-errors", "network-failure-category", "navigation-failures", "process-failures" },
            excludes = new[] { "headers", "cookies", "request-bodies", "response-bodies", "websocket-payloads", "html", "form-values" }
        });
        _ = EnableDomainsAsync();
        return Task.CompletedTask;
    }

    private async Task EnableDomainsAsync()
    {
        try
        {
            // WebView2 can still be bringing up its GPU/utility processes immediately after
            // EnsureCoreWebView2Async. Diagnostic protocol activation must not race that startup.
            await Task.Delay(1000);
            if (_disposed) return;
            await _webView.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
            await _webView.CallDevToolsProtocolMethodAsync("Log.enable", "{}");
            await _webView.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
            Write("devtools-domains-enabled", new { domains = new[] { "Runtime", "Log", "Network" } });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Write("devtools-domain-enable-failed", new { error = SanitizeDiagnosticText(exception.Message) });
        }
    }

    private void ExceptionReceived(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        HandleProtocolEvent(() =>
        {
            using JsonDocument document = JsonDocument.Parse(args.ParameterObjectAsJson);
            JsonElement details = document.RootElement.GetProperty("exceptionDetails");
            string? description = null;
            if (details.TryGetProperty("exception", out JsonElement exception) &&
                exception.TryGetProperty("description", out JsonElement descriptionElement))
                description = descriptionElement.GetString();
            if (string.IsNullOrWhiteSpace(description) && details.TryGetProperty("text", out JsonElement text))
                description = text.GetString();
            Write("javascript-exception", new
            {
                description = SanitizeDiagnosticText(description),
                source = details.TryGetProperty("url", out JsonElement url) ? SafeLocation(url.GetString()) : null,
                line = details.TryGetProperty("lineNumber", out JsonElement line) ? line.GetInt32() + 1 : (int?)null,
                column = details.TryGetProperty("columnNumber", out JsonElement column) ? column.GetInt32() + 1 : (int?)null
            });
        });
    }

    private void ConsoleReceived(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        HandleProtocolEvent(() =>
        {
            using JsonDocument document = JsonDocument.Parse(args.ParameterObjectAsJson);
            JsonElement root = document.RootElement;
            string? type = root.TryGetProperty("type", out JsonElement typeElement) ? typeElement.GetString() : null;
            if (!string.Equals(type, "error", StringComparison.OrdinalIgnoreCase)) return;
            var parts = new List<string>();
            if (root.TryGetProperty("args", out JsonElement values))
            {
                foreach (JsonElement value in values.EnumerateArray().Take(4))
                {
                    if (value.TryGetProperty("value", out JsonElement scalar)) parts.Add(scalar.ToString());
                    else if (value.TryGetProperty("description", out JsonElement description)) parts.Add(description.GetString() ?? string.Empty);
                    else if (value.TryGetProperty("className", out JsonElement className)) parts.Add($"[{className.GetString()}]");
                }
            }
            Write("console-error", new { text = SanitizeDiagnosticText(string.Join(" ", parts)) });
        });
    }

    private void LogReceived(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        HandleProtocolEvent(() =>
        {
            using JsonDocument document = JsonDocument.Parse(args.ParameterObjectAsJson);
            JsonElement entry = document.RootElement.GetProperty("entry");
            string? level = entry.TryGetProperty("level", out JsonElement levelElement) ? levelElement.GetString() : null;
            if (!string.Equals(level, "error", StringComparison.OrdinalIgnoreCase)) return;
            Write("browser-log-error", new
            {
                source_kind = entry.TryGetProperty("source", out JsonElement sourceKind) ? sourceKind.GetString() : null,
                text = entry.TryGetProperty("text", out JsonElement text) ? SanitizeDiagnosticText(text.GetString()) : null,
                source = entry.TryGetProperty("url", out JsonElement url) ? SafeLocation(url.GetString()) : null,
                line = entry.TryGetProperty("lineNumber", out JsonElement line) ? line.GetInt32() : (int?)null
            });
        });
    }

    private void NetworkFailureReceived(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        HandleProtocolEvent(() =>
        {
            using JsonDocument document = JsonDocument.Parse(args.ParameterObjectAsJson);
            JsonElement root = document.RootElement;
            Write("resource-failed", new
            {
                resource_type = root.TryGetProperty("type", out JsonElement type) ? type.GetString() : null,
                error = root.TryGetProperty("errorText", out JsonElement error) ? SanitizeDiagnosticText(error.GetString()) : null,
                canceled = root.TryGetProperty("canceled", out JsonElement canceled) && canceled.GetBoolean(),
                blocked_reason = root.TryGetProperty("blockedReason", out JsonElement blocked) ? blocked.GetString() : null
            });
        });
    }

    private static void HandleProtocolEvent(Action action)
    {
        try { action(); }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
    }

    private void Write(string kind, object details)
    {
        if (_disposed) return;
        var entry = new
        {
            schema = Schema,
            timestamp_utc = DateTimeOffset.UtcNow,
            account = _accountNumber,
            kind,
            details
        };
        try
        {
            lock (_writeLock)
                File.AppendAllText(_logPath, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static object? SafeLocation(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)) return null;
        if (uri.Scheme is "http" or "https")
            return new { origin = uri.GetLeftPart(UriPartial.Authority), file = Path.GetFileName(uri.AbsolutePath) };
        if (uri.Scheme == "file") return new { origin = "file", file = Path.GetFileName(uri.LocalPath) };
        return new { origin = uri.Scheme, file = (string?)null };
    }

    private static string SafeLocationText(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)) return "[source]";
        string origin = uri.Scheme == "file" ? "file" : uri.GetLeftPart(UriPartial.Authority);
        string file = uri.Scheme == "file" ? Path.GetFileName(uri.LocalPath) : Path.GetFileName(uri.AbsolutePath);
        return string.IsNullOrWhiteSpace(file) ? $"[source:{origin}]" : $"[source:{origin}/{file}]";
    }

    [GeneratedRegex(@"(?i)\b(authorization|bearer|access[_-]?token|refresh[_-]?token|password|passwd|cookie|secret)\s*[:=]\s*[^\s,;]+")]
    private static partial Regex CredentialAssignmentRegex();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}(?:\.[A-Za-z0-9_-]{10,})?\b")]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"\b[A-Fa-f0-9]{48,}\b|\b[A-Za-z0-9_+/=-]{64,}\b")]
    private static partial Regex LongSecretRegex();

    [GeneratedRegex(@"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?i)\b(?:https?|file)://[^\s\]\[\)\(\}\{\""']+")]
    private static partial Regex UrlRegex();
}
