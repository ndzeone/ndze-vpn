using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NdzeVpn.Models;

namespace NdzeVpn.Services;

/// <summary>How the tunnel looks from here right now.</summary>
public enum TunnelHealth
{
    /// <summary>A request went through the tunnel and came back.</summary>
    Ok,

    /// <summary>The tunnel failed, but the machine has no internet either — not the node's fault.</summary>
    NoInternet,

    /// <summary>The machine is online, the tunnel is not carrying traffic.</summary>
    TunnelDown
}

/// <summary>
/// Node latency. Three measurements, deliberately kept apart: a TCP handshake to the node (cheap,
/// works on every node at once), a real HTTP round trip through the live tunnel (honest, but only
/// for the node you are connected to), and a health check that tells a dead node apart from dead Wi-Fi.
/// </summary>
public sealed class LatencyService
{
    /// <summary>Through-tunnel probes; the first one that answers wins.</summary>
    private static readonly string[] TunnelProbes =
    [
        "https://www.gstatic.com/generate_204",
        "https://cloudflare.com/cdn-cgi/trace",
        "https://www.google.com/generate_204"
    ];

    /// <summary>Reachable from Russia without a VPN: tells "node is dead" from "internet is dead".</summary>
    private static readonly string[] InternetProbes =
    [
        "https://ya.ru/",
        "https://dzen.ru/",
        "https://mail.ru/"
    ];

    /// <summary>
    /// TCP handshake time to <c>address:port</c>, best of <paramref name="probes"/> attempts.
    /// -1 means unreachable. The first attempt pays for DNS, so best-of lands much closer to the
    /// number other tools report than a single measurement does.
    /// </summary>
    public static async Task<int> TcpPingAsync(string address, int port, int timeoutMs, int probes = 3, CancellationToken ct = default)
    {
        // Resolve once: repeating the lookup would measure the DNS server, not the node.
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(address, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(address, ct).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct);
        }
        catch
        {
            return -1;
        }

        if (addresses.Length == 0) return -1;

