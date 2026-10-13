using CommunityToolkit.Mvvm.ComponentModel;
using Severino.App.Resources;
using Severino.Core.Configuration;
using Severino.Core.Routes;

namespace Severino.App.ViewModels;

public enum RouteHealthState
{
    Unknown,
    Up,
    Down,
    Disabled,
}

public enum RouteHttpsState
{
    /// <summary>HTTPS off for the route, or the local CA is not active.</summary>
    Off,
    On,
    /// <summary>HTTPS on, but the CA cannot sign for the domain until it is reissued.</summary>
    Uncovered,
}

/// <summary>
/// The routes sharing a group, with one switch for all of them. Routes without a group sit in a
/// group with no name, shown without a header.
/// </summary>
public sealed partial class RouteGroupViewModel(string name, IReadOnlyList<RouteItemViewModel> items, Action<string, bool> setEnabled) : ObservableObject
{
    public string Name { get; } = name;
    public bool HasName => Name.Length > 0;
    public IReadOnlyList<RouteItemViewModel> Items { get; } = items;

    /// <summary>"3 rotas · 2 ligadas".</summary>
    public string Summary
    {
        get
        {
            var on = Items.Count(i => i.Enabled);
            return $"{(Items.Count == 1 ? Strings.Count_Route : string.Format(Strings.Count_Routes, Items.Count))} · {(on == 1 ? Strings.Routes_EnabledOne : string.Format(Strings.Routes_EnabledMany, on))}";
        }
    }

    /// <summary>On when any route of the group is; switching it sets them all.</summary>
    public bool Enabled
    {
        get => Items.Any(i => i.Enabled);
        set
        {
            if (value != Enabled || Items.Any(i => i.Enabled != value))
                setEnabled(Name, value);
        }
    }

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>One row of the route list.</summary>
public sealed partial class RouteItemViewModel : ObservableObject
{
    private readonly Action<RouteItemViewModel, bool> _setEnabled;

    public RouteItemViewModel(RouteEntry route, Action<RouteItemViewModel, bool> setEnabled)
    {
        Route = route;
        _setEnabled = setEnabled;
        Health = route.Enabled ? RouteHealthState.Unknown : RouteHealthState.Disabled;
    }

    public RouteEntry Route { get; private set; }

    public Guid Id => Route.Id;
    public string Domain => Route.Domain;

    /// <summary>The domain, with the path when the route takes only part of it: meuapp.sev/api.</summary>
    public string DisplayName => Route.Domain + Route.Path;
    public string TargetDisplay => RouteRules.FolderOf(Route.Target) is { } folder ? folder
        : Uri.TryCreate(Route.Target, UriKind.Absolute, out var uri) ? (uri.Scheme == Uri.UriSchemeHttps ? "https://" : "") + uri.Authority
        : Route.Target;

    /// <summary>The DNS name the target goes by, when it does: shown with its IP, linking to the DNS tab.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DestinationText))]
    public partial Severino.Core.Dns.DnsDestination? Destination { get; set; }

    /// <summary>"gateway.k8s é 192.168.203.100".</summary>
    public string? DestinationText => Destination is { } d ? string.Format(Strings.Routes_DestinationIs, d.Name, d.Address) : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHttpsBadge), nameof(HttpsBadgeText), nameof(HttpsBadgeTooltip))]
    public partial RouteHttpsState HttpsState { get; set; }

    public bool HasHttpsBadge => HttpsState != RouteHttpsState.Off;
    /// <summary>The item's environment is not the active one: it does not count until it is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HealthText), nameof(EnvironmentTip))]
    public partial bool OtherEnvironment { get; set; }

    public string Environment => Route.Environment;

    public bool HasEnvironment => Route.Environment.Length > 0;

    public string EnvironmentTip => string.Format(OtherEnvironment ? Strings.Env_OtherTip : Strings.Env_ThisTip, Route.Environment);

    /// <summary>One per rule on, so nobody forgets a delay or a simulated failure.</summary>
    public IReadOnlyList<RuleBadge> RuleBadges => RuleBadge.For(Route.Rules);

    public string HttpsBadgeText => HttpsState == RouteHttpsState.Uncovered ? "https ⚠" : "https";

    public string HttpsBadgeTooltip => HttpsState == RouteHttpsState.Uncovered
        ? Strings.Routes_HttpsUncoveredTip
        : Route.RedirectToHttps ? Strings.Routes_HttpsRedirectTip : Strings.Routes_HttpsBothTip;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HealthText))]
    public partial RouteHealthState Health { get; set; }

    /// <summary>Spelled out next to the dot, so the state does not rely on colour alone.</summary>
    public string HealthText => OtherEnvironment ? Strings.Env_Elsewhere : Health switch
    {
        RouteHealthState.Up => Strings.Health_Up,
        RouteHealthState.Down => Strings.Health_Down,
        RouteHealthState.Disabled => Strings.Routes_HealthOff,
        _ => Strings.Health_Checking,
    };

    public bool Enabled
    {
        get => Route.Enabled;
        set
        {
            if (value != Route.Enabled)
                _setEnabled(this, value);
        }
    }

    /// <summary>Refreshes the row from a newer copy of the same route.</summary>
    public void Update(RouteEntry route)
    {
        var wasEnabled = Route.Enabled;
        Route = route;
        OnPropertyChanged(string.Empty);
        if (!route.Enabled)
            Health = RouteHealthState.Disabled;
        else if (!wasEnabled)
            Health = RouteHealthState.Unknown;
    }
}
