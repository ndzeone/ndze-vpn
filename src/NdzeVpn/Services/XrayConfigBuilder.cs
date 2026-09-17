using System.Text.Json;
using System.Text.Json.Serialization;
using NdzeVpn.Models;

namespace NdzeVpn.Services;

/// <summary>
/// Builds the Xray config. Everything about "smart connection" lives here: which traffic gets the
/// tunnel and which keeps the home IP is decided entirely by the routing rule list this produces.
/// </summary>
public sealed class XrayConfigBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public const string TagProxy = "proxy";
    public const string TagDirect = "direct";
    public const string TagBlock = "block";
    public const string TagApi = "api";

    /// <summary>Geosite tags that were skipped because the installed data file lacks them.</summary>
    public List<string> DroppedGeoSites { get; } = [];

    /// <summary>Tags known to exist in the current geosite.dat. Null = unknown, accept everything
    /// and let <c>xray run -test</c> catch it.</summary>
    public HashSet<string>? KnownGeoSites { get; set; }

    public string Build(ServerProfile profile, AppSettings s)
    {
        var config = new Dictionary<string, object?>
        {
            ["log"] = new Dictionary<string, object?>
            {
                ["loglevel"] = s.XrayLogLevel,
                ["access"] = "none"
            },
            ["stats"] = new Dictionary<string, object?>(),
            ["api"] = new Dictionary<string, object?>
            {
                ["tag"] = TagApi,
                ["services"] = new[] { "StatsService" }
            },
            ["policy"] = BuildPolicy(),
            ["inbounds"] = BuildInbounds(s),
            ["outbounds"] = BuildOutbounds(profile, s),
            ["routing"] = BuildRouting(s)
        };

        if (s.EnableDnsRouting)
            config["dns"] = BuildDns(s);

        return JsonSerializer.Serialize(config, JsonOptions);
    }

    // ------------------------------------------------------------------ policy

    private static object BuildPolicy() => new Dictionary<string, object?>
    {
        ["levels"] = new Dictionary<string, object?>
        {
            ["0"] = new Dictionary<string, object?>
            {
                ["handshake"] = 4,
                ["connIdle"] = 300,
                ["uplinkOnly"] = 1,
                ["downlinkOnly"] = 1,
                ["statsUserUplink"] = true,
                ["statsUserDownlink"] = true,
                ["bufferSize"] = 4
            }
        },
        ["system"] = new Dictionary<string, object?>
        {
            ["statsInboundUplink"] = true,
            ["statsInboundDownlink"] = true,
            ["statsOutboundUplink"] = true,
            ["statsOutboundDownlink"] = true
        }
    };

    // ------------------------------------------------------------------ inbounds

    private static List<object> BuildInbounds(AppSettings s)
    {
        var listen = s.AllowLanConnections ? "0.0.0.0" : "127.0.0.1";

        object? Sniffing() => s.EnableSniffing
            ? new Dictionary<string, object?>
            {
                ["enabled"] = true,
                // Sniffing the real hostname out of the TLS ClientHello is what lets a rule like
                // "youtube.com -> proxy" fire when the app hands us a bare IP (TUN mode especially).
                ["destOverride"] = new[] { "http", "tls", "quic" },
                ["routeOnly"] = false
            }
            : null;

        var inbounds = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["tag"] = "socks-in",
                ["listen"] = listen,
                ["port"] = s.SocksPort,
                ["protocol"] = "socks",
                ["settings"] = new Dictionary<string, object?>
                {
                    ["auth"] = "noauth",
                    ["udp"] = s.EnableUdp,
                    ["allowTransparent"] = false
                },
                ["sniffing"] = Sniffing()
            },
            new Dictionary<string, object?>
            {
                ["tag"] = "http-in",
                ["listen"] = listen,
                ["port"] = s.HttpPort,
                ["protocol"] = "http",
                ["settings"] = new Dictionary<string, object?> { ["allowTransparent"] = false },
                ["sniffing"] = Sniffing()
            },
            // Stats endpoint. Polled through `xray api statsquery`, so no gRPC dependency here.
            new Dictionary<string, object?>
            {
                ["tag"] = "api-in",
                ["listen"] = "127.0.0.1",
                ["port"] = s.ApiPort,
                ["protocol"] = "dokodemo-door",
                ["settings"] = new Dictionary<string, object?> { ["address"] = "127.0.0.1" }
            }
        };

        return inbounds;
    }

    // ------------------------------------------------------------------ outbounds

    private static List<object> BuildOutbounds(ServerProfile p, AppSettings s)
    {
        return
        [
            BuildProxyOutbound(p),
            new Dictionary<string, object?>
            {
                ["tag"] = TagDirect,
                ["protocol"] = "freedom",
                ["settings"] = new Dictionary<string, object?> { ["domainStrategy"] = "UseIP" }
            },
            new Dictionary<string, object?>
            {
                ["tag"] = TagBlock,
                ["protocol"] = "blackhole",
                ["settings"] = new Dictionary<string, object?>
                {
                    ["response"] = new Dictionary<string, object?> { ["type"] = "http" }
                }
            }
        ];
    }

    private static object BuildProxyOutbound(ServerProfile p)
    {
        var outbound = new Dictionary<string, object?>
        {
            ["tag"] = TagProxy,
            ["protocol"] = p.Protocol switch
            {
                ProxyProtocol.Vless => "vless",
                ProxyProtocol.Vmess => "vmess",
                ProxyProtocol.Trojan => "trojan",
                ProxyProtocol.Shadowsocks => "shadowsocks",
                _ => "vless"
            },
            ["settings"] = BuildProxySettings(p),
            ["streamSettings"] = BuildStreamSettings(p),
            ["mux"] = new Dictionary<string, object?>
            {
                // Mux breaks XTLS Vision, and Vision is the fast path — so leave it off there.
                ["enabled"] = false,
                ["concurrency"] = -1
            }
        };

        return outbound;
    }

    private static object BuildProxySettings(ServerProfile p)
    {
        switch (p.Protocol)
        {
            case ProxyProtocol.Vless:
                var vlessUser = new Dictionary<string, object?>
                {
                    ["id"] = p.Credential,
                    ["encryption"] = string.IsNullOrEmpty(p.Encryption) ? "none" : p.Encryption,
                    ["level"] = 0
                };
                if (!string.IsNullOrEmpty(p.Flow)) vlessUser["flow"] = p.Flow;

                return new Dictionary<string, object?>
                {
                    ["vnext"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["address"] = p.Address,
                            ["port"] = p.Port,
                            ["users"] = new[] { vlessUser }
                        }
                    }
                };

            case ProxyProtocol.Vmess:
                return new Dictionary<string, object?>
                {
                    ["vnext"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["address"] = p.Address,
                            ["port"] = p.Port,
                            ["users"] = new[]
                            {
                                new Dictionary<string, object?>
                                {
                                    ["id"] = p.Credential,
                                    ["alterId"] = p.AlterId,
                                    ["security"] = string.IsNullOrEmpty(p.Security) ? "auto" : p.Security,
                                    ["level"] = 0
                                }
                            }
                        }
                    }
                };

            case ProxyProtocol.Trojan:
                return new Dictionary<string, object?>
                {
                    ["servers"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["address"] = p.Address,
                            ["port"] = p.Port,
                            ["password"] = p.Credential,
                            ["level"] = 0
                        }
                    }
                };

            case ProxyProtocol.Shadowsocks:
                return new Dictionary<string, object?>
                {
                    ["servers"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["address"] = p.Address,
                            ["port"] = p.Port,
                            ["method"] = p.Method,
                            ["password"] = p.Credential,
                            ["uot"] = false,
                            ["level"] = 0
                        }
                    }
                };

            default:
                throw new NotSupportedException($"Protocol {p.Protocol} is not supported.");
        }
    }

    private static object BuildStreamSettings(ServerProfile p)
    {
        var stream = new Dictionary<string, object?>
        {
            ["network"] = p.Network
        };

        // --- security layer ---
        var security = string.IsNullOrEmpty(p.StreamSecurity) ? "none" : p.StreamSecurity.ToLowerInvariant();
        if (security is "1" or "true") security = "tls";
        stream["security"] = security;

        var serverName = !string.IsNullOrEmpty(p.Sni) ? p.Sni
            : !string.IsNullOrEmpty(p.Host) ? p.Host.Split(',')[0].Trim()
            : p.Address;

        if (security == "tls")
        {
            var tls = new Dictionary<string, object?>
            {
                ["serverName"] = serverName,
                ["allowInsecure"] = p.AllowInsecure
            };
            if (!string.IsNullOrEmpty(p.Fingerprint)) tls["fingerprint"] = p.Fingerprint;
            if (!string.IsNullOrEmpty(p.Alpn))
                tls["alpn"] = p.Alpn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            stream["tlsSettings"] = tls;
        }
        else if (security == "reality")
        {
            stream["realitySettings"] = new Dictionary<string, object?>
            {
                ["serverName"] = serverName,
                // REALITY needs a uTLS fingerprint; chrome is the safe default when the link omits it.
                ["fingerprint"] = string.IsNullOrEmpty(p.Fingerprint) ? "chrome" : p.Fingerprint,
                ["show"] = false,
                ["publicKey"] = p.PublicKey,
                ["shortId"] = p.ShortId,
                ["spiderX"] = p.SpiderX
            };
        }

        // --- transport layer ---
        switch (p.Network.ToLowerInvariant())
        {
            case "ws":
                var ws = new Dictionary<string, object?>
                {
                    ["path"] = string.IsNullOrEmpty(p.Path) ? "/" : p.Path
                };
                if (!string.IsNullOrEmpty(p.Host))
                    ws["headers"] = new Dictionary<string, object?> { ["Host"] = p.Host };
                stream["wsSettings"] = ws;
                break;

            case "httpupgrade":
                stream["httpupgradeSettings"] = new Dictionary<string, object?>
                {
                    ["path"] = string.IsNullOrEmpty(p.Path) ? "/" : p.Path,
                    ["host"] = p.Host
                };
                break;

            case "xhttp":
            case "splithttp":
                stream["network"] = "xhttp";
                stream["xhttpSettings"] = new Dictionary<string, object?>
                {
                    ["path"] = string.IsNullOrEmpty(p.Path) ? "/" : p.Path,
                    ["host"] = p.Host,
                    ["mode"] = string.IsNullOrEmpty(p.Mode) ? "auto" : p.Mode
                };
                break;

            case "grpc":
                stream["grpcSettings"] = new Dictionary<string, object?>
                {
                    ["serviceName"] = p.ServiceName,
                    ["multiMode"] = p.Mode.Equals("multi", StringComparison.OrdinalIgnoreCase),
                    ["idle_timeout"] = 60,
                    ["health_check_timeout"] = 20
                };
                break;

            case "h2":
            case "http":
                stream["network"] = "h2";
                stream["httpSettings"] = new Dictionary<string, object?>
                {
                    ["path"] = string.IsNullOrEmpty(p.Path) ? "/" : p.Path,
                    ["host"] = string.IsNullOrEmpty(p.Host)
                        ? Array.Empty<string>()
                        : p.Host.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                };
                break;

            case "kcp":
                stream["kcpSettings"] = new Dictionary<string, object?>
                {
                    ["mtu"] = 1350,
                    ["tti"] = 50,
                    ["uplinkCapacity"] = 12,
                    ["downlinkCapacity"] = 100,
                    ["congestion"] = false,
                    ["readBufferSize"] = 2,
                    ["writeBufferSize"] = 2,
                    ["header"] = new Dictionary<string, object?>
                    {
                        ["type"] = string.IsNullOrEmpty(p.HeaderType) ? "none" : p.HeaderType
                    },
                    ["seed"] = string.IsNullOrEmpty(p.Seed) ? null : p.Seed
                };
                break;

            case "quic":
                stream["quicSettings"] = new Dictionary<string, object?>
                {
                    ["security"] = string.IsNullOrEmpty(p.QuicSecurity) ? "none" : p.QuicSecurity,
                    ["key"] = p.QuicKey,
                    ["header"] = new Dictionary<string, object?>
                    {
                        ["type"] = string.IsNullOrEmpty(p.HeaderType) ? "none" : p.HeaderType
                    }
                };
                break;

            default: // tcp
                var tcp = new Dictionary<string, object?>();
                if (p.HeaderType.Equals("http", StringComparison.OrdinalIgnoreCase))
                {
                    tcp["header"] = new Dictionary<string, object?>
                    {
                        ["type"] = "http",
                        ["request"] = new Dictionary<string, object?>
                        {
                            ["version"] = "1.1",
                            ["method"] = "GET",
                            ["path"] = new[] { string.IsNullOrEmpty(p.Path) ? "/" : p.Path },
                            ["headers"] = new Dictionary<string, object?>
                            {
                                ["Host"] = string.IsNullOrEmpty(p.Host)
                                    ? new[] { p.Address }
                                    : p.Host.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            }
                        }
                    };
                }
                else
                {
                    tcp["header"] = new Dictionary<string, object?> { ["type"] = "none" };
                }
                stream["tcpSettings"] = tcp;
                break;
        }

        return stream;
    }

    // ------------------------------------------------------------------ dns

    private object BuildDns(AppSettings s)
    {
        var servers = new List<object>();

        // Remote resolver, used only for what we are going to proxy anyway.
        if (!string.IsNullOrWhiteSpace(s.RemoteDns))
            servers.Add(s.RemoteDns);

        // Direct resolver, pinned to the domains that must stay local. Resolving a Russian CDN
        // through a foreign DNS is how you end up on a slow edge node, so this matters.
        var directDomains = new List<string>();
        directDomains.AddRange(RoutingPresets.RussianTlds);
        directDomains.AddRange(RoutingPresets.RussianEssential);
        directDomains.AddRange(FilterGeoSites(RoutingPresets.OptionalRussianGeoSites));

        if (!string.IsNullOrWhiteSpace(s.DirectDns))
        {
            servers.Add(new Dictionary<string, object?>
            {
                ["address"] = s.DirectDns,
                ["port"] = 53,
                ["domains"] = directDomains,
                ["expectIPs"] = new[] { "geoip:ru" },
                ["skipFallback"] = false
            });
            servers.Add(s.DirectDns);
        }

        servers.Add("localhost");

        return new Dictionary<string, object?>
        {
            ["servers"] = servers,
            ["queryStrategy"] = "UseIPv4",
            ["disableCache"] = false,
            ["tag"] = "dns-in"
        };
    }

    // ------------------------------------------------------------------ routing

    private object BuildRouting(AppSettings s)
    {
        var rules = new List<object>
        {
            // The stats endpoint must never leave the machine.
            new Dictionary<string, object?>
            {
                ["type"] = "field",
                ["inboundTag"] = new[] { "api-in" },
                ["outboundTag"] = TagApi
            }
        };

        // 1. User rules first — an explicit rule always beats a preset.
        foreach (var rule in s.CustomRules.Where(r => r.Enabled))
        {
            var built = BuildCustomRule(rule);
            if (built is not null) rules.Add(built);
        }

        switch (s.RoutingMode)
        {
            case RoutingMode.Custom:
                // Nothing injected; the user's list plus the default action is the whole policy.
                break;

            case RoutingMode.Direct:
                rules.Add(Rule(TagDirect, ip: ["0.0.0.0/0", "::/0"]));
                break;

            case RoutingMode.Global:
                rules.Add(Rule(TagDirect, ip: ["geoip:private"]));
                if (s.Smart.BlockQuic) rules.Add(QuicBlockRule());
                break;

            case RoutingMode.Smart:
                AddSmartRules(rules, s.Smart);
                break;
        }

        var defaultTag = s.RoutingMode == RoutingMode.Direct
            ? TagDirect
            : ActionToTag(s.Smart.DefaultAction);

        rules.Add(Rule(defaultTag, ip: ["0.0.0.0/0", "::/0"]));
        rules.Add(Rule(defaultTag, domain: ["regexp:.*"]));

        return new Dictionary<string, object?>
        {
            ["domainStrategy"] = s.Smart.DomainStrategy,
            ["domainMatcher"] = "hybrid",
            ["rules"] = rules
        };
    }

    /// <summary>
    /// The actual "smart connection" policy, in evaluation order:
    /// block what should never travel → keep Russian traffic on the home IP → tunnel what is blocked.
    /// </summary>
    private void AddSmartRules(List<object> rules, SmartRoutingSettings sm)
    {
        // --- block ---
        if (sm.BlockAds)
        {
            var adSites = FilterGeoSites(RoutingPresets.OptionalAdGeoSites);
            if (adSites.Count > 0) rules.Add(Rule(TagBlock, domain: adSites));
        }

        if (sm.BlockQuic) rules.Add(QuicBlockRule());

        // --- direct ---
        if (sm.BypassLan)
        {
            rules.Add(Rule(TagDirect, ip: ["geoip:private"]));
            rules.Add(Rule(TagDirect, domain: ["geosite:private", "domain:localhost", "domain:local"]));
        }

        if (sm.DirectBittorrent)
            rules.Add(Rule(TagDirect, protocol: ["bittorrent"]));

        // Banking/gov first: these must stay direct even when the user proxies everything else.
        if (sm.BypassRussianBanking)
            rules.Add(Rule(TagDirect, domain: [.. RoutingPresets.RussianEssential]));

        // --- proxy (before the broad .ru bypass, so a blocked service on a .ru domain
        //     still gets tunnelled) ---
        if (sm.ProxyPopularServices)
            rules.Add(Rule(TagProxy, domain: [.. RoutingPresets.BlockedPopular]));

        if (sm.ProxyDiscordVoice)
            rules.Add(Rule(TagProxy, domain: [.. RoutingPresets.DiscordVoice]));

        if (sm.ProxyBlockedList)
        {
            var blockedSites = FilterGeoSites(RoutingPresets.OptionalBlockedGeoSites);
            if (blockedSites.Count > 0) rules.Add(Rule(TagProxy, domain: blockedSites));
        }

        // --- the broad Russian bypass ---
        if (sm.BypassRussianDomains)
        {
            var ruDomains = new List<string>(RoutingPresets.RussianTlds);
            ruDomains.AddRange(FilterGeoSites(RoutingPresets.OptionalRussianGeoSites));
            rules.Add(Rule(TagDirect, domain: ruDomains));
        }

        // Last, because with domainStrategy=IPIfNonMatch this is what catches a bare hostname
        // that resolves to a Russian IP — the CDN case.
        if (sm.BypassRussianIps)
            rules.Add(Rule(TagDirect, ip: ["geoip:ru"]));
    }

    private static object QuicBlockRule() => new Dictionary<string, object?>
    {
        ["type"] = "field",
        ["network"] = "udp",
        ["port"] = "443",
        ["outboundTag"] = TagBlock
    };

    private object? BuildCustomRule(RoutingRule rule)
    {
        var values = rule.Values.ToList();
        if (values.Count == 0) return null;

        var tag = ActionToTag(rule.Action);

        return rule.Kind switch
        {
            RuleKind.Domain => Rule(tag, domain: [.. values.Select(v => v.StartsWith("domain:") ? v : $"domain:{v}")]),
            RuleKind.DomainFull => Rule(tag, domain: [.. values.Select(v => v.StartsWith("full:") ? v : $"full:{v}")]),
            RuleKind.DomainRegex => Rule(tag, domain: [.. values.Select(v => v.StartsWith("regexp:") ? v : $"regexp:{v}")]),
            RuleKind.GeoSite => BuildGeoSiteRule(tag, values),
            RuleKind.GeoIp => Rule(tag, ip: [.. values.Select(v => v.Contains('/') || char.IsDigit(v[0]) ? v : $"geoip:{v}")]),
            RuleKind.Ip => Rule(tag, ip: [.. values]),
            RuleKind.Port => Rule(tag, port: string.Join(',', values)),
            // Xray has no process matcher; these are handed to sing-box in TUN mode instead.
            RuleKind.Process => null,
            _ => null
        };
    }

    private object? BuildGeoSiteRule(string tag, List<string> values)
    {
        var normalised = values.Select(v => v.StartsWith("geosite:") ? v : $"geosite:{v}").ToList();
        var filtered = FilterGeoSites(normalised);
        return filtered.Count == 0 ? null : Rule(tag, domain: filtered);
    }

    /// <summary>Drop geosite tags the installed data file does not define, so Xray still starts.</summary>
    private List<string> FilterGeoSites(IEnumerable<string> tags)
    {
        var result = new List<string>();
        foreach (var tag in tags)
        {
            if (KnownGeoSites is null)
            {
                result.Add(tag);
                continue;
            }

            var name = tag.StartsWith("geosite:") ? tag["geosite:".Length..] : tag;
            // Strip an attribute suffix like "geosite:google@ads".
            var at = name.IndexOf('@');
            if (at >= 0) name = name[..at];

            if (KnownGeoSites.Contains(name)) result.Add(tag);
            else if (!DroppedGeoSites.Contains(tag)) DroppedGeoSites.Add(tag);
        }
        return result;
    }

    private static string ActionToTag(RuleAction action) => action switch
    {
        RuleAction.Proxy => TagProxy,
        RuleAction.Direct => TagDirect,
        RuleAction.Block => TagBlock,
        _ => TagProxy
    };

    private static object Rule(
        string outboundTag,
        List<string>? domain = null,
        List<string>? ip = null,
        string? port = null,
        List<string>? protocol = null,
        string? network = null)
    {
        var rule = new Dictionary<string, object?>
        {
            ["type"] = "field",
            ["outboundTag"] = outboundTag
        };
        if (domain is { Count: > 0 }) rule["domain"] = domain;
        if (ip is { Count: > 0 }) rule["ip"] = ip;
        if (!string.IsNullOrEmpty(port)) rule["port"] = port;
        if (protocol is { Count: > 0 }) rule["protocol"] = protocol;
        if (!string.IsNullOrEmpty(network)) rule["network"] = network;
        return rule;
    }
}
