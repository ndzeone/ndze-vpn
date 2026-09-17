using System.Windows;
using NdzeVpn.ViewModels;

namespace NdzeVpn.Views;

public partial class UpdateWindow : Window
{
    public UpdateWindow() => InitializeComponent();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        ((MainViewModel)DataContext).SkipUpdateCommand.Execute(null);
        Close();
    }
}
