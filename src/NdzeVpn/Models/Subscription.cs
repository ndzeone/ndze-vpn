namespace NdzeVpn.Models;

/// <summary>
/// A remote list of nodes. Covers both classic base64 subscription endpoints and the
/// "landing page" links some Telegram bots hand out (see <see cref="Services.SubscriptionService"/>,
/// which scrapes share links out of whatever HTML comes back).
/// </summary>
public sealed class Subscription
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>Pull this subscription through the proxy instead of directly. Useful when the
    /// subscription host is itself blocked.</summary>
    public bool UpdateThroughProxy { get; set; }

    /// <summary>User-Agent to send. Some panels return different node sets per client.</summary>
    public string UserAgent { get; set; } = "";

    public DateTime? LastUpdated { get; set; }
    public int NodeCount { get; set; }
    public string? LastError { get; set; }

    /// <summary>Quota line from the panel's subscription-userinfo header, e.g. "12 GB / 100 GB · until 01.10.2026".</summary>
    public string? TrafficInfo { get; set; }

    /// <summary>Auto-update interval in hours; 0 disables auto-update for this subscription.</summary>
    public int AutoUpdateHours { get; set; } = 12;
}
