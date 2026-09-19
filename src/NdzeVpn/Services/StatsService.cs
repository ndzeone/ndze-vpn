using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace NdzeVpn.Services;

public readonly record struct TrafficSnapshot(
    long UplinkTotal,
    long DownlinkTotal,
    double UploadBytesPerSecond,
    double DownloadBytesPerSecond,
    long ProxyUplink = 0,
    long ProxyDownlink = 0);

/// <summary>
/// Polls Xray's stats endpoint through the bundled CLI (<c>xray api statsquery</c>), which avoids
/// pulling a gRPC stack into the app just to read two counters.
/// </summary>
public sealed class StatsService : IAsyncDisposable
{
    private readonly int _apiPort;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private long _lastUp, _lastDown;
    private DateTime _lastSample = DateTime.MinValue;
    private int _failures;

    public event EventHandler<TrafficSnapshot>? Updated;

    public TrafficSnapshot Current { get; private set; }

    /// <summary>Totals since the tunnel came up, not since Xray started.</summary>
    public long SessionUplink { get; private set; }
    public long SessionDownlink { get; private set; }

    public StatsService(int apiPort) => _apiPort = apiPort;

    public void Start(TimeSpan interval)
    {
        Stop();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _lastUp = _lastDown = 0;
        _failures = 0;
        _lastSample = DateTime.MinValue;
        SessionUplink = SessionDownlink = 0;

        _loop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(token))
            {
                try { await PollAsync(token); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { LogBus.Instance.Debug("stats", ex.Message); }
            }
        }, token);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        _cts = null;
        _loop = null;
        Current = default;
    }

    private async Task PollAsync(CancellationToken ct)
    {
        // Every outbound, not just the tunnel: with smart routing most bytes leave through "direct",
        // and a speed readout that ignores them looks broken to the user.
        var json = await QueryAsync("outbound>>>", ct);
        if (json is null)
        {
            if (++_failures == 5)
                LogBus.Instance.Warn("stats", "Xray is not answering the stats API; traffic numbers stay at zero");
            return;
        }
        _failures = 0;

        long up = 0, down = 0, proxyUp = 0, proxyDown = 0;

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("stat", out var stats) && stats.ValueKind == JsonValueKind.Array)
        {
            foreach (var stat in stats.EnumerateArray())
            {
                var name = stat.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (!TryReadValue(stat, out var value)) continue;

                // outbound>>><tag>>>>traffic>>>uplink
                var parts = name.Split(">>>");
                var tag = parts.Length > 1 ? parts[1] : "";
                var isProxy = tag.Equals("proxy", StringComparison.OrdinalIgnoreCase);

                if (name.EndsWith(">>>uplink", StringComparison.Ordinal))
                {
                    up += value;
                    if (isProxy) proxyUp += value;
                }
                else if (name.EndsWith(">>>downlink", StringComparison.Ordinal))
                {
                    down += value;
                    if (isProxy) proxyDown += value;
                }
            }
        }

        var now = DateTime.UtcNow;
        double upRate = 0, downRate = 0;

        if (_lastSample != DateTime.MinValue)
        {
            var seconds = (now - _lastSample).TotalSeconds;
            if (seconds > 0.05)
            {
                // Counters reset if Xray restarted underneath us; treat a decrease as a fresh start.
                upRate = Math.Max(0, up - _lastUp) / seconds;
                downRate = Math.Max(0, down - _lastDown) / seconds;
            }
        }

        _lastUp = up;
        _lastDown = down;
        _lastSample = now;
        SessionUplink = up;
        SessionDownlink = down;

        Current = new TrafficSnapshot(up, down, upRate, downRate, proxyUp, proxyDown);
        Updated?.Invoke(this, Current);
    }

    private static bool TryReadValue(JsonElement stat, out long value)
    {
        value = 0;
        if (!stat.TryGetProperty("value", out var v)) return false;

        // The CLI emits the counter as a string; older builds emit a number.
        return v.ValueKind switch
        {
            JsonValueKind.String => long.TryParse(v.GetString(), out value),
            JsonValueKind.Number => v.TryGetInt64(out value),
            _ => false
        };
    }

    private async Task<string?> QueryAsync(string pattern, CancellationToken ct)
    {
        if (!File.Exists(AppPaths.XrayExe)) return null;

        var psi = new ProcessStartInfo
        {
            FileName = AppPaths.XrayExe,
            WorkingDirectory = AppPaths.CoreDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("api");
        psi.ArgumentList.Add("statsquery");
        psi.ArgumentList.Add($"--server=127.0.0.1:{_apiPort}");
        psi.ArgumentList.Add(pattern);

        using var proc = Process.Start(psi);
        if (proc is null) return null;

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0) return null;

        var start = stdout.IndexOf('{');
        return start < 0 ? null : stdout[start..];
    }

    public static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var i = 0;
        while (bytes >= 1024 && i < units.Length - 1) { bytes /= 1024; i++; }
        return $"{bytes:0.#} {units[i]}";
    }

    public static string FormatSpeed(double bytesPerSecond) => $"{FormatBytes(bytesPerSecond)}/s";

    public async ValueTask DisposeAsync()
    {
        Stop();
        if (_loop is not null)
        {
            try { await _loop; } catch { }
        }
    }
}
