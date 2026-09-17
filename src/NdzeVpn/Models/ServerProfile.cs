using System.Text.Json.Serialization;

namespace NdzeVpn.Models;

public enum ProxyProtocol
{
    Vless,
    Vmess,
    Trojan,
    Shadowsocks
}

/// <summary>
/// One proxy node, normalised from a vless:// / vmess:// / trojan:// / ss:// share link.
/// Field names mirror Xray's outbound schema so the config builder stays a straight mapping.
/// </summary>
public sealed class ServerProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public ProxyProtocol Protocol { get; set; } = ProxyProtocol.Vless;

    /// <summary>Display name (the #fragment of the share link).</summary>
    public string Remark { get; set; } = "";

    public string Address { get; set; } = "";
    public int Port { get; set; }

    /// <summary>UUID for vless/vmess, password for trojan/ss.</summary>
    public string Credential { get; set; } = "";

    // --- VMess only ---
    public int AlterId { get; set; }
    public string Security { get; set; } = "auto";   // vmess cipher

    // --- Shadowsocks only ---
    public string Method { get; set; } = "";

    // --- VLESS only ---
    public string Encryption { get; set; } = "none";
    public string Flow { get; set; } = "";           // xtls-rprx-vision

    // --- Transport ---
    public string Network { get; set; } = "tcp";     // tcp | ws | grpc | h2 | httpupgrade | xhttp | kcp | quic
    public string HeaderType { get; set; } = "none"; // tcp http obfuscation / kcp seed type
    public string Path { get; set; } = "";
    public string Host { get; set; } = "";           // ws/h2 Host header, grpc authority
    public string ServiceName { get; set; } = "";    // grpc
    public string Mode { get; set; } = "";           // grpc/xhttp mode
    public string Seed { get; set; } = "";           // kcp
    public string QuicSecurity { get; set; } = "";
    public string QuicKey { get; set; } = "";

    // --- TLS layer ---
    public string StreamSecurity { get; set; } = ""; // "" | tls | reality
    public string Sni { get; set; } = "";
    public string Alpn { get; set; } = "";
    public string Fingerprint { get; set; } = "";    // uTLS: chrome, firefox, safari, randomized...
    public bool AllowInsecure { get; set; }

    // --- REALITY ---
    public string PublicKey { get; set; } = "";      // pbk
    public string ShortId { get; set; } = "";        // sid
    public string SpiderX { get; set; } = "";        // spx

    /// <summary>Id of the owning <see cref="Subscription"/>, or null for a manually added node.</summary>
    public string? SubscriptionId { get; set; }

    /// <summary>The original share link, kept so a node can be re-exported or copied out.</summary>
    public string RawUri { get; set; } = "";

    // --- Runtime state (not the user's data, but handy to persist between runs) ---
    public int LatencyMs { get; set; } = -1;
    public DateTime? LastTested { get; set; }

    [JsonIgnore]
    public bool IsAlive => LatencyMs > 0;

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Remark) ? $"{Address}:{Port}" : Remark;

    [JsonIgnore]
    public string ProtocolLabel => Protocol switch
    {
        ProxyProtocol.Vless => string.IsNullOrEmpty(StreamSecurity) ? "VLESS" : $"VLESS · {StreamSecurity.ToUpperInvariant()}",
        ProxyProtocol.Vmess => "VMess",
        ProxyProtocol.Trojan => "Trojan",
        ProxyProtocol.Shadowsocks => "Shadowsocks",
        _ => Protocol.ToString()
    };

    [JsonIgnore]
    public string TransportLabel =>
        Network.Equals("tcp", StringComparison.OrdinalIgnoreCase) && HeaderType is "none" or ""
            ? "TCP"
            : $"{Network.ToUpperInvariant()}{(string.IsNullOrEmpty(Path) ? "" : " " + Path)}";

    public ServerProfile Clone() => (ServerProfile)MemberwiseClone();
}
