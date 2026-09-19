using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NdzeVpn.Models;
using NdzeVpn.ViewModels;

namespace NdzeVpn;

/// <summary>
/// Dev aid: <c>NdzeVpn.exe --snapshot &lt;dir&gt;</c> renders every page in every theme to PNG from an
/// off-screen window, then exits. Nothing is shown, focused or captured from the real desktop, and
/// the settings file is left untouched (theme is switched in memory only).
/// </summary>
internal static class Snapshots
{
    public static async Task RenderAsync(MainWindow window, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var vm = (MainViewModel)window.DataContext;
        var originalTheme = vm.Settings.Theme;

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();

        foreach (var theme in Enum.GetValues<AppTheme>())
        {
            App.ApplyTheme(theme);
            foreach (var page in Enum.GetValues<Page>())
            {
                vm.NavigateCommand.Execute(page);
                await Settle();
                Save(window, Path.Combine(outDir, $"{theme}-{page}.png"));
            }
        }

        // Collapsed sidebar and the secondary windows, in the default theme.
        App.ApplyTheme(AppTheme.Graphite);
        var collapsed = vm.IsSidebarCollapsed;
        vm.IsSidebarCollapsed = !collapsed;
        vm.NavigateCommand.Execute(Page.Home);
        await Task.Delay(400);
        await Settle();
        Save(window, Path.Combine(outDir, $"Graphite-Sidebar-{(collapsed ? "expanded" : "collapsed")}.png"));
        vm.IsSidebarCollapsed = collapsed;

        await RenderWindow(new Views.DiscordWindow { DataContext = vm }, Path.Combine(outDir, "Window-Discord.png"));
        await RenderWindow(new Views.KeyCheckWindow { DataContext = vm }, Path.Combine(outDir, "Window-KeyCheck.png"));

        vm.AvailableUpdate ??= new Services.UpdateInfo(new Version(9, 9, 9), "v9.9.9", "Ndze VPN 9.9.9",
            "• Пример заметок к релизу\n• Сворачиваемая боковая панель\n• Отдельное окно для Discord",
            "", "", "NdzeVPN-Setup-9.9.9.exe", 100_000_000, null);
        await RenderWindow(new Views.UpdateWindow { DataContext = vm }, Path.Combine(outDir, "Window-Update.png"));

        App.ApplyTheme(originalTheme);
        window.Hide();
    }

    private static async Task RenderWindow(Window w, string path)
    {
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -20000;
        w.Top = -20000;
        w.ShowActivated = false;
        w.ShowInTaskbar = false;
        w.Show();
        await Settle();
        Save(w, path);
        w.Close();
    }

    private static async Task Settle()
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(250);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static void Save(Window window, string path)
    {
        var root = (FrameworkElement)window.Content;
        root.UpdateLayout();

        var dpi = VisualTreeHelper.GetDpi(root);
        var width = (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY);

        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);

        // Paint the window background first; the root grid itself is transparent.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
