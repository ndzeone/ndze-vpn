using System.IO;

namespace NdzeVpn.Services;

/// <summary>
/// Every path the app touches. User data lives in %APPDATA% so the install directory can stay
/// read-only (and so an uninstall can leave profiles behind if the user wants).
/// </summary>
public static class AppPaths
{
    public static string InstallDir { get; } =
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    /// <summary>Bundled sidecars: xray.exe, sing-box.exe, wintun.dll, geo assets.</summary>
    public static string CoreDir { get; } = Path.Combine(InstallDir, "core");

    /// <summary>%APPDATA%\NdzeVpn, or NDZEVPN_DATA when set (portable use, or a throwaway profile for testing).</summary>
    public static string DataDir { get; } =
        Environment.GetEnvironmentVariable("NDZEVPN_DATA") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NdzeVpn");

    public static string LogDir { get; } = Path.Combine(DataDir, "logs");
    public static string RuntimeDir { get; } = Path.Combine(DataDir, "runtime");

    public static string SettingsFile { get; } = Path.Combine(DataDir, "settings.json");
    public static string ProfilesFile { get; } = Path.Combine(DataDir, "profiles.json");
    public static string SubscriptionsFile { get; } = Path.Combine(DataDir, "subscriptions.json");

    public static string XrayExe { get; } = Path.Combine(CoreDir, "xray.exe");
    public static string SingBoxExe { get; } = Path.Combine(CoreDir, "sing-box.exe");
    public static string WintunDll { get; } = Path.Combine(CoreDir, "wintun.dll");
    /// <summary>
    /// Writable copy of the rule data. The install dir may be Program Files (all-users install), where
    /// an unelevated app cannot replace files, so updates land here and Xray reads from here.
    /// </summary>
    public static string GeoDir { get; } = Path.Combine(DataDir, "geo");
    public static string GeoIpDat { get; } = Path.Combine(GeoDir, "geoip.dat");
    public static string GeoSiteDat { get; } = Path.Combine(GeoDir, "geosite.dat");

    public static string XrayConfigFile { get; } = Path.Combine(RuntimeDir, "xray.json");
    public static string SingBoxConfigFile { get; } = Path.Combine(RuntimeDir, "sing-box.json");
    public static string UpdateDir { get; } = Path.Combine(DataDir, "updates");

    private static bool _geoSeeded;

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogDir);
        Directory.CreateDirectory(RuntimeDir);
        Directory.CreateDirectory(GeoDir);
        SeedGeoAssets();
    }

    /// <summary>Copy the bundled .dat files into <see cref="GeoDir"/> when missing or older than the
    /// bundled ones (a new installer ships fresher data than a months-old download).</summary>
    private static void SeedGeoAssets()
    {
        // EnsureCreated runs on every log flush; the copy check only needs to happen once per launch.
        if (_geoSeeded) return;
        _geoSeeded = true;

        foreach (var name in new[] { "geoip.dat", "geosite.dat" })
        {
            try
            {
                var bundled = Path.Combine(CoreDir, name);
                var target = Path.Combine(GeoDir, name);
                if (!File.Exists(bundled)) continue;
                if (File.Exists(target) && File.GetLastWriteTimeUtc(target) >= File.GetLastWriteTimeUtc(bundled)) continue;
                File.Copy(bundled, target, overwrite: true);
            }
            catch
            {
                // Locked by a running xray or similar; the existing copy is still usable.
            }
        }
    }
}