        var best = -1;
        for (var i = 0; i < Math.Max(1, probes); i++)
        {
            var ms = await SingleTcpPingAsync(addresses[0], port, timeoutMs, ct);
            if (ms < 0)
            {
                // A node that refuses the very first connection is down; no point probing again.
                if (i == 0) return -1;
                continue;
            }
            if (best < 0 || ms < best) best = ms;
            if (ct.IsCancellationRequested) break;
        }
        return best;
    }

    private static async Task<int> SingleTcpPingAsync(IPAddress address, int port, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);

            var sw = Stopwatch.StartNew();
            await socket.ConnectAsync(new IPEndPoint(address, port), timeout.Token);
            sw.Stop();

            // Floor at 1 ms: a sub-millisecond handshake to a nearby CDN reads as "0 ms", which
            // looks like a failed measurement rather than a very good one.
            return socket.Connected ? Math.Max(1, (int)Math.Round(sw.Elapsed.TotalMilliseconds)) : -1;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Ping many nodes at once. <paramref name="onResult"/> fires per node so the UI fills the list
    /// in as results land instead of waiting for the slowest node.
    /// </summary>
    public static async Task TestAllAsync(
        IEnumerable<ServerProfile> profiles,
        AppSettings settings,
        Action<ServerProfile> onResult,
        CancellationToken ct = default)
    {
        using var gate = new SemaphoreSlim(Math.Max(1, settings.LatencyParallelism));

        var tasks = profiles.Select(async profile =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var ms = await TcpPingAsync(profile.Address, profile.Port, settings.LatencyTimeoutMs, settings.LatencyProbes, ct);
                profile.LatencyMs = ms;
                profile.LastTested = DateTime.Now;
                onResult(profile);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogBus.Instance.Debug("ping", $"{profile.DisplayName}: {ex.Message}");
                profile.LatencyMs = -1;
                onResult(profile);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    /// <summary>An HttpClient whose traffic goes through the app's own local proxy.</summary>
    public static HttpClient CreateProxiedClient(AppSettings settings, TimeSpan timeout, bool throughProxy = true)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = throughProxy,
            Proxy = throughProxy ? new WebProxy($"http://127.0.0.1:{settings.HttpPort}") : null,
            UseCookies = false,
            AllowAutoRedirect = false,
            // Keep-alive matters: a fresh TCP + TLS handshake per probe would multiply the numbers.
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = timeout,
            MaxConnectionsPerServer = 16
        };

        var http = new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) NdzeVpn");
        return http;
    }

    /// <summary>
    /// End-to-end latency through the running tunnel — the number that reflects how the connection
    /// actually feels. A warm-up request pays for the handshakes, then the best of three round trips
    /// on the live connection is reported, so this is RTT rather than connect time. -1 on failure.
    /// </summary>
    public static async Task<int> RealDelayAsync(AppSettings settings, CancellationToken ct = default)
    {
        foreach (var url in Probes(settings))
        {
            var ms = await MeasureAsync(settings, url, ct);
            if (ms >= 0) return ms;
            if (ct.IsCancellationRequested) break;
        }
        return -1;
    }

    private static IEnumerable<string> Probes(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.LatencyTestUrl)) yield return settings.LatencyTestUrl.Trim();
        foreach (var p in TunnelProbes) yield return p;
    }

    private static async Task<int> MeasureAsync(AppSettings settings, string url, CancellationToken ct)
    {
        try
        {
            using var http = CreateProxiedClient(settings, TimeSpan.FromMilliseconds(settings.LatencyTimeoutMs));

            // Warm-up: pays for the proxy handshake, TLS and the node's own connect.
            using (var warm = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if ((int)warm.StatusCode >= 500) return -1;
            }

            var best = int.MaxValue;
            for (var i = 0; i < 3; i++)
            {
                var sw = Stopwatch.StartNew();
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                await response.Content.CopyToAsync(Stream.Null, ct);
                sw.Stop();

                best = Math.Min(best, Math.Max(1, (int)Math.Round(sw.Elapsed.TotalMilliseconds)));
            }

            return best == int.MaxValue ? -1 : best;
        }
        catch (Exception ex)
        {
            LogBus.Instance.Debug("ping", $"Real delay via {url} failed: {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// Is the tunnel carrying traffic — and if not, is that because the machine is offline?
    /// Auto-failover uses this so a dropped Wi-Fi is never blamed on the node.
    /// </summary>
    public static async Task<TunnelHealth> CheckHealthAsync(AppSettings settings, CancellationToken ct = default)
    {
        if (await RealDelayAsync(settings, ct) >= 0) return TunnelHealth.Ok;
        return await IsInternetUpAsync(settings, ct) ? TunnelHealth.TunnelDown : TunnelHealth.NoInternet;
    }

    /// <summary>Does anything answer at all? Russian hosts, so they stay reachable without the VPN.</summary>
    public static async Task<bool> IsInternetUpAsync(AppSettings settings, CancellationToken ct = default)
    {
        foreach (var url in InternetProbes)
        {
            try
            {
                using var http = CreateProxiedClient(settings, TimeSpan.FromSeconds(6), throughProxy: false);
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return true; }
            catch { }
        }
        return false;
    }

    /// <summary>
    /// What the outside world sees as your IP right now. Used by the UI to prove the split tunnel
    /// is doing its job — the proxied answer and the direct answer should differ.
    /// </summary>
    public static async Task<string?> DetectExternalIpAsync(AppSettings settings, bool throughProxy, CancellationToken ct = default)
    {
        string[] services = ["https://api.ipify.org", "https://ifconfig.me/ip", "https://icanhazip.com"];

        foreach (var service in services)
        {
            try
            {
                using var http = CreateProxiedClient(settings, TimeSpan.FromSeconds(8), throughProxy);
                var text = await http.GetStringAsync(service, ct);
                var ip = text.Trim();
                if (IPAddress.TryParse(ip, out _)) return ip;
            }
            catch { }
        }
        return null;
    }
}
