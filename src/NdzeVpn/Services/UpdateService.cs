using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace NdzeVpn.Services;

public sealed record UpdateInfo(
    Version Version,
    string Tag,
    string Title,
    string Notes,
    string PageUrl,
    string InstallerUrl,
    string InstallerName,
    long InstallerSize,
    string? Sha256);

/// <summary>
/// App updates from GitHub Releases. The latest release must carry an asset named
/// <c>NdzeVPN-Setup-&lt;version&gt;.exe</c>; its tag (<c>v1.2.0</c>) is compared with this build's version.
/// The installer is downloaded to the data dir, checked against the SHA-256 GitHub publishes for the
/// asset, then run silently with <c>/relaunch=1</c> so it starts the new version when done.
/// </summary>
public sealed class UpdateService
{
    public const string Owner = "ndzeone";
    public const string Repo = "ndze-vpn";

    public static string RepositoryUrl => $"https://github.com/{Owner}/{Repo}";
    public static string ReleasesUrl => $"{RepositoryUrl}/releases";

    public static Version CurrentVersion { get; } = Normalize(
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0));

    public static string CurrentVersionText => CurrentVersion.ToString(3);

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"NdzeVpn/{CurrentVersionText}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>Null when this build is current. Throws on network / API errors.</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var http = CreateClient(TimeSpan.FromSeconds(20));
        using var response = await http.GetAsync($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest", ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null; // repository has no published release yet
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseVersion(tag, out var version) || version <= CurrentVersion) return null;

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!name.StartsWith("NdzeVPN-Setup", StringComparison.OrdinalIgnoreCase)
                || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

            // GitHub exposes "digest": "sha256:<hex>" for release assets.
            string? sha = null;
            if (asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } d
                && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                sha = d["sha256:".Length..];

            return new UpdateInfo(
                version,
                tag,
                root.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } title ? title : tag,
                root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                root.GetProperty("html_url").GetString() ?? ReleasesUrl,
                asset.GetProperty("browser_download_url").GetString()!,
                name,
                asset.GetProperty("size").GetInt64(),
                sha);
        }

        LogBus.Instance.Warn("update", $"Release {tag} has no installer asset");
        return null;
    }

    /// <summary>Download the installer; reports progress 0..1. Returns the local path.</summary>
    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(AppPaths.UpdateDir);
        foreach (var old in Directory.GetFiles(AppPaths.UpdateDir))
        {
            try { File.Delete(old); } catch { }
        }

        var target = Path.Combine(AppPaths.UpdateDir, info.InstallerName);
        var temp = target + ".part";

        using var http = CreateClient(TimeSpan.FromMinutes(30));
        http.DefaultRequestHeaders.Accept.Clear();
        using var response = await http.GetAsync(info.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? info.InstallerSize;
        using var sha = SHA256.Create();

        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = File.Create(temp))
        {
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), ct);
                sha.TransformBlock(buffer, 0, n, null, 0);
                read += n;
                if (total > 0) progress?.Report((double)read / total);
            }
            sha.TransformFinalBlock([], 0, 0);
        }

        var actual = Convert.ToHexString(sha.Hash!);
        if (info.Sha256 is { } expected && !actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(temp);
            throw new InvalidDataException("Контрольная сумма установщика не совпала — файл повреждён при загрузке.");
        }

        File.Move(temp, target, overwrite: true);
        LogBus.Instance.Info("update", $"Downloaded {info.InstallerName} ({StatsService.FormatBytes(new FileInfo(target).Length)}), sha256 {actual[..12]}…");
        return target;
    }

    /// <summary>Start the installer detached. The caller must exit right after so files are unlocked.</summary>
    public static void LaunchInstaller(string path)
    {
        // ShellExecute so an all-users install can raise its UAC prompt.
        Process.Start(new ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART /relaunch=1")
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(path)!
        });
    }

    public static bool TryParseVersion(string tag, out Version version)
    {
        version = new Version(0, 0, 0);
        var text = tag.Trim().TrimStart('v', 'V');
        var dash = text.IndexOfAny(['-', '+']);
        if (dash >= 0) text = text[..dash];
        if (!Version.TryParse(text, out var parsed)) return false;
        version = Normalize(parsed);
        return true;
    }

    private static Version Normalize(Version v) =>
        new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));
}
