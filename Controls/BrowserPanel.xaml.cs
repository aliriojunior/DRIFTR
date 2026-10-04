using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using PokeQuad.Config;
using PokeQuad.Models;
using PokeQuad.Services;

namespace PokeQuad.Controls;

public sealed class BrowserShortcutEventArgs(Key key, ModifierKeys modifiers) : EventArgs
{
    public Key Key { get; } = key;
    public ModifierKeys Modifiers { get; } = modifiers;
    public bool Handled { get; set; }
}

public partial class BrowserPanel : UserControl, IDisposable
{
    private static readonly Brush NormalBorderBrush = MakeBrush(41, 48, 59);
    private static readonly Brush LoadingBrush = MakeBrush(219, 171, 89);
    private static readonly Brush ReadyBrush = MakeBrush(103, 201, 165);
    private static readonly Brush ErrorBrush = MakeBrush(225, 105, 116);
    private static readonly Brush MutedBrush = MakeBrush(137, 147, 163);
    private static readonly Brush[] AccentBrushes =
    [
        MakeBrush(103, 201, 165),
        MakeBrush(104, 157, 220),
        MakeBrush(174, 132, 206),
        MakeBrush(205, 153, 91)
    ];

    private bool _isInitialized;
    private bool _isInitializing;
    private bool _isDisposed;
    private bool _sessionEnabled;
    private bool _browserProcessUnavailable;
    private CoreWebView2Environment? _environment;
    private BrowserSessionDiagnostics? _sessionDiagnostics;
    private int _coreEventHandlerCount;
    private int _wpfEventHandlerCount = 3;
    private double _zoomFactor = 1.0;
    private int _accentIndex;
    private string? _initialNavigationTarget;
    private bool _firstNavigationRetryAttempted;
    private bool _initialNavigationIssued;
    private bool _clearDiskCacheBeforeInitialNavigation;
    private bool _repairCacheClearCompleted;
    private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _firstNavigation = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public BrowserPanel()
    {
        InitializeComponent();
        SetAccountName($"Account {AccountNumber}");
        SetStatus("LOADING", LoadingBrush);
        SetAccentIndex(0, false);
        Loaded += BrowserPanel_Loaded;
        Browser.PreviewKeyDown += Browser_PreviewKeyDown;
        PreviewMouseDown += BrowserPanel_PreviewMouseDown;
    }

    public int AccountNumber
    {
        get => (int)GetValue(AccountNumberProperty);
        set => SetValue(AccountNumberProperty, value);
    }

