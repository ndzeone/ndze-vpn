using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NdzeVpn.Models;
using NdzeVpn.Services;

namespace NdzeVpn.ViewModels;

public enum Page { Home, Servers, Subscriptions, Routing, Settings, Logs }

public sealed partial class MainViewModel : ObservableObject
{
    private readonly VpnController _vpn;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _clock;
    private readonly Stack<Page> _back = new();
    private readonly Stack<Page> _forward = new();
    private CancellationTokenSource? _pingCts;

    public MainViewModel(VpnController vpn)
    {
        _vpn = vpn;
        _ui = Application.Current.Dispatcher;

        ServersView = CollectionViewSource.GetDefaultView(Servers);
        ServersView.Filter = FilterServer;

        LoadServers();
        LoadSubscriptions();
        LoadRules();
        LoadLogs();

        _vpn.StateChanged += (_, _) => _ui.BeginInvoke(SyncState);
        _vpn.TrafficUpdated += (_, _) => _ui.BeginInvoke(SyncTraffic);
        _vpn.ProfilesChanged += (_, _) => _ui.BeginInvoke(LoadServers);
        _vpn.SubscriptionsChanged += (_, _) => _ui.BeginInvoke(() =>
        {
            foreach (var s in Subscriptions) s.Refresh();
            OnPropertyChanged(nameof(HasSubscriptions));
        });
        LogBus.Instance.EntryAdded += (_, e) => _ui.BeginInvoke(() => AppendLog(e));

        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick(), _ui);
        _clock.Start();

