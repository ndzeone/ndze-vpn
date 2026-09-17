using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NdzeVpn.Models;

namespace NdzeVpn.Services;

/// <summary>
/// Persistence for settings, nodes and subscriptions. Writes are atomic (temp + replace) so a
/// crash mid-save cannot leave an unparsable file behind.
/// </summary>
public sealed class AppStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly object _lock = new();

    public AppSettings Settings { get; private set; } = new();
    public List<ServerProfile> Profiles { get; private set; } = [];
    public List<Subscription> Subscriptions { get; private set; } = [];

    public void Load()
    {
        AppPaths.EnsureCreated();
        Settings = Read<AppSettings>(AppPaths.SettingsFile) ?? new AppSettings();
        Profiles = Read<List<ServerProfile>>(AppPaths.ProfilesFile) ?? [];
        Subscriptions = Read<List<Subscription>>(AppPaths.SubscriptionsFile) ?? [];

        LogBus.Instance.Capacity = Settings.LogBufferLines;
        LogBus.Instance.Info("store",
            $"Loaded {Profiles.Count} node(s), {Subscriptions.Count} subscription(s)");
    }

    public void SaveSettings() => Write(AppPaths.SettingsFile, Settings);
    public void SaveProfiles() => Write(AppPaths.ProfilesFile, Profiles);
    public void SaveSubscriptions() => Write(AppPaths.SubscriptionsFile, Subscriptions);

    public void SaveAll()
    {
        SaveSettings();
        SaveProfiles();
        SaveSubscriptions();
    }

    /// <summary>
    /// Replace the nodes belonging to one subscription, preserving measured latency for nodes
    /// that are still present so a refresh does not wipe the whole list's ping column.
    /// </summary>
    public int ReplaceSubscriptionNodes(string subscriptionId, List<ServerProfile> fresh)
    {
        lock (_lock)
        {
            var previous = Profiles
                .Where(p => p.SubscriptionId == subscriptionId)
                .ToDictionary(Identity, p => p, StringComparer.OrdinalIgnoreCase);

            Profiles.RemoveAll(p => p.SubscriptionId == subscriptionId);

            foreach (var node in fresh)
            {
                node.SubscriptionId = subscriptionId;
                if (previous.TryGetValue(Identity(node), out var old))
                {
                    node.Id = old.Id;
                    node.LatencyMs = old.LatencyMs;
                    node.LastTested = old.LastTested;
                }
                Profiles.Add(node);
            }

            SaveProfiles();
            return fresh.Count;
        }
    }

    public void AddProfiles(IEnumerable<ServerProfile> nodes, out int added, out int skipped)
    {
        added = 0;
        skipped = 0;
        lock (_lock)
        {
            var known = Profiles.Select(Identity).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var node in nodes)
            {
                if (known.Add(Identity(node)))
                {
                    Profiles.Add(node);
                    added++;
                }
                else skipped++;
            }
            SaveProfiles();
        }
    }

    private static string Identity(ServerProfile p) =>
        $"{p.Protocol}|{p.Address}|{p.Port}|{p.Credential}|{p.Network}|{p.Path}|{p.ServiceName}";

    // ------------------------------------------------------------------ io

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = MigrateLegacyValues(File.ReadAllText(path));
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (Exception ex)
        {
            LogBus.Instance.Error("store", $"Failed to read {Path.GetFileName(path)}", ex);

            // Keep the broken file around; overwriting it silently would lose the user's nodes.
            try { File.Move(path, path + ".broken", overwrite: true); } catch { }
            return null;
        }
    }

    /// <summary>
    /// Theme names from 1.0 no longer exist. An unknown enum string would fail deserialization and
    /// reset every setting, so map them to their successors first.
    /// </summary>
    private static string MigrateLegacyValues(string json) => json
        .Replace("\"Theme\": \"Spotify\"", "\"Theme\": \"Graphite\"")
        .Replace("\"Theme\": \"Midnight\"", "\"Theme\": \"Ink\"")
        .Replace("\"Theme\": \"Aurora\"", "\"Theme\": \"Ink\"");

    private void Write<T>(string path, T value)
    {
        try
        {
            AppPaths.EnsureCreated();
            var json = JsonSerializer.Serialize(value, Options);
            var temp = path + ".tmp";

            lock (_lock)
            {
                File.WriteAllText(temp, json);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
        }
        catch (Exception ex)
        {
            LogBus.Instance.Error("store", $"Failed to save {Path.GetFileName(path)}", ex);
        }
    }
}
