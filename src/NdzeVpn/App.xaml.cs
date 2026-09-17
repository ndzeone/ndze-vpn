using System.Drawing;
using System.Windows;
using System.Windows.Threading;
using NdzeVpn.Models;
using NdzeVpn.Services;
using NdzeVpn.ViewModels;
using Forms = System.Windows.Forms;

namespace NdzeVpn;

public partial class App : Application
{
    private const string MutexName = "NdzeVpn.SingleInstance";
    private const string ShowEventName = "NdzeVpn.ShowWindow";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _trayToggle;
    private bool _exiting;

    public static VpnController Vpn { get; private set; } = null!;
    public static MainViewModel ViewModel { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var snapshotIdx = Array.IndexOf(e.Args, "--snapshot");
        var isSnapshot = snapshotIdx >= 0 && snapshotIdx + 1 < e.Args.Length;

        // Snapshot runs are offscreen dev renders: never poke or block the user's running instance.
        var isFirst = true;
        if (!isSnapshot) _mutex = new Mutex(true, MutexName, out isFirst);
        if (!isFirst)
        {
            // Already running: poke the existing instance to show itself, then leave.
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            Shutdown();
            return;
        }

        if (!isSnapshot)
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            new Thread(() =>
            {
                while (_showEvent.WaitOne())
                    Dispatcher.BeginInvoke(ShowMainWindow);
            }) { IsBackground = true, Name = "single-instance" }.Start();
        }

        HookCrashHandlers();

        Vpn = new VpnController();
        Vpn.Initialize();
        ApplyTheme(Vpn.Settings.Theme);

        ViewModel = new MainViewModel(Vpn);
        ViewModel.RefreshGeoStatus();

        var window = new MainWindow { DataContext = ViewModel };
        MainWindow = window;

        if (isSnapshot)
        {
            await Snapshots.RenderAsync(window, e.Args[snapshotIdx + 1]);
            _exiting = true;
            Shutdown();
            return;
        }

        CreateTray();
        Vpn.StateChanged += (_, _) => Dispatcher.BeginInvoke(UpdateTray);

        var autostart = e.Args.Contains("--autostart");
        if (!(autostart && Vpn.Settings.StartMinimized)) window.Show();

        // Keys can be passed straight on the command line: NdzeVpn.exe "vless://…"
        var keys = string.Join('\n', e.Args.Where(a => a.Contains("://")));
        if (keys.Length > 0)
        {
            var nodes = ShareLinkParser.ParseMany(keys);
            if (nodes.Count > 0) Vpn.AddProfiles(nodes, out _, out _);
        }

        StartUpdateChecks(window);

