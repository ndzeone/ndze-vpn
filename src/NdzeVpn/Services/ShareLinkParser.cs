using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using NdzeVpn.Models;

namespace NdzeVpn.Services;

/// <summary>
/// Turns share links into <see cref="ServerProfile"/>s. Handles vless://, vmess:// (both the
/// base64-JSON form and the newer URI form), trojan:// and ss://.
/// </summary>
public static partial class ShareLinkParser
{
    [GeneratedRegex(@"(?:vless|vmess|trojan|ss)://[^\s""'<>\\]+", RegexOptions.IgnoreCase)]
    private static partial Regex ShareLinkRegex();

    /// <summary>Pull every share link out of an arbitrary blob of text (plain list, HTML page, JSON…).</summary>
    public static IEnumerable<string> ExtractLinks(string text) =>
        ShareLinkRegex().Matches(text).Select(m => m.Value.TrimEnd('.', ',', ')', ']', '}', '"', '\''));

    public static ServerProfile? Parse(string uri)
    {
        uri = uri.Trim();
        if (uri.Length == 0) return null;

        try
        {
            if (uri.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)) return ParseVless(uri);
            if (uri.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase)) return ParseVmess(uri);
            if (uri.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase)) return ParseTrojan(uri);
            if (uri.StartsWith("ss://", StringComparison.OrdinalIgnoreCase)) return ParseShadowsocks(uri);
        }
        catch
        {
            // A malformed node in a subscription must not take the whole import down.
        }
        return null;
    }

    public static List<ServerProfile> ParseMany(string text)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ServerProfile>();

        foreach (var link in ExtractLinks(text))
        {
            var profile = Parse(link);
            if (profile is null) continue;

            // Dedupe on the wire identity, not the remark — panels love renaming nodes.
            var key = $"{profile.Protocol}|{profile.Address}|{profile.Port}|{profile.Credential}|{profile.Network}|{profile.Path}";
            if (seen.Add(key)) result.Add(profile);
        }
        return result;
    }

    // ---------------------------------------------------------------- VLESS

    private static ServerProfile ParseVless(string uri)
    {
        var u = new Uri(uri);
        var q = HttpUtility.ParseQueryString(u.Query);

        var p = new ServerProfile
        {
            Protocol = ProxyProtocol.Vless,
            RawUri = uri,
            Credential = Uri.UnescapeDataString(u.UserInfo),
            Address = StripBrackets(u.Host),
            Port = u.Port > 0 ? u.Port : 443,
            Remark = DecodeFragment(u.Fragment),
            Encryption = q["encryption"] ?? "none",
            Flow = q["flow"] ?? "",
            Network = (q["type"] ?? "tcp").ToLowerInvariant(),
            StreamSecurity = (q["security"] ?? "").ToLowerInvariant(),
            HeaderType = q["headerType"] ?? "none",
            Sni = q["sni"] ?? "",
            Alpn = q["alpn"] ?? "",
            Fingerprint = q["fp"] ?? "",
            PublicKey = q["pbk"] ?? "",
            ShortId = q["sid"] ?? "",
            SpiderX = q["spx"] ?? "",
            Mode = q["mode"] ?? "",
            Seed = q["seed"] ?? "",
            QuicSecurity = q["quicSecurity"] ?? "",
            QuicKey = q["key"] ?? "",
            AllowInsecure = q["allowInsecure"] is "1" or "true"
        };

        ApplyTransportFields(p, q["path"], q["host"], q["serviceName"], q["authority"]);

        // A REALITY link is sometimes tagged only by the presence of pbk.
        if (string.IsNullOrEmpty(p.StreamSecurity) && !string.IsNullOrEmpty(p.PublicKey))
            p.StreamSecurity = "reality";

        return p;
    }

    // ---------------------------------------------------------------- VMess

    private static ServerProfile ParseVmess(string uri)
    {
        var body = uri["vmess://".Length..];

        // Newer panels emit a URI-shaped vmess link instead of base64 JSON.
        if (body.Contains('@'))
        {
            var u = new Uri(uri);
            var q = HttpUtility.ParseQueryString(u.Query);
            var vp = new ServerProfile
            {
                Protocol = ProxyProtocol.Vmess,
                RawUri = uri,
                Credential = Uri.UnescapeDataString(u.UserInfo),
                Address = StripBrackets(u.Host),
                Port = u.Port > 0 ? u.Port : 443,
                Remark = DecodeFragment(u.Fragment),
                Network = (q["type"] ?? "tcp").ToLowerInvariant(),
                StreamSecurity = (q["security"] ?? "").ToLowerInvariant(),
                Sni = q["sni"] ?? "",
                Alpn = q["alpn"] ?? "",
                Fingerprint = q["fp"] ?? "",
                HeaderType = q["headerType"] ?? "none"
            };
            ApplyTransportFields(vp, q["path"], q["host"], q["serviceName"], q["authority"]);
            return vp;
        }

        using var doc = JsonDocument.Parse(Base64Decode(body));
        var root = doc.RootElement;

        string S(string name) =>
            root.TryGetProperty(name, out var v)
                ? v.ValueKind == JsonValueKind.Number ? v.GetRawText() : v.GetString() ?? ""
                : "";

        var p = new ServerProfile
        {
            Protocol = ProxyProtocol.Vmess,
            RawUri = uri,
            Remark = S("ps"),
            Address = S("add"),
            Port = int.TryParse(S("port"), out var port) ? port : 443,
            Credential = S("id"),
            AlterId = int.TryParse(S("aid"), out var aid) ? aid : 0,
            Security = string.IsNullOrEmpty(S("scy")) ? "auto" : S("scy"),
            Network = string.IsNullOrEmpty(S("net")) ? "tcp" : S("net").ToLowerInvariant(),
            HeaderType = string.IsNullOrEmpty(S("type")) ? "none" : S("type"),
            StreamSecurity = S("tls").ToLowerInvariant(),
            Sni = S("sni"),
            Alpn = S("alpn"),
            Fingerprint = S("fp")
        };

        ApplyTransportFields(p, S("path"), S("host"), S("path"), S("host"));
        return p;
    }

    // ---------------------------------------------------------------- Trojan

    private static ServerProfile ParseTrojan(string uri)
    {
        var u = new Uri(uri);
        var q = HttpUtility.ParseQueryString(u.Query);

        var p = new ServerProfile
        {
            Protocol = ProxyProtocol.Trojan,
            RawUri = uri,
            Credential = Uri.UnescapeDataString(u.UserInfo),
            Address = StripBrackets(u.Host),
            Port = u.Port > 0 ? u.Port : 443,
            Remark = DecodeFragment(u.Fragment),
            Network = (q["type"] ?? "tcp").ToLowerInvariant(),
            // Trojan is TLS by definition; a link that omits security still means TLS.
            StreamSecurity = string.IsNullOrEmpty(q["security"]) ? "tls" : q["security"]!.ToLowerInvariant(),
            Sni = q["sni"] ?? q["peer"] ?? "",
            Alpn = q["alpn"] ?? "",
            Fingerprint = q["fp"] ?? "",
            HeaderType = q["headerType"] ?? "none",
            PublicKey = q["pbk"] ?? "",
            ShortId = q["sid"] ?? "",
            AllowInsecure = q["allowInsecure"] is "1" or "true"
        };

        ApplyTransportFields(p, q["path"], q["host"], q["serviceName"], q["authority"]);
        return p;
    }

    // ---------------------------------------------------------------- Shadowsocks

    private static ServerProfile ParseShadowsocks(string uri)
    {
        var raw = uri["ss://".Length..];
        var remark = "";

        var hash = raw.IndexOf('#');
        if (hash >= 0)
        {
            remark = Uri.UnescapeDataString(raw[(hash + 1)..]);
            raw = raw[..hash];
        }

        var queryIdx = raw.IndexOf('?');
        if (queryIdx >= 0) raw = raw[..queryIdx];

        string method, password, host;
        int port;

        var at = raw.LastIndexOf('@');
        if (at >= 0)
        {
            // SIP002: ss://base64(method:password)@host:port
            var userInfo = raw[..at];
            var hostPart = raw[(at + 1)..];

            if (!userInfo.Contains(':')) userInfo = Base64Decode(userInfo);
            var up = userInfo.Split(':', 2);
            method = up[0];
            password = up.Length > 1 ? up[1] : "";

            var (h, pt) = SplitHostPort(hostPart);
            host = h; port = pt;
        }
        else
        {
            // Legacy: ss://base64(method:password@host:port)
            var decoded = Base64Decode(raw);
            var atIdx = decoded.LastIndexOf('@');
            var up = decoded[..atIdx].Split(':', 2);
            method = up[0];
            password = up.Length > 1 ? up[1] : "";

            var (h, pt) = SplitHostPort(decoded[(atIdx + 1)..]);
            host = h; port = pt;
        }

        return new ServerProfile
        {
            Protocol = ProxyProtocol.Shadowsocks,
            RawUri = uri,
            Remark = remark,
            Address = StripBrackets(host),
            Port = port,
            Method = method,
            Credential = password,
            Network = "tcp"
        };
    }

    // ---------------------------------------------------------------- helpers

    private static void ApplyTransportFields(ServerProfile p, string? path, string? host, string? serviceName, string? authority)
    {
        switch (p.Network)
        {
            case "ws":
            case "httpupgrade":
            case "xhttp":
            case "splithttp":
                p.Path = string.IsNullOrEmpty(path) ? "/" : Uri.UnescapeDataString(path);
                p.Host = host ?? "";
                break;
            case "grpc":
                p.ServiceName = serviceName ?? path ?? "";
                p.Host = authority ?? host ?? "";
                break;
            case "h2":
            case "http":
                p.Path = string.IsNullOrEmpty(path) ? "/" : Uri.UnescapeDataString(path);
                p.Host = host ?? "";
                break;
            case "kcp":
                p.Seed = p.Seed.Length > 0 ? p.Seed : path ?? "";
                break;
            default:
                p.Path = path is null ? "" : Uri.UnescapeDataString(path);
                p.Host = host ?? "";
                break;
        }
    }

    private static (string Host, int Port) SplitHostPort(string value)
    {
        // IPv6 literals arrive as [::1]:443.
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            var h = value[1..close];
            var p = value[(close + 1)..].TrimStart(':');
            return (h, int.TryParse(p, out var pi) ? pi : 443);
        }

        var idx = value.LastIndexOf(':');
        if (idx < 0) return (value, 443);
        return (value[..idx], int.TryParse(value[(idx + 1)..], out var port) ? port : 443);
    }

    private static string StripBrackets(string host) => host.Trim('[', ']');

    private static string DecodeFragment(string fragment) =>
        fragment.Length <= 1 ? "" : Uri.UnescapeDataString(fragment[1..]);

    public static string Base64Decode(string value)
    {
        value = value.Trim().Replace('-', '+').Replace('_', '/');
        var pad = value.Length % 4;
        if (pad > 0) value += new string('=', 4 - pad);
        return Encoding.UTF8.GetString(Convert.FromBase64String(value));
    }

    public static bool LooksLikeBase64(string value)
    {
        var trimmed = new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (trimmed.Length < 16) return false;
        return trimmed.All(c => char.IsLetterOrDigit(c) || c is '+' or '/' or '=' or '-' or '_');
    }
}
