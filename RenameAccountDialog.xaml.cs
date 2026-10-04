using System.Windows;
using System.Windows.Input;

namespace PokeQuad;

public partial class RenameAccountDialog : Window
{
    public RenameAccountDialog(string currentName)
    {
        InitializeComponent();
        NameTextBox.Text = currentName;
        Loaded += (_, _) =>
        {
            NameTextBox.Focus();
            NameTextBox.SelectAll();
        };
    }

    public string AccountName => NameTextBox.Text.Trim();

    private void SaveButton_Click(object sender, RoutedEventArgs e) => Save();

    private void NameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Save();
            e.Handled = true;
        }
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(AccountName))
        {
            MessageBox.Show(this, "Enter an account name.", "DRIFTR", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}