    public static readonly DependencyProperty AccountNumberProperty = DependencyProperty.Register(
        nameof(AccountNumber), typeof(int), typeof(BrowserPanel),
        new PropertyMetadata(1, (dependencyObject, args) =>
        {
            if (dependencyObject is BrowserPanel panel)
            {
                panel.SetAccountName($"Account {(int)args.NewValue}");
            }
        }));

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
        nameof(IsSelected), typeof(bool), typeof(BrowserPanel),
        new PropertyMetadata(false, (dependencyObject, args) =>
        {
            if (dependencyObject is BrowserPanel panel)
            {
                panel.UpdateSelectionVisual();
            }
        }));

    public string AccountName { get; private set; } = string.Empty;
    public bool IsBrowserInitialized => _isInitialized;
    public bool IsDisposed => _isDisposed;
    public double ZoomFactor => _zoomFactor;
    public int AccentIndex => _accentIndex;
    public bool RepairCacheClearCompleted => _repairCacheClearCompleted;
    public string ProfilePath => AppConfig.GetProfileDirectory(AccountNumber);

    public AccountMemorySnapshot CaptureMemoryDiagnostics(bool isDetached)
    {
        var processes = new List<WebViewProcessMemorySnapshot>();
        if (_environment is not null)
        {
            try
            {
                foreach (CoreWebView2ProcessInfo processInfo in _environment.GetProcessInfos())
                {
                    long? workingSet = null;
                    long? privateMemory = null;
                    try
                    {
                        using Process process = Process.GetProcessById(processInfo.ProcessId);
                        process.Refresh();
                        workingSet = process.WorkingSet64;
                        privateMemory = process.PrivateMemorySize64;
                    }
                    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // Chromium processes can exit between the WebView2 snapshot and process inspection.
                    }

                    processes.Add(new WebViewProcessMemorySnapshot(
                        processInfo.ProcessId,
                        processInfo.Kind.ToString(),
                        workingSet,
                        privateMemory));
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // The environment can become unavailable during renderer/browser shutdown.
            }
        }

        int? browserProcessId = null;
        if (_isInitialized && !_browserProcessUnavailable)
        {
            try
            {
                if (Browser.CoreWebView2 is { } coreWebView)
                    browserProcessId = (int)coreWebView.BrowserProcessId;
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        }

        return new AccountMemorySnapshot(
            AccountNumber,
            _isInitialized,
            isDetached,
            IsVisible,
            RuntimeHelpers.GetHashCode(this),
            RuntimeHelpers.GetHashCode(Browser),
            _environment is null ? null : RuntimeHelpers.GetHashCode(_environment),
            browserProcessId,
            _coreEventHandlerCount,
            "Processes returned by this account's distinct CoreWebView2Environment/user-data folder.",
            processes,
            _wpfEventHandlerCount);
    }

    public void SetInitialNavigationTarget(string? target)
    {
        if (_isInitializing || _isInitialized) throw new InvalidOperationException("The navigation target must be set before initialization.");
        _initialNavigationTarget = IsSafeNavigationTarget(target) ? target : null;
    }

    public string? CaptureSafeNavigationTarget()
    {
        if (!_isInitialized || _browserProcessUnavailable) return null;
        try
        {
            string target = Browser.Source?.AbsoluteUri ?? Browser.CoreWebView2.Source;
            return IsSafeNavigationTarget(target) ? target : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    public async Task WaitForInitializationAsync(TimeSpan timeout) => await _initialized.Task.WaitAsync(timeout);

    public async Task WaitForFirstNavigationAsync(TimeSpan timeout) => await _firstNavigation.Task.WaitAsync(timeout);

    public event EventHandler? Activated;
    public event EventHandler? MaximizeRequested;
    public event EventHandler? PopOutRequested;
    public event EventHandler? RenameRequested;
    public event EventHandler? RepairRequested;
    public event EventHandler? ZoomChanged;
    public event EventHandler? AccentChanged;
    public event EventHandler<BrowserShortcutEventArgs>? ShortcutRequested;

    public void SetSessionEnabled(bool enabled)
    {
        _sessionEnabled = enabled;
        PopOutButton.IsEnabled = enabled;
        MinimalPopOutButton.IsEnabled = enabled;
        MaximizeButton.IsEnabled = enabled;
        OptionsButton.IsEnabled = enabled;
        MinimalOptionsButton.IsEnabled = enabled;
        if (!enabled && !_isInitialized)
        {
            SetStatus("LOCKED", MutedBrush);
            StatusTitle.Text = "Pro session";
            StatusMessage.Text = "Upgrade to use this independent browser profile.";
            StatusOverlay.Visibility = Visibility.Visible;
            RetryButton.Visibility = Visibility.Collapsed;
            return;
        }

        if (enabled && IsLoaded && !_isInitialized && !_isInitializing)
        {
            _ = InitializeBrowserAsync();
        }
    }

    public void SetDetached(bool detached)
    {
        PopOutButton.Visibility = detached ? Visibility.Collapsed : Visibility.Visible;
        MinimalPopOutButton.Visibility = detached ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetAccountName(string accountName)
    {
        AccountName = accountName;
        string displayName = accountName.ToUpperInvariant();
        AccountLabel.Text = displayName;
        MinimalAccountLabel.Text = displayName;
    }

    public void SetToolbarVisible(bool visible)
    {
        FullToolbar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        MinimalToolbar.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
    }

    public void ConfigureRepairInitialization()
    {
        if (_isInitializing || _isInitialized)
            throw new InvalidOperationException("Repair initialization must be configured before browser startup.");
        _clearDiskCacheBeforeInitialNavigation = true;
    }

    public void SetLifecycleControlsEnabled(bool enabled)
    {
        PopOutButton.IsEnabled = enabled && _sessionEnabled;
        MinimalPopOutButton.IsEnabled = enabled && _sessionEnabled;
        MaximizeButton.IsEnabled = enabled && _sessionEnabled;
        OptionsButton.IsEnabled = enabled && _sessionEnabled;
        MinimalOptionsButton.IsEnabled = enabled && _sessionEnabled;
    }

    public void ShowRepairFailure()
    {
        ShowError("Session repair did not complete",
            "Your browser data was not reset. Try again or restart DRIFTR.");
        RetryButton.Visibility = Visibility.Collapsed;
    }

    public void SetZoom(double zoomFactor, bool notify = false)
    {
        double normalized = Math.Round(Math.Clamp(zoomFactor, 0.5, 1.5), 1);
        if (Math.Abs(normalized - _zoomFactor) < 0.001 && !notify)
        {
            ZoomLabel.Text = $"{normalized * 100:0}%";
            return;
        }

        _zoomFactor = normalized;
        ZoomLabel.Text = $"{_zoomFactor * 100:0}%";
        if (_isInitialized)
        {
            Browser.ZoomFactor = _zoomFactor;
        }

        if (notify)
        {
            ZoomChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ZoomIn() => SetZoom(_zoomFactor + 0.1, true);
    public void ZoomOut() => SetZoom(_zoomFactor - 0.1, true);
    public void ResetZoom() => SetZoom(1.0, true);

    public void SetAccentIndex(int accentIndex, bool notify = false)
    {
        _accentIndex = ((accentIndex % AccentBrushes.Length) + AccentBrushes.Length) % AccentBrushes.Length;
        Brush accent = AccentBrushes[_accentIndex];
        AccentStrip.Background = accent;
        AccentButton.Foreground = accent;
        UpdateSelectionVisual();

        if (notify)
        {
            AccentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Refresh()
    {
        if (_isInitialized && !_browserProcessUnavailable)
        {
            TryBrowserAction(Browser.Reload, "The page could not be refreshed.");
        }
        else if (_browserProcessUnavailable)
        {
            ShowBrowserRestartRequired();
        }
    }

    private static SolidColorBrush MakeBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private void UpdateSelectionVisual()
    {
        PanelBorder.BorderBrush = IsSelected ? AccentBrushes[_accentIndex] : NormalBorderBrush;
        PanelBorder.BorderThickness = IsSelected ? new Thickness(1.5) : new Thickness(1);
    }

    private void SetStatus(string status, Brush brush)
    {
        StatusLabel.Text = status;
        MinimalStatusLabel.Text = status;
        StatusLabel.Foreground = brush;
        MinimalStatusLabel.Foreground = brush;
        StatusDot.Fill = brush;
        MinimalStatusDot.Fill = brush;
    }

    private async void BrowserPanel_Loaded(object sender, RoutedEventArgs e)
    {
        if (_sessionEnabled)
        {
            await InitializeBrowserAsync();
        }
    }

    private async Task InitializeBrowserAsync()
    {
        if (!_sessionEnabled || _isInitialized || _isInitializing || _isDisposed)
        {
            return;
        }

        _isInitializing = true;
        SetStatus("LOADING", LoadingBrush);
        RetryButton.Visibility = Visibility.Collapsed;
        StatusTitle.Text = "Starting browser...";
        StatusMessage.Text = "Preparing an independent account profile";
        StatusOverlay.Visibility = Visibility.Visible;

        try
        {
            _environment = await BrowserEnvironmentService.CreateForAccountAsync(AccountNumber);
            await Browser.EnsureCoreWebView2Async(_environment);

            Browser.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
            Browser.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            Browser.CoreWebView2.HistoryChanged += CoreWebView2_HistoryChanged;
            Browser.CoreWebView2.ProcessFailed += CoreWebView2_ProcessFailed;
            _coreEventHandlerCount += 4;
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = true;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = true;
            if (_clearDiskCacheBeforeInitialNavigation)
            {
                await Browser.CoreWebView2.Profile.ClearBrowsingDataAsync(SessionRepairPolicy.BrowsingDataToClear);
                _repairCacheClearCompleted = true;
            }
            _sessionDiagnostics = await BrowserSessionDiagnostics.TryStartAsync(Browser.CoreWebView2, AccountNumber);

            Browser.ZoomFactor = _zoomFactor;
            bool diagnosticInitialization = _initialNavigationTarget is not null || AppConfig.SkipInitialNavigationForDiagnostics;
            if (diagnosticInitialization)
            {
                // Recreated diagnostic controls can otherwise race WebView2's initial about:blank navigation.
                await Task.Delay(250);
            }
            if (_isDisposed) return;
            _isInitialized = true;
            if (AppConfig.SkipInitialNavigationForDiagnostics)
            {
                _initialized.TrySetResult();
                _firstNavigation.TrySetResult();
                _browserProcessUnavailable = false;
                SetStatus("READY", ReadyBrush);
                StatusOverlay.Visibility = Visibility.Collapsed;
                return;
            }
            _initialNavigationIssued = true;
            Browser.CoreWebView2.Navigate(_initialNavigationTarget ?? AppConfig.BrowserHomeUrl);
            _initialized.TrySetResult();
            if (diagnosticInitialization)
            {
                await Task.Delay(100);
                TryCompleteFirstNavigationAtTarget();
            }
        }
        catch (Exception ex)
        {
            _initialized.TrySetException(ex);
            _firstNavigation.TrySetException(ex);
            ShowError(
                "Browser could not start",
                ex is WebView2RuntimeNotFoundException
                    ? "Microsoft Edge WebView2 Runtime is not installed. Install the Evergreen Runtime, then retry."
                    : $"Account {AccountNumber} could not initialize. {ex.Message}");
        }
        finally
        {
            _isInitializing = false;
        }
    }

    private void CoreWebView2_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        _sessionDiagnostics?.RecordNavigationStarting(e.Uri, e.NavigationId, e.IsRedirected);
        SetStatus("LOADING", LoadingBrush);
        StatusTitle.Text = "Loading...";
        StatusMessage.Text = e.Uri;
        StatusOverlay.Visibility = Visibility.Visible;
        RetryButton.Visibility = Visibility.Collapsed;
    }

    private void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _sessionDiagnostics?.RecordNavigationCompleted(e);
        UpdateNavigationButtons();
        if (e.IsSuccess)
        {
            _browserProcessUnavailable = false;
            SetStatus("READY", ReadyBrush);
            StatusOverlay.Visibility = Visibility.Collapsed;
            if (_initialNavigationIssued) _firstNavigation.TrySetResult();
            return;
        }

        if (_initialNavigationIssued && e.WebErrorStatus == CoreWebView2WebErrorStatus.ConnectionAborted &&
            !_firstNavigationRetryAttempted)
        {
            _firstNavigationRetryAttempted = true;
            Dispatcher.BeginInvoke(async () =>
            {
                await Task.Delay(250);
                if (!_isDisposed && _isInitialized)
                {
                    _initialNavigationIssued = true;
                    Browser.CoreWebView2.Navigate(_initialNavigationTarget ?? AppConfig.BrowserHomeUrl);
                    await Task.Delay(100);
                    TryCompleteFirstNavigationAtTarget();
                }
            });
            return;
        }
        if (_initialNavigationIssued)
            _firstNavigation.TrySetException(new InvalidOperationException($"Navigation failed ({e.WebErrorStatus})."));
        ShowError("Page could not be loaded", $"Navigation failed ({e.WebErrorStatus}). Check your connection and try again.");
    }

    private void CoreWebView2_HistoryChanged(object? sender, object e) => UpdateNavigationButtons();

    private void CoreWebView2_ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        _sessionDiagnostics?.RecordProcessFailed(e);
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                _browserProcessUnavailable = true;
                ShowBrowserRestartRequired();
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
                ShowError("Page process stopped", "Reload this account to start a fresh page process. Your profile and session data are safe.");
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                ShowError("Page is not responding", "The page process is busy or unresponsive. You can wait or reload this account.");
                break;
        }
    }

    private void Browser_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var shortcut = new BrowserShortcutEventArgs(e.Key, Keyboard.Modifiers);
        ShortcutRequested?.Invoke(this, shortcut);
        e.Handled = shortcut.Handled;
    }

    private void UpdateNavigationButtons()
    {
        if (_isInitialized && !_browserProcessUnavailable)
        {
            TryBrowserAction(
                () =>
                {
                    BackButton.IsEnabled = Browser.CoreWebView2.CanGoBack;
                    ForwardButton.IsEnabled = Browser.CoreWebView2.CanGoForward;
                },
                "Navigation history is temporarily unavailable.");
        }
    }

    private void ShowError(string title, string message)
    {
        SetStatus("ERROR", ErrorBrush);
        StatusTitle.Text = title;
        StatusMessage.Text = message;
        RetryButton.Visibility = Visibility.Visible;
        StatusOverlay.Visibility = Visibility.Visible;
    }

    private void ShowBrowserRestartRequired()
    {
        ShowError(
            "Browser restart required",
            $"Account {AccountNumber}'s browser process stopped. Close and reopen DRIFTR to recover it. The profile and session data have not been deleted.");
        RetryButton.Content = "Restart DRIFTR";
    }

    private bool TryBrowserAction(Action action, string failureMessage)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            ShowError("Browser action failed", $"{failureMessage} {ex.Message}");
            return false;
        }
    }

    private void BrowserPanel_PreviewMouseDown(object sender, MouseButtonEventArgs e) =>
        Activated?.Invoke(this, EventArgs.Empty);

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        Activated?.Invoke(this, EventArgs.Empty);
        if (_isInitialized && !_browserProcessUnavailable)
        {
            TryBrowserAction(
                () =>
                {
                    if (Browser.CoreWebView2.CanGoBack)
                    {
                        Browser.CoreWebView2.GoBack();
                    }
                },
                "Back navigation failed.");
        }
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        Activated?.Invoke(this, EventArgs.Empty);
        if (_isInitialized && !_browserProcessUnavailable)
        {
            TryBrowserAction(
                () =>
                {
                    if (Browser.CoreWebView2.CanGoForward)
                    {
                        Browser.CoreWebView2.GoForward();
                    }
                },
                "Forward navigation failed.");
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        Activated?.Invoke(this, EventArgs.Empty);
        Refresh();
    }

    private void HomeButton_Click(object sender, RoutedEventArgs e)
    {
        Activated?.Invoke(this, EventArgs.Empty);
        if (_isInitialized && !_browserProcessUnavailable)
        {
            TryBrowserAction(
                () => Browser.CoreWebView2.Navigate(AppConfig.BrowserHomeUrl),
                "Home navigation failed.");
        }
        else if (_browserProcessUnavailable)
        {
            ShowBrowserRestartRequired();
        }
    }

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
    {
        Activated?.Invoke(this, EventArgs.Empty);
        ZoomOut();
    }

    private void ZoomInButton_Click(object sender, RoutedEventArgs e)
    {
        Activated?.Invoke(this, EventArgs.Empty);
        ZoomIn();
    }

    private void AccentButton_Click(object sender, RoutedEventArgs e)
    {
        Activated?.Invoke(this, EventArgs.Empty);
        SetAccentIndex(_accentIndex + 1, true);
    }

    private void RenameButton_Click(object sender, RoutedEventArgs e)
    {
        Activated?.Invoke(this, EventArgs.Empty);
        RenameRequested?.Invoke(this, EventArgs.Empty);
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        Activated?.Invoke(this, EventArgs.Empty);
        MaximizeRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OptionsButton_Click(object sender, RoutedEventArgs e)
    {
        Activated?.Invoke(this, EventArgs.Empty);
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    private void RepairSessionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Activated?.Invoke(this, EventArgs.Empty);
        if (_sessionEnabled && _isInitialized && !_browserProcessUnavailable)
            RepairRequested?.Invoke(this, EventArgs.Empty);
    }

    private void PopOutButton_Click(object sender, RoutedEventArgs e)
    {
        Activated?.Invoke(this, EventArgs.Empty);
        if (_sessionEnabled)
        {
            PopOutRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_browserProcessUnavailable)
        {
            ShowBrowserRestartRequired();
        }
        else if (_isInitialized)
        {
            RetryButton.Content = "Retry";
            TryBrowserAction(Browser.Reload, "The page could not be reloaded.");
        }
        else
        {
            await InitializeBrowserAsync();
        }
    }

    public async Task<BrowserShutdownResult> ShutdownAsync(TimeSpan timeout)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        if (_isDisposed)
        {
            return new BrowserShutdownResult(started, DateTimeOffset.UtcNow, false, true, [], [], null);
        }

        CoreWebView2Environment? environment = _environment;
        int[] processIds = CaptureMemoryDiagnostics(false).Processes.Select(process => process.ProcessId).Distinct().ToArray();
        TaskCompletionSource browserExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<CoreWebView2BrowserProcessExitedEventArgs>? exitedHandler = null;
        bool exitObserved = false;
        string? error = null;

        if (environment is not null && !_browserProcessUnavailable)
        {
            exitedHandler = (_, _) => { exitObserved = true; browserExited.TrySetResult(); };
            environment.BrowserProcessExited += exitedHandler;
        }

        DisposeCore();

        if (exitedHandler is not null && environment is not null)
        {
            try
            {
                await browserExited.Task.WaitAsync(timeout);
            }
            catch (TimeoutException exception)
            {
                error = exception.Message;
            }
            finally
            {
                environment.BrowserProcessExited -= exitedHandler;
            }
        }

        int[] remaining = processIds.Where(IsProcessAlive).ToArray();
        return new BrowserShutdownResult(started, DateTimeOffset.UtcNow, exitObserved, remaining.Length == 0,
            processIds, remaining, error);
    }

    public void Dispose()
    {
        DisposeCore();
        GC.SuppressFinalize(this);
    }

    private void DisposeCore()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Loaded -= BrowserPanel_Loaded;
        PreviewMouseDown -= BrowserPanel_PreviewMouseDown;
        Browser.PreviewKeyDown -= Browser_PreviewKeyDown;
        _wpfEventHandlerCount = 0;

        if (_isInitialized)
        {
            try
            {
                Browser.CoreWebView2.NavigationStarting -= CoreWebView2_NavigationStarting;
                Browser.CoreWebView2.NavigationCompleted -= CoreWebView2_NavigationCompleted;
                Browser.CoreWebView2.HistoryChanged -= CoreWebView2_HistoryChanged;
                Browser.CoreWebView2.ProcessFailed -= CoreWebView2_ProcessFailed;
                _coreEventHandlerCount = Math.Max(0, _coreEventHandlerCount - 4);
            }
            catch (Exception)
            {
                // A crashed browser process may already have released its event source.
            }
        }

        _sessionDiagnostics?.Dispose();
        _sessionDiagnostics = null;
        Browser.Dispose();
        _environment = null;
        _isInitialized = false;
    }

    private static bool IsSafeNavigationTarget(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
        uri.Scheme is "http" or "https" or "file" &&
        string.IsNullOrEmpty(uri.UserInfo);

    private void TryCompleteFirstNavigationAtTarget()
    {
        if (_firstNavigation.Task.IsCompleted || !_isInitialized || _browserProcessUnavailable) return;
        try
        {
            string intended = _initialNavigationTarget ?? AppConfig.BrowserHomeUrl;
            string current = Browser.CoreWebView2.Source;
            if (Uri.TryCreate(intended, UriKind.Absolute, out Uri? intendedUri) &&
                Uri.TryCreate(current, UriKind.Absolute, out Uri? currentUri) &&
                Uri.Compare(intendedUri, currentUri, UriComponents.AbsoluteUri, UriFormat.SafeUnescaped,
                    StringComparison.OrdinalIgnoreCase) == 0)
                _firstNavigation.TrySetResult();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
