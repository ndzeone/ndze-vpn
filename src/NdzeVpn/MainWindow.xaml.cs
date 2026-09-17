using System.ComponentModel;
using System.Windows;

namespace NdzeVpn;

public partial class MainWindow : Window
{
    private bool _trayHintShown;

    public MainWindow()
    {
        InitializeComponent();
        StateChanged += (_, _) => SyncMaximized();
    }

    private void SyncMaximized()
    {
        // A WindowStyle=None window overhangs the screen by the resize border when maximized.
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxIcon.Data = (System.Windows.Media.Geometry)FindResource(WindowState == WindowState.Maximized ? "I.WinRestore" : "I.WinMax");
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        if (App.Vpn.Settings.MinimizeToTray) Hide();
        else WindowState = WindowState.Minimized;
    }

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override async void OnClosing(CancelEventArgs e)
    {
        var app = (App)Application.Current;
        if (app.IsExiting)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;

        if (App.Vpn.Settings.CloseToTray)
        {
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                app.NotifyTray("Ndze VPN работает в фоне", "Открыть или выйти — через значок в трее.");
            }
            return;
        }

        await app.ExitAsync();
    }
}
