using System.Text;

namespace NdzeVpn.Services;

/// <summary>
/// Manages geoip.dat / geosite.dat: downloads them and enumerates which tags a given file actually
/// defines. That second part matters — Xray refuses to start on an unknown <c>geosite:</c> tag,
/// and rule sets disagree about names, so the config builder asks this class what is safe to emit.
/// </summary>
public sealed class GeoAssetService
{
    public sealed record AssetSource(string Key, string Name, string GeoIpUrl, string GeoSiteUrl, string Description);

    public static readonly AssetSource[] Sources =
    [
        new("runetfreedom", "Runet Freedom (Russia)",
            "https://github.com/runetfreedom/russia-v2ray-rules-dat/releases/latest/download/geoip.dat",
            "https://github.com/runetfreedom/russia-v2ray-rules-dat/releases/latest/download/geosite.dat",
            "Built from the Russian blocklist registry. The most accurate option for this use case."),

        new("loyalsoldier", "Loyalsoldier (general)",
            "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geoip.dat",
            "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geosite.dat",
            "Large general-purpose rule set. Good country data, weaker on Russian blocking."),

        new("v2fly", "v2fly (official)",
            "https://github.com/v2fly/geoip/releases/latest/download/geoip.dat",
            "https://github.com/v2fly/domain-list-community/releases/latest/download/dlc.dat",
            "Upstream reference data. Conservative and stable.")
    ];

    public static AssetSource Resolve(string key) =>
        Sources.FirstOrDefault(s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? Sources[0];

    public event EventHandler<string>? Progress;

    public async Task<bool> UpdateAsync(string sourceKey, CancellationToken ct = default)
    {
        var source = Resolve(sourceKey);
        Directory.CreateDirectory(AppPaths.GeoDir);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NdzeVpn/1.0");

        var ok = true;
        ok &= await DownloadAsync(http, source.GeoIpUrl, AppPaths.GeoIpDat, "geoip.dat", ct);
        ok &= await DownloadAsync(http, source.GeoSiteUrl, AppPaths.GeoSiteDat, "geosite.dat", ct);

        if (ok) LogBus.Instance.Info("geo", $"Updated rule data from {source.Name}");
        return ok;
    }

    private async Task<bool> DownloadAsync(HttpClient http, string url, string target, string label, CancellationToken ct)
    {
        try
        {
            Progress?.Invoke(this, $"Downloading {label}…");

            var bytes = await http.GetByteArrayAsync(url, ct);

            // A truncated or HTML error page would break Xray at startup; sanity-check the size.
            if (bytes.Length < 10_000)
                throw new InvalidDataException($"{label} came back as only {bytes.Length} bytes.");

            var temp = target + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes, ct);

            if (File.Exists(target)) File.Replace(temp, target, null);
            else File.Move(temp, target);

            Progress?.Invoke(this, $"{label}: {StatsService.FormatBytes(bytes.Length)}");
            return true;
        }
        catch (Exception ex)
        {
            LogBus.Instance.Error("geo", $"Failed to download {label}", ex);
            Progress?.Invoke(this, $"{label} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Read the tag names out of a geosite.dat. The file is a protobuf
    /// <c>GeoSiteList { repeated GeoSite entry = 1 }</c> and <c>GeoSite.country_code</c> is field 1,
    /// so we only need to walk the outer entries and pull one string out of each — no protobuf
    /// runtime, no schema.
    /// </summary>
    public static HashSet<string>? ReadGeoSiteTags(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            var data = File.ReadAllBytes(path);
            var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pos = 0;

            while (pos < data.Length)
            {
                if (!TryReadVarint(data, ref pos, out var key)) break;

                var fieldNumber = (int)(key >> 3);
                var wireType = (int)(key & 0x7);

                if (fieldNumber == 1 && wireType == 2)
                {
                    if (!TryReadVarint(data, ref pos, out var len)) break;
                    var end = pos + (int)len;
                    if (end > data.Length) break;

                    var name = ReadFirstString(data, pos, end);
                    if (!string.IsNullOrEmpty(name)) tags.Add(name);

                    pos = end;
                }
                else if (!SkipField(data, ref pos, wireType))
                {
                    break;
                }
            }

            return tags.Count > 0 ? tags : null;
        }
        catch (Exception ex)
        {
            LogBus.Instance.Warn("geo", $"Could not enumerate geosite tags: {ex.Message}");
            return null;
        }
    }

    /// <summary>Field 1 of a GeoSite message is its name; stop as soon as we have it.</summary>
    private static string? ReadFirstString(byte[] data, int pos, int end)
    {
        while (pos < end)
        {
            if (!TryReadVarint(data, ref pos, out var key)) return null;

            var fieldNumber = (int)(key >> 3);
            var wireType = (int)(key & 0x7);

            if (fieldNumber == 1 && wireType == 2)
            {
                if (!TryReadVarint(data, ref pos, out var len)) return null;
                if (pos + (int)len > end) return null;
                return Encoding.UTF8.GetString(data, pos, (int)len);
            }

            if (!SkipField(data, ref pos, wireType)) return null;
        }
        return null;
    }

    private static bool SkipField(byte[] data, ref int pos, int wireType)
    {
        switch (wireType)
        {
            case 0:
                return TryReadVarint(data, ref pos, out _);
            case 1:
                pos += 8;
                return pos <= data.Length;
            case 2:
                if (!TryReadVarint(data, ref pos, out var len)) return false;
                pos += (int)len;
                return pos <= data.Length;
            case 5:
                pos += 4;
                return pos <= data.Length;
            default:
                return false;
        }
    }

    private static bool TryReadVarint(byte[] data, ref int pos, out ulong value)
    {
        value = 0;
        var shift = 0;
        while (pos < data.Length && shift < 64)
        {
            var b = data[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
        }
        return false;
    }

    public static (bool GeoIp, bool GeoSite, long GeoIpSize, long GeoSiteSize) Status()
    {
        var ipExists = File.Exists(AppPaths.GeoIpDat);
        var siteExists = File.Exists(AppPaths.GeoSiteDat);
        return (
            ipExists,
            siteExists,
            ipExists ? new FileInfo(AppPaths.GeoIpDat).Length : 0,
            siteExists ? new FileInfo(AppPaths.GeoSiteDat).Length : 0);
    }
}
