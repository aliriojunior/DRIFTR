using System.Windows;
using PokeQuad.Models;
using PokeQuad.Services;

namespace PokeQuad;

public partial class App : Application
{
    private LicensingApiClient? _apiClient;
    private LicenseCoordinator? _licenseCoordinator;
    private bool _switchingWindows;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (string.Equals(Environment.GetEnvironmentVariable("DRIFTR_RUNTIME_PROBE"), "1", StringComparison.Ordinal) &&
            string.Equals(System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name,
                "DRIFTR.MemoryRuntimeProbe", StringComparison.Ordinal))
            return;
        DataMigrationService.MigrateLegacyDataIfNeeded();
        _apiClient = new LicensingApiClient();
        _licenseCoordinator = new LicenseCoordinator(_apiClient, new DeviceIdentityService(), new AuthSessionService());

        LicensingSession? session = null;
        string? startupMessage = null;
        try { session = await _licenseCoordinator.TryResumeAsync(); }
        catch (LicensingException exception) { startupMessage = exception.UserMessage; }
        catch (Exception) { startupMessage = "DRIFTR could not validate the saved session. Please sign in again."; }

        if (session is not null) OpenMainWindow(session);
        else ShowAuthentication(startupMessage);
    }

    private void ShowAuthentication(string? initialMessage = null)
    {
        if (_licenseCoordinator is null) { Shutdown(1); return; }
        var authWindow = new AuthWindow(_licenseCoordinator, initialMessage);
        MainWindow = authWindow;
        if (authWindow.ShowDialog() == true && authWindow.ResultSession is not null) OpenMainWindow(authWindow.ResultSession);
        else Shutdown();
    }

    private void OpenMainWindow(LicensingSession session)
    {
        var window = new MainWindow(session);
        window.SignOutRequested += MainWindow_SignOutRequested;
        window.SignOutEverywhereRequested += MainWindow_SignOutEverywhereRequested;
        window.DeactivateRequested += MainWindow_DeactivateRequested;
        window.Closed += MainWindow_Closed;
        MainWindow = window;
        window.Show();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        if (!_switchingWindows) Shutdown();
    }

    private async void MainWindow_SignOutRequested(object? sender, EventArgs e)
    {
        if (sender is not MainWindow window || _licenseCoordinator is null) return;
        window.IsEnabled = false;
        bool confirmed = await _licenseCoordinator.SignOutAsync();
        await CloseForAuthenticationAsync(window);
        ShowAuthentication(confirmed
            ? null
            : "You were signed out locally. Server-side session revocation could not be confirmed.");
    }

    private async void MainWindow_SignOutEverywhereRequested(object? sender, EventArgs e)
    {
        if (sender is not MainWindow window || _licenseCoordinator is null) return;
        window.IsEnabled = false;
        try
        {
            await _licenseCoordinator.SignOutEverywhereAsync();
            await CloseForAuthenticationAsync(window);
            ShowAuthentication("You have been signed out everywhere.");
        }
        catch (LicensingException exception) when (exception.Kind == LicensingErrorKind.SessionExpired)
        {
            await CloseForAuthenticationAsync(window);
            ShowAuthentication(exception.UserMessage);
        }
        catch (LicensingException exception)
        {
            window.IsEnabled = true;
            MessageBox.Show(window, exception.UserMessage, "Sign Out Everywhere", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception)
        {
            window.IsEnabled = true;
            MessageBox.Show(window, "DRIFTR could not sign out everywhere. Please try again.",
                "Sign Out Everywhere", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void MainWindow_DeactivateRequested(object? sender, EventArgs e)
    {
        if (sender is not MainWindow window || _licenseCoordinator is null) return;
        window.IsEnabled = false;
        try
        {
            await _licenseCoordinator.DeactivateAsync();
            await CloseForAuthenticationAsync(window);
            ShowAuthentication("This device was deactivated. Sign in to activate it again.");
        }
        catch (LicensingException exception) when (exception.Kind == LicensingErrorKind.SessionExpired)
        {
            await CloseForAuthenticationAsync(window);
            ShowAuthentication(exception.UserMessage);
        }
        catch (LicensingException exception)
        {
            window.IsEnabled = true;
            MessageBox.Show(window, exception.UserMessage, "Device Deactivation", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception)
        {
            window.IsEnabled = true;
            MessageBox.Show(window, "DRIFTR could not deactivate this device. Please try again.",
                "Device Deactivation", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task CloseForAuthenticationAsync(MainWindow window)
    {
        _switchingWindows = true;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        window.Close();
        await closed.Task;
        _switchingWindows = false;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _apiClient?.Dispose();
        base.OnExit(e);
    }
}
