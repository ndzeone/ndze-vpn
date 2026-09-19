namespace NdzeVpn.Models;

public enum TunnelMode
{
    /// <summary>Xray listens on localhost; Windows' WinINET proxy points at it.
    /// No admin rights, no driver. Apps that ignore the system proxy stay direct.</summary>
    SystemProxy,

    /// <summary>A wintun virtual adapter captures everything and feeds it into Xray.
    /// Catches games, Discord voice and anything else that ignores the system proxy.
    /// Needs administrator rights.</summary>
    Tun
}

public enum AppTheme
{
    Graphite,
    Paper,
    Ink
}

public sealed class DiscordSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Discord application id (discord.com/developers → New Application). Its name is what
    /// Discord prints as "Playing …", its Rich Presence art assets back the image keys below.
    /// Empty = presence disabled.</summary>
    public string ApplicationId { get; set; } = "";

    /// <summary>0 = Playing, 2 = Listening, 3 = Watching, 5 = Competing. Listening/Watching get the
    /// Spotify-style card with a progress bar.</summary>
    public int ActivityType { get; set; } = 2;

    /// <summary>Show a progress bar that fills over <see cref="ProgressCycleMinutes"/> and restarts.</summary>
    public bool ShowProgressBar { get; set; } = true;
    public int ProgressCycleMinutes { get; set; } = 60;

    /// <summary>Show the node name / country in the presence. Off = a generic "Protected" line,
    /// which is what you want if you do not feel like broadcasting which server you are on.</summary>
    public bool ShowServerName { get; set; } = true;

    /// <summary>Show live up/down speed as the second presence line.</summary>
    public bool ShowSpeed { get; set; } = true;

    /// <summary>Show the elapsed-time counter (the "01:40" bar in Discord).</summary>
    public bool ShowElapsed { get; set; } = true;

    /// <summary>Keep the presence up while disconnected, showing an idle state.</summary>
    public bool ShowWhenDisconnected { get; set; }

    public string DetailsTemplate { get; set; } = "{server}";
    public string StateTemplate { get; set; } = "{mode} · {ping} ms";

    /// <summary>Art asset key from the Developer Portal, or an https image URL. The built-in keys
    /// <c>logo</c> / <c>shield</c> resolve to images hosted in the GitHub repo, so nothing has to be uploaded.</summary>
    public string LargeImageKey { get; set; } = "logo";
    public string SmallImageKey { get; set; } = "shield";

    public bool ShowButtons { get; set; } = true;
    public string Button1Label { get; set; } = "Ndze VPN";
    public string Button1Url { get; set; } = "";

    private const string RawAssets = "https://raw.githubusercontent.com/"
        + Services.UpdateService.Owner + "/" + Services.UpdateService.Repo + "/main/assets/discord/";

    public static string ResolveImage(string key) => key.Trim().ToLowerInvariant() switch
    {
        "" => "",
        "logo" => RawAssets + "logo.png",
        "shield" => RawAssets + "shield.png",
        _ => key.Trim()
    };

    /// <summary>Empty or the old bare github.com default point at the project page.</summary>
    public static string ResolveUrl(string url) =>
        string.IsNullOrWhiteSpace(url) || url.TrimEnd('/') == "https://github.com"
            ? Services.UpdateService.RepositoryUrl
            : url.Trim();
}

