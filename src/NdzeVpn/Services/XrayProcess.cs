using System.Diagnostics;
using System.IO;
using System.Text;

namespace NdzeVpn.Services;

/// <summary>Owns the xray.exe child process: config validation, start, stop, log relay.</summary>
public sealed class XrayProcess : IDisposable
{
    private Process? _process;
    private readonly object _lock = new();

    public bool IsRunning
    {
        get
        {
            lock (_lock) return _process is { HasExited: false };
        }
    }

    public event EventHandler<int>? Exited;

    /// <summary>Ask Xray to parse a config without running it. Returns stderr/stdout on failure.</summary>
    public static async Task<(bool Ok, string Output)> TestConfigAsync(string configPath, CancellationToken ct = default)
    {
        if (!File.Exists(AppPaths.XrayExe))
            return (false, $"xray.exe not found at {AppPaths.XrayExe}. Run build\\fetch-deps.ps1.");

        var psi = new ProcessStartInfo
        {
            FileName = AppPaths.XrayExe,
            WorkingDirectory = AppPaths.CoreDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("-test");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(configPath);
        ApplyAssetLocation(psi);

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return (false, "Failed to start xray.exe");

            var stdout = proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            var output = ((await stdout) + Environment.NewLine + (await stderr)).Trim();
            return (proc.ExitCode == 0, output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public bool Start(string configPath)
    {
        Stop();

        if (!File.Exists(AppPaths.XrayExe))
        {
            LogBus.Instance.Error("xray", $"xray.exe not found at {AppPaths.XrayExe}");
            return false;
        }

        var psi = new ProcessStartInfo
        {
            FileName = AppPaths.XrayExe,
            WorkingDirectory = AppPaths.CoreDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(configPath);
        ApplyAssetLocation(psi);

        try
        {
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

            proc.OutputDataReceived += (_, e) => Relay(e.Data);
            proc.ErrorDataReceived += (_, e) => Relay(e.Data);
            proc.Exited += (_, _) =>
            {
                var code = 0;
                try { code = proc.ExitCode; } catch { }
                LogBus.Instance.Warn("xray", $"Process exited with code {code}");
                Exited?.Invoke(this, code);
            };

            if (!proc.Start())
            {
                LogBus.Instance.Error("xray", "Process.Start returned false");
                return false;
            }

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            lock (_lock) _process = proc;

            LogBus.Instance.Info("xray", $"Started (pid {proc.Id})");
            return true;
        }
        catch (Exception ex)
        {
            LogBus.Instance.Error("xray", "Failed to start", ex);
            return false;
        }
    }

    public void Stop()
    {
        Process? proc;
        lock (_lock)
        {
            proc = _process;
            _process = null;
        }

        if (proc is null) return;

        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(4000);
                LogBus.Instance.Info("xray", "Stopped");
            }
        }
        catch (Exception ex)
        {
            LogBus.Instance.Warn("xray", $"Stop failed: {ex.Message}");
        }
        finally
        {
            proc.Dispose();
        }
    }

    private static void ApplyAssetLocation(ProcessStartInfo psi)
    {
        // Xray looks for geoip.dat/geosite.dat next to the binary unless told otherwise; ours live in
        // the writable data dir so updates work from a Program Files install.
        AppPaths.EnsureCreated();
        psi.Environment["XRAY_LOCATION_ASSET"] = AppPaths.GeoDir;
        psi.Environment["V2RAY_LOCATION_ASSET"] = AppPaths.GeoDir;
    }

    private static void Relay(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        var level = line.Contains("[Error]", StringComparison.OrdinalIgnoreCase) ? LogLevel.Error
            : line.Contains("[Warning]", StringComparison.OrdinalIgnoreCase) ? LogLevel.Warn
            : line.Contains("[Debug]", StringComparison.OrdinalIgnoreCase) ? LogLevel.Debug
            : LogLevel.Info;

        LogBus.Instance.Write(level, "xray", line);
    }

    public void Dispose() => Stop();
}
