using System.Net.Sockets;
using NdzeVpn.Models;

namespace NdzeVpn.Services;

public enum ConnectionState { Disconnected, Connecting, Connected, Disconnecting, Error }

/// <summary>
/// The one place that knows the order things must happen in: config → Xray → proxy/TUN → stats →
/// presence, and the reverse on the way down. The UI only ever talks to this class.
/// </summary>
public sealed class VpnController : IAsyncDisposable
{
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly XrayProcess _xray = new();
    private readonly SystemProxyService _proxy = new();
    private readonly TunService _tun = new();
    private readonly DiscordRpcService _discord = new();
    private readonly SubscriptionService _subscriptions = new();
    private readonly GeoAssetService _geo = new();

    private StatsService? _stats;
    private CancellationTokenSource? _backgroundCts;
    private bool _userRequestedStop;

    public AppStore Store { get; } = new();
    public AppSettings Settings => Store.Settings;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public ServerProfile? ActiveProfile { get; private set; }
    public DateTimeOffset? ConnectedAt { get; private set; }
    public TunnelMode? ActiveMode { get; private set; }
    public TrafficSnapshot Traffic { get; private set; }
    public int RealDelayMs { get; private set; } = -1;
    public string? LastError { get; private set; }

    /// <summary>Last non-fatal thing the user should know about (picked up by the UI as a toast).</summary>
    public string? Notice { get; set; }

    public SpeedTestResult? LastSpeedTest { get; private set; }
    public IReadOnlyList<string> DroppedGeoSites { get; private set; } = [];

    public bool DiscordConnected => _discord.IsConnected;
    public GeoAssetService Geo => _geo;

    public event EventHandler? StateChanged;
    public event EventHandler<TrafficSnapshot>? TrafficUpdated;
    public event EventHandler? ProfilesChanged;
    public event EventHandler? SubscriptionsChanged;

