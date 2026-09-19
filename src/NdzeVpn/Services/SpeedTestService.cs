using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using NdzeVpn.Models;

namespace NdzeVpn.Services;

public sealed record SpeedTestResult(double DownMbps, double UpMbps, int PingMs, string? Error)
{
    public bool Ok => Error is null && DownMbps > 0;
}

public sealed record SpeedTestProgress(string Stage, double Mbps, double Fraction);

/// <summary>
/// A real throughput measurement, not a guess derived from the traffic counters: several parallel
/// streams to a public speed-test endpoint, timed over a window that starts after TCP slow-start has
/// settled. Runs through the app's own local proxy, so the number is what the tunnel actually gives.
/// </summary>
public sealed class SpeedTestService
{
    /// <summary>Download sources, tried in order. Each must serve a large, uncompressed body.</summary>
    private static readonly (string Name, string Url)[] DownloadSources =
    [
        ("Cloudflare", "https://speed.cloudflare.com/__down?bytes=104857600"),
        ("Hetzner", "https://speed.hetzner.de/100MB.bin"),
        ("OVH", "https://proof.ovh.net/files/100Mb.dat")
    ];

    private const string UploadUrl = "https://speed.cloudflare.com/__up";

    /// <summary>Ignore the first moments: TCP slow-start and the proxy handshake are not throughput.</summary>
    private static readonly TimeSpan RampUp = TimeSpan.FromMilliseconds(1200);

    public async Task<SpeedTestResult> RunAsync(
        AppSettings settings,
        bool throughProxy,
        IProgress<SpeedTestProgress>? progress,
        CancellationToken ct = default)
    {
        try
        {
            progress?.Report(new SpeedTestProgress("Задержка", 0, 0));
            var ping = throughProxy
                ? await LatencyService.RealDelayAsync(settings, ct)
                : -1;

            var seconds = Math.Clamp(settings.SpeedTestSeconds, 3, 30);

            progress?.Report(new SpeedTestProgress("Загрузка", 0, 0));
            var down = await MeasureDownloadAsync(settings, throughProxy, seconds, progress, ct);

            var up = 0d;
            if (settings.SpeedTestUpload && down > 0)
            {
                progress?.Report(new SpeedTestProgress("Отдача", 0, 0));
                up = await MeasureUploadAsync(settings, throughProxy, Math.Max(3, seconds - 2), progress, ct);
            }

            if (down <= 0)
                return new SpeedTestResult(0, 0, ping, "Ни один сервер замера не ответил");

            LogBus.Instance.Info("speed", $"Speed test: down {down:0.0} Mbit/s, up {up:0.0} Mbit/s, ping {ping} ms");
            return new SpeedTestResult(down, up, ping, null);
        }
        catch (OperationCanceledException)
        {
            return new SpeedTestResult(0, 0, -1, "Замер отменён");
        }
        catch (Exception ex)
        {
            LogBus.Instance.Error("speed", "Speed test failed", ex);
            return new SpeedTestResult(0, 0, -1, ex.Message);
        }
    }

    // ------------------------------------------------------------------ download

    private static async Task<double> MeasureDownloadAsync(
        AppSettings settings, bool throughProxy, int seconds, IProgress<SpeedTestProgress>? progress, CancellationToken ct)
    {
        foreach (var (name, url) in DownloadSources)
        {
            var mbps = await StreamDownloadAsync(settings, throughProxy, url, seconds, progress, ct);
            if (mbps > 0) return mbps;
            LogBus.Instance.Debug("speed", $"{name} did not answer; trying the next source");
            ct.ThrowIfCancellationRequested();
        }
        return 0;
    }

