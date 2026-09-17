using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using NdzeVpn.ViewModels;

namespace NdzeVpn.Views;

public partial class ServersView : UserControl
{
    public ServersView() => InitializeComponent();

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void Chip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string filter } && Vm is { } vm)
            vm.ServerFilter = filter;
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src && ItemsControl.ContainerFromElement((ListBox)sender, src) is ListBoxItem { DataContext: ServerItemViewModel server })
            Vm?.ConnectToCommand.Execute(server);
    }

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (Vm?.SelectedServer is not { } server) return;

        switch (e.Key)
        {
            case Key.Enter:
                Vm.ConnectToCommand.Execute(server);
                e.Handled = true;
                break;
            case Key.Delete:
                Vm.RemoveServerCommand.Execute(server);
                e.Handled = true;
                break;
            case Key.F2:
                Vm.RenameServerCommand.Execute(server);
                e.Handled = true;
                break;
            case Key.C when Keyboard.Modifiers == ModifierKeys.Control:
                Vm.CopyServerLinkCommand.Execute(server);
                e.Handled = true;
                break;
        }
    }

    /// <summary>The "…" button opens the same context menu as a right click.</summary>
    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement btn) return;

        var row = btn.Parent as FrameworkElement;
        if (row?.ContextMenu is not { } menu) return;

        menu.PlacementTarget = row;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