        SyncState();
    }

    public AppSettings Settings => _vpn.Settings;
    public SmartRoutingSettings Smart => _vpn.Settings.Smart;
    public DiscordSettings Discord => _vpn.Settings.Discord;

    // ================================================================== navigation

    [ObservableProperty] private Page _currentPage = Page.Home;

    partial void OnCurrentPageChanged(Page oldValue, Page newValue)
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;

    [RelayCommand]
    private void Navigate(Page page)
    {
        if (page == CurrentPage) return;
        _back.Push(CurrentPage);
        _forward.Clear();
        CurrentPage = page;
    }

    [RelayCommand]
    private void GoBack()
    {
        if (_back.Count == 0) return;
        _forward.Push(CurrentPage);
        CurrentPage = _back.Pop();
    }

    [RelayCommand]
    private void GoForward()
    {
        if (_forward.Count == 0) return;
        _back.Push(CurrentPage);
        CurrentPage = _forward.Pop();
    }

    // ================================================================== toast

    [ObservableProperty] private string? _toast;
    private CancellationTokenSource? _toastCts;

    public async void ShowToast(string message)
    {
        _toastCts?.Cancel();
        var cts = _toastCts = new CancellationTokenSource();
        Toast = message;
        try
        {
            await Task.Delay(3500, cts.Token);
            Toast = null;
        }
        catch (TaskCanceledException) { }
    }

    // ================================================================== connection

    [ObservableProperty] private ConnectionState _state;
    [ObservableProperty] private string _statusText = "Не подключено";
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private ServerItemViewModel? _activeServer;
    [ObservableProperty] private string _elapsedText = "0:00";
    [ObservableProperty] private string _cycleEndText = "1:00:00";
    [ObservableProperty] private double _sessionProgress;
    [ObservableProperty] private string _uploadSpeed = "0 B/s";
    [ObservableProperty] private string _downloadSpeed = "0 B/s";
    [ObservableProperty] private string _totalTraffic = "0 B";
    [ObservableProperty] private string _pingText = "—";
    [ObservableProperty] private string _modeText = "";
    [ObservableProperty] private bool _discordConnected;
    [ObservableProperty] private string? _proxiedIp;
    [ObservableProperty] private string? _directIp;
    [ObservableProperty] private bool _isCheckingIp;

    public bool IsConnected => State == ConnectionState.Connected;
    public bool IsBusy => State is ConnectionState.Connecting or ConnectionState.Disconnecting;


    private void SyncState()
    {
        State = _vpn.State;
        ErrorText = _vpn.State == ConnectionState.Error ? _vpn.LastError : null;
        DiscordConnected = _vpn.DiscordConnected;

        StatusText = _vpn.State switch
        {
            ConnectionState.Connected => "Подключено",
            ConnectionState.Connecting => "Подключение…",
            ConnectionState.Disconnecting => "Отключение…",
            ConnectionState.Error => "Ошибка",
            _ => "Не подключено"
        };

        ModeText = _vpn.ActiveMode switch
        {
            TunnelMode.Tun => "TUN",
            TunnelMode.SystemProxy => "Системный прокси",
            _ => Settings.Mode == TunnelMode.Tun ? "TUN" : "Системный прокси"
        };

        var activeId = _vpn.ActiveProfile?.Id ?? Settings.ActiveProfileId;
        foreach (var s in Servers) s.IsActive = IsConnected && s.Id == activeId;
        ActiveServer = Servers.FirstOrDefault(s => s.Id == activeId) ?? Servers.FirstOrDefault();

        PingText = _vpn.RealDelayMs > 0 ? $"{_vpn.RealDelayMs} ms"
            : ActiveServer?.LatencyMs > 0 ? $"{ActiveServer.LatencyMs} ms"
            : "—";

        // Failovers and the "admin prompt declined" fallback report themselves through here.
        if (_vpn.Notice is { Length: > 0 } notice)
        {
            _vpn.Notice = null;
            ShowToast(notice);
            OnPropertyChanged(nameof(IsTunMode));
            OnPropertyChanged(nameof(TunnelMode));
        }

        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsBusy));
        UpdateDiscordPreview();

        if (!IsConnected)
        {
            UploadSpeed = DownloadSpeed = "0 B/s";
            SessionProgress = 0;
            ElapsedText = "0:00";
        }
    }

    private void SyncTraffic()
    {
        var t = _vpn.Traffic;
        UploadSpeed = StatsService.FormatSpeed(t.UploadBytesPerSecond);
        DownloadSpeed = StatsService.FormatSpeed(t.DownloadBytesPerSecond);
        TotalTraffic = StatsService.FormatBytes(t.UplinkTotal + t.DownlinkTotal);
    }

    private void Tick()
    {
        if (_vpn.ConnectedAt is { } at && IsConnected)
        {
            var elapsed = DateTimeOffset.Now - at;
            var cycle = TimeSpan.FromMinutes(Math.Max(1, Discord.ProgressCycleMinutes));
            var inCycle = TimeSpan.FromTicks(elapsed.Ticks % cycle.Ticks);

            ElapsedText = FormatSpan(elapsed);
            CycleEndText = FormatSpan(cycle);
            SessionProgress = inCycle.TotalSeconds / cycle.TotalSeconds * 100;
        }
        UpdateDiscordPreview();
    }

    private static string FormatSpan(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    [RelayCommand]
    private async Task ToggleConnection()
    {
        if (IsBusy) return;
        if (IsConnected || State == ConnectionState.Connecting)
        {
            await _vpn.DisconnectAsync();
            return;
        }

        if (Servers.Count == 0)
        {
            ShowToast("Сначала добавь ключ или подписку");
            Navigate(Page.Subscriptions);
            return;
        }

        var ok = await _vpn.ConnectAsync(ActiveServer?.Model);
        if (!ok && _vpn.LastError is { } err) ShowToast(err);
    }

    [RelayCommand]
    private async Task ConnectTo(ServerItemViewModel? server)
    {
        if (server is null || IsBusy) return;
        if (IsConnected && ActiveServer?.Id == server.Id)
        {
            await _vpn.DisconnectAsync();
            return;
        }
        ActiveServer = server;
        var ok = await _vpn.ConnectAsync(server.Model);
        if (!ok && _vpn.LastError is { } err) ShowToast(err);
    }

    [RelayCommand]
    private Task NextServer() => StepServer(+1);

    [RelayCommand]
    private Task PreviousServer() => StepServer(-1);

    private async Task StepServer(int direction)
    {
        var list = ServersView.Cast<ServerItemViewModel>().ToList();
        if (list.Count == 0) return;

        var idx = ActiveServer is null ? -1 : list.FindIndex(s => s.Id == ActiveServer.Id);
        var next = list[((idx + direction) % list.Count + list.Count) % list.Count];

        if (IsConnected) await ConnectTo(next);
        else
        {
            ActiveServer = next;
            Settings.ActiveProfileId = next.Id;
            _vpn.Store.SaveSettings();
        }
    }

    [RelayCommand]
    private async Task ConnectFastest()
    {
        await TestPing();
        var best = Servers.Where(s => s.LatencyMs > 0).MinBy(s => s.LatencyMs);
        if (best is null)
        {
            ShowToast("Ни один сервер не ответил");
            return;
        }
        await ConnectTo(best);
    }

    // ================================================================== toggles in the player bar

    public bool AutoFailover
    {
        get => Settings.AutoFailover;
        set { Settings.AutoFailover = value; _vpn.Store.SaveSettings(); OnPropertyChanged(); }
    }

    /// <summary>Player-bar toggle: applies (and reconnects) immediately.</summary>
    public bool IsTunMode
    {
        get => Settings.Mode == TunnelMode.Tun;
        set
        {
            var mode = value ? TunnelMode.Tun : TunnelMode.SystemProxy;
            if (Settings.Mode == mode) return;
            Settings.Mode = mode;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TunnelMode));
            _ = _vpn.ReapplyAsync();
            ShowToast(value ? "Режим TUN: понадобятся права администратора" : "Режим системного прокси");
        }
    }

    /// <summary>Settings-page card selection: takes effect on Apply, like the rest of that page.</summary>
    public TunnelMode TunnelMode => Settings.Mode;

    [RelayCommand]
    private void SetTunnelMode(string mode)
    {
        if (!Enum.TryParse<TunnelMode>(mode, out var value) || Settings.Mode == value) return;
        Settings.Mode = value;
        OnPropertyChanged(nameof(TunnelMode));
        OnPropertyChanged(nameof(IsTunMode));
    }

    /// <summary>Home screen segmented control: switch and reconnect straight away.</summary>
    [RelayCommand]
    private void ApplyTunnelMode(string mode) => IsTunMode = mode == nameof(TunnelMode.Tun);

    /// <summary>Home screen segmented control: switch routing and reconnect straight away.</summary>
    [RelayCommand]
    private async Task ApplyRoutingMode(string mode)
    {
        if (!Enum.TryParse<RoutingMode>(mode, out var value) || Settings.RoutingMode == value) return;
        Settings.RoutingMode = value;
        OnPropertyChanged(nameof(RoutingMode));
        await _vpn.ReapplyAsync();
    }

    public RoutingMode RoutingMode => Settings.RoutingMode;

    [RelayCommand]
    private void SetRoutingMode(string mode)
    {
        if (!Enum.TryParse<RoutingMode>(mode, out var value) || Settings.RoutingMode == value) return;
        Settings.RoutingMode = value;
        OnPropertyChanged(nameof(RoutingMode));
    }

    // ================================================================== servers

    public ObservableCollection<ServerItemViewModel> Servers { get; } = [];
    public ICollectionView ServersView { get; }

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _serverFilter = "all";
    [ObservableProperty] private string _serverSort = "default";
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private ServerItemViewModel? _selectedServer;

    partial void OnSearchTextChanged(string value) => ServersView.Refresh();
    partial void OnServerFilterChanged(string value) => ServersView.Refresh();

    partial void OnServerSortChanged(string value)
    {
        using (ServersView.DeferRefresh())
        {
            ServersView.SortDescriptions.Clear();
            switch (value)
            {
                case "ping":
                    ServersView.SortDescriptions.Add(new SortDescription(nameof(ServerItemViewModel.LatencySortKey), ListSortDirection.Ascending));
                    break;
                case "name":
                    ServersView.SortDescriptions.Add(new SortDescription(nameof(ServerItemViewModel.CleanName), ListSortDirection.Ascending));
                    break;
            }
        }
    }

    public bool HasServers => Servers.Count > 0;

    private bool FilterServer(object obj)
    {
        if (obj is not ServerItemViewModel s) return false;

        var matchesFilter = ServerFilter switch
        {
            "vless" => s.Model.Protocol == ProxyProtocol.Vless,
            "reality" => s.IsReality,
            "alive" => s.LatencyMs > 0,
            "manual" => string.IsNullOrEmpty(s.SubscriptionId),
            "all" => true,
            var subId => s.SubscriptionId == subId
        };
        if (!matchesFilter) return false;

        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        var q = SearchText.Trim();
        return s.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
               || s.Address.Contains(q, StringComparison.OrdinalIgnoreCase)
               || s.Protocol.Contains(q, StringComparison.OrdinalIgnoreCase)
               || s.Transport.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void LoadServers()
    {
        var selectedId = SelectedServer?.Id;
        Servers.Clear();
        var i = 1;
        foreach (var p in _vpn.Store.Profiles) Servers.Add(new ServerItemViewModel(p, i++));

        SelectedServer = Servers.FirstOrDefault(s => s.Id == selectedId);
        OnPropertyChanged(nameof(HasServers));
        SyncState();
    }

    [RelayCommand]
    private async Task TestPing()
    {
        if (IsTesting)
        {
            _pingCts?.Cancel();
            return;
        }

        IsTesting = true;
        _pingCts = new CancellationTokenSource();
        foreach (var s in Servers) s.IsTesting = true;

        try
        {
            await _vpn.TestLatencyAsync(profile => _ui.BeginInvoke(() =>
            {
                var vm = Servers.FirstOrDefault(s => s.Id == profile.Id);
                if (vm is null) return;
                vm.RefreshLatency();
                vm.IsTesting = false;
            }), _pingCts.Token);

            var alive = Servers.Count(s => s.LatencyMs > 0);
            ShowToast($"Пинг: отвечают {alive} из {Servers.Count}");
        }
        catch (OperationCanceledException) { }
        finally
        {
            foreach (var s in Servers) s.IsTesting = false;
            IsTesting = false;
            if (ServerSort == "ping") ServersView.Refresh();
            SyncState();
        }
    }

    [RelayCommand]
    private void RemoveServer(ServerItemViewModel? server)
    {
        if (server is null) return;
        _vpn.RemoveProfile(server.Model);
        ShowToast($"Удалён: {server.CleanName}");
    }

    [RelayCommand]
    private void CopyServerLink(ServerItemViewModel? server)
    {
        if (server is null || string.IsNullOrEmpty(server.Model.RawUri)) return;
        try
        {
            Clipboard.SetText(server.Model.RawUri);
            ShowToast("Ссылка скопирована");
        }
        catch { ShowToast("Буфер обмена занят"); }
    }

    [RelayCommand]
    private void RenameServer(ServerItemViewModel? server)
    {
        if (server is null) return;
        var name = Views.InputDialog.Ask("Переименовать сервер", "Новое имя", server.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        server.Model.Remark = name.Trim();
        _vpn.SaveProfiles();
    }

    // ================================================================== import

    [ObservableProperty] private string _importText = "";

    /// <summary>
    /// The search box doubles as the import box: paste a vless:// key or a subscription URL there and
    /// press Enter. Anything else just filters the server list.
    /// </summary>
    [RelayCommand]
    private async Task SubmitSearch()
    {
        var text = SearchText.Trim();
        if (text.Length == 0) return;

        if (ShareLinkParser.ExtractLinks(text).Any())
        {
            ImportKeys(text);
            SearchText = "";
            return;
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            SearchText = "";
            await AddSubscriptionFromUrl(text);
            return;
        }

        Navigate(Page.Servers);
    }

    [RelayCommand]
    private async Task PasteFromClipboard()
    {
        string text;
        try { text = Clipboard.GetText(); }
        catch { ShowToast("Не удалось прочитать буфер обмена"); return; }

        if (string.IsNullOrWhiteSpace(text))
        {
            ShowToast("Буфер обмена пуст");
            return;
        }

        if (ShareLinkParser.ExtractLinks(text).Any())
        {
            ImportKeys(text);
            return;
        }

        if (Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            await AddSubscriptionFromUrl(text.Trim());
            return;
        }

        // Maybe a base64 blob copied out of a bot message.
        if (ShareLinkParser.LooksLikeBase64(text))
        {
            try
            {
                var decoded = ShareLinkParser.Base64Decode(text);
                if (ShareLinkParser.ExtractLinks(decoded).Any())
                {
                    ImportKeys(decoded);
                    return;
                }
            }
            catch { }
        }

        ShowToast("В буфере нет ключа или ссылки на подписку");
    }

    [RelayCommand]
    private void ImportFromBox()
    {
        if (string.IsNullOrWhiteSpace(ImportText)) return;

        var text = ImportText;
        var urls = text.Split(['\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (ShareLinkParser.ExtractLinks(text).Any()) ImportKeys(text);
        foreach (var url in urls) _ = AddSubscriptionFromUrl(url);

        ImportText = "";
    }

    private void ImportKeys(string text)
    {
        var nodes = ShareLinkParser.ParseMany(text);
        if (nodes.Count == 0)
        {
            ShowToast("Ключи найдены, но не распознаны");
            return;
        }

        _vpn.AddProfiles(nodes, out var added, out var skipped);
        ShowToast(skipped > 0 ? $"Добавлено {added}, уже были: {skipped}" : $"Добавлено серверов: {added}");

        if (Servers.Count == added && added > 0)
        {
            // First ever import — make it the active node so the play button just works.
            Settings.ActiveProfileId = nodes[0].Id;
            _vpn.Store.SaveSettings();
            SyncState();
        }
    }

    // ================================================================== subscriptions

    public ObservableCollection<SubscriptionItemViewModel> Subscriptions { get; } = [];
    public bool HasSubscriptions => Subscriptions.Count > 0;

    [ObservableProperty] private string _newSubscriptionUrl = "";
    [ObservableProperty] private string _newSubscriptionName = "";

    private void LoadSubscriptions()
    {
        Subscriptions.Clear();
        foreach (var s in _vpn.Store.Subscriptions) Subscriptions.Add(new SubscriptionItemViewModel(s));
        OnPropertyChanged(nameof(HasSubscriptions));
    }

    [RelayCommand]
    private async Task AddSubscription()
    {
        var url = NewSubscriptionUrl.Trim();
        if (url.Length == 0) return;

        // The "subscription" box also accepts raw keys — people paste whatever the bot gave them.
        if (ShareLinkParser.ExtractLinks(url).Any() && !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            ImportKeys(url);
            NewSubscriptionUrl = "";
            return;
        }

        await AddSubscriptionFromUrl(url, NewSubscriptionName);
        NewSubscriptionUrl = "";
        NewSubscriptionName = "";
    }

    private async Task AddSubscriptionFromUrl(string url, string? name = null)
    {
        if (_vpn.Store.Subscriptions.Any(s => s.Url.Equals(url, StringComparison.OrdinalIgnoreCase)))
        {
            ShowToast("Эта подписка уже добавлена");
            return;
        }

        var sub = _vpn.AddSubscription(url, name);
        var vm = new SubscriptionItemViewModel(sub);
        Subscriptions.Add(vm);
        OnPropertyChanged(nameof(HasSubscriptions));

        await UpdateSubscription(vm);
    }

    [RelayCommand]
    private async Task UpdateSubscription(SubscriptionItemViewModel? sub)
    {
        if (sub is null || sub.IsUpdating) return;
        sub.IsUpdating = true;
        try
        {
            var result = await _vpn.UpdateSubscriptionAsync(sub.Model);
            sub.Refresh();
            ShowToast(result.Nodes.Count > 0
                ? $"{sub.Name}: {result.Nodes.Count} серв."
                : $"{sub.Name}: {result.Error}");

            if (Settings.ActiveProfileId is null && Servers.Count > 0)
            {
                Settings.ActiveProfileId = Servers[0].Id;
                _vpn.Store.SaveSettings();
                SyncState();
            }
        }
        finally
        {
            sub.IsUpdating = false;
        }
    }

    [RelayCommand]
    private async Task UpdateAllSubscriptions()
    {
        foreach (var s in Subscriptions.ToList()) await UpdateSubscription(s);
    }

    [RelayCommand]
    private void RemoveSubscription(SubscriptionItemViewModel? sub)
    {
        if (sub is null) return;
        var answer = MessageBox.Show(
            $"Удалить подписку «{sub.Name}» вместе с её серверами?",
            "Ndze VPN", MessageBoxButton.YesNoCancel, MessageBoxImage.Question,
            MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.Cancel) return;

        _vpn.RemoveSubscription(sub.Model, removeNodes: answer == MessageBoxResult.Yes);
        Subscriptions.Remove(sub);
        OnPropertyChanged(nameof(HasSubscriptions));
    }

    [RelayCommand]
    private void SaveSubscriptions()
    {
        _vpn.Store.SaveSubscriptions();
        foreach (var s in Subscriptions) s.Refresh();
        ShowToast("Подписки сохранены");
    }

    [RelayCommand]
    private void ShowSubscriptionServers(SubscriptionItemViewModel? sub)
    {
        if (sub is null) return;
        ServerFilter = sub.Id;
        Navigate(Page.Servers);
    }

    // ================================================================== routing

    public ObservableCollection<RuleItemViewModel> Rules { get; } = [];

    public IReadOnlyList<Option<RuleKind>> RuleKinds { get; } =
    [
        new(RuleKind.Domain, "Домен"),
        new(RuleKind.DomainFull, "Точный домен"),
        new(RuleKind.DomainRegex, "Regex"),
        new(RuleKind.GeoSite, "GeoSite"),
        new(RuleKind.GeoIp, "GeoIP"),
        new(RuleKind.Ip, "IP / CIDR"),
        new(RuleKind.Port, "Порт"),
        new(RuleKind.Process, "Процесс")
    ];

    public IReadOnlyList<Option<RuleAction>> RuleActions { get; } =
    [
        new(RuleAction.Proxy, "Через VPN"),
        new(RuleAction.Direct, "Напрямую"),
        new(RuleAction.Block, "Блок")
    ];

    public IReadOnlyList<Option<string>> DomainStrategies { get; } =
    [
        new("IPIfNonMatch", "IPIfNonMatch — точно (рекомендуется)"),
        new("AsIs", "AsIs — быстро, только по доменам"),
        new("IPOnDemand", "IPOnDemand — всегда резолвить")
    ];

    public IReadOnlyList<Option<string>> GeoSources { get; } =
        GeoAssetService.Sources.Select(s => new Option<string>(s.Key, s.Name, s.Description)).ToList();

    public IReadOnlyList<Option<string>> LogLevels { get; } =
    [
        new("debug", "Debug"), new("info", "Info"), new("warning", "Warning"), new("error", "Error"), new("none", "Выкл")
    ];

    public IReadOnlyList<Option<int>> DiscordActivityTypes { get; } =
    [
        new(2, "Слушает"), new(3, "Смотрит"), new(0, "Играет"), new(5, "Соревнуется")
    ];

    private void LoadRules()
    {
        Rules.Clear();
        foreach (var r in Settings.CustomRules) Rules.Add(new RuleItemViewModel(r));
    }

    [RelayCommand]
    private void AddRule(string? preset)
    {
        var rule = preset switch
        {
            "discord" => new RoutingRule { Kind = RuleKind.Process, Action = RuleAction.Proxy, Value = "Discord.exe, Update.exe", Note = "Discord целиком (TUN)" },
            "games" => new RoutingRule { Kind = RuleKind.Process, Action = RuleAction.Direct, Value = "cs2.exe, javaw.exe, r5apex.exe", Note = "Игры напрямую — меньше пинг" },
            "steam" => new RoutingRule { Kind = RuleKind.Domain, Action = RuleAction.Direct, Value = "steampowered.com, steamcontent.com, steamserver.net", Note = "Загрузки Steam напрямую" },
            "torrent" => new RoutingRule { Kind = RuleKind.Process, Action = RuleAction.Direct, Value = "qbittorrent.exe, utorrent.exe", Note = "Торренты мимо VPN" },
            _ => new RoutingRule { Kind = RuleKind.Domain, Action = RuleAction.Proxy }
        };

        Settings.CustomRules.Add(rule);
        Rules.Add(new RuleItemViewModel(rule));
    }

    [RelayCommand]
    private void RemoveRule(RuleItemViewModel? rule)
    {
        if (rule is null) return;
        Settings.CustomRules.Remove(rule.Model);
        Rules.Remove(rule);
    }

    [RelayCommand]
    private void MoveRuleUp(RuleItemViewModel? rule) => MoveRule(rule, -1);

    [RelayCommand]
    private void MoveRuleDown(RuleItemViewModel? rule) => MoveRule(rule, +1);

    private void MoveRule(RuleItemViewModel? rule, int delta)
    {
        if (rule is null) return;
        var i = Rules.IndexOf(rule);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= Rules.Count) return;

        Rules.Move(i, j);
        Settings.CustomRules.RemoveAt(i);
        Settings.CustomRules.Insert(j, rule.Model);
    }

    [RelayCommand]
    private void ResetSmartRouting()
    {
        Settings.Smart = new SmartRoutingSettings();
        OnPropertyChanged(nameof(Smart));
        ShowToast("Умная маршрутизация сброшена по умолчанию");
    }

    // ================================================================== speed test

    [ObservableProperty] private bool _isSpeedTesting;
    [ObservableProperty] private string _speedTestStage = "";
    [ObservableProperty] private double _speedTestProgress;
    [ObservableProperty] private string _downMbps = "—";
    [ObservableProperty] private string _upMbps = "—";
    [ObservableProperty] private string? _speedTestSummary;
    private CancellationTokenSource? _speedCts;

    [RelayCommand]
    private async Task RunSpeedTest()
    {
        if (IsSpeedTesting)
        {
            _speedCts?.Cancel();
            return;
        }

        IsSpeedTesting = true;
        SpeedTestProgress = 0;
        SpeedTestSummary = null;
        DownMbps = UpMbps = "…";
        _speedCts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<SpeedTestProgress>(p =>
            {
                SpeedTestStage = p.Stage;
                SpeedTestProgress = p.Fraction * 100;
                if (p.Mbps <= 0) return;
                if (p.Stage == "Отдача") UpMbps = SpeedTestService.FormatMbps(p.Mbps);
                else DownMbps = SpeedTestService.FormatMbps(p.Mbps);
            });

            var result = await _vpn.RunSpeedTestAsync(progress, _speedCts.Token);

            DownMbps = SpeedTestService.FormatMbps(result.DownMbps);
            UpMbps = SpeedTestService.FormatMbps(result.UpMbps);
            SpeedTestSummary = result.Ok
                ? (IsConnected ? "Через VPN" : "Без VPN") + $" · {DateTime.Now:HH:mm}"
                : result.Error;

            if (!result.Ok) ShowToast(result.Error ?? "Замер не удался");
            if (result.PingMs > 0) PingText = $"{result.PingMs} ms";
        }
        finally
        {
            IsSpeedTesting = false;
            SpeedTestStage = "";
            SpeedTestProgress = 0;
        }
    }

    [RelayCommand]
    private async Task CheckIp()
    {
        if (IsCheckingIp) return;
        IsCheckingIp = true;
        try
        {
            var direct = LatencyService.DetectExternalIpAsync(Settings, throughProxy: false);
            var proxied = IsConnected ? LatencyService.DetectExternalIpAsync(Settings, throughProxy: true) : Task.FromResult<string?>(null);
            DirectIp = await direct ?? "не удалось";
            ProxiedIp = IsConnected ? await proxied ?? "не удалось" : "нет подключения";
        }
        finally
        {
            IsCheckingIp = false;
        }
    }

    // ================================================================== settings

    public AppTheme Theme => Settings.Theme;

    [RelayCommand]
    private void SetTheme(string theme)
    {
        if (!Enum.TryParse<AppTheme>(theme, out var value) || Settings.Theme == value) return;
        Settings.Theme = value;
        App.ApplyTheme(value);
        _vpn.Store.SaveSettings();
        OnPropertyChanged(nameof(Theme));
    }

    [ObservableProperty] private string _geoStatus = "";
    [ObservableProperty] private bool _isUpdatingGeo;

    [RelayCommand]
    private async Task Apply()
    {
        ApplyStartup(Settings.LaunchOnStartup);
        LogBus.Instance.Capacity = Settings.LogBufferLines;
        _vpn.Store.SaveSettings();

        OnPropertyChanged(nameof(IsTunMode));
        OnPropertyChanged(nameof(AutoFailover));

        if (IsConnected)
        {
            ShowToast("Применяю — переподключение…");
            await _vpn.ReapplyAsync();
        }
        else ShowToast("Настройки сохранены");

        await _vpn.UpdatePresenceAsync();
    }

    [RelayCommand]
    private async Task UpdateGeo()
    {
        if (IsUpdatingGeo) return;
        IsUpdatingGeo = true;

        void OnProgress(object? _, string msg) => _ui.BeginInvoke(() => GeoStatus = msg);
        _vpn.Geo.Progress += OnProgress;
        try
        {
            if (await _vpn.Geo.UpdateAsync(Settings.GeoAssetSource))
            {
                Settings.GeoAssetsUpdated = DateTime.Now;
                _vpn.Store.SaveSettings();
                RefreshGeoStatus();
                ShowToast("Базы правил обновлены");
                if (IsConnected) await _vpn.ReapplyAsync();
            }
            else ShowToast("Не удалось обновить базы — см. логи");
        }
        finally
        {
            _vpn.Geo.Progress -= OnProgress;
            IsUpdatingGeo = false;
        }
    }

    public void RefreshGeoStatus()
    {
        var (ip, site, ipSize, siteSize) = GeoAssetService.Status();
        var tags = site ? GeoAssetService.ReadGeoSiteTags(AppPaths.GeoSiteDat)?.Count ?? 0 : 0;
        GeoStatus = !ip || !site
            ? "Базы не загружены — загрузятся при первом подключении"
            : $"geoip {StatsService.FormatBytes(ipSize)} · geosite {StatsService.FormatBytes(siteSize)} ({tags} категорий)"
              + (Settings.GeoAssetsUpdated is { } at ? $" · {at:dd.MM.yyyy HH:mm}" : "");
    }

    [RelayCommand]
    private void OpenDataFolder() => OpenPath(AppPaths.DataDir);

    [RelayCommand]
    private void OpenLogsFolder() => OpenPath(AppPaths.LogDir);

    [RelayCommand]
    private void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private static void OpenPath(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { }
    }

    private static void ApplyStartup(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key is null) return;
            if (enabled) key.SetValue("NdzeVpn", $"\"{Environment.ProcessPath}\" --autostart");
            else key.DeleteValue("NdzeVpn", throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            LogBus.Instance.Warn("app", $"Autostart change failed: {ex.Message}");
        }
    }

    // ================================================================== discord preview

    [ObservableProperty] private string _discordPreviewTitle = "";
    [ObservableProperty] private string _discordPreviewDetails = "";
    [ObservableProperty] private string _discordPreviewState = "";
    [ObservableProperty] private string _discordPreviewHeader = "";

    private void UpdateDiscordPreview()
    {
        DiscordConnected = _vpn.DiscordConnected;

        DiscordPreviewHeader = Discord.ActivityType switch
        {
            2 => "Слушает Ndze VPN",
            3 => "Смотрит Ndze VPN",
            5 => "Соревнуется в Ndze VPN",
            _ => "Играет в Ndze VPN"
        };

        var server = ActiveServer is null ? "Сервер"
            : Discord.ShowServerName ? ActiveServer.CleanName : "Защищённое соединение";

        string Render(string template) => template
            .Replace("{server}", server)
            .Replace("{protocol}", ActiveServer?.Protocol ?? "VLESS")
            .Replace("{mode}", Settings.Mode == TunnelMode.Tun ? "TUN" : "Proxy")
            .Replace("{routing}", Settings.RoutingMode == RoutingMode.Smart ? "Умный" : Settings.RoutingMode.ToString())
            .Replace("{ping}", _vpn.RealDelayMs > 0 ? _vpn.RealDelayMs.ToString() : ActiveServer?.LatencyMs > 0 ? ActiveServer.LatencyMs.ToString() : "—")
            .Replace("{up}", UploadSpeed)
            .Replace("{down}", DownloadSpeed)
            .Replace("{total}", TotalTraffic);

        DiscordPreviewDetails = Render(Discord.DetailsTemplate);
        DiscordPreviewState = Render(Discord.StateTemplate);
        DiscordPreviewTitle = ActiveServer?.Protocol ?? "Ndze VPN";
    }

    [RelayCommand]
    private void OpenDiscordPortal() => OpenUrl("https://discord.com/developers/applications");

    partial void OnDiscordConnectedChanged(bool value) => OnPropertyChanged(nameof(DiscordSummary));

    public string DiscordLargeImageUrl => DiscordSettings.ResolveImage(Discord.LargeImageKey);

    /// <summary>One line for the Settings row that opens the Discord window.</summary>
    public string DiscordSummary =>
        !Discord.Enabled ? "Выключено"
        : string.IsNullOrWhiteSpace(Discord.ApplicationId) ? "Нужен Application ID"
        : $"{DiscordActivityTypes.FirstOrDefault(t => t.Value == Discord.ActivityType)?.Label ?? "Играет"} · {(DiscordConnected ? "Discord подключён" : "Discord не найден")}";

    [RelayCommand]
    private void OpenDiscordSettings()
    {
        var owner = Application.Current.MainWindow;
        var existing = Application.Current.Windows.OfType<Views.DiscordWindow>().FirstOrDefault();
        if (existing is not null)
        {
            existing.Activate();
            return;
        }
        var window = new Views.DiscordWindow { DataContext = this };
        if (owner is { IsVisible: true }) window.Owner = owner;
        window.Closed += (_, _) => OnPropertyChanged(nameof(DiscordSummary));
        window.Show();
    }

    [RelayCommand]
    private async Task SaveDiscord()
    {
        _vpn.Store.SaveSettings();
        OnPropertyChanged(nameof(DiscordSummary));
        OnPropertyChanged(nameof(DiscordLargeImageUrl));
        await _vpn.UpdatePresenceAsync();
        ShowToast("Активность Discord сохранена");
    }

    // ================================================================== sidebar

    public bool IsSidebarCollapsed
    {
        get => Settings.SidebarCollapsed;
        set
        {
            if (Settings.SidebarCollapsed == value) return;
            Settings.SidebarCollapsed = value;
            _vpn.Store.SaveSettings();
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarCollapsed = !IsSidebarCollapsed;

    // ================================================================== app updates

    private readonly UpdateService _updates = new();
    private CancellationTokenSource? _updateCts;

    public string AppVersion => UpdateService.CurrentVersionText;
    public string RepositoryUrl => UpdateService.RepositoryUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    private UpdateInfo? _availableUpdate;

    [ObservableProperty] private bool _isCheckingUpdate;
    [ObservableProperty] private bool _isDownloadingUpdate;
    [ObservableProperty] private double _updateProgress;
    [ObservableProperty] private string _updateStatus = "Новые версии скачиваются с GitHub";

    public bool HasUpdate => AvailableUpdate is not null;

    /// <summary>Quiet check used at launch and on a timer. Returns true when a new, not-skipped version exists.</summary>
    public async Task<bool> CheckForUpdatesQuietAsync()
    {
        if (IsCheckingUpdate || IsDownloadingUpdate) return false;
        IsCheckingUpdate = true;
        try
        {
            var info = await _updates.CheckAsync();
            AvailableUpdate = info;
            UpdateStatus = info is null ? $"Установлена последняя версия {AppVersion}" : $"Доступна версия {info.Version.ToString(3)}";
            return info is not null && info.Version.ToString(3) != Settings.SkippedUpdateVersion;
        }
        catch (Exception ex)
        {
            LogBus.Instance.Warn("update", $"Update check failed: {ex.Message}");
            UpdateStatus = "Не удалось проверить обновления";
            return false;
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    [RelayCommand]
    private async Task CheckForUpdates()
    {
        UpdateStatus = "Проверяю…";
        await CheckForUpdatesQuietAsync();
        if (AvailableUpdate is not null) OpenUpdateWindow();
        else ShowToast(UpdateStatus);
    }

    [RelayCommand]
    public void OpenUpdateWindow()
    {
        if (AvailableUpdate is null) return;
        var existing = Application.Current.Windows.OfType<Views.UpdateWindow>().FirstOrDefault();
        if (existing is not null)
        {
            existing.Activate();
            return;
        }
        var owner = Application.Current.MainWindow;
        var window = new Views.UpdateWindow { DataContext = this };
        if (owner is { IsVisible: true }) window.Owner = owner;
        window.Show();
    }

    [RelayCommand]
    private async Task InstallUpdate()
    {
        if (AvailableUpdate is not { } info || IsDownloadingUpdate) return;

        IsDownloadingUpdate = true;
        UpdateProgress = 0;
        UpdateStatus = "Загрузка…";
        _updateCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<double>(p =>
            {
                UpdateProgress = p * 100;
                UpdateStatus = $"Загрузка… {p * 100:0}%  ·  {StatsService.FormatBytes((long)(info.InstallerSize * p))} из {StatsService.FormatBytes(info.InstallerSize)}";
            });
            var path = await _updates.DownloadAsync(info, progress, _updateCts.Token);

            UpdateStatus = "Устанавливаю — программа перезапустится";
            await ((App)Application.Current).ExitForUpdateAsync(path);
        }
        catch (OperationCanceledException)
        {
            UpdateStatus = "Загрузка отменена";
        }
        catch (Exception ex)
        {
            LogBus.Instance.Error("update", "Update failed", ex);
            UpdateStatus = $"Ошибка: {ex.Message}";
        }
        finally
        {
            IsDownloadingUpdate = false;
        }
    }

    [RelayCommand]
    private void CancelUpdate() => _updateCts?.Cancel();

    [RelayCommand]
    private void SkipUpdate()
    {
        if (AvailableUpdate is null) return;
        Settings.SkippedUpdateVersion = AvailableUpdate.Version.ToString(3);
        _vpn.Store.SaveSettings();
    }

    [RelayCommand]
    private void OpenReleasePage() => OpenUrl(AvailableUpdate?.PageUrl ?? UpdateService.ReleasesUrl);

    [RelayCommand]
    private void OpenRepository() => OpenUrl(UpdateService.RepositoryUrl);

    // ================================================================== logs

    public ObservableCollection<LogLineViewModel> Logs { get; } = [];

    [ObservableProperty] private string _logFilter = "";
    [ObservableProperty] private bool _logAutoScroll = true;
    [ObservableProperty] private bool _logShowDebug;

    public event EventHandler? LogAppended;

    private void LoadLogs()
    {
        Logs.Clear();
        foreach (var e in LogBus.Instance.Snapshot().TakeLast(500)) AppendLog(e, notify: false);
    }

    private void AppendLog(LogEntry e, bool notify = true)
    {
        if (e.Level == LogLevel.Debug && !LogShowDebug) return;
        if (!string.IsNullOrEmpty(LogFilter)
            && !e.Message.Contains(LogFilter, StringComparison.OrdinalIgnoreCase)
            && !e.Source.Contains(LogFilter, StringComparison.OrdinalIgnoreCase)) return;

        Logs.Add(new LogLineViewModel(e));
        while (Logs.Count > 1500) Logs.RemoveAt(0);

        if (notify) LogAppended?.Invoke(this, EventArgs.Empty);
    }

    partial void OnLogFilterChanged(string value) => LoadLogs();
    partial void OnLogShowDebugChanged(bool value) => LoadLogs();

    [RelayCommand]
    private void ClearLogs()
    {
        LogBus.Instance.Clear();
        Logs.Clear();
    }

    [RelayCommand]
    private void CopyLogs()
    {
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, Logs.Select(l => $"{l.Time} {l.Level,-5} [{l.Source}] {l.Message}")));
            ShowToast("Логи скопированы");
        }
        catch { }
    }
}
