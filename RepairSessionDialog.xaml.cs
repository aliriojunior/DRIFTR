using System.Windows;
using PokeQuad.Services;

namespace PokeQuad;

public partial class RepairSessionDialog : Window
{
    public RepairSessionDialog(int accountNumber)
    {
        InitializeComponent();
        TitleText.Text = SessionRepairPolicy.ConfirmationTitle(accountNumber);
    }

    private void RepairButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
