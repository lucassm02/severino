using CommunityToolkit.Mvvm.ComponentModel;
using Severino.Core.Configuration;

namespace Severino.App.ViewModels;

public enum RouteHealthState
{
    Unknown,
    Up,
    Down,
    Disabled,
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
    public string TargetDisplay => Uri.TryCreate(Route.Target, UriKind.Absolute, out var uri)
        ? (uri.Scheme == Uri.UriSchemeHttps ? "https://" : "") + uri.Authority
        : Route.Target;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HealthText))]
    public partial RouteHealthState Health { get; set; }

    /// <summary>Spelled out next to the dot, so the state does not rely on colour alone.</summary>
    public string HealthText => Health switch
    {
        RouteHealthState.Up => "Respondendo",
        RouteHealthState.Down => "Fora do ar",
        RouteHealthState.Disabled => "Desligada",
        _ => "Verificando…",
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
