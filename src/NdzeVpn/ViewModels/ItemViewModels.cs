using CommunityToolkit.Mvvm.ComponentModel;
using NdzeVpn.Models;
using NdzeVpn.Services;

namespace NdzeVpn.ViewModels;

public sealed partial class ServerItemViewModel : ObservableObject
{
    public ServerProfile Model { get; }

    public ServerItemViewModel(ServerProfile model, int index)
    {
        Model = model;
        _index = index;
        _latencyMs = model.LatencyMs;
        (CountryCode, CleanName) = CountryFlag.Split(model.DisplayName);
    }

    [ObservableProperty] private int _index;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatencySortKey))]
    private int _latencyMs;

    /// <summary>Unreachable/untested nodes sort after every reachable one.</summary>
    public int LatencySortKey => LatencyMs > 0 ? LatencyMs : int.MaxValue;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isTesting;

    public string Id => Model.Id;
    public string Name => Model.DisplayName;

    /// <summary>Name with the leading flag emoji removed — Windows cannot render flag emoji,
    /// so the country is shown as a two-letter badge instead.</summary>
    public string CleanName { get; }

    public string CountryCode { get; }
    public bool HasCountry => CountryCode.Length == 2;

    public string Protocol => Model.ProtocolLabel;

    /// <summary>Badge text on cover art ("VLESS", "TROJAN"…), where the full label would not fit.</summary>
    public string ProtocolShort => Model.Protocol switch
    {
        ProxyProtocol.Shadowsocks => "SS",
        var p => p.ToString().ToUpperInvariant()
    };
    public string Transport => Model.TransportLabel;
    public string Address => $"{Model.Address}:{Model.Port}";
    public string SubscriptionId => Model.SubscriptionId ?? "";
    public bool IsReality => Model.StreamSecurity == "reality";
    public string Flow => string.IsNullOrEmpty(Model.Flow) ? "" : Model.Flow;

    public void RefreshLatency()
    {
        LatencyMs = Model.LatencyMs;
    }
}

public sealed partial class SubscriptionItemViewModel : ObservableObject
{
    public Subscription Model { get; }

    public SubscriptionItemViewModel(Subscription model) => Model = model;

    [ObservableProperty] private bool _isUpdating;

    public string Id => Model.Id;

    public string Name
    {
        get => Model.Name;
        set
        {
            if (Model.Name == value) return;
            Model.Name = value;
            OnPropertyChanged();
        }
    }

    public string Url
    {
        get => Model.Url;
        set
        {
            if (Model.Url == value) return;
            Model.Url = value;
            OnPropertyChanged();
        }
    }

    public bool Enabled
    {
        get => Model.Enabled;
        set { Model.Enabled = value; OnPropertyChanged(); }
    }

    public bool UpdateThroughProxy
    {
        get => Model.UpdateThroughProxy;
        set { Model.UpdateThroughProxy = value; OnPropertyChanged(); }
    }

    public int AutoUpdateHours
    {
        get => Model.AutoUpdateHours;
        set { Model.AutoUpdateHours = Math.Max(0, value); OnPropertyChanged(); }
    }

    public string UserAgent
    {
        get => Model.UserAgent;
        set { Model.UserAgent = value; OnPropertyChanged(); }
    }

    public int NodeCount => Model.NodeCount;
    public string? LastError => Model.LastError;
    public string? TrafficInfo => Model.TrafficInfo;
    public bool HasError => !string.IsNullOrEmpty(Model.LastError);

    public string StatusLine
    {
        get
        {
            var parts = new List<string> { $"{Model.NodeCount} серв." };
            if (Model.LastUpdated is { } at) parts.Add($"обновлено {at:dd.MM HH:mm}");
            else parts.Add("ещё не обновлялась");
            if (!string.IsNullOrEmpty(Model.TrafficInfo)) parts.Add(Model.TrafficInfo);
            return string.Join(" · ", parts);
        }
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(NodeCount));
        OnPropertyChanged(nameof(LastError));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(TrafficInfo));
        OnPropertyChanged(nameof(StatusLine));
    }
}

public sealed partial class RuleItemViewModel : ObservableObject
{
    public RoutingRule Model { get; }

    public RuleItemViewModel(RoutingRule model) => Model = model;

    public bool Enabled
    {
        get => Model.Enabled;
        set { Model.Enabled = value; OnPropertyChanged(); }
    }

    public RuleKind Kind
    {
        get => Model.Kind;
        set { Model.Kind = value; OnPropertyChanged(); OnPropertyChanged(nameof(Hint)); }
    }

    public RuleAction Action
    {
        get => Model.Action;
        set { Model.Action = value; OnPropertyChanged(); }
    }

    public string Value
    {
        get => Model.Value;
        set { Model.Value = value; OnPropertyChanged(); }
    }

    public string Note
    {
        get => Model.Note;
        set { Model.Note = value; OnPropertyChanged(); }
    }

    public string Hint => Kind switch
    {
        RuleKind.Domain => "youtube.com, discord.gg",
        RuleKind.DomainFull => "api.example.com",
        RuleKind.DomainRegex => @".*\.google\..*",
        RuleKind.GeoSite => "category-ru, google, netflix",
        RuleKind.GeoIp => "ru, us, private",
        RuleKind.Ip => "1.1.1.1, 10.0.0.0/8",
        RuleKind.Port => "443, 27015-27050",
        RuleKind.Process => "Discord.exe, cs2.exe (только TUN)",
        _ => ""
    };
}

public sealed record Option<T>(T Value, string Label, string Description = "")
{
    public override string ToString() => Label;
}

public sealed partial class LogLineViewModel(LogEntry entry)
{
    public string Time { get; } = entry.Timestamp.ToString("HH:mm:ss");
    public string Level { get; } = entry.Level.ToString().ToUpperInvariant();
    public string Source { get; } = entry.Source;
    public string Message { get; } = entry.Message;
    public LogLevel Severity { get; } = entry.Level;
}