    public VpnController()
    {
        _xray.Exited += OnXrayExited;
        _discord.ConnectionChanged += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ================================================================== lifecycle

    public void Initialize()
    {
        Store.Load();
        LogBus.Instance.Prune();

        // Crash recovery: if a previous run died with the proxy still pointed at us, every browser
        // on the machine is currently broken. Fix that before anything else.
        if (SystemProxyService.IsPointedAt(Settings.HttpPort))
        {
            LogBus.Instance.Warn("vpn", "System proxy was left pointing at a dead listener; restoring.");
            _proxy.Apply(Settings.HttpPort);
            _proxy.Clear();
        }

        _backgroundCts = new CancellationTokenSource();
        _ = BackgroundLoopAsync(_backgroundCts.Token);
        _ = PresenceLoopAsync(_backgroundCts.Token);
    }

    public async Task<bool> ConnectAsync(ServerProfile? profile = null)
    {
        profile ??= ResolveProfileToConnect();
        if (profile is null)
        {
            Fail("No server to connect to. Add a key or subscription first.");
            return false;
        }

        await _transition.WaitAsync();
        try
        {
            // Reconnecting in TUN mode keeps the adapter: it only feeds a fixed local port, so there
            // is nothing to rebuild and no second administrator prompt.
            if (State == ConnectionState.Connected) await StopCoreAsync(keepTun: Settings.Mode == TunnelMode.Tun);

            _userRequestedStop = false;
            LastError = null;
            ActiveProfile = profile;
            SetState(ConnectionState.Connecting);

            LogBus.Instance.Info("vpn", $"Connecting to {profile.DisplayName} ({profile.ProtocolLabel}, {Settings.Mode})");

            if (!await PrepareAndStartXrayAsync(profile)) return false;

            if (!await WaitForPortAsync(Settings.HttpPort, TimeSpan.FromSeconds(8)))
            {
                _xray.Stop();
                Fail($"Xray did not open port {Settings.HttpPort}. Is another program using it?");
                return false;
            }

            if (Settings.Mode == TunnelMode.Tun)
            {
                // StartAsync is a no-op when the adapter is already up with the same settings, so a
                // node switch or a routing edit does not raise another administrator prompt.
                if (!await _tun.StartAsync(Settings))
                {
                    if (_tun.PromptDeclined)
                    {
                        // Do not keep asking. Fall back to the proxy so the connection still works;
                        // the user can switch back to TUN whenever they want to approve the prompt.
                        LogBus.Instance.Warn("vpn", "Admin prompt declined; staying on the system proxy this session");
                        Settings.Mode = TunnelMode.SystemProxy;
                        Store.SaveSettings();
                        Notice = "Права администратора не выданы — работаю через системный прокси";

                        if (!_proxy.Apply(Settings.HttpPort))
                        {
                            _xray.Stop();
                            Fail("Could not set the Windows system proxy.");
                            return false;
                        }
                    }
                    else
                    {
                        _xray.Stop();
                        Fail("TUN mode failed to start. See Logs for the sing-box error.");
                        return false;
                    }
                }
            }
            else if (!_proxy.Apply(Settings.HttpPort))
            {
                _xray.Stop();
                Fail("Could not set the Windows system proxy.");
                return false;
            }

            ActiveMode = Settings.Mode;
            ConnectedAt = DateTimeOffset.Now;

            _stats = new StatsService(Settings.ApiPort);
            _stats.Updated += (_, snap) =>
            {
                Traffic = snap;
                TrafficUpdated?.Invoke(this, snap);
            };
            _stats.Start(TimeSpan.FromSeconds(1));

            Settings.ActiveProfileId = profile.Id;
            Store.SaveSettings();

            SetState(ConnectionState.Connected);
            LogBus.Instance.Info("vpn", "Connected");

            _ = MeasureRealDelayAsync();
            _ = UpdatePresenceAsync();
            return true;
        }
        catch (Exception ex)
        {
            LogBus.Instance.Error("vpn", "Connect failed", ex);
            await StopCoreAsync();
            Fail(ex.Message);
            return false;
        }
        finally
        {
            _transition.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _transition.WaitAsync();
        try
        {
            if (State is ConnectionState.Disconnected) return;

            _userRequestedStop = true;
            SetState(ConnectionState.Disconnecting);
            await StopCoreAsync();
            SetState(ConnectionState.Disconnected);
            LogBus.Instance.Info("vpn", "Disconnected");
            _ = UpdatePresenceAsync();
        }
        finally
        {
            _transition.Release();
        }
    }

    public async Task ToggleAsync()
    {
        if (State is ConnectionState.Connected or ConnectionState.Connecting) await DisconnectAsync();
        else await ConnectAsync();
    }

    /// <summary>Re-apply settings to a live connection (routing edits, mode switch, port change).</summary>
    public async Task ReapplyAsync()
    {
        Store.SaveSettings();
        if (State == ConnectionState.Connected && ActiveProfile is not null)
        {
            LogBus.Instance.Info("vpn", "Settings changed; reconnecting to apply");
            await ConnectAsync(ActiveProfile);
        }
    }

    private async Task StopCoreAsync(bool keepTun = false)
    {
        if (_stats is not null)
        {
            await _stats.DisposeAsync();
            _stats = null;
        }

        if (!keepTun) _tun.Stop();
        _proxy.Clear();
        _xray.Stop();

        ConnectedAt = null;
        ActiveMode = null;
        RealDelayMs = -1;
        Traffic = default;
    }

    // ================================================================== xray

    private async Task<bool> PrepareAndStartXrayAsync(ServerProfile profile)
    {
        if (!File.Exists(AppPaths.XrayExe))
        {
            Fail("xray.exe is missing. Reinstall, or run build\\fetch-deps.ps1 for a dev build.");
            return false;
        }

        if (!File.Exists(AppPaths.GeoIpDat) || !File.Exists(AppPaths.GeoSiteDat))
        {
            LogBus.Instance.Warn("vpn", "Rule data missing; downloading before first connect");
            await _geo.UpdateAsync(Settings.GeoAssetSource);
        }

        var builder = new XrayConfigBuilder
        {
            KnownGeoSites = GeoAssetService.ReadGeoSiteTags(AppPaths.GeoSiteDat)
        };

        AppPaths.EnsureCreated();
        await File.WriteAllTextAsync(AppPaths.XrayConfigFile, builder.Build(profile, Settings));

        DroppedGeoSites = builder.DroppedGeoSites.ToList();
        if (DroppedGeoSites.Count > 0)
            LogBus.Instance.Info("vpn", $"Rule data has no {string.Join(", ", DroppedGeoSites)}; using built-in lists for those");

        var (ok, output) = await XrayProcess.TestConfigAsync(AppPaths.XrayConfigFile);
        if (!ok && output.Contains("geosite", StringComparison.OrdinalIgnoreCase))
        {
            // Tag enumeration missed something (unusual .dat layout). Fall back to zero geosite
            // references — the curated lists in RoutingPresets still cover the essentials.
            LogBus.Instance.Warn("vpn", "Rule data rejected a geosite tag; retrying with built-in lists only");
            builder = new XrayConfigBuilder { KnownGeoSites = ["private"] };
            await File.WriteAllTextAsync(AppPaths.XrayConfigFile, builder.Build(profile, Settings));
            (ok, output) = await XrayProcess.TestConfigAsync(AppPaths.XrayConfigFile);
        }

        if (!ok)
        {
            LogBus.Instance.Error("xray", output);
            Fail("Xray rejected the generated config. Details are in Logs.");
            return false;
        }

        if (!_xray.Start(AppPaths.XrayConfigFile))
        {
            Fail("Failed to start xray.exe.");
            return false;
        }

        return true;
    }

    private int _crashRestarts;
    private DateTime _lastCrash = DateTime.MinValue;

    private void OnXrayExited(object? sender, int code)
    {
        if (_userRequestedStop || State != ConnectionState.Connected) return;

        LogBus.Instance.Error("vpn", $"Xray died unexpectedly (code {code})");
        var profile = ActiveProfile;

        _ = Task.Run(async () =>
        {
            // Never leave the machine pointed at a dead proxy.
            await _transition.WaitAsync();
            try { await StopCoreAsync(keepTun: Settings.Mode == TunnelMode.Tun); }
            finally { _transition.Release(); }

            // A crash streak means something is really wrong; a crash after hours of uptime is a
            // one-off. Reset the counter once the connection has been healthy for a while.
            if (DateTime.UtcNow - _lastCrash > TimeSpan.FromMinutes(10)) _crashRestarts = 0;
            _lastCrash = DateTime.UtcNow;
            _crashRestarts++;

            if (_crashRestarts > 3)
            {
                Fail("Xray keeps stopping. Check the logs — auto-restart is paused.");
                return;
            }

            // Same node first: restarting the core fixes most crashes and keeps the user in place.
            await Task.Delay(TimeSpan.FromSeconds(2 * _crashRestarts));
            if (profile is not null && await ConnectAsync(profile)) return;

            Fail("Xray stopped unexpectedly.");
            if (Settings.AutoFailover && Store.Profiles.Count > 1) await FailoverAsync();
        });
    }

    private static async Task<bool> WaitForPortAsync(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", port).WaitAsync(TimeSpan.FromMilliseconds(300));
                return true;
            }
            catch
            {
                await Task.Delay(150);
            }
        }
        return false;
    }

