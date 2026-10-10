using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Severino.Core.Configuration;
using Severino.Core.Discovery;
using Severino.Core.Routes;

namespace Severino.App.ViewModels;

/// <summary>Service routes that came from the same place, with "Atualizar" for the lot.</summary>
public sealed partial class ServiceGroupViewModel(ServiceOrigin? origin) : ObservableObject
{
    /// <summary>The source part of the routes' origin; null for routes made by hand.</summary>
    public ServiceOrigin? Origin { get; } = origin;

    public string Title => Origin is null ? "Criados à mão" : Describe(Origin);

    /// <summary>Pasted services cannot be asked again.</summary>
    public bool CanRefresh => Origin is not null && Origin.Source != ImportServicesViewModel.PastedSource;

    public ObservableCollection<ServiceItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial bool IsRefreshing { get; set; }

    public bool Holds(ServiceRoute route) =>
        Origin is null ? route.Origin is null : route.Origin is { } o && ServiceImport.SameSource(o, Origin);

    /// <summary>"Kubernetes · kubernetes-admin@kubernetes · WSL · Ubuntu-22.04", "Docker · Windows".</summary>
    public static string Describe(ServiceOrigin origin)
    {
        var where = origin.Source == ImportServicesViewModel.PastedSource ? "colado" : CommandSource.FromId(origin.Source).ToString();
        return origin.Kind == ServiceKind.Kubernetes && origin.Context.Length > 0
            ? $"Kubernetes · {origin.Context} · {where}"
            : $"{(origin.Kind == ServiceKind.Kubernetes ? "Kubernetes" : "Docker")} · {where}";
    }
}

/// <summary>One service route in the list.</summary>
public sealed partial class ServiceItemViewModel : ObservableObject
{
    private readonly Action<ServiceItemViewModel, bool> _setEnabled;

    public ServiceItemViewModel(ServiceRoute route, Action<ServiceItemViewModel, bool> setEnabled)
    {
        Route = route;
        _setEnabled = setEnabled;
        Health = route.Enabled ? RouteHealthState.Unknown : RouteHealthState.Disabled;
    }

    public ServiceRoute Route { get; private set; }

    public Guid Id => Route.Id;

    /// <summary>The shortest name, the one apps usually call.</summary>
    public string Name => Route.Names.Count > 0 ? Route.Names[0] : "(sem nome)";

    public string? MoreNames => Route.Names.Count switch
    {
        <= 1 => null,
        2 => "+1 nome",
        var n => $"+{n - 1} nomes",
    };

    public string AllNames => string.Join(Environment.NewLine, Route.Names);

    /// <summary>Namespace or Compose project it came from.</summary>
    public string? Namespace => Route.Origin is { Namespace.Length: > 0 } o ? o.Namespace : null;

    public string Detail => $"{Route.Address}   " + string.Join("   ", Route.Ports);

    /// <summary>"Port-forward rodando", or why it is starting over; null for a route without one.</summary>
    [ObservableProperty]
    public partial string? ForwardText { get; set; }

    [ObservableProperty]
    public partial bool ForwardProblem { get; set; }

    public void ApplyForward(Severino.Core.Discovery.PortForwardStatus? status)
    {
        ForwardProblem = status?.State == Severino.Core.Discovery.PortForwardState.Restarting;
        ForwardText = !Route.PortForward ? null : status?.State switch
        {
            Severino.Core.Discovery.PortForwardState.Running => "kubectl port-forward rodando",
            Severino.Core.Discovery.PortForwardState.Restarting => $"port-forward reiniciando: {status.Detail}",
            Severino.Core.Discovery.PortForwardState.Starting => "port-forward iniciando…",
            _ => Route.Enabled ? "port-forward parado" : null,
        };
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HealthText))]
    public partial RouteHealthState Health { get; set; }

    public string HealthText => Health switch
    {
        RouteHealthState.Up => "Respondendo",
        RouteHealthState.Down => "Fora do ar",
        RouteHealthState.Disabled => "Desligado",
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

    public void Update(ServiceRoute route)
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