public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.Graphite;

    /// <summary>Accent override; empty = the theme's own accent.</summary>
    public string AccentOverride { get; set; } = "";

    public TunnelMode Mode { get; set; } = TunnelMode.SystemProxy;

    // --- Local listeners ---
    public int SocksPort { get; set; } = 10808;
    public int HttpPort { get; set; } = 10809;
    public int ApiPort { get; set; } = 10085;
    public bool AllowLanConnections { get; set; }
    public bool EnableSniffing { get; set; } = true;
    public bool EnableUdp { get; set; } = true;

    // --- Routing ---
    public RoutingMode RoutingMode { get; set; } = RoutingMode.Smart;
    public SmartRoutingSettings Smart { get; set; } = new();
    public List<RoutingRule> CustomRules { get; set; } = [];

    // --- DNS ---
    /// <summary>Resolver used for domains routed through the tunnel.</summary>
    public string RemoteDns { get; set; } = "https://1.1.1.1/dns-query";
    /// <summary>Resolver used for domains routed direct. A Russian resolver here keeps
    /// CDN answers local, which is half of why Smart mode feels fast.</summary>
    public string DirectDns { get; set; } = "77.88.8.8";
    public bool EnableDnsRouting { get; set; } = true;

    // --- TUN ---
    public string TunInterfaceName { get; set; } = "NdzeVpnTun";
    public string TunAddress { get; set; } = "172.19.0.1/30";
    public int TunMtu { get; set; } = 9000;
    public bool TunStrictRoute { get; set; } = true;
    /// <summary>Process names never routed through the TUN adapter (anti-loop / latency-sensitive).</summary>
    public List<string> TunBypassProcesses { get; set; } = ["NdzeVpn.exe", "xray.exe", "sing-box.exe"];

    // --- Behaviour ---
    public bool LaunchOnStartup { get; set; }
    public bool StartMinimized { get; set; }
    public bool AutoConnectOnLaunch { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    /// <summary>Re-test every node and switch if the active one dies.</summary>
    public bool AutoFailover { get; set; } = true;
    public int FailoverCheckSeconds { get; set; } = 60;

    /// <summary>How many checks in a row must fail before switching node. A single dropped probe on
    /// a mobile link is normal, and switching on it is what makes a client feel like it "glitches".</summary>
    public int FailoverFailures { get; set; } = 3;

    /// <summary>Minimum gap between two automatic switches; stops reconnect loops.</summary>
    public int FailoverCooldownMinutes { get; set; } = 10;
    public string? ActiveProfileId { get; set; }
    public bool SidebarCollapsed { get; set; }

    // --- App updates (GitHub Releases) ---
    public bool CheckUpdatesOnStartup { get; set; } = true;
    /// <summary>Version the user dismissed with "skip"; not offered again at startup.</summary>
    public string? SkippedUpdateVersion { get; set; }

    /// <summary>Device id sent to panels with a device limit. Empty = derived from this computer.
    /// Set it by hand to reuse the slot another client already registered.</summary>
    public string DeviceId { get; set; } = "";

    // --- Assets ---
    public bool AutoUpdateGeoAssets { get; set; } = true;
    public int GeoAssetUpdateHours { get; set; } = 72;
    public DateTime? GeoAssetsUpdated { get; set; }
    /// <summary>Where geoip.dat / geosite.dat come from. The default is the Russia-specific
    /// rule set, which is far more accurate here than the generic Loyalsoldier one.</summary>
    public string GeoAssetSource { get; set; } = "runetfreedom";

    // --- Logging ---
    public string XrayLogLevel { get; set; } = "warning";
    public int LogBufferLines { get; set; } = 2000;

    public DiscordSettings Discord { get; set; } = new();

    public string LatencyTestUrl { get; set; } = "https://www.gstatic.com/generate_204";
    public int LatencyTimeoutMs { get; set; } = 5000;
    public int LatencyParallelism { get; set; } = 16;
    /// <summary>TCP handshakes per node; the best one is reported (the first pays for DNS).</summary>
    public int LatencyProbes { get; set; } = 3;

    // --- Speed test ---
    /// <summary>Length of the measurement window, excluding ramp-up.</summary>
    public int SpeedTestSeconds { get; set; } = 8;
    /// <summary>Parallel streams. One stream rarely saturates a proxied link.</summary>
    public int SpeedTestStreams { get; set; } = 4;
    public bool SpeedTestUpload { get; set; } = true;
}
