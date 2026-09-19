using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using NdzeVpn.Models;

namespace NdzeVpn.Services;

public sealed record SubscriptionResult(
    List<ServerProfile> Nodes,
    string? Error,
    string? TrafficInfo)
{
    /// <summary>Which body format the panel answered with ("ссылки", "Xray JSON", …).</summary>
    public string Format { get; init; } = "";

    /// <summary>Name the panel suggests for this subscription (Profile-Title header).</summary>
    public string? Title { get; init; }

    /// <summary>Message the panel wants shown to the user (Announce header).</summary>
    public string? Announce { get; init; }

    /// <summary>Nodes found but not runnable by this app's core (Hysteria, TUIC, WireGuard…).</summary>
    public List<string> Unsupported { get; init; } = [];
}

/// <summary>
/// Fetches node lists from subscription URLs.
///
/// Panels answer differently depending on who asks: the same link returns share links to one
/// client, Xray JSON to Happ, sing-box JSON to SFI and a Clash config to Clash. Panels with a
/// device limit hand out a placeholder node unless the request identifies the device. So this
/// sends the device headers, tries a series of client identities, and parses every format
/// (see <see cref="ConfigImporter"/>). Telegram bots often serve a landing page instead, so one
/// level of subscription-looking links is followed as a last resort.
/// </summary>
public sealed partial class SubscriptionService
{
    [GeneratedRegex("""href\s*=\s*["']([^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex HrefRegex();

    [GeneratedRegex("""(https?://[^\s"'<>]*(?:sub|subscribe|subscription|clash|v2ray|link|api)[^\s"'<>]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex SubLinkRegex();

    public async Task<SubscriptionResult> FetchAsync(Subscription sub, AppSettings settings, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sub.Url))
            return new SubscriptionResult([], "Ссылка подписки пуста.", null);

        var url = ClientLinks.Unwrap(sub.Url.Trim());

        // A "subscription" that is itself just a key needs no network round trip.
        var inline = ConfigImporter.Parse(url);
        if (inline.Nodes.Count > 0)
            return new SubscriptionResult(inline.Nodes, null, null) { Format = inline.Format, Unsupported = inline.Unsupported };

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return new SubscriptionResult([], "Это не ссылка на подписку и не ключ.", null);

        string? lastError = null;
        Fetched? lastFetch = null;

        try
        {
            // The first identity that yields real nodes wins. A panel that does not know us answers
            // with a placeholder node, which counts as "nothing" here.
            foreach (var agent in Agents(sub))
            {
                ct.ThrowIfCancellationRequested();

                using var http = CreateClient(sub, settings, agent);
                try
                {
                    var fetched = await GetAsync(http, url, ct);
                    lastFetch = fetched;

                    var report = ConfigImporter.Parse(fetched.Body);
                    var nodes = report.Nodes.Where(n => !ConfigImporter.IsPlaceholder(n)).ToList();

                    if (nodes.Count > 0)
                    {
                        LogBus.Instance.Info("sub",
                            $"'{sub.Name}': {nodes.Count} node(s), format {report.Format}, as {Short(agent)}");
                        return Success(nodes, report, fetched);
                    }

                    if (report.Nodes.Count > 0)
                        LogBus.Instance.Debug("sub", $"Panel answered '{Short(agent)}' with a placeholder node only");
                }
                catch (HttpRequestException ex)
                {
                    lastError = ex.Message;
                    LogBus.Instance.Debug("sub", $"As {Short(agent)}: {ex.Message}");
                }
            }

            // Landing page: follow the most subscription-looking link on it, once.
            if (lastFetch is { } page)
            {
                using var http = CreateClient(sub, settings, Agents(sub).First());
                foreach (var candidate in FindCandidateLinks(page.Body, url).Take(4))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        LogBus.Instance.Debug("sub", $"Following candidate link {candidate}");
                        var inner = await GetAsync(http, candidate, ct);
                        var report = ConfigImporter.Parse(inner.Body);
                        var nodes = report.Nodes.Where(n => !ConfigImporter.IsPlaceholder(n)).ToList();
                        if (nodes.Count > 0) return Success(nodes, report, inner with { Traffic = inner.Traffic ?? page.Traffic });
                    }
                    catch (Exception ex)
                    {
                        LogBus.Instance.Debug("sub", $"Candidate {candidate} failed: {ex.Message}");
                    }
                }
            }

            var message = lastFetch?.Body.Length > 0
                ? "Панель ответила, но ключей в ответе нет. Возможно, исчерпан лимит устройств на подписке."
                : lastError ?? "Ничего не удалось загрузить по этой ссылке.";

            return new SubscriptionResult([], message, lastFetch?.Traffic)
            {
                Title = lastFetch?.Title,
                Announce = lastFetch?.Announce
            };
        }
        catch (OperationCanceledException)
        {
            return new SubscriptionResult([], "Отменено.", null);
        }
        catch (Exception ex)
        {
            return new SubscriptionResult([], ex.Message, null);
        }
    }

    private static SubscriptionResult Success(List<ServerProfile> nodes, ImportReport report, Fetched fetched) =>
        new(nodes, null, fetched.Traffic)
        {
            Format = report.Format,
            Title = fetched.Title,
            Announce = fetched.Announce,
            Unsupported = report.Unsupported
        };

    private static IEnumerable<string> Agents(Subscription sub)
    {
        if (!string.IsNullOrWhiteSpace(sub.UserAgent)) yield return sub.UserAgent.Trim();
        foreach (var agent in ClientIdentity.UserAgents) yield return agent;
    }

    private static string Short(string userAgent) => userAgent.Split('/', ' ')[0];

    // ------------------------------------------------------------------ http

    private sealed record Fetched(string Body, string? Traffic, string? Title, string? Announce);

    private static async Task<Fetched> GetAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);

        string? Header(string name) =>
            response.Headers.TryGetValues(name, out var values) ? string.Join("; ", values) : null;

        // Most panels report quota in this header: upload=…; download=…; total=…; expire=…
        var traffic = Header("subscription-userinfo") is { } info ? FormatTraffic(info) : null;

        return new Fetched(body, traffic, DecodeHeader(Header("profile-title")), DecodeHeader(Header("announce")));
    }

    /// <summary>Panels send these either as plain text or as <c>base64:…</c>.</summary>
    private static string? DecodeHeader(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();

        if (!value.StartsWith("base64:", StringComparison.OrdinalIgnoreCase)) return value;

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value["base64:".Length..].Trim())).Trim();
        }
        catch
        {
            return null;
        }
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
                : $"{StatsService.FormatBytes(used)} · безлимит";

            if (expire > 0)
                text += $" · до {DateTimeOffset.FromUnixTimeSeconds(expire).LocalDateTime:dd.MM.yyyy}";

            return text;
        }
        catch
        {
            return null;
        }
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

    private static HttpClient CreateClient(Subscription sub, AppSettings settings, string userAgent)
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
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");

        foreach (var (name, value) in ClientIdentity.Headers(settings.DeviceId))
            client.DefaultRequestHeaders.TryAddWithoutValidation(name, value);

        return client;
    }
}
