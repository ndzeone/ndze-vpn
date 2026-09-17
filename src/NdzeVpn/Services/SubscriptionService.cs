using System.Net;
using System.Text.RegularExpressions;
using NdzeVpn.Models;

namespace NdzeVpn.Services;

public sealed record SubscriptionResult(
    List<ServerProfile> Nodes,
    string? Error,
    string? TrafficInfo);

/// <summary>
/// Fetches node lists from subscription URLs.
///
/// Telegram VPN bots rarely hand out a clean base64 subscription — the link is usually a landing
/// page that shows the key, or a redirect chain ending at one. So this tries, in order:
/// share links anywhere in the body, base64 of the whole body, then one level of following
/// subscription-looking links found in the HTML.
/// </summary>
public sealed partial class SubscriptionService
{
    private const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";

    [GeneratedRegex("""href\s*=\s*["']([^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex HrefRegex();

    [GeneratedRegex("""(https?://[^\s"'<>]*(?:sub|subscribe|subscription|clash|v2ray|link|api)[^\s"'<>]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex SubLinkRegex();

    public async Task<SubscriptionResult> FetchAsync(Subscription sub, AppSettings settings, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sub.Url))
            return new SubscriptionResult([], "Subscription URL is empty.", null);

        // A "subscription" that is itself just a share link needs no network round trip.
        var inline = ShareLinkParser.ParseMany(sub.Url);
        if (inline.Count > 0)
            return new SubscriptionResult(inline, null, null);

        using var http = CreateClient(sub, settings);

        try
        {
            var (body, traffic) = await GetAsync(http, sub.Url, ct);

            var nodes = ExtractNodes(body);
            if (nodes.Count > 0)
                return new SubscriptionResult(nodes, null, traffic);

            // Landing page: follow the most subscription-looking link on it, once.
            foreach (var candidate in FindCandidateLinks(body, sub.Url).Take(4))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    LogBus.Instance.Debug("sub", $"Following candidate link {candidate}");
                    var (inner, innerTraffic) = await GetAsync(http, candidate, ct);
                    var innerNodes = ExtractNodes(inner);
                    if (innerNodes.Count > 0)
                        return new SubscriptionResult(innerNodes, null, innerTraffic ?? traffic);
                }
                catch (Exception ex)
                {
                    LogBus.Instance.Debug("sub", $"Candidate {candidate} failed: {ex.Message}");
                }
            }

            return new SubscriptionResult([], "No nodes found at this URL. Open it in a browser and paste the key manually.", traffic);
        }
        catch (OperationCanceledException)
        {
            return new SubscriptionResult([], "Cancelled.", null);
        }
        catch (HttpRequestException ex)
        {
            return new SubscriptionResult([], $"Network error: {ex.Message}", null);
        }
        catch (Exception ex)
        {
            return new SubscriptionResult([], ex.Message, null);
        }
    }

    private static List<ServerProfile> ExtractNodes(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return [];

        // Plain list, or links embedded in HTML/JSON.
        var direct = ShareLinkParser.ParseMany(body);
        if (direct.Count > 0) return direct;

        // Classic base64 subscription.
        if (ShareLinkParser.LooksLikeBase64(body))
        {
            try
            {
                var decoded = ShareLinkParser.Base64Decode(body);
                var fromBase64 = ShareLinkParser.ParseMany(decoded);
                if (fromBase64.Count > 0) return fromBase64;
            }
            catch { }
        }

        // Some panels base64 each line separately.
        var perLine = new List<ServerProfile>();
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!ShareLinkParser.LooksLikeBase64(line)) continue;
            try
            {
                perLine.AddRange(ShareLinkParser.ParseMany(ShareLinkParser.Base64Decode(line)));
            }
            catch { }
        }
        return perLine;
    }

    private static IEnumerable<string> FindCandidateLinks(string body, string baseUrl)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var baseUri = Uri.TryCreate(baseUrl, UriKind.Absolute, out var b) ? b : null;

        IEnumerable<string> Candidates()
        {
            foreach (Match m in SubLinkRegex().Matches(body)) yield return m.Groups[1].Value;
            foreach (Match m in HrefRegex().Matches(body)) yield return m.Groups[1].Value;
        }

        foreach (var raw in Candidates())
        {
            var value = WebUtility.HtmlDecode(raw).Trim();
            if (value.Length == 0 || value.StartsWith('#')) continue;
            if (value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) continue;
            if (value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) continue;

            Uri? uri;
            if (Uri.TryCreate(value, UriKind.Absolute, out uri)) { }
            else if (baseUri is not null && Uri.TryCreate(baseUri, value, out uri)) { }
            else continue;

            if (uri.Scheme is not ("http" or "https")) continue;
            if (!seen.Add(uri.ToString())) continue;

            yield return uri.ToString();
        }
    }

    private static async Task<(string Body, string? Traffic)> GetAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);

        // Most panels report quota in this header: upload=…; download=…; total=…; expire=…
        string? traffic = null;
        if (response.Headers.TryGetValues("subscription-userinfo", out var values))
            traffic = FormatTraffic(string.Join("; ", values));

        return (body, traffic);
    }

    private static string? FormatTraffic(string header)
    {
        try
        {
            var parts = header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => p.Split('=', 2))
                .Where(p => p.Length == 2)
                .ToDictionary(p => p[0].Trim().ToLowerInvariant(), p => p[1].Trim());

            long Get(string key) => parts.TryGetValue(key, out var v) && long.TryParse(v, out var n) ? n : 0;

            var used = Get("upload") + Get("download");
            var total = Get("total");
            var expire = Get("expire");

            var text = total > 0
                ? $"{StatsService.FormatBytes(used)} / {StatsService.FormatBytes(total)}"
                : StatsService.FormatBytes(used);

            if (expire > 0)
                text += $" · until {DateTimeOffset.FromUnixTimeSeconds(expire).LocalDateTime:dd.MM.yyyy}";

            return text;
        }
        catch
        {
            return null;
        }
    }

    private static HttpClient CreateClient(Subscription sub, AppSettings settings)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true
        };

        if (sub.UpdateThroughProxy)
        {
            // For when the panel itself is blocked — pull the node list through the tunnel.
            handler.Proxy = new WebProxy($"http://127.0.0.1:{settings.HttpPort}");
            handler.UseProxy = true;
        }
        else
        {
            handler.UseProxy = false;
        }

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            string.IsNullOrWhiteSpace(sub.UserAgent) ? DefaultUserAgent : sub.UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");

        return client;
    }
}
