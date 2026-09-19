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
    private string? _runningConfig;

    public bool IsRunning => _process is { HasExited: false } && IsSingBoxAlive();

    /// <summary>The supervisor outlives a failed sing-box for a moment, so check the adapter itself.</summary>
    private static bool IsSingBoxAlive()
    {
        try { return Process.GetProcessesByName("sing-box").Length > 0; }
        catch { return true; }
    }

    /// <summary>True when the adapter is already up with exactly this configuration, so restarting
    /// it (and asking for administrator rights again) would achieve nothing.</summary>
    public bool IsRunningWith(AppSettings s) => IsRunning && _runningConfig == BuildConfig(s);

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
        // Switching node or editing routing only changes Xray's config; the adapter feeds a fixed
        // local SOCKS port, so leaving it running avoids a fresh UAC prompt on every reconnect.
        if (IsRunningWith(s))
        {
            LogBus.Instance.Debug("tun", "Adapter already running with this configuration; keeping it");
            return true;
        }

        Stop();

        if (!IsAvailable)
        {
            LogBus.Instance.Error("tun",
                "sing-box.exe or wintun.dll is missing from the core folder. Run build\\fetch-deps.ps1.");
            return false;
        }

        var config = BuildConfig(s);
        PromptDeclined = false;

        try
        {
            AppPaths.EnsureCreated();
            await File.WriteAllTextAsync(AppPaths.SingBoxConfigFile, config);

            var psi = IsElevated
                ? new ProcessStartInfo
                {
                    FileName = AppPaths.SingBoxExe,
                    WorkingDirectory = AppPaths.CoreDir,
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    Arguments = $"run -c \"{AppPaths.SingBoxConfigFile}\" -D \"{AppPaths.RuntimeDir}\""
                }
                : BuildSupervisorStart();

            if (File.Exists(StopFlagFile)) File.Delete(StopFlagFile);
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
            await Task.Delay(1800);

            if (_process.HasExited || !IsSingBoxAlive())
            {
                LogBus.Instance.Error("tun", "sing-box exited immediately. Check sing-box.log for the reason.");
                return false;
            }

            _runningConfig = config;
            LogBus.Instance.Info("tun", $"TUN adapter '{s.TunInterfaceName}' up (pid {_process.Id})");
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            LogBus.Instance.Warn("tun", "Administrator prompt was declined; TUN mode not started.");
            PromptDeclined = true;
            return false;
        }
        catch (Exception ex)
        {
            LogBus.Instance.Error("tun", "Failed to start TUN mode", ex);
            return false;
        }
    }

    /// <summary>Set when the user clicked "No" on the administrator prompt for the last attempt.</summary>
    public bool PromptDeclined { get; private set; }

    public void Stop()
    {
        StopLogTail();

        var proc = _process;
        _process = null;
        _runningConfig = null;
        if (proc is null) return;

        try
        {
            if (proc.HasExited) return;

            // An unelevated process may not terminate an elevated one, so ask the supervisor to do
            // it: it polls for this file. Asking taskkill instead would pop another UAC prompt.
            File.WriteAllText(StopFlagFile, DateTime.UtcNow.ToString("O"));
            if (proc.WaitForExit(6000))
            {
                LogBus.Instance.Info("tun", "TUN adapter removed");
                return;
            }

            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(3000);
            LogBus.Instance.Info("tun", "TUN adapter removed");
        }
        catch (Exception ex)
        {
            LogBus.Instance.Warn("tun", $"Could not stop sing-box directly ({ex.Message}); trying taskkill");
            TryTaskKill();
        }
        finally
        {
            proc.Dispose();
        }
    }

    // ------------------------------------------------------------------ elevated supervisor

    private static string StopFlagFile => Path.Combine(AppPaths.RuntimeDir, "tun-stop");
    private static string SupervisorScript => Path.Combine(AppPaths.RuntimeDir, "tun-supervisor.ps1");

    /// <summary>
    /// Runs sing-box elevated, and — still elevated — stops it again when the app asks (stop file)
    /// or when the app is gone. Without it every disconnect needs a second administrator prompt,
    /// and a crashed app would leave the adapter behind.
    /// </summary>
    private static ProcessStartInfo BuildSupervisorStart()
    {
        var script = """
            param([int]$ParentPid, [string]$Exe, [string]$WorkDir, [string]$Config, [string]$DataDir, [string]$StopFile)
            $ErrorActionPreference = 'SilentlyContinue'
            $child = Start-Process -FilePath $Exe -WorkingDirectory $WorkDir -WindowStyle Hidden -PassThru `
                -ArgumentList @('run', '-c', $Config, '-D', $DataDir)
            if (-not $child) { exit 1 }
            while ($true) {
                Start-Sleep -Milliseconds 400
                if ($child.HasExited) { break }
                if (Test-Path $StopFile) { break }
                if (-not (Get-Process -Id $ParentPid -ErrorAction SilentlyContinue)) { break }
            }
            if (-not $child.HasExited) { Stop-Process -Id $child.Id -Force }
            Remove-Item $StopFile -Force -ErrorAction SilentlyContinue
            """;

        Directory.CreateDirectory(AppPaths.RuntimeDir);
        File.WriteAllText(SupervisorScript, script);

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = true,
            Verb = "runas",
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-WindowStyle");
        psi.ArgumentList.Add("Hidden");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(SupervisorScript);
        psi.ArgumentList.Add("-ParentPid");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        psi.ArgumentList.Add("-Exe");
        psi.ArgumentList.Add(AppPaths.SingBoxExe);
        psi.ArgumentList.Add("-WorkDir");
        psi.ArgumentList.Add(AppPaths.CoreDir);
        psi.ArgumentList.Add("-Config");
        psi.ArgumentList.Add(AppPaths.SingBoxConfigFile);
        psi.ArgumentList.Add("-DataDir");
        psi.ArgumentList.Add(AppPaths.RuntimeDir);
        psi.ArgumentList.Add("-StopFile");
        psi.ArgumentList.Add(StopFlagFile);
        return psi;
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
