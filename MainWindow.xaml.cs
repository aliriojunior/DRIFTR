using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PokeQuad.Controls;
using PokeQuad.Models;
using PokeQuad.Services;

namespace PokeQuad;

public partial class MainWindow : Window
{
    private static readonly Brush ActiveButtonBrush = MakeBrush(44, 78, 70);
    private static readonly Brush NormalButtonBrush = MakeBrush(25, 31, 42);
    private readonly SettingsService _settingsService = new();
    private readonly AppSettings _settings;
    private readonly LicensingSession _licensingSession;
    private readonly int _maxSessions;
    private readonly PopOutSessionState _popOutState;
    private readonly MemoryDiagnosticsService? _memoryDiagnostics;
    private readonly MemoryDiagnosticsOptions _diagnosticsOptions;
    private readonly SessionRestartDiagnosticsLogger? _restartDiagnostics;
    private readonly CancellationTokenSource _restartSamplingCancellation = new();
    private readonly CancellationTokenSource _updateCheckCancellation = new();
    private readonly GitHubReleaseMetadataProvider _releaseProvider = new();
    private readonly UpdateChecker _updateChecker;
    private readonly Dictionary<int, DetachedBrowserWindow> _detachedWindows = [];
    private readonly Dictionary<int, DetachedWindowSettings> _savedDetachedBounds = [];
    private BrowserPanel[] _panels = [];
    private Button[] _accountButtons = [];
    private int _selectedAccount = 1;
    private int _duoAccount1 = 1;
    private int _duoAccount2 = 2;
    private DuoOrientation _duoOrientation = DuoOrientation.Horizontal;
    private ThreeSessionLayout _threeLayout = ThreeSessionLayout.ThreeLargeLeft;
    private int _threeLargeAccount = 1;
    private LayoutMode _layout = LayoutMode.Solo;
    private bool _toolbarVisible = true;
    private bool _isFullscreen;
    private bool _shutdownStarted;
    private bool _shutdownComplete;
    private bool _restartInProgress;
    private bool _repairInProgress;
    private WindowState _windowStateBeforeFullscreen = WindowState.Normal;

    public MainWindow(LicensingSession licensingSession)
    {
        _updateChecker = new UpdateChecker(_releaseProvider);
        _licensingSession = licensingSession;
        _maxSessions = EntitlementPolicy.NormalizeMaxSessions(licensingSession.License.MaxSessions);
        _popOutState = new PopOutSessionState(_maxSessions);
        InitializeComponent();
        _settings = _settingsService.Load();
        NormalizeSettings();
        _panels = [Account1Panel, Account2Panel, Account3Panel, Account4Panel];
        _accountButtons = [Account1Button, Account2Button, Account3Button, Account4Button];

        for (int index = 0; index < _panels.Length; index++)
        {
            BrowserPanel panel = _panels[index];
            panel.SetSessionEnabled(index < _maxSessions);
            SubscribePanel(panel);
            panel.SetAccountName(_settings.AccountNames[index]);
            panel.SetZoom(_settings.AccountZoomLevels[index]);
            panel.SetAccentIndex(_settings.AccountAccentIndexes[index]);
        }

        _diagnosticsOptions = MemoryDiagnosticsOptions.FromEnvironment();
        _memoryDiagnostics = MemoryDiagnosticsService.TryCreate(
            Dispatcher,
            CaptureAccountMemoryDiagnostics,
            CurrentLayoutDiagnosticName,
            _diagnosticsOptions);
        _restartDiagnostics = _diagnosticsOptions.Enabled
            ? new SessionRestartDiagnosticsLogger(_diagnosticsOptions.OutputDirectory)
            : null;
        RestartSessionButton.Visibility = _diagnosticsOptions.Enabled ? Visibility.Visible : Visibility.Collapsed;

        EmailLabel.Text = licensingSession.Email;
        PlanLabel.Text = _maxSessions == 4 ? "DRIFTR PRO · 4 SESSIONS" : "DRIFTR FREE · 1 SESSION";
        ConfigureEntitlements();
        _toolbarVisible = _settings.ToolbarVisible;
        ApplyToolbarVisibility();
        RestoreWindowSettings();
        _selectedAccount = EntitlementPolicy.CanUseAccount(_settings.SelectedAccount, _maxSessions) ? _settings.SelectedAccount : 1;
        _duoAccount1 = Math.Clamp(_settings.DuoAccount1, 1, _maxSessions);
        _duoAccount2 = Math.Clamp(_settings.DuoAccount2, 1, _maxSessions);
        if (_duoAccount1 == _duoAccount2) _duoAccount2 = _duoAccount1 == 1 ? 2 : 1;
        _duoOrientation = Enum.TryParse(_settings.DuoOrientation, true, out DuoOrientation orientation)
            ? orientation
            : DuoOrientation.Horizontal;
        _threeLayout = WorkspaceLayoutPolicy.ParseThreeSessionLayout(_settings.ThreeSessionLayout);
        _threeLargeAccount = EntitlementPolicy.CanUseAccount(_settings.ThreeLargeAccount, _maxSessions)
            ? _settings.ThreeLargeAccount
            : 1;
        UpdateDuoOrientationButton();
        UpdateThreeLayoutButton();
        LayoutMode preferred = Enum.TryParse(_settings.PreferredLayout, true, out LayoutMode parsed) ? parsed : LayoutMode.Solo;
        ApplyLayout(EntitlementPolicy.EnforceLayout(preferred, _maxSessions));
        Loaded += MainWindow_Loaded;
    }

    public LicensingSession LicensingSession => _licensingSession;
    public event EventHandler? SignOutRequested;
    public event EventHandler? SignOutEverywhereRequested;
    public event EventHandler? DeactivateRequested;

    private static SolidColorBrush MakeBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private void NormalizeSettings()
    {
        string[] names = _settings.AccountNames ?? [];
        double[] zooms = _settings.AccountZoomLevels ?? [];
        int[] accents = _settings.AccountAccentIndexes ?? [];
        _settings.DetachedAccounts ??= [];
        _settings.DetachedWindows ??= [];
        foreach (DetachedWindowSettings bounds in _settings.DetachedWindows
                     .Where(bounds => EntitlementPolicy.CanUseAccount(bounds.AccountNumber, _maxSessions)))
        {
            _savedDetachedBounds[bounds.AccountNumber] = bounds;
        }
        _settings.AccountNames = Enumerable.Range(0, 4).Select(index =>
        {
            string value = index < names.Length ? names[index].Trim() : string.Empty;
            return string.IsNullOrWhiteSpace(value) ? $"Account {index + 1}" : value[..Math.Min(value.Length, 24)];
        }).ToArray();
        _settings.AccountZoomLevels = Enumerable.Range(0, 4).Select(index =>
            index < zooms.Length && double.IsFinite(zooms[index]) ? Math.Round(Math.Clamp(zooms[index], .5, 1.5), 1) : 1.0).ToArray();
        _settings.AccountAccentIndexes = Enumerable.Range(0, 4).Select(index =>
            index < accents.Length ? ((accents[index] % 4) + 4) % 4 : index).ToArray();
    }

