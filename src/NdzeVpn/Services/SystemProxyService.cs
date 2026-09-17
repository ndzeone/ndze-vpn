using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NdzeVpn.Services;

/// <summary>
/// Points Windows' per-user WinINET proxy at the local Xray listener, and puts it back the way it
/// was on disconnect. No admin rights needed — this is HKCU only.
/// </summary>
public sealed partial class SystemProxyService
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    private const int INTERNET_OPTION_REFRESH = 37;

    [LibraryImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    private sealed record ProxyState(int Enable, string? Server, string? Override, string? AutoConfigUrl);

    private ProxyState? _saved;

    public bool IsApplied { get; private set; }

    /// <summary>
    /// Route WinINET through <c>127.0.0.1:port</c>. Everything smart happens inside Xray, so the
    /// bypass list here is only the things that must never take the round trip through a proxy at all.
    /// </summary>
    public bool Apply(int httpPort, IEnumerable<string>? extraBypass = null)
    {
        try
        {
            _saved ??= Capture();

            var bypass = new List<string> { "localhost", "127.*", "10.*", "172.16.*", "172.17.*",
                "172.18.*", "172.19.*", "172.20.*", "172.21.*", "172.22.*", "172.23.*", "172.24.*",
                "172.25.*", "172.26.*", "172.27.*", "172.28.*", "172.29.*", "172.30.*", "172.31.*",
                "192.168.*", "<local>" };

            if (extraBypass is not null) bypass.AddRange(extraBypass);

            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true)
                            ?? throw new InvalidOperationException("Internet Settings key is missing.");

            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            key.SetValue("ProxyServer", $"127.0.0.1:{httpPort}", RegistryValueKind.String);
            key.SetValue("ProxyOverride", string.Join(';', bypass.Distinct()), RegistryValueKind.String);

            // A leftover PAC url silently wins over ProxyServer, so clear it.
            if (key.GetValue("AutoConfigURL") is not null) key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);

            Notify();
            IsApplied = true;
            LogBus.Instance.Info("proxy", $"System proxy set to 127.0.0.1:{httpPort}");
            return true;
        }
        catch (Exception ex)
        {
            LogBus.Instance.Error("proxy", "Failed to set system proxy", ex);
            return false;
        }
    }

    /// <summary>Restore whatever the proxy settings were before we touched them.</summary>
    public void Clear()
    {
        if (!IsApplied && _saved is null) return;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true);
            if (key is null) return;

            if (_saved is { } state)
            {
                key.SetValue("ProxyEnable", state.Enable, RegistryValueKind.DWord);

                if (state.Server is not null) key.SetValue("ProxyServer", state.Server, RegistryValueKind.String);
                else key.DeleteValue("ProxyServer", throwOnMissingValue: false);

                if (state.Override is not null) key.SetValue("ProxyOverride", state.Override, RegistryValueKind.String);
                else key.DeleteValue("ProxyOverride", throwOnMissingValue: false);

                if (state.AutoConfigUrl is not null) key.SetValue("AutoConfigURL", state.AutoConfigUrl, RegistryValueKind.String);
                else key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
            }
            else
            {
                key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                key.DeleteValue("ProxyServer", throwOnMissingValue: false);
            }

            Notify();
            LogBus.Instance.Info("proxy", "System proxy restored");
        }
        catch (Exception ex)
        {
            LogBus.Instance.Error("proxy", "Failed to restore system proxy", ex);
        }
        finally
        {
            IsApplied = false;
            _saved = null;
        }
    }

    /// <summary>True if WinINET is currently pointed at our own listener.</summary>
    public static bool IsPointedAt(int httpPort)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            if (key?.GetValue("ProxyEnable") is not int enable || enable == 0) return false;
            return key.GetValue("ProxyServer") as string == $"127.0.0.1:{httpPort}";
        }
        catch { return false; }
    }

    private static ProxyState Capture()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        return new ProxyState(
            key?.GetValue("ProxyEnable") as int? ?? 0,
            key?.GetValue("ProxyServer") as string,
            key?.GetValue("ProxyOverride") as string,
            key?.GetValue("AutoConfigURL") as string);
    }

    /// <summary>Tell running apps to re-read the settings instead of waiting for a restart.</summary>
    private static void Notify()
    {
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
    }
}
