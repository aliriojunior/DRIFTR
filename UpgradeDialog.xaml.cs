using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using PokeQuad.Models;
using PokeQuad.Services;

namespace PokeQuad;

public partial class UpgradeDialog : Window
{
    private readonly LicenseCoordinator _licenseCoordinator;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private bool _operationInProgress;
    private bool _entitlementConfirmed;
    private bool _verificationAttempted;

    public UpgradeDialog(LicenseCoordinator licenseCoordinator)
    {
        InitializeComponent();
        _licenseCoordinator = licenseCoordinator;
    }

    public LicensingSession? ConfirmedSession { get; private set; }

    private async void ContinueButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operationInProgress) return;
        _verificationAttempted = true;
        SetError(null);
        SetBusy(true, "CREATING CHECKOUT…");
        try
        {
            string plan = MonthlyRadio.IsChecked == true ? BillingPolicy.ProMonthly : BillingPolicy.ProAnnual;
            BillingCheckoutResponse checkout = await _licenseCoordinator.CreateBillingCheckoutAsync(
                plan,
                _lifetimeCancellation.Token);
            Uri checkoutUri = BillingPolicy.RequireSecureCheckoutUri(checkout.CheckoutUrl);
            Process? browser = Process.Start(new ProcessStartInfo(checkoutUri.AbsoluteUri) { UseShellExecute = true });
            if (browser is null)
            {
                throw new InvalidOperationException("The system browser could not be started.");
            }

            ShowCheckoutOpened();
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (LicensingException exception)
        {
            SetError(exception.UserMessage);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            SetError("DRIFTR couldn't open your browser. Check your default browser settings and try again.");
        }
        catch (Exception)
        {
            SetError("DRIFTR couldn't create or open checkout. Please try again.");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void VerifyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_entitlementConfirmed)
        {
            DialogResult = true;
            return;
        }

        if (_operationInProgress) return;
        SetError(null);
        SetBusy(true, "CHECKING…");
        try
        {
            LicensingSession session = await _licenseCoordinator.RefreshEntitlementAsync(_lifetimeCancellation.Token);
            if (EntitlementPolicy.NormalizeMaxSessions(session.License.MaxSessions) >= 4)
            {
                ConfirmedSession = session;
                _entitlementConfirmed = true;
                CheckoutStatusTitle.Text = "DRIFTR PRO is active.";
                CheckoutStatusMessage.Text = "Your entitlement has been confirmed by DRIFTR.";
                VerifyButton.Content = "DONE";
                CancelButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                CheckoutStatusTitle.Text = "Payment hasn't been confirmed yet.";
                CheckoutStatusMessage.Text = "If you just paid, wait a moment and try again.";
                VerifyButton.Content = "CHECK AGAIN";
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (LicensingException exception)
        {
            SetError(exception.UserMessage);
        }
        catch (Exception)
        {
            SetError("DRIFTR couldn't refresh your plan right now. Please try again.");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ShowCheckoutOpened()
    {
        PlanSelectionPanel.Visibility = Visibility.Collapsed;
        CheckoutStatusPanel.Visibility = Visibility.Visible;
        ContinueButton.Visibility = Visibility.Collapsed;
        VerifyButton.Visibility = Visibility.Visible;
        CancelButton.Content = "CLOSE";
    }

    private void SetBusy(bool busy, string? buttonText = null)
    {
        _operationInProgress = busy;
        MonthlyRadio.IsEnabled = !busy;
        AnnualRadio.IsEnabled = !busy;
        ContinueButton.IsEnabled = !busy;
        VerifyButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
        if (busy && buttonText is not null)
        {
            if (ContinueButton.Visibility == Visibility.Visible) ContinueButton.Content = buttonText;
            if (VerifyButton.Visibility == Visibility.Visible) VerifyButton.Content = buttonText;
        }
        else if (!_entitlementConfirmed)
        {
            ContinueButton.Content = "CONTINUE TO CHECKOUT";
            if (VerifyButton.Visibility == Visibility.Visible)
                VerifyButton.Content = _verificationAttempted ? "CHECK AGAIN" : "I'VE COMPLETED PAYMENT";
        }
    }

    private void SetError(string? message)
    {
        ErrorText.Text = message ?? string.Empty;
        ErrorText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _lifetimeCancellation.Cancel();
    }
}
