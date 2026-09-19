using Microsoft.Win32;

namespace NdzeVpn.Services;

/// <summary>
/// Who this app claims to be when it asks a panel for a node list.
///
/// Panels built on Remnawave (and Marzban with device limits) only hand out the real configs to a
/// client that identifies its device: without an <c>x-hwid</c> header they answer with a single
/// placeholder node called "App not supported". Happ, INCY, v2RayTun and Hiddify all send these
/// headers, so we send them too — one stable id per computer, which counts as one device slot.
/// </summary>
public static class ClientIdentity
{
    /// <summary>User-Agents to try, in order, when a panel gives nothing useful. Panels map these to
    /// a response format: share links, Xray JSON (Happ), sing-box JSON (SFI/Karing), Clash YAML.</summary>
    public static readonly string[] UserAgents =
    [
        "v2rayNG/1.9.16",
        "Happ/1.14.0 (com.happproxy; build:1; Windows NT 10.0)",
        "Streisand/1.6.0",
        "SFI/1.9.0",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36"
    ];

    private static string? _cached;

    /// <summary>
    /// Stable per-machine id. Derived from the Windows machine GUID, so it survives reinstalls of
    /// the app and does not eat a second device slot on the subscription.
    /// </summary>
    public static string DeviceId(string? overrideValue = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideValue)) return overrideValue.Trim();
        if (_cached is not null) return _cached;

        string id;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            id = key?.GetValue("MachineGuid") as string ?? "";
        }
        catch
        {
            id = "";
        }

        if (string.IsNullOrWhiteSpace(id))
            id = Environment.MachineName + "-" + Environment.UserName;

        return _cached = "ndze-" + id.Trim().ToLowerInvariant();
    }

    /// <summary>Headers every subscription request carries, alongside the User-Agent.</summary>
    public static IEnumerable<(string Name, string Value)> Headers(string? deviceIdOverride = null)
    {
        yield return ("x-hwid", DeviceId(deviceIdOverride));
        yield return ("x-device-os", "Windows");
        yield return ("x-ver-os", Environment.OSVersion.Version.ToString());
        yield return ("x-device-model", Environment.MachineName);
    }
}