        if (Vpn.Settings.AutoConnectOnLaunch && Vpn.Store.Profiles.Count > 0)
            await Vpn.ConnectAsync();
    }

    /// <summary>Check GitHub shortly after launch, then every 6 hours while running.</summary>
    private void StartUpdateChecks(Window window)
    {
        if (!Vpn.Settings.CheckUpdatesOnStartup) return;

        string? announced = null;

        async Task CheckAsync(bool atStartup)
        {
            if (!await ViewModel.CheckForUpdatesQuietAsync()) return;
            var version = ViewModel.AvailableUpdate!.Version.ToString(3);
            if (announced == version) return; // the sidebar entry keeps showing it; don't nag
            announced = version;
            if (window.IsVisible && atStartup) ViewModel.OpenUpdateWindow();
            else NotifyTray("Доступно обновление Ndze VPN", $"Версия {version}. Открой программу, чтобы обновить.");
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        var first = true;
        timer.Tick += async (_, _) =>
        {
            timer.Interval = TimeSpan.FromHours(6);
            var atStartup = first;
            first = false;
            await CheckAsync(atStartup);
        };
        timer.Start();
    }

    // ================================================================== theme

    public static void ApplyTheme(AppTheme theme)
    {
        var uri = new Uri($"Themes/{theme}.xaml", UriKind.Relative);
        var dict = new ResourceDictionary { Source = uri };

        var merged = Current.Resources.MergedDictionaries;
        merged[0] = dict;
    }

    // ================================================================== window / tray

    public void ShowMainWindow()
    {
        if (MainWindow is null) return;
        MainWindow.Show();
        if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
        MainWindow.Topmost = true;
        MainWindow.Topmost = false;
        MainWindow.Focus();
    }

    private void CreateTray()
    {
        Icon? icon = null;
        try
        {
            // Ask for the small-icon size at the current DPI so the tray does not downscale a 256px frame.
            var size = Forms.SystemInformation.SmallIconSize;
            using var stream = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))!.Stream;
            icon = new Icon(stream, size);
        }
        catch
        {
            try { icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        }

        var menu = new Forms.ContextMenuStrip
        {
            ShowImageMargin = false,
            BackColor = Color.FromArgb(40, 40, 40),
            ForeColor = Color.White,
            Renderer = new Forms.ToolStripProfessionalRenderer(new DarkMenuColors())
        };

        _trayToggle = new Forms.ToolStripMenuItem("Подключить", null, async (_, _) => await Vpn.ToggleAsync());
        _trayToggle.Font = new Font(_trayToggle.Font, System.Drawing.FontStyle.Bold);

        menu.Items.Add(_trayToggle);
        menu.Items.Add("Самый быстрый сервер", null, (_, _) => ViewModel.ConnectFastestCommand.Execute(null));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Открыть", null, (_, _) => ShowMainWindow());
        menu.Items.Add("Выход", null, async (_, _) => await ExitAsync());

        _tray = new Forms.NotifyIcon
        {
            Icon = icon ?? SystemIcons.Shield,
            Text = "Ndze VPN",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) ShowMainWindow();
        };
        _tray.MouseDoubleClick += async (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) await Vpn.ToggleAsync();
        };

        UpdateTray();
    }

    private void UpdateTray()
    {
        if (_tray is null || _trayToggle is null) return;

        var connected = Vpn.State == ConnectionState.Connected;
        _trayToggle.Text = connected ? "Отключить" : "Подключить";

        var text = connected ? $"Ndze VPN — {Vpn.ActiveProfile?.DisplayName}" : $"Ndze VPN — {Vpn.State}";
        // NotifyIcon.Text throws past 63 characters.
        _tray.Text = text.Length > 63 ? text[..62] + "…" : text;
    }

    public void NotifyTray(string title, string message)
    {
        if (_tray is null) return;
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText = message;
        _tray.ShowBalloonTip(2500);
    }

    // ================================================================== shutdown

    public bool IsExiting => _exiting;

    public async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;

        try
        {
            Vpn.Store.SaveAll();
            await Vpn.DisposeAsync();
        }
        catch (Exception ex)
        {
            LogBus.Instance.Error("app", "Shutdown cleanup failed", ex);
        }

        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }

        Shutdown();
    }

    /// <summary>
    /// Tear the tunnel down cleanly (so Windows is not left pointing at a dead proxy), start the
    /// installer, then quit so it can replace our files. The installer relaunches the new version.
    /// </summary>
    public async Task ExitForUpdateAsync(string installerPath)
    {
        await Vpn.DisconnectAsync();
        UpdateService.LaunchInstaller(installerPath);
        await ExitAsync();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Logoff/shutdown: there is no time for async niceties, but the proxy MUST be restored.
        EmergencyCleanup();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        EmergencyCleanup();
        _mutex?.Dispose();
        _showEvent?.Dispose();
        base.OnExit(e);
    }

    private void HookCrashHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            LogBus.Instance.Error("app", "Unhandled UI exception", args.Exception);
            MessageBox.Show(args.Exception.Message, "Ndze VPN — ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            LogBus.Instance.Error("app", $"Fatal: {args.ExceptionObject}");
            EmergencyCleanup();
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogBus.Instance.Error("app", "Unobserved task exception", args.Exception);
            args.SetObserved();
        };
    }

    /// <summary>Synchronous last-resort teardown: never leave Windows pointed at a dead proxy.</summary>
    private static void EmergencyCleanup()
    {
        try
        {
            if (Vpn is null) return;
            if (SystemProxyService.IsPointedAt(Vpn.Settings.HttpPort))
            {
                var proxy = new SystemProxyService();
                proxy.Apply(Vpn.Settings.HttpPort);
                proxy.Clear();
            }
            Vpn.DisposeAsync().AsTask().Wait(3000);
        }
        catch { }
    }

    private sealed class DarkMenuColors : Forms.ProfessionalColorTable
    {
        public override Color MenuItemSelected => Color.FromArgb(62, 62, 62);
        public override Color MenuItemBorder => Color.FromArgb(62, 62, 62);
        public override Color MenuBorder => Color.FromArgb(30, 30, 30);
        public override Color ToolStripDropDownBackground => Color.FromArgb(40, 40, 40);
        public override Color ImageMarginGradientBegin => Color.FromArgb(40, 40, 40);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(40, 40, 40);
        public override Color ImageMarginGradientEnd => Color.FromArgb(40, 40, 40);
        public override Color SeparatorDark => Color.FromArgb(70, 70, 70);
        public override Color SeparatorLight => Color.FromArgb(70, 70, 70);
    }
}
