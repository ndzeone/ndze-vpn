using System.Text.Json.Serialization;

namespace NdzeVpn.Models;

/// <summary>Where a matched request is sent.</summary>
public enum RuleAction
{
    /// <summary>Through the VLESS/VMess/… outbound.</summary>
    Proxy,
    /// <summary>Straight out of the machine's normal internet connection.</summary>
    Direct,
    /// <summary>Dropped.</summary>
    Block
}

public enum RuleKind
{
    /// <summary>Plain domain suffix, e.g. <c>youtube.com</c>.</summary>
    Domain,
    /// <summary>Exact domain, e.g. <c>full:api.example.com</c>.</summary>
    DomainFull,
    /// <summary>Regex over the domain.</summary>
    DomainRegex,
    /// <summary>Named list from geosite.dat, e.g. <c>category-ru</c>.</summary>
    GeoSite,
    /// <summary>Named list from geoip.dat, e.g. <c>ru</c>, or a CIDR like <c>10.0.0.0/8</c>.</summary>
    GeoIp,
    /// <summary>Literal IP or CIDR.</summary>
    Ip,
    /// <summary>Destination port or range, e.g. <c>443</c> or <c>1000-2000</c>.</summary>
    Port,
    /// <summary>Local process name, e.g. <c>Discord.exe</c>. Only effective in TUN mode.</summary>
    Process
}

/// <summary>One user-authored routing rule. Evaluated top-down, first match wins.</summary>
public sealed class RoutingRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; } = true;
    public RuleKind Kind { get; set; } = RuleKind.Domain;
    public RuleAction Action { get; set; } = RuleAction.Proxy;

    /// <summary>Comma or newline separated values.</summary>
    public string Value { get; set; } = "";

    public string Note { get; set; } = "";

    [JsonIgnore]
    public IEnumerable<string> Values =>
        Value.Split([',', '\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public enum RoutingMode
{
    /// <summary>The point of this app: only what Russia blocks goes through the tunnel,
    /// everything .ru-shaped keeps the home IP.</summary>
    Smart,
    /// <summary>Everything except LAN/loopback through the tunnel.</summary>
    Global,
    /// <summary>Nothing through the tunnel. Tunnel stays up for manually added rules.</summary>
    Direct,
    /// <summary>Only the user's own rule list decides; no presets are injected.</summary>
    Custom
}

/// <summary>The knobs behind Smart mode. Every one of these is exposed in the Routing page.</summary>
public sealed class SmartRoutingSettings
{
    /// <summary>geoip:private → direct. Turning this off breaks LAN access; kept as a knob anyway.</summary>
    public bool BypassLan { get; set; } = true;

    /// <summary>geoip:ru → direct. The big one: any server physically in Russia keeps the home IP.</summary>
    public bool BypassRussianIps { get; set; } = true;

    /// <summary>geosite:category-ru + the .ru/.su/.рф TLDs → direct.</summary>
    public bool BypassRussianDomains { get; set; } = true;

    /// <summary>Banks, gosuslugi, mos.ru and friends → always direct. These actively refuse
    /// foreign IPs, so this one is worth keeping on even if you proxy everything else.</summary>
    public bool BypassRussianBanking { get; set; } = true;

    /// <summary>Known-blocked-in-RU lists (geosite:refilter / russia-blocked) → proxy.</summary>
    public bool ProxyBlockedList { get; set; } = true;

    /// <summary>Discord, YouTube, Twitter/X, Instagram, Telegram… → proxy, by name, so it works
    /// even when the geosite lists lag behind.</summary>
    public bool ProxyPopularServices { get; set; } = true;

    /// <summary>Route Discord's voice UDP through the tunnel too. Needs TUN mode to have any effect.</summary>
    public bool ProxyDiscordVoice { get; set; } = true;

    /// <summary>geosite:category-ads-all → block.</summary>
    public bool BlockAds { get; set; }

    /// <summary>Drop QUIC (UDP/443) so browsers fall back to TCP. Often fixes YouTube over a proxy.</summary>
    public bool BlockQuic { get; set; } = true;

    /// <summary>Send bittorrent to direct so the tunnel never carries it.</summary>
    public bool DirectBittorrent { get; set; } = true;

    /// <summary>What to do with traffic no rule matched.</summary>
    public RuleAction DefaultAction { get; set; } = RuleAction.Proxy;

    /// <summary>"IPIfNonMatch" resolves the domain and retries against IP rules — that is what
    /// makes geoip:ru work for a plain hostname. "AsIs" is faster but far less accurate.</summary>
    public string DomainStrategy { get; set; } = "IPIfNonMatch";

    public SmartRoutingSettings Clone() => (SmartRoutingSettings)MemberwiseClone();
}
