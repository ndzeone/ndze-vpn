using System.Text.Json;
using System.Text.RegularExpressions;
using NdzeVpn.Models;

namespace NdzeVpn.Services;

/// <summary>What a subscription body turned out to be, and what came out of it.</summary>
public sealed record ImportReport(
    List<ServerProfile> Nodes,
    List<string> Unsupported,
    string Format)
{
    public static ImportReport Empty => new([], [], "");
}

/// <summary>
/// Reads every node-list format the popular clients are fed. Panels pick the format from the
/// User-Agent, so the same subscription can arrive as any of these:
///
/// * share links (<c>vless://…</c>), plain or base64 — v2rayNG, Streisand, v2rayTun
/// * Xray JSON — an array of complete Xray configs, one per node (this is what Happ gets)
/// * sing-box JSON — one config whose outbounds are the nodes (SFI, Karing, INCY)
/// * Clash / Mihomo YAML — a <c>proxies:</c> list
///
/// Protocols this app's core cannot run (Hysteria, TUIC, WireGuard) are not silently dropped;
/// they are listed in <see cref="ImportReport.Unsupported"/> so the key checker can say so.
/// </summary>
public static partial class ConfigImporter
{
    private static readonly string[] CoreProtocols = ["vless", "vmess", "trojan", "shadowsocks", "ss"];

    /// <summary>Placeholder node Remnawave panels serve when they do not recognise the client.</summary>
    public static bool IsPlaceholder(ServerProfile p) =>
        p.Address is "0.0.0.0" or "127.0.0.1" or "" ||
        p.Credential == "00000000-0000-0000-0000-000000000000";

    public static ImportReport Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return ImportReport.Empty;

        var text = body.Trim();

        // JSON first: an Xray-JSON body also contains "vless" strings, so link scraping would
        // mis-read it as a share-link list.
        if (text.StartsWith('[') || text.StartsWith('{'))
        {
            var json = ParseJson(text);
            if (json.Nodes.Count > 0 || json.Unsupported.Count > 0) return json;
        }

        var links = ShareLinkParser.ParseMany(text);
        if (links.Count > 0) return new ImportReport(links, FindUnsupportedLinks(text), "ссылки");

        if (ShareLinkParser.LooksLikeBase64(text))
        {
            try
            {
                var decoded = ShareLinkParser.Base64Decode(text);
                var inner = Parse(decoded);
                if (inner.Nodes.Count > 0 || inner.Unsupported.Count > 0)
                    return inner with { Format = inner.Format == "ссылки" ? "base64" : inner.Format };
            }
            catch { }
        }