    private void ConfigureEntitlements()
    {
        for (int index = 0; index < _accountButtons.Length; index++)
        {
            bool allowed = index < _maxSessions;
            _accountButtons[index].IsEnabled = allowed;
            _accountButtons[index].Content = allowed ? $"{index + 1}" : $"{index + 1} 🔒";
        }
        DuoButton.IsEnabled = EntitlementPolicy.CanUseLayout(LayoutMode.Duo, _maxSessions);
        DuoOrientationButton.IsEnabled = DuoButton.IsEnabled;
        QuadButton.IsEnabled = EntitlementPolicy.CanUseLayout(LayoutMode.Quad, _maxSessions);
        ThreeLayoutButton.IsEnabled = EntitlementPolicy.CanUseLayout(LayoutMode.Three, _maxSessions);
        UpdateDuoOrientationButton();
    }

    private void RestoreWindowSettings()
    {
        if (double.IsFinite(_settings.WindowWidth) && _settings.WindowWidth >= MinWidth) Width = _settings.WindowWidth;
        if (double.IsFinite(_settings.WindowHeight) && _settings.WindowHeight >= MinHeight) Height = _settings.WindowHeight;
        if (_settings.WindowLeft is double left && _settings.WindowTop is double top && IsPositionVisible(left, top))
        {
            WindowStartupLocation = WindowStartupLocation.Manual; Left = left; Top = top;
        }
        if (_settings.IsMaximized) WindowState = WindowState.Maximized;
    }

    private static bool IsPositionVisible(double left, double top) => double.IsFinite(left) && double.IsFinite(top) &&
        left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80 &&
        top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 80 &&
        left + 80 > SystemParameters.VirtualScreenLeft && top + 80 > SystemParameters.VirtualScreenTop;

