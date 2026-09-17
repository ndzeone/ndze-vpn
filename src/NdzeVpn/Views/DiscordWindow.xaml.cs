using System.Windows;

namespace NdzeVpn.Views;

public partial class DiscordWindow : Window
{
    public DiscordWindow() => InitializeComponent();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
