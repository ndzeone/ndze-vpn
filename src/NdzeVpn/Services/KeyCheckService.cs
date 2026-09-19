using NdzeVpn.Models;

namespace NdzeVpn.Services;

public sealed record KeyCheckResult(
    bool Ok,
    string Kind,
    string Format,
    List<ServerProfile> Nodes,
    List<string> Unsupported,
    string? Title,
    string? Traffic,
    string? Announce,
    string? Error,
    string? SourceApp,
    bool IsSubscription,
    string Target);

/// <summary>
/// "What is inside this key?" — takes anything the user can copy (a raw key, a subscription URL, a
/// Happ/INCY/Hiddify import link) and reports what it contains, without touching the saved servers.
/// </summary>
public sealed class KeyCheckService
{
    public async Task<KeyCheckResult> CheckAsync(string input, AppSettings settings, CancellationToken ct = default)
    {
        var raw = (input ?? "").Trim();
        if (raw.Length == 0)
            return Fail("Пусто — вставь ключ или ссылку", raw);

        var sourceApp = ClientLinks.SourceApp(raw);

        if (ClientLinks.IsEncryptedHapp(raw))
            return Fail("Это зашифрованная ссылка Happ (happ://crypt…). Её умеет открыть только Happ — попроси у продавца обычную ссылку подписки.", raw) with { SourceApp = sourceApp };

        var target = ClientLinks.Unwrap(raw);

        // Keys pasted directly, in any supported format.
        var direct = ConfigImporter.Parse(target);
        var directNodes = direct.Nodes.Where(n => !ConfigImporter.IsPlaceholder(n)).ToList();
        if (directNodes.Count > 0)
        {
            await MeasureAsync(directNodes, settings, ct);
            return new KeyCheckResult(true, "Ключи", direct.Format, directNodes, direct.Unsupported,
                null, null, null, null, sourceApp, false, target);
        }

        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return direct.Unsupported.Count > 0
                ? Fail($"Формат не поддерживается этим приложением: {string.Join(", ", direct.Unsupported)}", target) with { SourceApp = sourceApp }
                : Fail("Не похоже ни на ключ, ни на ссылку подписки", target) with { SourceApp = sourceApp };
        }

        var probe = new Subscription { Url = target, Name = uri.Host };
        var result = await new SubscriptionService().FetchAsync(probe, settings, ct);

        if (result.Nodes.Count == 0)
            return new KeyCheckResult(false, "Подписка", result.Format, [], result.Unsupported,
                result.Title, result.TrafficInfo, result.Announce, result.Error, sourceApp, true, target);

        await MeasureAsync(result.Nodes, settings, ct);

        return new KeyCheckResult(true, "Подписка", result.Format, result.Nodes, result.Unsupported,
            result.Title, result.TrafficInfo, result.Announce, null, sourceApp, true, target);
    }

    /// <summary>Ping what was found, so the check answers "does it work" and not just "does it parse".</summary>
    private static async Task MeasureAsync(List<ServerProfile> nodes, AppSettings settings, CancellationToken ct)
    {
        try
        {
            await LatencyService.TestAllAsync(nodes.Take(60), settings, _ => { }, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogBus.Instance.Debug("check", $"Ping during check failed: {ex.Message}");
        }
    }

    private static KeyCheckResult Fail(string error, string target) =>
        new(false, "", "", [], [], null, null, null, error, null, false, target);
}
