using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using NdzeVpn.Models;

namespace NdzeVpn.Services;

/// <summary>
/// TUN mode. A wintun virtual adapter (driven by sing-box) swallows all traffic and hands it to
/// Xray's SOCKS listener, so the smart routing rules apply to games, Discord voice and anything
/// else that ignores the system proxy.
///
/// sing-box needs administrator rights to create the adapter, so it is launched with the
/// <c>runas</c> verb: the UI itself stays unelevated.
/// </summary>
public sealed class TunService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private Process? _process;
    private FileSystemWatcher? _logWatcher;
    private long _logOffset;

    public bool IsRunning => _process is { HasExited: false };

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    public static bool IsAvailable => File.Exists(AppPaths.SingBoxExe) && File.Exists(AppPaths.WintunDll);

    public string BuildConfig(AppSettings s)
    {
        var singBoxLog = Path.Combine(AppPaths.LogDir, "sing-box.log");

        // Written for sing-box 1.13+ rule actions (the legacy inbound "sniff" fields and the
        // "block" outbound no longer exist there). build\fetch-deps.ps1 pins the matching version.
        var routeRules = new List<object>
        {
            // Never loop our own traffic back through the adapter.
            new Dictionary<string, object?>
            {
                ["process_name"] = s.TunBypassProcesses.ToArray(),
                ["action"] = "route",
                ["outbound"] = "direct"
            },
            new Dictionary<string, object?>
            {
                ["ip_cidr"] = new[] { "127.0.0.0/8", "::1/128" },
                ["action"] = "route",
                ["outbound"] = "direct"
            },
            // Recover hostnames so Xray's domain rules match traffic that arrives as bare IPs.
            new Dictionary<string, object?> { ["action"] = "sniff" }
        };

        // Xray has no process matcher, so per-application rules are enforced here instead.
        foreach (var rule in s.CustomRules.Where(r => r.Enabled && r.Kind == RuleKind.Process))
        {
            var names = rule.Values.ToArray();
            if (names.Length == 0) continue;

            routeRules.Add(rule.Action == RuleAction.Block
                ? new Dictionary<string, object?> { ["process_name"] = names, ["action"] = "reject" }
                : new Dictionary<string, object?>
                {
                    ["process_name"] = names,
                    ["action"] = "route",
                    ["outbound"] = rule.Action == RuleAction.Direct ? "direct" : "proxy-out"
                });
        }

        var config = new Dictionary<string, object?>
        {
            ["log"] = new Dictionary<string, object?>
            {
                ["level"] = s.XrayLogLevel == "debug" ? "debug" : "warn",
                ["output"] = singBoxLog,
                ["timestamp"] = true
            },
            ["inbounds"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "tun",
                    ["tag"] = "tun-in",
                    ["interface_name"] = s.TunInterfaceName,
                    ["address"] = new[] { s.TunAddress },
                    ["mtu"] = s.TunMtu,
                    ["auto_route"] = true,
                    ["strict_route"] = s.TunStrictRoute,
                    // gvisor is slower than the system stack but survives odd NIC drivers;
                    // it is the safer default for a consumer machine.
                    ["stack"] = "gvisor"
                }
            },
            ["outbounds"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "socks",
                    ["tag"] = "proxy-out",
                    ["server"] = "127.0.0.1",
                    ["server_port"] = s.SocksPort,
                    ["version"] = "5",
                    ["udp_over_tcp"] = false
                },
                new Dictionary<string, object?> { ["type"] = "direct", ["tag"] = "direct" }
            },
            ["route"] = new Dictionary<string, object?>
            {
                ["auto_detect_interface"] = true,
                ["rules"] = routeRules,
                ["final"] = "proxy-out"
            }
        };

        return JsonSerializer.Serialize(config, JsonOptions);
    }

    public async Task<bool> StartAsync(AppSettings s)
    {
        Stop();

        if (!IsAvailable)
        {
            LogBus.Instance.Error("tun",
                "sing-box.exe or wintun.dll is missing from the core folder. Run build\\fetch-deps.ps1.");
            return false;
        }

        try
        {
            AppPaths.EnsureCreated();
            await File.WriteAllTextAsync(AppPaths.SingBoxConfigFile, BuildConfig(s));

            var psi = new ProcessStartInfo
            {
                FileName = AppPaths.SingBoxExe,
                WorkingDirectory = AppPaths.CoreDir,
                // Elevation is required for the adapter; that forces UseShellExecute, which in turn
                // means no stdout redirect — sing-box logs to a file and we tail it.
                UseShellExecute = true,
                Verb = IsElevated ? "" : "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"run -c \"{AppPaths.SingBoxConfigFile}\" -D \"{AppPaths.RuntimeDir}\""
            };

            _process = Process.Start(psi);
            if (_process is null)
            {
                LogBus.Instance.Error("tun", "Failed to start sing-box");
                return false;
            }

            _process.EnableRaisingEvents = true;
            _process.Exited += (_, _) => LogBus.Instance.Warn("tun", "sing-box exited");

            StartLogTail();

            // Give the adapter a moment to come up before the caller reports success.
            await Task.Delay(1200);

            if (_process.HasExited)
            {
                LogBus.Instance.Error("tun", $"sing-box exited immediately (code {_process.ExitCode}). Check sing-box.log.");
                return false;
            }

            LogBus.Instance.Info("tun", $"TUN adapter '{s.TunInterfaceName}' up (pid {_process.Id})");
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            LogBus.Instance.Warn("tun", "Administrator prompt was declined; TUN mode not started.");
            return false;
        }
        catch (Exception ex)
        {
            LogBus.Instance.Error("tun", "Failed to start TUN mode", ex);
            return false;
        }
    }

    public void Stop()
    {
        StopLogTail();

        var proc = _process;
        _process = null;
        if (proc is null) return;

        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(5000);
                LogBus.Instance.Info("tun", "TUN adapter removed");
            }
        }
        catch (Exception ex)
        {
            // An elevated child cannot always be killed from an unelevated parent.
            LogBus.Instance.Warn("tun", $"Could not stop sing-box directly ({ex.Message}); trying taskkill");
            TryTaskKill();
        }
        finally
        {
            proc.Dispose();
        }
    }

    private static void TryTaskKill()
    {
        try
        {
            var psi = new ProcessStartInfo("taskkill.exe", "/F /T /IM sing-box.exe")
            {
                UseShellExecute = true,
                Verb = IsElevated ? "" : "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi)?.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            LogBus.Instance.Error("tun", "taskkill failed", ex);
        }
    }

    // ------------------------------------------------------------------ log tail

    private void StartLogTail()
    {
        var path = Path.Combine(AppPaths.LogDir, "sing-box.log");
        try
        {
            if (!File.Exists(path)) File.WriteAllText(path, "");
            _logOffset = new FileInfo(path).Length;

            _logWatcher = new FileSystemWatcher(AppPaths.LogDir, "sing-box.log")
            {
                NotifyFilter = NotifyFilters.Size | NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };
            _logWatcher.Changed += (_, _) => DrainLog(path);
        }
        catch (Exception ex)
        {
            LogBus.Instance.Warn("tun", $"Log tail unavailable: {ex.Message}");
        }
    }

    private void DrainLog(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < _logOffset) _logOffset = 0; // rotated
            fs.Seek(_logOffset, SeekOrigin.Begin);

            using var reader = new StreamReader(fs);
            while (reader.ReadLine() is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line)) LogBus.Instance.Info("sing-box", line);
            }
            _logOffset = fs.Position;
        }
        catch { }
    }

    private void StopLogTail()
    {
        _logWatcher?.Dispose();
        _logWatcher = null;
    }

    public void Dispose() => Stop();
}
