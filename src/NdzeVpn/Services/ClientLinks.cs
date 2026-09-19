using System.Text;
using System.Web;

namespace NdzeVpn.Services;

/// <summary>
/// Import links of other clients. Happ, INCY, Hiddify, v2RayTun, Streisand, Clash and sing-box all
/// wrap the same subscription URL in their own scheme, usually base64-encoded, so a key copied out
/// of any of them can be pasted here unchanged.
/// </summary>
public static class ClientLinks
{
    /// <summary>Schemes that carry a subscription URL or a key inside them.</summary>
    private static readonly string[] Schemes =
    [
        "happ://", "incy://", "hiddify://", "v2raytun://", "v2box://", "streisand://", "sn://",
        "clash://", "mihomo://", "sing-box://", "karing://", "nekobox://", "nekoray://", "shadowrocket://",
        "v2rayng://", "flclash://", "throne://"
    ];

    /// <summary>True when this looks like another client's import link rather than a plain URL.</summary>
    public static bool IsClientLink(string text) =>
        Schemes.Any(s => text.TrimStart().StartsWith(s, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns the subscription URL or key hidden inside a client link; anything else is returned
    /// unchanged. Encrypted Happ links (<c>happ://crypt…</c>) cannot be opened by other clients and
    /// come back as-is, so the caller can explain that instead of failing silently.
    /// </summary>
    public static string Unwrap(string text)
    {
        var value = text.Trim();
        if (!IsClientLink(value)) return value;

        var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        var scheme = value[..schemeEnd].ToLowerInvariant();
        var rest = value[(schemeEnd + 3)..];

        // ?url=… / ?sub=… — the Clash, Hiddify and Karing style.
        var query = rest.IndexOf('?');
        if (query >= 0)
        {
            var parsed = HttpUtility.ParseQueryString(rest[(query + 1)..]);
            foreach (var key in new[] { "url", "sub", "subscription", "config", "link" })
            {
                var candidate = parsed[key];
                if (!string.IsNullOrWhiteSpace(candidate)) return Decode(candidate.Trim());
            }
            rest = rest[..query];
        }

        // happ://add/<payload>, v2raytun://import/<payload>, sing-box://import-remote-profile/<payload>
        foreach (var verb in new[]
                 {
                     "add/", "import/", "import-remote-profile/", "install-config/", "install-sub/",
                     "subscribe/", "sub/", "profile/"
                 })
        {
            if (rest.StartsWith(verb, StringComparison.OrdinalIgnoreCase))
            {
                rest = rest[verb.Length..];
                break;
            }
        }

        // Encrypted or app-specific payloads we cannot read.
        if (scheme == "happ" && (rest.StartsWith("crypt", StringComparison.OrdinalIgnoreCase)
                                 || rest.StartsWith("routing", StringComparison.OrdinalIgnoreCase)))
            return value;

        return Decode(rest);
    }

    private static string Decode(string payload)
    {
        var value = payload.Trim().TrimEnd('/');
        if (value.Length == 0) return value;

        value = HttpUtility.UrlDecode(value);

        if (value.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
            ShareLinkParser.ExtractLinks(value).Any())
            return value;

        try
        {
            var decoded = ShareLinkParser.Base64Decode(value).Trim();
            if (decoded.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                ShareLinkParser.ExtractLinks(decoded).Any())
                return decoded;
        }
        catch { }

        return value;
    }

    /// <summary>Human-readable name of the client a link came from, for the key checker.</summary>
    public static string? SourceApp(string text)
    {
        var value = text.TrimStart();
        var scheme = Schemes.FirstOrDefault(s => value.StartsWith(s, StringComparison.OrdinalIgnoreCase));
        if (scheme is null) return null;

        return scheme.TrimEnd(':', '/').ToLowerInvariant() switch
        {
            "happ" => "Happ",
            "incy" => "INCY",
            "hiddify" => "Hiddify",
            "v2raytun" => "v2RayTun",
            "v2box" => "V2Box",
            "streisand" or "sn" => "Streisand",
            "clash" or "mihomo" or "flclash" => "Clash",
            "sing-box" => "sing-box",
            "karing" => "Karing",
            "nekobox" or "nekoray" => "NekoBox",
            "shadowrocket" => "Shadowrocket",
            "v2rayng" => "v2rayNG",
            "throne" => "Throne",
            var other => other
        };
    }

    /// <summary>True for a Happ link whose payload is encrypted and unreadable outside Happ.</summary>
    public static bool IsEncryptedHapp(string text)
    {
        var value = text.TrimStart();
        return value.StartsWith("happ://crypt", StringComparison.OrdinalIgnoreCase);
    }

    public static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
