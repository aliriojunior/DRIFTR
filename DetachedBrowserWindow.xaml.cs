using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using PokeQuad.Controls;
using PokeQuad.Models;

namespace PokeQuad;

public partial class DetachedBrowserWindow : Window
{
    private bool _allowClose;

    public DetachedBrowserWindow(BrowserPanel panel, DetachedWindowSettings bounds)
    {
        InitializeComponent();
        Panel = panel;
        AccountNumber = panel.AccountNumber;
        AutomationProperties.SetAutomationId(this, $"DetachedAccount{AccountNumber}");
        Title = $"DRIFTR — Account {AccountNumber}";
        AccountTitle.Text = $"ACCOUNT {AccountNumber} · {panel.AccountName.ToUpperInvariant()}";
        BrowserHost.Content = panel;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
        if (bounds.IsMaximized) WindowState = WindowState.Maximized;
    }

    public int AccountNumber { get; }
    public BrowserPanel Panel { get; private set; }
    public event EventHandler? DockBackRequested;

    public DetachedWindowSettings CaptureBounds()
    {
        Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        return new DetachedWindowSettings
        {
            AccountNumber = AccountNumber,
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            IsMaximized = WindowState == WindowState.Maximized
        };
    }

    public BrowserPanel ReleasePanel()
    {
        BrowserHost.Content = null;
        return Panel;
    }

    public void ReplacePanel(BrowserPanel panel)
    {
        if (panel.AccountNumber != AccountNumber)
            throw new InvalidOperationException("A detached window can only host its original account number.");
        BrowserHost.Content = null;
        Panel = panel;
        AccountTitle.Text = $"ACCOUNT {AccountNumber} · {panel.AccountName.ToUpperInvariant()}";
        BrowserHost.Content = panel;
    }

    public void CloseWithoutDocking()
    {
        _allowClose = true;
        Close();
    }

    public void SetDockingEnabled(bool enabled) => DockBackButton.IsEnabled = enabled;

    private void DockBackButton_Click(object sender, RoutedEventArgs e) =>
        DockBackRequested?.Invoke(this, EventArgs.Empty);

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        Dispatcher.BeginInvoke(() => DockBackRequested?.Invoke(this, EventArgs.Empty));
    }
}
