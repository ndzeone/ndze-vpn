using System.Windows;
using System.Windows.Input;
using NdzeVpn.ViewModels;

namespace NdzeVpn.Views;

public partial class KeyCheckWindow : Window
{
    public KeyCheckWindow() => InitializeComponent();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ((MainViewModel)DataContext).CheckKeyCommand.Execute(null);
        e.Handled = true;
    }
}