    private void AccountButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string value } || !int.TryParse(value, out int account) ||
            !EntitlementPolicy.CanUseAccount(account, _maxSessions)) return;
        if (_detachedWindows.TryGetValue(account, out DetachedBrowserWindow? detached))
        {
            if (detached.WindowState == WindowState.Minimized) detached.WindowState = WindowState.Normal;
            detached.Activate();
            return;
        }
        if (_layout == LayoutMode.Duo)
        {
            int previouslySelected = _selectedAccount;
            if (account != _duoAccount1 && account != _duoAccount2)
            {
                if (previouslySelected == _duoAccount1) _duoAccount1 = account;
                else _duoAccount2 = account;
            }
            _selectedAccount = account;
            ShowDuo();
        }
        else if (_layout == LayoutMode.Three)
        {
            _threeLargeAccount = account;
            ShowThree();
        }
        else ShowSolo(account);
    }

    private void SoloButton_Click(object sender, RoutedEventArgs e) => ShowSolo(_selectedAccount);
    private void DuoButton_Click(object sender, RoutedEventArgs e) { if (DuoButton.IsEnabled) ShowDuo(); }
    private void DuoOrientationButton_Click(object sender, RoutedEventArgs e)
    {
        if (!DuoOrientationButton.IsEnabled) return;
        _duoOrientation = _duoOrientation == DuoOrientation.Horizontal
            ? DuoOrientation.Vertical
            : DuoOrientation.Horizontal;
        UpdateDuoOrientationButton();
        if (_layout == LayoutMode.Duo) ShowDuo();
    }
    private void QuadButton_Click(object sender, RoutedEventArgs e) { if (QuadButton.IsEnabled) ShowQuad(); }
    private void ThreeLayoutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_popOutState.DockedAccounts.Count != 3) return;
        ThreeSessionLayout[] layouts = Enum.GetValues<ThreeSessionLayout>();
        _threeLayout = layouts[(Array.IndexOf(layouts, _threeLayout) + 1) % layouts.Length];
        UpdateThreeLayoutButton();
        ShowThree();
    }
    private void RestoreAllButton_Click(object sender, RoutedEventArgs e) => RestoreAllDetached();
    private async void RestartSessionButton_Click(object sender, RoutedEventArgs e)
    {
        int account = _selectedAccount;
        if (!_diagnosticsOptions.Enabled || _restartInProgress ||
            !EntitlementPolicy.CanUseAccount(account, _maxSessions)) return;
        if (MessageBox.Show(this,
                $"Restart Account {account} browser session?\n\nThis will restart only this browser session. The website may require you to sign in again. Other DRIFTR sessions will remain running.",
                "Restart Session Diagnostic", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
            return;

        try
        {
            RestartSessionButton.IsEnabled = false;
            SessionRestartResult result = await DiagnosticRestartSessionAsync(account);
            MessageBox.Show(this,
                $"Account {account} browser lifecycle restarted.\nOld process group exited: {result.Shutdown.AllCapturedProcessesExited}\n" +
                $"Warm-up working-set change: {result.ReclaimedWorkingSetBytes / 1048576d:+0.0;-0.0;0.0} MB " +
                $"({result.ReclaimedWorkingSetPercent:+0.0;-0.0;0.0}%)." +
                (result.Succeeded ? string.Empty : $"\nNavigation did not complete: {result.Error}") +
                "\n\nThis is diagnostic evidence, not a leak conclusion.",
                "Restart Session Diagnostic", MessageBoxButton.OK,
                result.Succeeded ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Account {account} could not be restarted. Other sessions were left running.\n\n{exception.Message}",
                "Restart Session Diagnostic", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            RestartSessionButton.IsEnabled = true;
        }
    }
    private void ToolbarButton_Click(object sender, RoutedEventArgs e) => ToggleToolbars();
    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e) =>
        await CheckForUpdatesAsync(isManual: true);
    private void SignOutButton_Click(object sender, RoutedEventArgs e) => SignOutRequested?.Invoke(this, EventArgs.Empty);

    private void SignOutEverywhereButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Sign out of DRIFTR on every device? Browser profiles will not be deleted.",
                "Sign Out Everywhere", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            SignOutEverywhereRequested?.Invoke(this, EventArgs.Empty);
    }

    private void DeactivateButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Deactivate this device and sign out? Your browser profiles will remain on this computer.",
                "Deactivate Device", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            DeactivateRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ReloadAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Reload all available sessions?", "Reload Sessions", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            foreach (BrowserPanel panel in _panels.Take(_maxSessions)) panel.Refresh();
    }

    private void Panel_Activated(object? sender, EventArgs e)
    {
        if (sender is BrowserPanel panel && EntitlementPolicy.CanUseAccount(panel.AccountNumber, _maxSessions)) SelectAccount(panel.AccountNumber);
    }

    private void Panel_MaximizeRequested(object? sender, EventArgs e)
    {
        if (_repairInProgress) return;
        if (sender is not BrowserPanel panel || !EntitlementPolicy.CanUseAccount(panel.AccountNumber, _maxSessions)) return;
        if (_detachedWindows.TryGetValue(panel.AccountNumber, out DetachedBrowserWindow? detached))
        {
            detached.WindowState = detached.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }
        if (_layout == LayoutMode.Solo && _selectedAccount == panel.AccountNumber && _maxSessions == 4) ShowQuad();
        else ShowSolo(panel.AccountNumber);
    }

    private void Panel_PopOutRequested(object? sender, EventArgs e)
    {
        if (_repairInProgress) return;
        if (sender is BrowserPanel panel) DetachAccount(panel.AccountNumber);
    }

    private void Panel_RenameRequested(object? sender, EventArgs e)
    {
        if (sender is not BrowserPanel panel || !EntitlementPolicy.CanUseAccount(panel.AccountNumber, _maxSessions)) return;
        var dialog = new RenameAccountDialog(panel.AccountName) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            panel.SetAccountName(dialog.AccountName);
            _settings.AccountNames[panel.AccountNumber - 1] = dialog.AccountName;
        }
    }

    private async void Panel_RepairRequested(object? sender, EventArgs e)
    {
        if (sender is not BrowserPanel panel || _repairInProgress || _restartInProgress ||
            !panel.IsBrowserInitialized || !EntitlementPolicy.CanUseAccount(panel.AccountNumber, _maxSessions)) return;
        SelectAccount(panel.AccountNumber);
        var dialog = new RepairSessionDialog(panel.AccountNumber) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        try
        {
            SessionRepairResult result = await RepairSessionAsync(panel.AccountNumber);
            string message = result.NavigationCompleted
                ? $"Account {panel.AccountNumber} was repaired. Temporary browser cache for this session was cleared.\n\nIf the website asks, sign in again."
                : $"Account {panel.AccountNumber}'s temporary browser cache was cleared, but DRIFTR Home did not finish loading. Try Home or restart DRIFTR.";
            MessageBox.Show(this, message, "Repair Session", MessageBoxButton.OK,
                result.NavigationCompleted ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch
        {
            bool cacheWasCleared = _panels[panel.AccountNumber - 1].RepairCacheClearCompleted;
            MessageBox.Show(this,
                cacheWasCleared
                    ? "DRIFTR cleared this session's temporary cache, but couldn't reopen the session. Restart DRIFTR and try again."
                    : "DRIFTR couldn't repair this session. Your browser data was not reset. Try again or restart DRIFTR.",
                "Repair Session", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Panel_ZoomChanged(object? sender, EventArgs e) { if (sender is BrowserPanel panel) _settings.AccountZoomLevels[panel.AccountNumber - 1] = panel.ZoomFactor; }
    private void Panel_AccentChanged(object? sender, EventArgs e) { if (sender is BrowserPanel panel) _settings.AccountAccentIndexes[panel.AccountNumber - 1] = panel.AccentIndex; }
    private void Panel_ShortcutRequested(object? sender, BrowserShortcutEventArgs e) => e.Handled = HandleShortcut(e.Key, e.Modifiers);

    private void SelectAccount(int account)
    {
        if (!EntitlementPolicy.CanUseAccount(account, _maxSessions)) return;
        _selectedAccount = account;
        for (int index = 0; index < _panels.Length; index++) _panels[index].IsSelected = index == account - 1;
        UpdateHeaderButtons();
    }

    private void SubscribePanel(BrowserPanel panel)
    {
        panel.Activated += Panel_Activated;
        panel.MaximizeRequested += Panel_MaximizeRequested;
        panel.PopOutRequested += Panel_PopOutRequested;
        panel.RenameRequested += Panel_RenameRequested;
        panel.RepairRequested += Panel_RepairRequested;
        panel.ZoomChanged += Panel_ZoomChanged;
        panel.AccentChanged += Panel_AccentChanged;
        panel.ShortcutRequested += Panel_ShortcutRequested;
    }

    private void UnsubscribePanel(BrowserPanel panel)
    {
        panel.Activated -= Panel_Activated;
        panel.MaximizeRequested -= Panel_MaximizeRequested;
        panel.PopOutRequested -= Panel_PopOutRequested;
        panel.RenameRequested -= Panel_RenameRequested;
        panel.RepairRequested -= Panel_RepairRequested;
        panel.ZoomChanged -= Panel_ZoomChanged;
        panel.AccentChanged -= Panel_AccentChanged;
        panel.ShortcutRequested -= Panel_ShortcutRequested;
    }

    public async Task<SessionRepairResult> RepairSessionAsync(int accountNumber)
    {
        if (!EntitlementPolicy.CanUseAccount(accountNumber, _maxSessions))
            throw new InvalidOperationException($"Account {accountNumber} is not available for this entitlement.");
        if (_repairInProgress || _restartInProgress)
            throw new InvalidOperationException("Another session lifecycle operation is already in progress.");

        BrowserPanel oldPanel = _panels[accountNumber - 1];
        if (!oldPanel.IsBrowserInitialized)
            throw new InvalidOperationException($"Account {accountNumber} is not initialized.");

        _repairInProgress = true;
        Guid repairId = Guid.NewGuid();
        bool wasDetached = _detachedWindows.TryGetValue(accountNumber, out DetachedBrowserWindow? detachedWindow);
        DetachedWindowSettings? detachedBounds = wasDetached ? detachedWindow!.CaptureBounds() : null;
        string profileBefore = Path.GetFullPath(oldPanel.ProfilePath);
        AccountMemorySnapshot before = oldPanel.CaptureMemoryDiagnostics(wasDetached);
        AccountMemorySnapshot[] otherBefore = CaptureAccountMemoryDiagnostics()
            .Where(snapshot => snapshot.AccountNumber != accountNumber).ToArray();
        BrowserPanel? replacement = null;
        BrowserShutdownResult? shutdown = null;
        SetRepairUiState(true, accountNumber);
        await Dispatcher.Yield(DispatcherPriority.Render);

        try
        {
            UnsubscribePanel(oldPanel);
            if (wasDetached)
            {
                _savedDetachedBounds[accountNumber] = detachedBounds!;
                _detachedWindows.Remove(accountNumber);
                detachedWindow!.DockBackRequested -= DetachedWindow_DockBackRequested;
                _ = detachedWindow.ReleasePanel();
                detachedWindow.CloseWithoutDocking();
                detachedWindow = null;
            }
            else BrowserGrid.Children.Remove(oldPanel);

            shutdown = await oldPanel.ShutdownAsync(TimeSpan.FromSeconds(20));
            if (!shutdown.AllCapturedProcessesExited)
                throw new InvalidOperationException(
                    $"The old Account {accountNumber} WebView2 process group did not terminate naturally within the repair timeout.");

            replacement = CreateReplacementPanel(accountNumber, null, clearDiskCacheBeforeNavigation: true);
            replacement.IsEnabled = false;
            replacement.SetLifecycleControlsEnabled(false);
            // A WPF WebView2 initialized inside a detached top-level window may be torn down when
            // that source window later closes. Initialize it in the main visual tree, as normal
            // DRIFTR sessions are, and move it to a fresh detached host only after navigation.
            AttachReplacementPanel(accountNumber, replacement, wasDetached: false, detachedWindow: null);
            if (wasDetached)
            {
                replacement.Visibility = Visibility.Visible;
                replacement.Opacity = 0;
                replacement.IsHitTestVisible = false;
            }
            await replacement.WaitForInitializationAsync(TimeSpan.FromSeconds(90));
            if (!replacement.RepairCacheClearCompleted)
                throw new InvalidOperationException("WebView2 did not confirm that the temporary disk cache was cleared.");

            string? navigationError = null;
            try { await replacement.WaitForFirstNavigationAsync(TimeSpan.FromSeconds(30)); }
            catch (Exception exception) when (exception is TimeoutException or InvalidOperationException)
            {
                navigationError = exception.Message;
            }

            if (wasDetached) MoveReplacementToDetachedWindow(accountNumber, replacement);

            AccountMemorySnapshot after = replacement.CaptureMemoryDiagnostics(wasDetached);
            VerifyIsolation(accountNumber, before, after, otherBefore);
            string profileAfter = Path.GetFullPath(replacement.ProfilePath);
            if (!StringComparer.OrdinalIgnoreCase.Equals(profileBefore, profileAfter))
                throw new InvalidOperationException("The replacement browser profile path changed unexpectedly.");

            return new SessionRepairResult(repairId, accountNumber, wasDetached, profileBefore, profileAfter,
                before, after, shutdown, true, navigationError is null, navigationError);
        }
        catch
        {
            if (replacement is null)
            {
                replacement = CreateReplacementPanel(accountNumber, null, enableSession: false);
                AttachReplacementPanel(accountNumber, replacement, wasDetached: false, detachedWindow: null);
            }
            if (wasDetached && !_detachedWindows.ContainsKey(accountNumber))
                MoveReplacementToDetachedWindow(accountNumber, replacement);
            if (!replacement.IsBrowserInitialized) replacement.ShowRepairFailure();
            throw;
        }
        finally
        {
            _repairInProgress = false;
            SetRepairUiState(false, accountNumber);
            UpdateHeaderButtons();
        }
    }

    private void SetRepairUiState(bool repairing, int accountNumber)
    {
        AppTopBar.IsEnabled = !repairing;
        foreach (BrowserPanel panel in _panels)
        {
            panel.SetLifecycleControlsEnabled(!repairing);
            if (panel.AccountNumber == accountNumber) panel.IsEnabled = !repairing;
        }
        foreach (DetachedBrowserWindow detached in _detachedWindows.Values)
            detached.SetDockingEnabled(!repairing);
    }

    public async Task<SessionRestartResult> DiagnosticRestartSessionAsync(int accountNumber)
    {
        if (!_diagnosticsOptions.Enabled)
            throw new InvalidOperationException("Session restart is available only when DRIFTR_MEMORY_DIAGNOSTICS=1.");
        if (!EntitlementPolicy.CanUseAccount(accountNumber, _maxSessions))
            throw new InvalidOperationException($"Account {accountNumber} is not available for this entitlement.");
        if (_restartInProgress || _repairInProgress)
            throw new InvalidOperationException("Another diagnostic session restart is already in progress.");

        _restartInProgress = true;
        Guid restartId = Guid.NewGuid();
        BrowserPanel oldPanel = _panels[accountNumber - 1];
        bool wasDetached = _detachedWindows.TryGetValue(accountNumber, out DetachedBrowserWindow? detachedWindow);
        string profileBefore = Path.GetFullPath(oldPanel.ProfilePath);
        string? navigationTarget = oldPanel.CaptureSafeNavigationTarget();
        AccountMemorySnapshot before = oldPanel.CaptureMemoryDiagnostics(wasDetached);
        AccountMemorySnapshot[] otherBefore = CaptureAccountMemoryDiagnostics()
            .Where(snapshot => snapshot.AccountNumber != accountNumber).ToArray();
        int[] oldProcessIds = before.Processes.Select(process => process.ProcessId).Distinct().ToArray();
        _restartDiagnostics!.Write(restartId, "before-restart", accountNumber, CurrentLayoutDiagnosticName(),
            wasDetached, profileBefore, before, oldProcessIds, oldProcessIds);
        _memoryDiagnostics?.CaptureSample();

        BrowserPanel? replacement = null;
        BrowserShutdownResult? shutdown = null;
        try
        {
            UnsubscribePanel(oldPanel);
            if (wasDetached) _ = detachedWindow!.ReleasePanel();
            else BrowserGrid.Children.Remove(oldPanel);

            shutdown = await oldPanel.ShutdownAsync(TimeSpan.FromSeconds(20));
            _restartDiagnostics.Write(restartId, "after-old-environment-disposal", accountNumber,
                CurrentLayoutDiagnosticName(), wasDetached, profileBefore, null, oldProcessIds, shutdown.RemainingProcessIds,
                error: shutdown.Error);
            _memoryDiagnostics?.CaptureSample();
            if (!shutdown.AllCapturedProcessesExited)
                throw new InvalidOperationException(
                    $"The old Account {accountNumber} WebView2 process group did not terminate naturally within the diagnostic timeout. " +
                    $"No replacement environment was created. Remaining PIDs: {string.Join(", ", shutdown.RemainingProcessIds)}.");

            replacement = CreateReplacementPanel(accountNumber, navigationTarget);
            AttachReplacementPanel(accountNumber, replacement, wasDetached, detachedWindow);
            await replacement.WaitForInitializationAsync(TimeSpan.FromSeconds(90));
            AccountMemorySnapshot afterCreation = replacement.CaptureMemoryDiagnostics(wasDetached);
            _restartDiagnostics.Write(restartId, "after-new-environment-creation", accountNumber,
                CurrentLayoutDiagnosticName(), wasDetached, replacement.ProfilePath, afterCreation, oldProcessIds, []);
            _memoryDiagnostics?.CaptureSample();

            string? navigationError = null;
            try { await replacement.WaitForFirstNavigationAsync(TimeSpan.FromSeconds(30)); }
            catch (Exception exception) when (exception is TimeoutException or InvalidOperationException)
            {
                navigationError = exception.Message;
            }
            AccountMemorySnapshot afterLoad = replacement.CaptureMemoryDiagnostics(wasDetached);
            VerifyIsolation(accountNumber, before, afterLoad, otherBefore);
            if (!StringComparer.OrdinalIgnoreCase.Equals(profileBefore, Path.GetFullPath(replacement.ProfilePath)))
                throw new InvalidOperationException("The replacement browser profile path changed unexpectedly.");

            long beforeBytes = before.Processes.Sum(process => process.WorkingSetBytes ?? 0);
            long afterBytes = afterLoad.Processes.Sum(process => process.WorkingSetBytes ?? 0);
            long reclaimed = beforeBytes - afterBytes;
            double? percent = beforeBytes > 0 ? reclaimed * 100d / beforeBytes : null;
            _restartDiagnostics.Write(restartId, navigationError is null ? "after-page-load" : "navigation-failed",
                accountNumber, CurrentLayoutDiagnosticName(), wasDetached, replacement.ProfilePath, afterLoad,
                oldProcessIds, [], reclaimed, percent, navigationError);
            _memoryDiagnostics?.CaptureSample();
            _ = CaptureDeferredRestartSamplesAsync(restartId, replacement, wasDetached, oldProcessIds, beforeBytes,
                _restartSamplingCancellation.Token);

            return new SessionRestartResult(restartId, accountNumber, wasDetached, profileBefore,
                Path.GetFullPath(replacement.ProfilePath), before, afterCreation, afterLoad, shutdown,
                navigationError is null, navigationError);
        }
        catch (Exception exception)
        {
            if (replacement is null)
            {
                replacement = CreateReplacementPanel(accountNumber, null, enableSession: false);
                AttachReplacementPanel(accountNumber, replacement, wasDetached, detachedWindow);
            }
            _restartDiagnostics.Write(restartId, "failed", accountNumber, CurrentLayoutDiagnosticName(), wasDetached,
                profileBefore, replacement.CaptureMemoryDiagnostics(wasDetached), oldProcessIds,
                shutdown?.RemainingProcessIds ?? oldProcessIds, error: exception.Message);
            throw;
        }
        finally
        {
            _restartInProgress = false;
            UpdateHeaderButtons();
        }
    }

    private BrowserPanel CreateReplacementPanel(int accountNumber, string? navigationTarget, bool enableSession = true,
        bool clearDiskCacheBeforeNavigation = false)
    {
        var panel = new BrowserPanel { AccountNumber = accountNumber };
        panel.SetAccountName(_settings.AccountNames[accountNumber - 1]);
        panel.SetZoom(_settings.AccountZoomLevels[accountNumber - 1]);
        panel.SetAccentIndex(_settings.AccountAccentIndexes[accountNumber - 1]);
        panel.SetToolbarVisible(_toolbarVisible);
        panel.SetInitialNavigationTarget(navigationTarget);
        if (clearDiskCacheBeforeNavigation) panel.ConfigureRepairInitialization();
        SubscribePanel(panel);
        panel.SetSessionEnabled(enableSession);
        return panel;
    }

    private void AttachReplacementPanel(int accountNumber, BrowserPanel panel, bool wasDetached,
        DetachedBrowserWindow? detachedWindow)
    {
        string name = $"Account{accountNumber}Panel";
        if (FindName(name) is not null) UnregisterName(name);
        RegisterName(name, panel);
        _panels[accountNumber - 1] = panel;
        switch (accountNumber)
        {
            case 1: Account1Panel = panel; break;
            case 2: Account2Panel = panel; break;
            case 3: Account3Panel = panel; break;
            case 4: Account4Panel = panel; break;
        }
        panel.IsSelected = _selectedAccount == accountNumber;
        if (wasDetached)
        {
            panel.SetDetached(true);
            if (detachedWindow is not null)
            {
                detachedWindow.ReplacePanel(panel);
            }
            else
            {
                DetachedWindowSettings bounds = WindowBoundsPolicy.Normalize(
                    _savedDetachedBounds.GetValueOrDefault(accountNumber),
                    accountNumber,
                    DisplayBoundsProvider.GetDisplays());
                var replacementWindow = new DetachedBrowserWindow(panel, bounds);
                replacementWindow.DockBackRequested += DetachedWindow_DockBackRequested;
                _detachedWindows.Add(accountNumber, replacementWindow);
                replacementWindow.Show();
            }
        }
        else
        {
            BrowserGrid.Children.Add(panel);
            ShowCurrentLayoutAfterReplacement();
        }
    }

    private void ShowCurrentLayoutAfterReplacement()
    {
        switch (_layout)
        {
            case LayoutMode.Duo: ShowDuo(); break;
            case LayoutMode.Three: ShowThree(); break;
            case LayoutMode.Quad: ShowQuad(); break;
            default: ShowSolo(_selectedAccount); break;
        }
    }

    private void MoveReplacementToDetachedWindow(int accountNumber, BrowserPanel panel)
    {
        BrowserGrid.Children.Remove(panel);
        panel.Opacity = 1;
        panel.IsHitTestVisible = true;
        panel.Visibility = Visibility.Visible;
        panel.SetDetached(true);
        DetachedWindowSettings bounds = WindowBoundsPolicy.Normalize(
            _savedDetachedBounds.GetValueOrDefault(accountNumber),
            accountNumber,
            DisplayBoundsProvider.GetDisplays());
        var replacementWindow = new DetachedBrowserWindow(panel, bounds);
        replacementWindow.DockBackRequested += DetachedWindow_DockBackRequested;
        _detachedWindows.Add(accountNumber, replacementWindow);
        replacementWindow.Show();
        UpdateDetachedControls();
    }

    private void VerifyIsolation(int accountNumber, AccountMemorySnapshot before, AccountMemorySnapshot after,
        IReadOnlyList<AccountMemorySnapshot> otherBefore)
    {
        if (before.BrowserPanelIdentity == after.BrowserPanelIdentity ||
            before.BrowserControlIdentity == after.BrowserControlIdentity ||
            before.EnvironmentIdentity == after.EnvironmentIdentity)
            throw new InvalidOperationException("The target account did not receive new panel, WebView, and environment identities.");

        AccountMemorySnapshot[] otherAfter = CaptureAccountMemoryDiagnostics()
            .Where(snapshot => snapshot.AccountNumber != accountNumber).ToArray();
        bool unchanged = otherBefore.Count == otherAfter.Length && otherBefore.Zip(otherAfter).All(pair =>
            pair.First.BrowserPanelIdentity == pair.Second.BrowserPanelIdentity &&
            pair.First.BrowserControlIdentity == pair.Second.BrowserControlIdentity &&
            pair.First.EnvironmentIdentity == pair.Second.EnvironmentIdentity &&
            (!pair.First.BrowserProcessId.HasValue || pair.First.BrowserProcessId == pair.Second.BrowserProcessId));
        if (!unchanged)
        {
            string differences = string.Join("; ", otherBefore.Zip(otherAfter)
                .Where(pair => pair.First.BrowserPanelIdentity != pair.Second.BrowserPanelIdentity ||
                               pair.First.BrowserControlIdentity != pair.Second.BrowserControlIdentity ||
                               pair.First.EnvironmentIdentity != pair.Second.EnvironmentIdentity ||
                               (pair.First.BrowserProcessId.HasValue && pair.First.BrowserProcessId != pair.Second.BrowserProcessId))
                .Select(pair => $"Account {pair.First.AccountNumber}: panel {pair.First.BrowserPanelIdentity}->{pair.Second.BrowserPanelIdentity}, " +
                                $"webview {pair.First.BrowserControlIdentity}->{pair.Second.BrowserControlIdentity}, " +
                                $"environment {pair.First.EnvironmentIdentity}->{pair.Second.EnvironmentIdentity}, " +
                                $"browser PID {pair.First.BrowserProcessId}->{pair.Second.BrowserProcessId}"));
            throw new InvalidOperationException($"A non-target account identity changed. {differences}");
        }
    }

    private async Task CaptureDeferredRestartSamplesAsync(Guid restartId, BrowserPanel panel, bool wasDetached,
        IReadOnlyList<int> oldProcessIds, long beforeBytes, CancellationToken cancellationToken)
    {
        foreach ((TimeSpan delay, string stage) in new[]
                 { (TimeSpan.FromMinutes(1), "one-minute"), (TimeSpan.FromMinutes(4), "five-minute") })
        {
            try { await Task.Delay(delay, cancellationToken); }
            catch (OperationCanceledException) { return; }
            if (panel.IsDisposed || _panels[panel.AccountNumber - 1] != panel) return;
            AccountMemorySnapshot snapshot = panel.CaptureMemoryDiagnostics(wasDetached);
            long afterBytes = snapshot.Processes.Sum(process => process.WorkingSetBytes ?? 0);
            long reclaimed = beforeBytes - afterBytes;
            double? percent = beforeBytes > 0 ? reclaimed * 100d / beforeBytes : null;
            _restartDiagnostics?.Write(restartId, stage, panel.AccountNumber, CurrentLayoutDiagnosticName(),
                wasDetached, panel.ProfilePath, snapshot, oldProcessIds, [], reclaimed, percent);
            _memoryDiagnostics?.CaptureSample();
        }
    }

    private void ApplyLayout(LayoutMode layout)
    {
        if (layout == LayoutMode.Duo) ShowDuo();
        else if (layout == LayoutMode.Three) ShowDockedLayout();
        else if (layout == LayoutMode.Quad) ShowQuad();
        else ShowSolo(_selectedAccount);
    }

    private void ShowSolo(int account)
    {
        if (!EntitlementPolicy.CanUseAccount(account, _maxSessions)) account = 1;
        IReadOnlyList<int> docked = _popOutState.DockedAccounts;
        if (docked.Count == 0) { ShowDockedLayout(); return; }
        if (!docked.Contains(account)) account = docked[0];
        _layout = LayoutMode.Solo;
        SelectAccount(account);
        ApplyPanelPlacements(WorkspaceLayoutPolicy.Solo(account));
        UpdateHeaderButtons();
    }

    private void ShowDuo()
    {
        if (!EntitlementPolicy.CanUseLayout(LayoutMode.Duo, _maxSessions)) { ShowSolo(1); return; }
        IReadOnlyList<int> docked = _popOutState.DockedAccounts;
        if (docked.Count < 2) { ShowDockedLayout(); return; }
        _layout = LayoutMode.Duo;
        if (_duoAccount1 == _duoAccount2) _duoAccount2 = _duoAccount1 == 1 ? 2 : 1;
        int first = docked.Contains(_duoAccount1) ? _duoAccount1 : docked[0];
        int second = docked.Contains(_duoAccount2) && _duoAccount2 != first
            ? _duoAccount2
            : docked.First(account => account != first);
        ApplyPanelPlacements(WorkspaceLayoutPolicy.Duo(first, second, _duoOrientation));
        SelectAccount(_selectedAccount == first || _selectedAccount == second ? _selectedAccount : first);
        UpdateHeaderButtons();
    }

    private void ShowQuad()
    {
        if (!EntitlementPolicy.CanUseLayout(LayoutMode.Quad, _maxSessions)) { ShowSolo(1); return; }
        if (_popOutState.DetachedAccounts.Count > 0) { ShowDockedLayout(); return; }
        _layout = LayoutMode.Quad;
        ApplyPanelPlacements(WorkspaceLayoutPolicy.Quad());
        SelectAccount(_selectedAccount);
        UpdateHeaderButtons();
    }

    private void ShowThree()
    {
        IReadOnlyList<int> docked = _popOutState.DockedAccounts;
        if (docked.Count != 3) { ShowDockedLayout(); return; }
        _layout = LayoutMode.Three;
        int largeAccount = docked.Contains(_threeLargeAccount)
            ? _threeLargeAccount
            : docked.Contains(_selectedAccount) ? _selectedAccount : docked[0];
        ApplyPanelPlacements(WorkspaceLayoutPolicy.Three(docked, largeAccount, _threeLayout));
        SelectAccount(largeAccount);
        UpdateThreeLayoutButton();
        UpdateHeaderButtons();
    }

    private static void SetPanelLayout(BrowserPanel panel, bool visible, int row, int column, int rowSpan, int columnSpan)
    {
        panel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        Grid.SetRow(panel, row); Grid.SetColumn(panel, column); Grid.SetRowSpan(panel, rowSpan); Grid.SetColumnSpan(panel, columnSpan);
    }

    private void ApplyPanelPlacements(IReadOnlyList<PanelPlacement> placements)
    {
        PanelPlacement[] visiblePlacements = placements.Where(placement => placement.Visible).ToArray();
        int rowCount = visiblePlacements.Length == 0 ? 2 : visiblePlacements.Max(placement => placement.Row + placement.RowSpan);
        int columnCount = visiblePlacements.Length == 0 ? 2 : visiblePlacements.Max(placement => placement.Column + placement.ColumnSpan);
        ConfigureBrowserGrid(rowCount, columnCount);
        for (int index = 0; index < _panels.Length; index++)
        {
            if (_detachedWindows.ContainsKey(index + 1))
            {
                _panels[index].Visibility = Visibility.Visible;
                continue;
            }
            PanelPlacement placement = placements[index];
            SetPanelLayout(_panels[index], placement.Visible, placement.Row, placement.Column,
                placement.RowSpan, placement.ColumnSpan);
        }
        UpdateDetachedControls();
    }

    private void ConfigureBrowserGrid(int rowCount, int columnCount)
    {
        BrowserGrid.RowDefinitions.Clear();
        BrowserGrid.ColumnDefinitions.Clear();
        for (int row = 0; row < Math.Max(1, rowCount); row++)
            BrowserGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        for (int column = 0; column < Math.Max(1, columnCount); column++)
            BrowserGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(EmptyWorkspace, 0);
        Grid.SetColumn(EmptyWorkspace, 0);
        Grid.SetRowSpan(EmptyWorkspace, Math.Max(1, rowCount));
        Grid.SetColumnSpan(EmptyWorkspace, Math.Max(1, columnCount));
    }

    private void ShowDockedLayout()
    {
        IReadOnlyList<int> docked = _popOutState.DockedAccounts;
        _layout = docked.Count switch
        {
            >= 4 => LayoutMode.Quad,
            3 => LayoutMode.Three,
            2 => LayoutMode.Duo,
            _ => LayoutMode.Solo
        };
        int largeAccount = docked.Contains(_threeLargeAccount)
            ? _threeLargeAccount
            : docked.Contains(_selectedAccount) ? _selectedAccount : docked.FirstOrDefault();
        ApplyPanelPlacements(WorkspaceLayoutPolicy.Docked(docked, _duoOrientation, _threeLayout, largeAccount));
        if (docked.Count > 0)
        {
            SelectAccount(docked.Count == 3 ? largeAccount : docked.Contains(_selectedAccount) ? _selectedAccount : docked[0]);
        }
        UpdateThreeLayoutButton();
        UpdateHeaderButtons();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        Dispatcher.BeginInvoke(() =>
        {
            _popOutState.Restore(_settings.DetachedAccounts);
            int[] accounts = _popOutState.DetachedAccounts.Order().ToArray();
            _popOutState.RestoreAll();
            foreach (int account in accounts) DetachAccount(account);
            _memoryDiagnostics?.Start();
            if (!string.Equals(Environment.GetEnvironmentVariable("DRIFTR_RUNTIME_PROBE"), "1", StringComparison.Ordinal))
                _ = CheckForUpdatesAsync(isManual: false);
        }, DispatcherPriority.ContextIdle);
    }

    private async Task CheckForUpdatesAsync(bool isManual)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!isManual && !UpdateChecker.ShouldCheckAutomatically(_settings.LastUpdateCheckUtc, now)) return;
        if (isManual) CheckUpdatesButton.IsEnabled = false;
        try
        {
            // HttpClient I/O proceeds asynchronously; this path never gates browser or layout initialization.
            UpdateCheckResult result = await _updateChecker.CheckAsync(_updateCheckCancellation.Token);
            if (_shutdownStarted) return;
            _settings.LastUpdateCheckUtc = now;
            _settingsService.Save(_settings);
            if (result.Status == UpdateCheckStatus.UpdateAvailable && result.Update is not null)
            {
                string version = result.Update.Version.ToString();
                if (!isManual && !UpdateChecker.ShouldNotifyAutomatically(_settings.LastNotifiedVersion, result.Update.Version)) return;
                _settings.LastNotifiedVersion = version;
                _settingsService.Save(_settings);
                var dialog = new UpdateAvailableDialog(version, result.CurrentVersion.ToString()) { Owner = this };
                if (dialog.ShowDialog() == true && UpdateChecker.IsOfficialReleaseUrl(result.Update.ReleasePage))
                {
                    Process.Start(new ProcessStartInfo(result.Update.ReleasePage.AbsoluteUri) { UseShellExecute = true });
                }
            }
            else if (isManual && result.Status == UpdateCheckStatus.UpToDate)
            {
                MessageBox.Show(this,
                    $"You're up to date.\nDRIFTR {result.CurrentVersion} is the newest version available for your update channel.",
                    "Check for Updates", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (isManual)
            {
                MessageBox.Show(this,
                    "Couldn't check for updates right now.\nPlease check your internet connection and try again.",
                    "Check for Updates", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (OperationCanceledException) when (_shutdownStarted)
        {
        }
        catch
        {
            if (isManual && !_shutdownStarted)
                MessageBox.Show(this,
                    "Couldn't check for updates right now.\nPlease check your internet connection and try again.",
                    "Check for Updates", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (isManual && !_shutdownStarted) CheckUpdatesButton.IsEnabled = true;
        }
    }

    private IReadOnlyList<AccountMemorySnapshot> CaptureAccountMemoryDiagnostics() => _panels
        .Take(_maxSessions)
        .Select(panel => panel.CaptureMemoryDiagnostics(_detachedWindows.ContainsKey(panel.AccountNumber)))
        .ToArray();

    private void DetachAccount(int accountNumber)
    {
        if (!_popOutState.Detach(accountNumber)) return;
        BrowserPanel panel = _panels[accountNumber - 1];
        if (!BrowserGrid.Children.Contains(panel))
        {
            _popOutState.Dock(accountNumber);
            return;
        }

        BrowserGrid.Children.Remove(panel);
        panel.SetDetached(true);
        DetachedWindowSettings bounds = WindowBoundsPolicy.Normalize(
            _savedDetachedBounds.GetValueOrDefault(accountNumber),
            accountNumber,
            DisplayBoundsProvider.GetDisplays());
        var window = new DetachedBrowserWindow(panel, bounds);
        window.DockBackRequested += DetachedWindow_DockBackRequested;
        _detachedWindows.Add(accountNumber, window);
        try
        {
            window.Show();
            ShowDockedLayout();
        }
        catch
        {
            _detachedWindows.Remove(accountNumber);
            _popOutState.Dock(accountNumber);
            _ = window.ReleasePanel();
            window.CloseWithoutDocking();
            panel.SetDetached(false);
            BrowserGrid.Children.Add(panel);
            ShowDockedLayout();
            MessageBox.Show(this,
                $"Account {accountNumber} could not be moved to a separate window. It has been restored to the main workspace.",
                "Pop Out", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DetachedWindow_DockBackRequested(object? sender, EventArgs e)
    {
        if (sender is DetachedBrowserWindow window) DockAccount(window.AccountNumber);
    }

    private void DockAccount(int accountNumber)
    {
        if (!_detachedWindows.Remove(accountNumber, out DetachedBrowserWindow? window)) return;
        _savedDetachedBounds[accountNumber] = window.CaptureBounds();
        window.DockBackRequested -= DetachedWindow_DockBackRequested;
        BrowserPanel panel = window.ReleasePanel();
        panel.SetDetached(false);
        _popOutState.Dock(accountNumber);
        BrowserGrid.Children.Add(panel);
        window.CloseWithoutDocking();
        ShowDockedLayout();
    }

    private void RestoreAllDetached()
    {
        foreach (int account in _detachedWindows.Keys.Order().ToArray()) DockAccount(account);
        _popOutState.RestoreAll();
        if (_maxSessions >= 4) ShowQuad();
        else ShowSolo(1);
    }

    private void UpdateDetachedControls()
    {
        bool anyDetached = _detachedWindows.Count > 0;
        RestoreAllButton.Visibility = anyDetached ? Visibility.Visible : Visibility.Collapsed;
        EmptyWorkspace.Visibility = _popOutState.DockedAccounts.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateLayoutControlVisibility();
    }

    private void UpdateDuoOrientationButton()
    {
        bool horizontal = _duoOrientation == DuoOrientation.Horizontal;
        DuoOrientationButton.Content = horizontal ? "SIDE BY SIDE" : "STACKED";
        DuoOrientationButton.ToolTip = horizontal
            ? "Duo orientation: Side by Side. Click for Stacked."
            : "Duo orientation: Stacked. Click for Side by Side.";
    }

    private void UpdateThreeLayoutButton()
    {
        ThreeLayoutButton.Content = _threeLayout switch
        {
            ThreeSessionLayout.ThreeLargeLeft => "3: LARGE LEFT",
            ThreeSessionLayout.ThreeLargeRight => "3: LARGE RIGHT",
            ThreeSessionLayout.ThreeLargeTop => "3: LARGE TOP",
            ThreeSessionLayout.ThreeLargeBottom => "3: LARGE BOTTOM",
            ThreeSessionLayout.ThreeColumns => "3: COLUMNS",
            ThreeSessionLayout.ThreeRows => "3: ROWS",
            _ => "3: LARGE LEFT"
        };
        ThreeLayoutButton.ToolTip = "Cycle the arrangement of the three docked accounts. Use an account number to choose the large browser.";
    }

    private void UpdateLayoutControlVisibility()
    {
        int dockedCount = _popOutState.DockedAccounts.Count;
        SoloButton.Visibility = dockedCount is 1 or 4 ? Visibility.Visible : Visibility.Collapsed;
        DuoButton.Visibility = dockedCount is 2 or 4 ? Visibility.Visible : Visibility.Collapsed;
        DuoOrientationButton.Visibility = dockedCount is 2 or 4 ? Visibility.Visible : Visibility.Collapsed;
        ThreeLayoutButton.Visibility = dockedCount == 3 ? Visibility.Visible : Visibility.Collapsed;
        QuadButton.Visibility = dockedCount == 4 ? Visibility.Visible : Visibility.Collapsed;
    }

    private string CurrentLayoutDiagnosticName() => _layout == LayoutMode.Three
        ? _threeLayout.ToString()
        : _layout.ToString();

    private void UpdateHeaderButtons()
    {
        for (int index = 0; index < _accountButtons.Length; index++)
            _accountButtons[index].Background = index == _selectedAccount - 1 ? ActiveButtonBrush : NormalButtonBrush;
        SoloButton.Background = _layout == LayoutMode.Solo ? ActiveButtonBrush : NormalButtonBrush;
        DuoButton.Background = _layout == LayoutMode.Duo ? ActiveButtonBrush : NormalButtonBrush;
        DuoOrientationButton.Background = _layout == LayoutMode.Duo ? ActiveButtonBrush : NormalButtonBrush;
        QuadButton.Background = _layout == LayoutMode.Quad ? ActiveButtonBrush : NormalButtonBrush;
        ThreeLayoutButton.Background = _layout == LayoutMode.Three ? ActiveButtonBrush : NormalButtonBrush;
        RestartSessionButton.IsEnabled = _diagnosticsOptions.Enabled && !_restartInProgress &&
                                         !_repairInProgress &&
                                         EntitlementPolicy.CanUseAccount(_selectedAccount, _maxSessions);
        UpdateLayoutControlVisibility();
    }

    private void ToggleToolbars() { _toolbarVisible = !_toolbarVisible; ApplyToolbarVisibility(); }
    private void ApplyToolbarVisibility()
    {
        foreach (BrowserPanel panel in _panels) panel.SetToolbarVisible(_toolbarVisible);
        ToolbarButton.Content = _toolbarVisible ? "TOOLS: ON" : "TOOLS: OFF";
        _settings.ToolbarVisible = _toolbarVisible;
    }

    private void ToggleFullscreen()
    {
        if (_isFullscreen)
        {
            WindowStyle = WindowStyle.SingleBorderWindow; AppTopBar.Visibility = Visibility.Visible;
            TopBarRow.Height = new GridLength(62); WindowState = _windowStateBeforeFullscreen; _isFullscreen = false;
        }
        else
        {
            _windowStateBeforeFullscreen = WindowState; WindowStyle = WindowStyle.None;
            AppTopBar.Visibility = Visibility.Collapsed; TopBarRow.Height = new GridLength(0);
            WindowState = WindowState.Maximized; _isFullscreen = true;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e) => e.Handled = HandleShortcut(e.Key, Keyboard.Modifiers);
    private bool HandleShortcut(Key key, ModifierKeys modifiers)
    {
        if (_repairInProgress) return false;
        if ((modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (modifiers == ModifierKeys.Control)
            {
                int? account = key switch { Key.D1 or Key.NumPad1 => 1, Key.D2 or Key.NumPad2 => 2, Key.D3 or Key.NumPad3 => 3, Key.D4 or Key.NumPad4 => 4, _ => null };
                if (account.HasValue && EntitlementPolicy.CanUseAccount(account.Value, _maxSessions)) { ShowSolo(account.Value); return true; }
                if (key == Key.S) { ShowSolo(_selectedAccount); return true; }
                if (key == Key.D && DuoButton.IsEnabled) { ShowDuo(); return true; }
                if (key == Key.Q && QuadButton.IsEnabled) { ShowQuad(); return true; }
                if (key == Key.T) { ToggleToolbars(); return true; }
                if (key is Key.OemMinus or Key.Subtract) { _panels[_selectedAccount - 1].ZoomOut(); return true; }
                if (key is Key.D0 or Key.NumPad0) { _panels[_selectedAccount - 1].ResetZoom(); return true; }
            }
            if (key is Key.OemPlus or Key.Add) { _panels[_selectedAccount - 1].ZoomIn(); return true; }
        }
        if (modifiers == ModifierKeys.None && key == Key.F5) { _panels[_selectedAccount - 1].Refresh(); return true; }
        if (modifiers == ModifierKeys.None && key == Key.F11) { ToggleFullscreen(); return true; }
        return false;
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete) return;
        e.Cancel = true;
        if (_shutdownStarted) return;
        _shutdownStarted = true;
        if (_isFullscreen) ToggleFullscreen();
        Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _settings.WindowLeft = bounds.Left; _settings.WindowTop = bounds.Top;
        _settings.WindowWidth = bounds.Width; _settings.WindowHeight = bounds.Height;
        _settings.IsMaximized = WindowState == WindowState.Maximized;
        _settings.LastView = _layout == LayoutMode.Solo ? $"Account{_selectedAccount}" : _layout.ToString();
        _settings.PreferredLayout = _layout.ToString();
        _settings.SelectedAccount = _selectedAccount;
        _settings.DuoAccount1 = _duoAccount1; _settings.DuoAccount2 = _duoAccount2;
        _settings.DuoOrientation = _duoOrientation.ToString();
        _settings.ThreeSessionLayout = _threeLayout.ToString();
        _settings.ThreeLargeAccount = _threeLargeAccount;
        _settings.DetachedAccounts = _detachedWindows.Keys.Order().ToArray();
        _settings.DetachedWindows = _detachedWindows.Values.Select(window => window.CaptureBounds()).ToArray();
        _settings.ToolbarVisible = _toolbarVisible;
        _settingsService.Save(_settings);
        _restartSamplingCancellation.Cancel();
        _updateCheckCancellation.Cancel();
        _memoryDiagnostics?.Stop();
        IsEnabled = false; Hide();
        foreach (DetachedBrowserWindow detached in _detachedWindows.Values.ToArray())
        {
            detached.DockBackRequested -= DetachedWindow_DockBackRequested;
            detached.CloseWithoutDocking();
        }
        _detachedWindows.Clear();
        try { await Task.WhenAll(_panels.Select(panel => panel.ShutdownAsync(TimeSpan.FromSeconds(15)))); }
        finally { _releaseProvider.Dispose(); _updateCheckCancellation.Dispose(); _shutdownComplete = true; Close(); }
    }
}