        // Some panels base64 every line on its own.
        var perLine = new List<ServerProfile>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!ShareLinkParser.LooksLikeBase64(line)) continue;
            try { perLine.AddRange(ShareLinkParser.ParseMany(ShareLinkParser.Base64Decode(line))); }
            catch { }
        }
        if (perLine.Count > 0) return new ImportReport(perLine, [], "base64");

        if (LooksLikeClash(text))
        {
            var clash = ParseClash(text);
            if (clash.Nodes.Count > 0 || clash.Unsupported.Count > 0) return clash;
        }

        return ImportReport.Empty;
    }

    // ------------------------------------------------------------------ share links

    [GeneratedRegex(@"\b(hysteria2?|hy2|tuic|wireguard|wg|juicity|ssh|snell|anytls)://[^\s""'<>\\]+", RegexOptions.IgnoreCase)]
    private static partial Regex UnsupportedLinkRegex();

    private static List<string> FindUnsupportedLinks(string text) =>
        UnsupportedLinkRegex().Matches(text)
            .Select(m => Describe(m.Groups[1].Value, Remark(m.Value)))
            .Distinct()
            .ToList();

    private static string Remark(string link)
    {
        var hash = link.IndexOf('#');
        return hash < 0 ? "" : Uri.UnescapeDataString(link[(hash + 1)..]);
    }

    private static string Describe(string protocol, string remark) =>
        string.IsNullOrWhiteSpace(remark) ? protocol.ToLowerInvariant() : $"{protocol.ToLowerInvariant()} · {remark}";

    // ------------------------------------------------------------------ JSON

    private static ImportReport ParseJson(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            // Xray JSON subscription: an array of full configs, each with its own remarks.
            if (root.ValueKind == JsonValueKind.Array)
            {
                var nodes = new List<ServerProfile>();
                var unsupported = new List<string>();
                var format = "Xray JSON";

                foreach (var element in root.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object) continue;

                    var remark = element.TryGetProperty("remarks", out var r) ? r.GetString() ?? "" : "";
                    if (element.TryGetProperty("outbounds", out var outbounds))
                        ReadXrayOutbounds(outbounds, remark, nodes, unsupported);
                    else
                        ReadXrayOutbound(element, remark, nodes, unsupported);
                }

                return new ImportReport(nodes, unsupported, format);
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                // sing-box: outbounds carry "type"; Xray: "protocol".
                if (root.TryGetProperty("outbounds", out var outbounds) && outbounds.ValueKind == JsonValueKind.Array)
                {
                    var isSingBox = outbounds.EnumerateArray()
                        .Any(o => o.ValueKind == JsonValueKind.Object && o.TryGetProperty("type", out _));

                    var nodes = new List<ServerProfile>();
                    var unsupported = new List<string>();

                    if (isSingBox) ReadSingBoxOutbounds(outbounds, nodes, unsupported);
                    else ReadXrayOutbounds(outbounds, root.TryGetProperty("remarks", out var rr) ? rr.GetString() ?? "" : "", nodes, unsupported);

                    return new ImportReport(nodes, unsupported, isSingBox ? "sing-box JSON" : "Xray JSON");
                }
            }
        }
        catch (Exception ex)
        {
            LogBus.Instance.Debug("import", $"JSON parse failed: {ex.Message}");
        }

        return ImportReport.Empty;
    }

    private static void ReadXrayOutbounds(JsonElement outbounds, string remark, List<ServerProfile> nodes, List<string> unsupported)
    {
        if (outbounds.ValueKind != JsonValueKind.Array) return;
        foreach (var outbound in outbounds.EnumerateArray())
            ReadXrayOutbound(outbound, remark, nodes, unsupported);
    }

    private static void ReadXrayOutbound(JsonElement outbound, string remark, List<ServerProfile> nodes, List<string> unsupported)
    {
        if (outbound.ValueKind != JsonValueKind.Object) return;

        var protocol = (outbound.TryGetProperty("protocol", out var p) ? p.GetString() ?? "" : "").ToLowerInvariant();
        if (protocol is "freedom" or "blackhole" or "dns" or "loopback" or "") return;

        var tag = outbound.TryGetProperty("tag", out var t) ? t.GetString() ?? "" : "";
        var name = string.IsNullOrWhiteSpace(remark) ? tag : remark;

        if (!CoreProtocols.Contains(protocol))
        {
            unsupported.Add(Describe(protocol, name));
            return;
        }

        if (!outbound.TryGetProperty("settings", out var settings)) return;

        var profile = new ServerProfile { Remark = name };

        switch (protocol)
        {
            case "vless" or "vmess":
            {
                if (!TryFirst(settings, "vnext", out var vnext)) return;
                profile.Protocol = protocol == "vless" ? ProxyProtocol.Vless : ProxyProtocol.Vmess;
                profile.Address = Str(vnext, "address");
                profile.Port = Num(vnext, "port");

                if (!TryFirst(vnext, "users", out var user)) return;
                profile.Credential = Str(user, "id");
                profile.Flow = Str(user, "flow");
                profile.Encryption = Str(user, "encryption") is { Length: > 0 } enc ? enc : "none";
                profile.AlterId = Num(user, "alterId");
                if (Str(user, "security") is { Length: > 0 } sec) profile.Security = sec;
                break;
            }
            case "trojan":
            {
                if (!TryFirst(settings, "servers", out var server)) return;
                profile.Protocol = ProxyProtocol.Trojan;
                profile.Address = Str(server, "address");
                profile.Port = Num(server, "port");
                profile.Credential = Str(server, "password");
                break;
            }
            case "shadowsocks" or "ss":
            {
                if (!TryFirst(settings, "servers", out var server)) return;
                profile.Protocol = ProxyProtocol.Shadowsocks;
                profile.Address = Str(server, "address");
                profile.Port = Num(server, "port");
                profile.Credential = Str(server, "password");
                profile.Method = Str(server, "method");
                break;
            }
        }

        if (outbound.TryGetProperty("streamSettings", out var stream)) ReadXrayStream(profile, stream);

        if (profile.Address.Length > 0 && profile.Port > 0) nodes.Add(profile);
    }

    private static void ReadXrayStream(ServerProfile profile, JsonElement stream)
    {
        profile.Network = Str(stream, "network") is { Length: > 0 } net ? net : "tcp";
        profile.StreamSecurity = Str(stream, "security") is { Length: > 0 } sec && sec != "none" ? sec : "";

        if (stream.TryGetProperty("realitySettings", out var reality))
        {
            profile.Sni = Str(reality, "serverName");
            profile.PublicKey = Str(reality, "publicKey");
            profile.ShortId = Str(reality, "shortId");
            profile.SpiderX = Str(reality, "spiderX");
            profile.Fingerprint = Str(reality, "fingerprint");
        }
        else if (stream.TryGetProperty("tlsSettings", out var tls))
        {
            profile.Sni = Str(tls, "serverName");
            profile.Fingerprint = Str(tls, "fingerprint");
            profile.AllowInsecure = Bool(tls, "allowInsecure");
            if (tls.TryGetProperty("alpn", out var alpn) && alpn.ValueKind == JsonValueKind.Array)
                profile.Alpn = string.Join(",", alpn.EnumerateArray().Select(a => a.GetString()).Where(a => a is not null));
        }

        if (stream.TryGetProperty("wsSettings", out var ws))
        {
            profile.Path = Str(ws, "path");
            profile.Host = Header(ws, "Host");
        }
        else if (stream.TryGetProperty("httpupgradeSettings", out var hu))
        {
            profile.Path = Str(hu, "path");
            profile.Host = Str(hu, "host") is { Length: > 0 } h ? h : Header(hu, "Host");
        }
        else if (stream.TryGetProperty("xhttpSettings", out var xh) || stream.TryGetProperty("splithttpSettings", out xh))
        {
            profile.Path = Str(xh, "path");
            profile.Host = Str(xh, "host");
            profile.Mode = Str(xh, "mode");
        }
        else if (stream.TryGetProperty("grpcSettings", out var grpc))
        {
            profile.ServiceName = Str(grpc, "serviceName");
            if (Bool(grpc, "multiMode")) profile.Mode = "multi";
        }
        else if (stream.TryGetProperty("httpSettings", out var h2))
        {
            profile.Path = Str(h2, "path");
            if (h2.TryGetProperty("host", out var hosts) && hosts.ValueKind == JsonValueKind.Array)
                profile.Host = hosts.EnumerateArray().Select(x => x.GetString()).FirstOrDefault(x => x is not null) ?? "";
        }
        else if (stream.TryGetProperty("kcpSettings", out var kcp))
        {
            profile.Seed = Str(kcp, "seed");
            if (kcp.TryGetProperty("header", out var kcpHeader)) profile.HeaderType = Str(kcpHeader, "type");
        }
        else if (stream.TryGetProperty("tcpSettings", out var tcp) && tcp.TryGetProperty("header", out var tcpHeader))
        {
            profile.HeaderType = Str(tcpHeader, "type");
            if (tcpHeader.TryGetProperty("request", out var request))
            {
                if (request.TryGetProperty("path", out var paths) && paths.ValueKind == JsonValueKind.Array)
                    profile.Path = paths.EnumerateArray().Select(x => x.GetString()).FirstOrDefault(x => x is not null) ?? "";
                if (request.TryGetProperty("headers", out var headers)) profile.Host = Header(headers, "Host", direct: true);
            }
        }
    }

    // ------------------------------------------------------------------ sing-box

    private static void ReadSingBoxOutbounds(JsonElement outbounds, List<ServerProfile> nodes, List<string> unsupported)
    {
        foreach (var outbound in outbounds.EnumerateArray())
        {
            if (outbound.ValueKind != JsonValueKind.Object) continue;

            var type = Str(outbound, "type").ToLowerInvariant();
            if (type is "direct" or "block" or "dns" or "selector" or "urltest" or "") continue;

            var tag = Str(outbound, "tag");

            if (!CoreProtocols.Contains(type))
            {
                unsupported.Add(Describe(type, tag));
                continue;
            }

            var profile = new ServerProfile
            {
                Remark = tag,
                Address = Str(outbound, "server"),
                Port = Num(outbound, "server_port")
            };

            switch (type)
            {
                case "vless":
                    profile.Protocol = ProxyProtocol.Vless;
                    profile.Credential = Str(outbound, "uuid");
                    profile.Flow = Str(outbound, "flow");
                    break;
                case "vmess":
                    profile.Protocol = ProxyProtocol.Vmess;
                    profile.Credential = Str(outbound, "uuid");
                    profile.AlterId = Num(outbound, "alter_id");
                    if (Str(outbound, "security") is { Length: > 0 } sec) profile.Security = sec;
                    break;
                case "trojan":
                    profile.Protocol = ProxyProtocol.Trojan;
                    profile.Credential = Str(outbound, "password");
                    break;
                case "shadowsocks" or "ss":
                    profile.Protocol = ProxyProtocol.Shadowsocks;
                    profile.Credential = Str(outbound, "password");
                    profile.Method = Str(outbound, "method");
                    break;
            }

            if (outbound.TryGetProperty("tls", out var tls) && Bool(tls, "enabled"))
            {
                profile.StreamSecurity = "tls";
                profile.Sni = Str(tls, "server_name");
                profile.AllowInsecure = Bool(tls, "insecure");

                if (tls.TryGetProperty("alpn", out var alpn) && alpn.ValueKind == JsonValueKind.Array)
                    profile.Alpn = string.Join(",", alpn.EnumerateArray().Select(a => a.GetString()).Where(a => a is not null));

                if (tls.TryGetProperty("utls", out var utls)) profile.Fingerprint = Str(utls, "fingerprint");

                if (tls.TryGetProperty("reality", out var reality) && Bool(reality, "enabled"))
                {
                    profile.StreamSecurity = "reality";
                    profile.PublicKey = Str(reality, "public_key");
                    profile.ShortId = Str(reality, "short_id");
                }
            }

            profile.Network = "tcp";
            if (outbound.TryGetProperty("transport", out var transport))
            {
                var transportType = Str(transport, "type").ToLowerInvariant();
                profile.Network = transportType switch
                {
                    "ws" => "ws",
                    "grpc" => "grpc",
                    "http" => "h2",
                    "httpupgrade" => "httpupgrade",
                    "quic" => "quic",
                    _ => "tcp"
                };
                profile.Path = Str(transport, "path");
                profile.ServiceName = Str(transport, "service_name");
                if (transport.TryGetProperty("headers", out var headers)) profile.Host = Header(headers, "Host", direct: true);
                if (profile.Host.Length == 0 && transport.TryGetProperty("host", out var host))
                    profile.Host = host.ValueKind == JsonValueKind.Array
                        ? host.EnumerateArray().Select(x => x.GetString()).FirstOrDefault(x => x is not null) ?? ""
                        : host.GetString() ?? "";
            }

            if (profile.Address.Length > 0 && profile.Port > 0) nodes.Add(profile);
        }
    }

    // ------------------------------------------------------------------ Clash / Mihomo

    private static bool LooksLikeClash(string text) =>
        text.Contains("proxies:", StringComparison.OrdinalIgnoreCase) &&
        (text.Contains("type:", StringComparison.OrdinalIgnoreCase) || text.Contains("server:", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Enough YAML for a Clash <c>proxies:</c> list — both the flow style panels emit
    /// (<c>- {name: x, type: vless, …}</c>) and the block style. Not a general YAML parser.
    /// </summary>
    private static ImportReport ParseClash(string text)
    {
        var nodes = new List<ServerProfile>();
        var unsupported = new List<string>();

        var lines = text.Replace("\r", "").Split('\n');
        var inProxies = false;
        Dictionary<string, string>? current = null;
        var baseIndent = -1;

        void Flush()
        {
            if (current is null) return;
            var node = FromClash(current, unsupported);
            if (node is not null) nodes.Add(node);
            current = null;
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.TrimStart().StartsWith('#')) continue;

            var indent = line.Length - line.TrimStart().Length;
            var trimmed = line.TrimStart();

            if (!inProxies)
            {
                if (trimmed.StartsWith("proxies:", StringComparison.OrdinalIgnoreCase))
                {
                    inProxies = true;
                    baseIndent = indent;
                }
                continue;
            }

            // A new top-level key at or above the proxies indent ends the list.
            if (indent <= baseIndent && !trimmed.StartsWith('-'))
            {
                Flush();
                inProxies = false;
                continue;
            }

            if (trimmed.StartsWith('-'))
            {
                Flush();
                current = [];
                var rest = trimmed[1..].Trim();
                if (rest.StartsWith('{')) ReadFlowMapping(rest, current);
                else if (rest.Length > 0) ReadKeyValue(rest, current);
                continue;
            }

            if (current is not null) ReadKeyValue(trimmed, current);
        }
        Flush();

        return new ImportReport(nodes, unsupported, "Clash YAML");
    }

    private static void ReadFlowMapping(string text, Dictionary<string, string> into)
    {
        var body = text.Trim().Trim('{', '}');
        foreach (var part in SplitTopLevel(body))
            ReadKeyValue(part, into);
    }

    /// <summary>Split on commas that are not inside quotes or nested braces/brackets.</summary>
    private static IEnumerable<string> SplitTopLevel(string text)
    {
        var depth = 0;
        var quote = '\0';
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }
            switch (c)
            {
                case '"' or '\'': quote = c; break;
                case '{' or '[': depth++; break;
                case '}' or ']': depth--; break;
                case ',' when depth == 0:
                    yield return text[start..i];
                    start = i + 1;
                    break;
            }
        }
        if (start < text.Length) yield return text[start..];
    }

    private static void ReadKeyValue(string text, Dictionary<string, string> into)
    {
        var colon = text.IndexOf(':');
        if (colon <= 0) return;

        var key = text[..colon].Trim().Trim('"', '\'').ToLowerInvariant();
        var value = text[(colon + 1)..].Trim().Trim(',').Trim().Trim('"', '\'');
        if (key.Length == 0) return;
        into[key] = value;
    }

    private static ServerProfile? FromClash(Dictionary<string, string> map, List<string> unsupported)
    {
        string Get(params string[] keys)
        {
            foreach (var k in keys)
                if (map.TryGetValue(k, out var v) && v.Length > 0) return v;
            return "";
        }

        var type = Get("type").ToLowerInvariant();
        var name = Get("name");
        if (type.Length == 0) return null;

        if (!CoreProtocols.Contains(type))
        {
            unsupported.Add(Describe(type, name));
            return null;
        }

        var profile = new ServerProfile
        {
            Remark = name,
            Address = Get("server"),
            Port = int.TryParse(Get("port"), out var port) ? port : 0,
            Sni = Get("servername", "sni"),
            Fingerprint = Get("client-fingerprint"),
            AllowInsecure = Get("skip-cert-verify").Equals("true", StringComparison.OrdinalIgnoreCase)
        };

        switch (type)
        {
            case "vless":
                profile.Protocol = ProxyProtocol.Vless;
                profile.Credential = Get("uuid");
                profile.Flow = Get("flow");
                break;
            case "vmess":
                profile.Protocol = ProxyProtocol.Vmess;
                profile.Credential = Get("uuid");
                profile.AlterId = int.TryParse(Get("alterid"), out var aid) ? aid : 0;
                if (Get("cipher") is { Length: > 0 } cipher) profile.Security = cipher;
                break;
            case "trojan":
                profile.Protocol = ProxyProtocol.Trojan;
                profile.Credential = Get("password");
                profile.StreamSecurity = "tls";
                break;
            case "ss" or "shadowsocks":
                profile.Protocol = ProxyProtocol.Shadowsocks;
                profile.Credential = Get("password");
                profile.Method = Get("cipher");
                break;
        }

        var network = Get("network").ToLowerInvariant();
        profile.Network = network.Length > 0 ? network : "tcp";
        profile.ServiceName = Get("grpc-service-name");

        if (Get("reality-opts").Length > 0 || map.Keys.Any(k => k.StartsWith("reality")))
            profile.StreamSecurity = "reality";
        else if (Get("tls").Equals("true", StringComparison.OrdinalIgnoreCase) && profile.StreamSecurity.Length == 0)
            profile.StreamSecurity = "tls";

        // ws-opts / reality-opts arrive flattened by ReadKeyValue, so pick what we can.
        if (map.TryGetValue("path", out var path)) profile.Path = path;
        if (map.TryGetValue("host", out var host)) profile.Host = host;
        if (map.TryGetValue("public-key", out var pbk)) profile.PublicKey = pbk;
        if (map.TryGetValue("short-id", out var sid)) profile.ShortId = sid;

        return profile.Address.Length > 0 && profile.Port > 0 ? profile : null;
    }

    // ------------------------------------------------------------------ json helpers

    private static bool TryFirst(JsonElement parent, string property, out JsonElement first)
    {
        first = default;
        if (!parent.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array) return false;

        foreach (var item in array.EnumerateArray())
        {
            first = item;
            return true;
        }
        return false;
    }

    private static string Str(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Number => value.ToString(),
                _ => ""
            }
            : "";

    private static int Num(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out var n) ? n : 0,
            JsonValueKind.String => int.TryParse(value.GetString(), out var s) ? s : 0,
            _ => 0
        };
    }

    private static bool Bool(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => value.GetString()?.Equals("true", StringComparison.OrdinalIgnoreCase) == true,
            _ => false
        };

    private static string Header(JsonElement parent, string name, bool direct = false)
    {
        var headers = direct ? parent : parent.TryGetProperty("headers", out var h) ? h : default;
        if (headers.ValueKind != JsonValueKind.Object) return "";

        foreach (var property in headers.EnumerateObject())
        {
            if (!property.NameEquals(name) && !property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            return property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? "",
                JsonValueKind.Array => property.Value.EnumerateArray().Select(x => x.GetString()).FirstOrDefault(x => x is not null) ?? "",
                _ => ""
            };
        }
        return "";
    }
}
