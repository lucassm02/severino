using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Severino.App.Services;
using Severino.Core.Configuration;
using Severino.Core.Discovery;
using Severino.Core.Dns;
using Severino.Core.Routes;
using Severino.Proxy;

namespace Severino.App.ViewModels;

/// <summary>
/// The Serviços tab: names that go straight to a port, in any protocol, for databases, queues and
/// other programs. Grouped by where they came from, with "Atualizar" for each import.
/// </summary>
public sealed partial class ServicesViewModel : ListPageViewModel
{
    private readonly ServiceRouteService _services;
    private readonly ServiceDiscovery _discovery;
    private readonly ConfigService _config;
    private readonly HealthMonitor _health;
    private readonly DialogService _dialogs;
    private readonly Navigation _navigation;
    private readonly DnsService? _dns;
    private readonly PortForwards? _forwards;

    public ServicesViewModel(ServiceRouteService services, ServiceDiscovery discovery, ConfigService config, HealthMonitor health, DialogService dialogs, Navigation navigation,
        DnsService? dns = null, PortForwards? forwards = null, ServiceWatcher? watcher = null)
    {
        _services = services;
        _discovery = discovery;
        _config = config;
        _health = health;
        _dialogs = dialogs;
        _navigation = navigation;
        _dns = dns;
        _forwards = forwards;
        if (forwards is not null)
            forwards.Changed += (_, _) => Dispatch(ApplyForwards);
        if (watcher is not null)
            watcher.Updated += (_, message) => Dispatch(() => ShowToast(message));

        Reconcile(config.Current);
        config.Changed += (_, c) => Dispatch(() => Reconcile(c));
        health.Changed += (_, h) => Dispatch(() => ApplyHealth(h.RouteId));
    }