    private static async Task<double> StreamDownloadAsync(
        AppSettings settings, bool throughProxy, string url, int seconds, IProgress<SpeedTestProgress>? progress, CancellationToken ct)
    {
        var streams = Math.Clamp(settings.SpeedTestStreams, 1, 16);

        using var http = LatencyService.CreateProxiedClient(settings, TimeSpan.FromSeconds(seconds + 20), throughProxy);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds + RampUp.TotalSeconds + 5));

        long counted = 0;
        var started = Stopwatch.StartNew();
        var windowStart = TimeSpan.Zero;
        var measuring = false;
        var stop = false;

        // A reporting loop keeps the UI live while the streams run.
        var reporter = Task.Run(async () =>
        {
            while (!stop && !deadline.IsCancellationRequested)
            {
                await Task.Delay(250, CancellationToken.None);
                if (!measuring) continue;

                var elapsed = started.Elapsed - windowStart;
                if (elapsed.TotalSeconds <= 0.2) continue;

                var mbps = Interlocked.Read(ref counted) * 8 / 1e6 / elapsed.TotalSeconds;
                progress?.Report(new SpeedTestProgress("Загрузка", mbps, Math.Min(1, elapsed.TotalSeconds / seconds)));
            }
        }, CancellationToken.None);

        async Task PumpAsync(int index)
        {
            var buffer = new byte[128 * 1024];
            try
            {
                // Stagger the streams: several identical requests landing in the same millisecond
                // look like abuse and earn a 429 from the public endpoints.
                if (index > 0) await Task.Delay(index * 150, deadline.Token);

                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                response.EnsureSuccessStatusCode();
                await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);

                int read;
                while ((read = await body.ReadAsync(buffer, deadline.Token)) > 0)
                {
                    if (!measuring)
                    {
                        // Still ramping up: bytes before the window do not count.
                        if (started.Elapsed < RampUp) continue;
                        lock (buffer)
                        {
                            if (!measuring)
                            {
                                windowStart = started.Elapsed;
                                Interlocked.Exchange(ref counted, 0);
                                measuring = true;
                            }
                        }
                    }

                    Interlocked.Add(ref counted, read);
                    if (started.Elapsed - windowStart >= TimeSpan.FromSeconds(seconds)) break;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogBus.Instance.Debug("speed", $"download stream: {ex.Message}");
            }
        }

        await Task.WhenAll(Enumerable.Range(0, streams).Select(PumpAsync));
        stop = true;
        await reporter;

        if (!measuring) return 0;

        var window = started.Elapsed - windowStart;
        var bytes = Interlocked.Read(ref counted);
        if (window.TotalSeconds < 0.5 || bytes < 256 * 1024) return 0;

        return bytes * 8 / 1e6 / window.TotalSeconds;
    }

    // ------------------------------------------------------------------ upload

    /// <summary>
    /// Fixed-size POSTs repeated until the window closes. A chunked body of unknown length looks
    /// like an attack to some endpoints and gets dropped without an error, which measured as zero.
    /// </summary>
    private static async Task<double> MeasureUploadAsync(
        AppSettings settings, bool throughProxy, int seconds, IProgress<SpeedTestProgress>? progress, CancellationToken ct)
    {
        const int chunkSize = 4 * 1024 * 1024;
        var streams = Math.Clamp(settings.SpeedTestStreams, 1, 8);

        var payload = new byte[chunkSize];
        Random.Shared.NextBytes(payload);

        using var http = LatencyService.CreateProxiedClient(settings, TimeSpan.FromSeconds(seconds + 20), throughProxy);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds + 15));

        long counted = 0;
        var started = Stopwatch.StartNew();
        var window = TimeSpan.FromSeconds(seconds);

        async Task PushAsync()
        {
            while (!deadline.IsCancellationRequested && started.Elapsed < window)
            {
                try
                {
                    var content = new ByteArrayContent(payload);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                    using var response = await http.PostAsync(UploadUrl, content, deadline.Token);
                    if (!response.IsSuccessStatusCode)
                    {
                        LogBus.Instance.Debug("speed", $"upload rejected: {(int)response.StatusCode}");
                        return;
                    }

                    Interlocked.Add(ref counted, chunkSize);

                    var elapsed = started.Elapsed;
                    if (elapsed.TotalSeconds > 0.2)
                    {
                        var live = Interlocked.Read(ref counted) * 8 / 1e6 / elapsed.TotalSeconds;
                        progress?.Report(new SpeedTestProgress("Отдача", live, Math.Min(1, elapsed.TotalSeconds / seconds)));
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    LogBus.Instance.Debug("speed", $"upload stream: {ex.Message}");
                    return;
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, streams).Select(_ => PushAsync()));

        var elapsedTotal = started.Elapsed;
        var bytes = Interlocked.Read(ref counted);
        if (elapsedTotal.TotalSeconds < 0.5 || bytes < chunkSize) return 0;

        return bytes * 8 / 1e6 / elapsedTotal.TotalSeconds;
    }

    public static string FormatMbps(double mbps) => mbps <= 0 ? "—" : $"{mbps:0.#} Мбит/с";
}
