using System.Windows;
using PokeQuad.Models;
using PokeQuad.Services;

namespace PokeQuad;

public partial class AuthWindow : Window
{
    private readonly LicenseCoordinator _licenseCoordinator;
    private bool _registerMode;
    private bool _requestInProgress;

    public AuthWindow(LicenseCoordinator licenseCoordinator, string? initialError = null)
    {
        InitializeComponent();
        _licenseCoordinator = licenseCoordinator;
        ErrorLabel.Text = initialError ?? string.Empty;
        Loaded += (_, _) => EmailBox.Focus();
    }

    public LicensingSession? ResultSession { get; private set; }

    private void SwitchModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_requestInProgress)
        {
            return;
        }

        _registerMode = !_registerMode;
        HeadingLabel.Text = _registerMode ? "Create your account" : "Welcome back";
        SubheadingLabel.Text = _registerMode
            ? "Start with one browser session on the Free plan."
            : "Sign in to continue to DRIFTR.";
        ConfirmPasswordPanel.Visibility = _registerMode ? Visibility.Visible : Visibility.Collapsed;
        SubmitButton.Content = _registerMode ? "CREATE ACCOUNT" : "SIGN IN";
        SwitchModeButton.Content = _registerMode ? "I ALREADY HAVE AN ACCOUNT" : "CREATE AN ACCOUNT";
        ErrorLabel.Text = string.Empty;
    }

    private async void SubmitButton_Click(object sender, RoutedEventArgs e)
    {
        if (_requestInProgress || !ValidateInput(out string email, out string password))
        {
            return;
        }

        SetBusy(true);
        try
        {
            ResultSession = _registerMode
                ? await _licenseCoordinator.RegisterAsync(email, password)
                : await _licenseCoordinator.SignInAsync(email, password);
            DialogResult = true;
        }
        catch (LicensingException exception)
        {
            ErrorLabel.Text = exception.UserMessage;
        }
        catch (Exception)
        {
            ErrorLabel.Text = "DRIFTR could not complete sign-in. Please try again.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private bool ValidateInput(out string email, out string password)
    {
        email = EmailBox.Text.Trim();
        password = PasswordBox.Password;

        if (email.Length < 3 || !email.Contains('@'))
        {
            ErrorLabel.Text = "Enter a valid email address.";
            return false;
        }

        if (password.Length < 8)
        {
            ErrorLabel.Text = "Password must be at least 8 characters.";
            return false;
        }

        if (_registerMode && password != ConfirmPasswordBox.Password)
        {
            ErrorLabel.Text = "Passwords do not match.";
            return false;
        }

        ErrorLabel.Text = string.Empty;
        return true;
    }

    private void SetBusy(bool busy)
    {
        _requestInProgress = busy;
        EmailBox.IsEnabled = !busy;
        PasswordBox.IsEnabled = !busy;
        ConfirmPasswordBox.IsEnabled = !busy;
        SwitchModeButton.IsEnabled = !busy;
        SubmitButton.IsEnabled = !busy;
        SubmitButton.Content = busy ? "PLEASE WAIT…" : _registerMode ? "CREATE ACCOUNT" : "SIGN IN";
    }
}