    public ObservableCollection<ServiceGroupViewModel> ServiceGroups { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool HasServices { get; set; }

    public bool IsEmpty => !HasServices;

    /// <summary>"12 serviços · 11 ligados".</summary>
    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    protected override void OnSearch() => Reconcile(_config.Current, force: true);

    [RelayCommand]
    private void ImportServices()
    {
        if (_dialogs.ImportServices() is not { } outcome)
            return;
        var result = outcome.Services;
        // Imported from a distro: its apps are the likely callers, so the distro gets the names too.
        var distros = result.Where(p => p.Route is not null)
            .Select(p => CommandSource.FromId(p.Candidate.Origin.Source))
            .Where(s => s.IsWsl && !_config.Current.Settings.WslDistros.Contains(s.Distro!, StringComparer.OrdinalIgnoreCase))
            .Select(s => s.Distro!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distros.Count > 0)
            _config.Update(c => c with { Settings = c.Settings with { WslDistros = [.. c.Settings.WslDistros, .. distros] } });
        var message = Describe(result, outcome.IngressRoutes.Count)
            + (distros.Count > 0 ? $" Apps no WSL · {string.Join(", ", distros)} também chamam pelos nomes." : "");
        // Ingress hosts became web routes, which live in the other tab.
        if (outcome.IngressRoutes.Count > 0)
            ShowToast(message, "Ver em Rotas", () => _navigation.Show(AppTab.Routes));
        else
            ShowToast(message);
    }

    /// <summary>"3 serviços importados.", "2 serviços importados · 1 rota web criada na aba Rotas."</summary>
    public static string Describe(IReadOnlyList<PlannedService> services, int ingressRoutes)
    {
        var routes = ingressRoutes switch
        {
            0 => null,
            1 => "1 rota web criada na aba Rotas",
            var n => $"{n} rotas web criadas na aba Rotas",
        };
        return (services.Count == 0 && routes is not null ? routes
            : ImportServicesViewModel.Describe(services) + (routes is null ? "" : $" · {routes}")) + ".";
    }

    [RelayCommand]
    private void NewService()
    {
        if (_dialogs.EditService(null) is { } saved)
            ShowToast($"{saved.Names[0]} pronto");
    }

    [RelayCommand]
    private void EditService(ServiceItemViewModel item) => _dialogs.EditService(item.Route);

    [RelayCommand]
    private void CopyServiceName(ServiceItemViewModel item)
    {
        Clipboard.SetText(item.Name);
        ShowToast("Nome copiado");
    }

    [RelayCommand]
    private void ShowDns(string name) => _navigation.Show(AppTab.Dns, name);

    [RelayCommand]
    private void RemoveService(ServiceItemViewModel item) => RemoveServices([item.Id], $"{item.Name} removido");

    [RelayCommand]
    private void RemoveGroup(ServiceGroupViewModel group) =>
        RemoveServices([.. group.Items.Select(i => i.Id)], $"{Plural(group.Items.Count, "serviço removido", "serviços removidos")}");

    private void RemoveServices(IReadOnlyCollection<Guid> ids, string message)
    {
        var removed = _services.Remove(ids);
        if (removed.Count == 0)
            return;
        ShowUndo(message, () => _services.Restore(removed) == removed.Count ? null : "Parte não voltou: os nomes ou endereços já estão em outra rota.");
    }

    /// <summary>Asks the group's source again and moves its routes to the ports of now.</summary>
    [RelayCommand]
    private async Task RefreshGroupAsync(ServiceGroupViewModel group)
    {
        if (group.Origin is not { } origin || group.IsRefreshing)
            return;
        group.IsRefreshing = true;
        try
        {
            var outcome = await new ServiceRefresher(_discovery, _services, _dns).RefreshAsync(origin, CancellationToken.None);
            ShowToast(outcome.Describe());
        }
        finally
        {
            group.IsRefreshing = false;
        }
    }

    /// <summary>Groups by where they came from, imported groups first, in the order they were added.</summary>
    private void Reconcile(SeverinoConfig config, bool force = false)
    {
        var services = config.Services;
        HasServices = services.Count > 0;
        var enabled = services.Count(s => s.Enabled);
        Summary = $"{Plural(services.Count, "serviço", "serviços")} · {enabled} {(enabled == 1 ? "ligado" : "ligados")}";

        var search = Search.Trim();
        var visible = services.Where(s => Matches(s, search)).ToList();
        var origins = new List<ServiceOrigin?>();
        foreach (var service in visible.Where(s => s.Origin is not null))
        {
            if (!origins.Any(o => ServiceImport.SameSource(o!, service.Origin!)))
                origins.Add(service.Origin! with { Namespace = "", Name = "" });
        }
        if (visible.Any(s => s.Origin is null))
            origins.Add(null);

        if (force || !origins.SequenceEqual(ServiceGroups.Select(g => g.Origin)))
        {
            ServiceGroups.Clear();
            foreach (var origin in origins)
                ServiceGroups.Add(new ServiceGroupViewModel(origin));
        }

        var destinations = _dns?.Destinations() ?? [];
        foreach (var group in ServiceGroups)
        {
            var members = visible.Where(group.Holds).ToList();
            if (members.Select(s => s.Id).SequenceEqual(group.Items.Select(i => i.Id)))
            {
                for (var i = 0; i < members.Count; i++)
                    group.Items[i].Update(members[i]);
            }
            else
            {
                group.Items.Clear();
                foreach (var service in members)
                    group.Items.Add(new ServiceItemViewModel(service, (row, on) => _services.SetEnabled(row.Id, on)));
            }
            foreach (var item in group.Items)
            {
                ApplyHealth(item.Id);
                item.ApplyForward(_forwards?.StatusOf(item.Id));
                item.Destination = item.Route.Ports.Select(p => destinations.FirstOrDefault(d => d.Name.Equals(p.TargetHost, StringComparison.OrdinalIgnoreCase)))
                    .OfType<DnsDestination>().FirstOrDefault();
            }
        }
    }

    /// <summary>By name, namespace or destination host, so "gateway.k8s" finds what goes through it.</summary>
    private static bool Matches(ServiceRoute service, string search) =>
        search.Length == 0
        || service.Names.Any(n => n.Contains(search, StringComparison.OrdinalIgnoreCase))
        || service.Origin?.Namespace.Contains(search, StringComparison.OrdinalIgnoreCase) == true
        || service.Ports.Any(p => p.TargetHost.Contains(search, StringComparison.OrdinalIgnoreCase));

    private void ApplyForwards()
    {
        foreach (var item in ServiceGroups.SelectMany(g => g.Items))
            item.ApplyForward(_forwards?.StatusOf(item.Id));
    }

    private void ApplyHealth(Guid id)
    {
        if (ServiceGroups.SelectMany(g => g.Items).FirstOrDefault(i => i.Id == id) is { Enabled: true } service)
            service.Health = _health.IsUp(id) switch
            {
                true => RouteHealthState.Up,
                false => RouteHealthState.Down,
                null => RouteHealthState.Unknown,
            };
    }
}
