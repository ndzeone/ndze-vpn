using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NdzeVpn.Models;

namespace NdzeVpn.Services;

/// <summary>
/// Node latency. Two different measurements, deliberately kept apart:
/// a TCP handshake to the node (cheap, works on every node at once) and a real HTTP round trip
/// through the live tunnel (honest, but only possible for the node you are connected to).
/// </summary>
public sealed class LatencyService
{
    /// <summary>TCP handshake time to <c>address:port</c>. -1 means unreachable.</summary>
    public static async Task<int> TcpPingAsync(string address, int port, int timeoutMs, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient { NoDelay = true };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);

            await client.ConnectAsync(address, port, timeout.Token);
            sw.Stop();
            return client.Connected ? (int)sw.ElapsedMilliseconds : -1;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Ping many nodes at once. <paramref name="onResult"/> fires per node so the UI can fill the
    /// list in as results land instead of waiting for the slowest node.
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
                var ms = await TcpPingAsync(profile.Address, profile.Port, settings.LatencyTimeoutMs, ct);
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

    /// <summary>
    /// End-to-end latency through the running tunnel: the number that actually reflects how the
    /// connection feels. Returns -1 if the request failed.
    /// </summary>
    public static async Task<int> RealDelayAsync(AppSettings settings, CancellationToken ct = default)
    {
        try
        {
            var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{settings.HttpPort}"),
                UseProxy = true,
                UseCookies = false,
                AllowAutoRedirect = false
            };

            using var http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMilliseconds(settings.LatencyTimeoutMs)
            };
            http.DefaultRequestHeaders.ConnectionClose = true;

            var sw = Stopwatch.StartNew();
            using var response = await http.GetAsync(settings.LatencyTestUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            sw.Stop();

            return (int)sw.ElapsedMilliseconds;
        }
        catch (Exception ex)
        {
            LogBus.Instance.Debug("ping", $"Real delay failed: {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// What the outside world sees as your IP right now. Used by the UI to prove the split tunnel
    /// is doing its job — the proxied answer and the direct answer should differ.
    /// </summary>
    public static async Task<string?> DetectExternalIpAsync(AppSettings settings, bool throughProxy, CancellationToken ct = default)
    {
        try
        {
            var handler = new HttpClientHandler { UseProxy = throughProxy, AllowAutoRedirect = false };
            if (throughProxy) handler.Proxy = new WebProxy($"http://127.0.0.1:{settings.HttpPort}");

            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            var text = await http.GetStringAsync("https://api.ipify.org", ct);
            return text.Trim();
        }
        catch
        {
            return null;
        }
    }
}
