using System.Windows;

namespace PokeQuad;

public partial class UpdateAvailableDialog : Window
{
    public UpdateAvailableDialog(string availableVersion, string currentVersion)
    {
        InitializeComponent();
        AvailableVersionText.Text = $"DRIFTR {availableVersion} is available.";
        CurrentVersionText.Text = currentVersion;
    }

    private void ViewUpdate_Click(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
    private void Later_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
}
