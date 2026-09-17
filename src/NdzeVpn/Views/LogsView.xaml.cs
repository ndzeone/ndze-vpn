using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NdzeVpn.ViewModels;

namespace NdzeVpn.Views;

public partial class LogsView : UserControl
{
    private MainViewModel? _vm;
    private bool _scrollQueued;

    public LogsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null) _vm.LogAppended -= OnLogAppended;
        _vm = e.NewValue as MainViewModel;
        if (_vm is not null) _vm.LogAppended += OnLogAppended;
    }

    private void OnLogAppended(object? sender, EventArgs e)
    {
        if (_vm is not { LogAutoScroll: true } || !IsVisible || _scrollQueued) return;

        // Xray can emit hundreds of lines a second; coalesce scrolls into one per render.
        _scrollQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _scrollQueued = false;
            if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
        });
    }
}