    // ================================================================== nodes

    private ServerProfile? ResolveProfileToConnect()
    {
        var profiles = Store.Profiles;
        if (profiles.Count == 0) return null;

        return profiles.FirstOrDefault(p => p.Id == Settings.ActiveProfileId)
               ?? profiles.Where(p => p.IsAlive).MinBy(p => p.LatencyMs)
               ?? profiles[0];
    }

    public void AddProfiles(IEnumerable<ServerProfile> nodes, out int added, out int skipped)
    {
        Store.AddProfiles(nodes, out added, out skipped);
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveProfile(ServerProfile profile)
    {
        Store.Profiles.RemoveAll(p => p.Id == profile.Id);
        Store.SaveProfiles();
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SaveProfiles()
    {
        Store.SaveProfiles();
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task TestLatencyAsync(Action<ServerProfile> onResult, CancellationToken ct = default)
    {
        await LatencyService.TestAllAsync(Store.Profiles.ToList(), Settings, onResult, ct);
        Store.SaveProfiles();
        if (State == ConnectionState.Connected) await MeasureRealDelayAsync();
    }

    public async Task MeasureRealDelayAsync()
    {
        if (State != ConnectionState.Connected) return;
        RealDelayMs = await LatencyService.RealDelayAsync(Settings);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Real throughput through the tunnel right now. Heavy on purpose — user-triggered only.</summary>
    public async Task<SpeedTestResult> RunSpeedTestAsync(IProgress<SpeedTestProgress>? progress, CancellationToken ct = default)
    {
        var connected = State == ConnectionState.Connected;
        var result = await new SpeedTestService().RunAsync(Settings, throughProxy: connected, progress, ct);

        if (result.Ok)
        {
            LastSpeedTest = result;
            if (connected && result.PingMs > 0)
            {
                RealDelayMs = result.PingMs;
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        return result;
    }

    private int _healthFailures;
    private DateTime _lastFailover = DateTime.MinValue;

    /// <summary>
    /// Periodic liveness check. Deliberately hard to trigger: the node has to miss
    /// <see cref="AppSettings.FailoverFailures"/> checks in a row <em>while the machine still has
    /// internet</em>, and switches are rate-limited. A flaky Wi-Fi, a sleeping laptop or one dropped
    /// probe must never turn into a reconnect — that is what made the client feel unstable.
    /// </summary>
    private async Task MonitorHealthAsync(CancellationToken ct)
    {
        var health = await LatencyService.CheckHealthAsync(Settings, ct);

        if (health == TunnelHealth.Ok)
        {
            if (_healthFailures > 0) LogBus.Instance.Debug("vpn", "Tunnel responding again");
            _healthFailures = 0;
            await MeasureRealDelayAsync();
            return;
        }

        if (health == TunnelHealth.NoInternet)
        {
            // The computer itself is offline (sleep, Wi-Fi switch, cable out). Sit still.
            LogBus.Instance.Debug("vpn", "No internet on this machine; leaving the tunnel alone");
            _healthFailures = 0;
            return;
        }

        _healthFailures++;
        LogBus.Instance.Debug("vpn", $"Tunnel check failed ({_healthFailures}/{Settings.FailoverFailures})");

        if (!Settings.AutoFailover || _healthFailures < Math.Max(1, Settings.FailoverFailures)) return;

        var cooldown = TimeSpan.FromMinutes(Math.Max(1, Settings.FailoverCooldownMinutes));
        if (DateTime.UtcNow - _lastFailover < cooldown)
        {
            LogBus.Instance.Debug("vpn", "Node is unhealthy but a switch happened recently; waiting");
            return;
        }

        if (Store.Profiles.Count < 2)
        {
            LogBus.Instance.Warn("vpn", "Node is not responding and there is no other node to switch to");
            _healthFailures = 0;
            return;
        }

        LogBus.Instance.Warn("vpn", $"Node missed {_healthFailures} checks in a row; switching");
        _healthFailures = 0;
        await FailoverAsync();
    }

    /// <summary>Pick the fastest reachable node other than the one that just failed.</summary>
    private async Task FailoverAsync()
    {
        _lastFailover = DateTime.UtcNow;

        var failed = ActiveProfile?.Id;
        var candidates = Store.Profiles.Where(p => p.Id != failed).ToList();
        if (candidates.Count == 0) return;

        LogBus.Instance.Info("vpn", "Failover: re-testing nodes");
        await LatencyService.TestAllAsync(candidates, Settings, _ => { });

        var best = candidates.Where(p => p.IsAlive).MinBy(p => p.LatencyMs);
        if (best is null)
        {
            LogBus.Instance.Warn("vpn", "Failover: no reachable node");
            return;
        }

        LogBus.Instance.Info("vpn", $"Failover: switching to {best.DisplayName} ({best.LatencyMs} ms)");
        Notice = $"Сервер не отвечал — переключился на {best.DisplayName}";
        await ConnectAsync(best);
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    // ================================================================== subscriptions

    public Subscription AddSubscription(string url, string? name = null)
    {
        // A link copied out of Happ / INCY / Hiddify carries the real subscription URL inside it.
        url = ClientLinks.Unwrap(url.Trim());

        var sub = new Subscription
        {
            Url = url.Trim(),
            Name = string.IsNullOrWhiteSpace(name)
                ? (Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "Subscription")
                : name.Trim()
        };
        Store.Subscriptions.Add(sub);
        Store.SaveSubscriptions();
        SubscriptionsChanged?.Invoke(this, EventArgs.Empty);
        return sub;
    }

    public void RemoveSubscription(Subscription sub, bool removeNodes)
    {
        Store.Subscriptions.RemoveAll(s => s.Id == sub.Id);
        if (removeNodes) Store.Profiles.RemoveAll(p => p.SubscriptionId == sub.Id);
        Store.SaveAll();
        SubscriptionsChanged?.Invoke(this, EventArgs.Empty);
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<SubscriptionResult> UpdateSubscriptionAsync(Subscription sub, CancellationToken ct = default)
    {
        LogBus.Instance.Info("sub", $"Updating '{sub.Name}'");
        var result = await _subscriptions.FetchAsync(sub, Settings, ct);

        if (result.Nodes.Count > 0)
        {
            sub.NodeCount = Store.ReplaceSubscriptionNodes(sub.Id, result.Nodes);
            sub.LastError = null;
            LogBus.Instance.Info("sub", $"'{sub.Name}': {sub.NodeCount} node(s)");
        }
        else
        {
            // Keep the old nodes on failure; a flaky panel should not empty the server list.
            sub.LastError = result.Error;
            LogBus.Instance.Warn("sub", $"'{sub.Name}': {result.Error}");
        }

        sub.LastUpdated = DateTime.Now;
        if (result.TrafficInfo is not null) sub.TrafficInfo = result.TrafficInfo;
        Store.SaveSubscriptions();

        SubscriptionsChanged?.Invoke(this, EventArgs.Empty);
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public async Task UpdateAllSubscriptionsAsync(CancellationToken ct = default)
    {
        foreach (var sub in Store.Subscriptions.Where(s => s.Enabled).ToList())
            await UpdateSubscriptionAsync(sub, ct);
    }

    // ================================================================== background

    private async Task BackgroundLoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);

            if (Settings.AutoUpdateGeoAssets &&
                (Settings.GeoAssetsUpdated is null ||
                 DateTime.Now - Settings.GeoAssetsUpdated > TimeSpan.FromHours(Settings.GeoAssetUpdateHours) ||
                 !File.Exists(AppPaths.GeoSiteDat)))
            {
                if (await _geo.UpdateAsync(Settings.GeoAssetSource, ct))
                {
                    Settings.GeoAssetsUpdated = DateTime.Now;
                    Store.SaveSettings();
                }
            }

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(15, Settings.FailoverCheckSeconds)));
            while (await timer.WaitForNextTickAsync(ct))
            {
                foreach (var sub in Store.Subscriptions.Where(s => s.Enabled && s.AutoUpdateHours > 0).ToList())
                {
                    if (sub.LastUpdated is null || DateTime.Now - sub.LastUpdated > TimeSpan.FromHours(sub.AutoUpdateHours))
                        await UpdateSubscriptionAsync(sub, ct);
                }

                if (State == ConnectionState.Connected) await MonitorHealthAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogBus.Instance.Error("vpn", "Background loop crashed", ex);
        }
    }

    // ================================================================== discord

    private async Task PresenceLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            do
            {
                await UpdatePresenceAsync();
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
    }

    public async Task UpdatePresenceAsync()
    {
        var d = Settings.Discord;

        if (!d.Enabled || string.IsNullOrWhiteSpace(d.ApplicationId))
        {
            if (_discord.IsConnected)
            {
                await _discord.ClearPresenceAsync();
                _discord.Disconnect();
            }
            return;
        }

        var connected = State == ConnectionState.Connected && ActiveProfile is not null;

        if (!connected && !d.ShowWhenDisconnected)
        {
            if (_discord.IsConnected) await _discord.ClearPresenceAsync();
            return;
        }

        var details = connected ? Render(d.DetailsTemplate) : "Не подключено";
        var state = connected ? Render(d.StateTemplate) : "Ndze VPN";

        DateTimeOffset? start = null, end = null;
        if (connected && d.ShowElapsed && ConnectedAt is { } at)
        {
            start = at;
            if (d.ShowProgressBar && d.ActivityType is 2 or 3)
            {
                // The bar needs an end. Roll it over every cycle so it keeps moving for long sessions.
                var cycle = TimeSpan.FromMinutes(Math.Max(1, d.ProgressCycleMinutes));
                var elapsed = DateTimeOffset.Now - at;
                var cycles = Math.Floor(elapsed / cycle);
                start = at + cycle * cycles;
                end = start + cycle;
            }
        }

        var buttons = d.ShowButtons
            ? new List<(string, string)> { (d.Button1Label, DiscordSettings.ResolveUrl(d.Button1Url)) }
            : [];

        await _discord.SetPresenceAsync(d.ApplicationId, new PresenceState(
            d.ActivityType,
            details,
            state,
            start,
            end,
            DiscordSettings.ResolveImage(d.LargeImageKey),
            connected ? $"{ActiveProfile!.ProtocolLabel}" : "Ndze VPN",
            connected ? DiscordSettings.ResolveImage(d.SmallImageKey) : "",
            connected ? (ActiveMode == TunnelMode.Tun ? "TUN" : "System proxy") : "",
            buttons));
    }

    private string Render(string template)
    {
        var p = ActiveProfile;
        var d = Settings.Discord;
        var server = p is null ? "" : d.ShowServerName ? p.DisplayName : "Защищённое соединение";

        return template
            .Replace("{server}", server)
            .Replace("{protocol}", p?.ProtocolLabel ?? "")
            .Replace("{mode}", ActiveMode == TunnelMode.Tun ? "TUN" : "Proxy")
            .Replace("{routing}", Settings.RoutingMode switch
            {
                RoutingMode.Smart => "Умный",
                RoutingMode.Global => "Весь трафик",
                RoutingMode.Direct => "Напрямую",
                _ => "Свои правила"
            })
            .Replace("{ping}", RealDelayMs > 0 ? RealDelayMs.ToString() : p?.LatencyMs > 0 ? p.LatencyMs.ToString() : "—")
            .Replace("{up}", d.ShowSpeed ? StatsService.FormatSpeed(Traffic.UploadBytesPerSecond) : "")
            .Replace("{down}", d.ShowSpeed ? StatsService.FormatSpeed(Traffic.DownloadBytesPerSecond) : "")
            .Replace("{total}", StatsService.FormatBytes(Traffic.UplinkTotal + Traffic.DownlinkTotal));
    }

    // ================================================================== state

    private void SetState(ConnectionState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Fail(string message)
    {
        LastError = message;
        LogBus.Instance.Error("vpn", message);
        SetState(ConnectionState.Error);
    }

    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try { _backgroundCts?.Cancel(); } catch { }

        _userRequestedStop = true;
        await StopCoreAsync();

        try { await _discord.ClearPresenceAsync(); } catch { }
        _discord.Dispose();
        _xray.Dispose();
        _tun.Dispose();
    }
}
