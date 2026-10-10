using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Severino.App.Services;
using Severino.Core.Configuration;
using Severino.Core.Discovery;
using Severino.Core.Routes;
using Severino.Proxy;

namespace Severino.App.ViewModels;

public sealed partial class RoutesViewModel : ObservableObject
{
    private static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(5);

    private readonly RouteService _routes;
    private readonly ServiceRouteService _services;
    private readonly ServiceDiscovery _discovery;
    private readonly ConfigService _config;
    private readonly HealthMonitor _health;
    private readonly DialogService _dialogs;
    private readonly HttpsService _https;
    private readonly DispatcherTimer _undoTimer;

    /// <summary>Puts back what was removed; returns why it could not, or null.</summary>
    private Func<string?>? _undo;

    public RoutesViewModel(RouteService routes, ServiceRouteService services, ServiceDiscovery discovery, ConfigService config, HealthMonitor health, DialogService dialogs, HttpsService https)
    {
        _routes = routes;
        _services = services;
        _discovery = discovery;
        _config = config;
        _health = health;
        _dialogs = dialogs;
        _https = https;
        _undoTimer = new DispatcherTimer { Interval = UndoWindow };
        _undoTimer.Tick += (_, _) => DismissUndo();

        Reconcile(config.Current);
        config.Changed += (_, c) => Dispatch(() => Reconcile(c));
        health.Changed += (_, h) => Dispatch(() => ApplyHealth(h.RouteId));
        https.Changed += (_, _) => Dispatch(() => Reconcile(_config.Current));
    }

    public ObservableCollection<RouteItemViewModel> Items { get; } = [];

    public ObservableCollection<ServiceGroupViewModel> ServiceGroups { get; } = [];

    /// <summary>Any web route, before the search.</summary>
    [ObservableProperty]
    public partial bool HasRoutes { get; set; }

    [ObservableProperty]
    public partial bool HasServices { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool HasAnything { get; set; }

    public bool IsEmpty => !HasAnything;

    /// <summary>"3 rotas · 2 ligadas · 12 serviços".</summary>
    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    [ObservableProperty]
    public partial string? UndoMessage { get; set; }

    [ObservableProperty]
    public partial string? Toast { get; set; }

    partial void OnSearchChanged(string value) => Reconcile(_config.Current, force: true);

    [RelayCommand]
    private void ImportServices()
    {
        if (_dialogs.ImportServices() is not { } result)
            return;
        // Imported from a distro: its apps are the likely callers, so the distro gets the names too.
        var distros = result.Where(p => p.Route is not null)
            .Select(p => CommandSource.FromId(p.Candidate.Origin.Source))
            .Where(s => s.IsWsl && !_config.Current.Settings.WslDistros.Contains(s.Distro!, StringComparer.OrdinalIgnoreCase))
            .Select(s => s.Distro!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distros.Count > 0)
            _config.Update(c => c with { Settings = c.Settings with { WslDistros = [.. c.Settings.WslDistros, .. distros] } });
        ShowToast(ImportServicesViewModel.Describe(result)
            + (distros.Count > 0 ? $" · apps em WSL · {string.Join(", ", distros)} também chamam pelos nomes (Configurações › WSL)" : ""));
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
            var source = CommandSource.FromId(origin.Source);
            IReadOnlyList<DiscoveredService> found;
            if (origin.Kind == ServiceKind.Kubernetes)
            {
                // Keep the node the routes use, when it is still one of the cluster's.
                var node = group.Items.SelectMany(i => i.Route.Ports).Select(p => p.TargetHost).FirstOrDefault();
                var result = await _discovery.KubernetesAsync(source, CancellationToken.None, node);
                if (result.Error is { } error)
                {
                    ShowToast(error);
                    return;
                }
                if (result.Context != origin.Context)
                {
                    ShowToast($"O kubectl em {source} está no contexto {result.Context}, não em {origin.Context}. Troque o contexto e atualize de novo.");
                    return;
                }
                found = result.Services;
            }
            else
            {
                var result = await _discovery.DockerAsync(source, CancellationToken.None);
                if (result.Error is { } error)
                {
                    ShowToast(error);
                    return;
                }
                found = result.Containers;
            }

            var refresh = _services.Refresh(origin, found);
            var parts = new List<string>
            {
                refresh.Updated.Count == 0 ? "Nada mudou" : $"{Plural(refresh.Updated.Count, "serviço com portas novas", "serviços com portas novas")}",
            };
            if (refresh.Missing.Count > 0)
                parts.Add($"não encontrados agora: {string.Join(", ", refresh.Missing.Select(m => m.Names[0]))}");
            ShowToast(string.Join(" · ", parts));
        }
        finally
        {
            group.IsRefreshing = false;
        }
    }

    [RelayCommand]
    private async Task NewRouteAsync()
    {
        var saved = _dialogs.EditRoute(null);
        if (saved is not null && !await OfferReissueAsync(saved))
            ShowToast($"{saved.Domain} pronto");
    }

    [RelayCommand]
    private async Task EditAsync(RouteItemViewModel item)
    {
        if (_dialogs.EditRoute(item.Route) is { } saved)
            await OfferReissueAsync(saved);
    }

    [RelayCommand]
    private async Task DuplicateAsync(RouteItemViewModel item)
    {
        if (_dialogs.EditRoute(item.Route with { Id = Guid.NewGuid(), Domain = "" }, isCopy: true) is { } saved)
            await OfferReissueAsync(saved);
    }

    /// <summary>
    /// A saved HTTPS route outside the CA's coverage needs a new CA. Returns true when it asked,
    /// so the caller does not toast over the answer.
    /// </summary>
    private async Task<bool> OfferReissueAsync(RouteEntry saved)
    {
        if (!saved.Https || !_https.IsActive || _https.Covers(saved.Domain))
            return false;

        try
        {
            var lead = $"A CA atual não cobre {saved.Domain}. Para esta rota ter HTTPS, o Severino cria uma CA nova para todas as rotas com HTTPS.";
            ShowToast((await _https.ReissueAsync(prompt => DialogService.ConfirmTrustAsync("Reemitir a CA", prompt, lead))) switch
            {
                HttpsActionResult.Done or HttpsActionResult.DoneOldRootKept => $"CA reemitida. {saved.Domain} já abre com https://",
                HttpsActionResult.Cancelled => $"{saved.Domain} fica sem HTTPS até reemitir em Configurações.",
                _ => $"O Windows não instalou a CA nova. {saved.Domain} fica sem HTTPS até reemitir.",
            });
        }
        catch (Exception ex)
        {
            ShowToast($"Não deu para reemitir a CA: {ex.Message}");
        }
        return true;
    }

    [RelayCommand]
    private void Open(RouteItemViewModel item) => Browser.Open(Url(item));

    [RelayCommand]
    private void CopyUrl(RouteItemViewModel item)
    {
        Clipboard.SetText(Url(item));
        ShowToast("URL copiada");
    }

    [RelayCommand]
    private void Remove(RouteItemViewModel item)
    {
        if (_routes.Remove(item.Id) is not { } removed)
            return;
        ShowUndo($"{item.Domain} removido", () => _routes.Restore(removed) ? null : "Não deu para desfazer: o domínio já está em outra rota.");
    }

    private void ShowUndo(string message, Func<string?> undo)
    {
        _undo = undo;
        UndoMessage = message;
        _undoTimer.Stop();
        _undoTimer.Start();
    }

    [RelayCommand]
    private void Undo()
    {
        if (_undo?.Invoke() is { } error)
            ShowToast(error);
        DismissUndo();
    }

    private string Url(RouteItemViewModel item)
    {
        var settings = _config.Current.Settings;
        return Browser.UrlFor(item.Domain, settings.HttpPort, item.HttpsState == RouteHttpsState.On ? settings.HttpsPort : null);
    }

    private void DismissUndo()
    {
        _undoTimer.Stop();
        _undo = null;
        UndoMessage = null;
    }

    private async void ShowToast(string message)
    {
        Toast = message;
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (Toast == message)
            Toast = null;
    }

    private void Reconcile(SeverinoConfig config, bool force = false)
    {
        var routes = config.Routes;
        HasRoutes = routes.Count > 0;
        HasServices = config.Services.Count > 0;
        HasAnything = HasRoutes || HasServices;
        var enabled = routes.Count(r => r.Enabled);
        Summary = $"{routes.Count} {(routes.Count == 1 ? "rota" : "rotas")} · {enabled} {(enabled == 1 ? "ligada" : "ligadas")}"
            + (HasServices ? $" · {Plural(config.Services.Count, "serviço", "serviços")}" : "");
        var visible = routes
            .Where(r => Search.Length == 0 || r.Domain.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Same rows in the same order: update in place so toggles keep focus and animation.
        if (!force && visible.Select(r => r.Id).SequenceEqual(Items.Select(i => i.Id)))
        {
            for (var i = 0; i < visible.Count; i++)
                Items[i].Update(visible[i]);
        }
        else
        {
            Items.Clear();
            foreach (var route in visible)
                Items.Add(new RouteItemViewModel(route, (row, enabled) => _routes.SetEnabled(row.Id, enabled)));
        }

        foreach (var item in Items)
        {
            ApplyHealth(item.Id);
            item.HttpsState = !item.Route.Https || !_https.IsActive ? RouteHttpsState.Off
                : _https.Covers(item.Domain) ? RouteHttpsState.On
                : RouteHttpsState.Uncovered;
        }

        ReconcileServices(config.Services, force);
    }

    /// <summary>Groups by where they came from, imported groups first, in the order they were added.</summary>
    private void ReconcileServices(IReadOnlyList<ServiceRoute> services, bool force)
    {
        var search = Search.Trim();
        var visible = services
            .Where(s => search.Length == 0 || s.Names.Any(n => n.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .ToList();
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
                    group.Items.Add(new ServiceItemViewModel(service, (row, enabled) => _services.SetEnabled(row.Id, enabled)));
            }
            foreach (var item in group.Items)
                ApplyHealth(item.Id);
        }
    }

    private void ApplyHealth(Guid routeId)
    {
        var state = _health.IsUp(routeId) switch
        {
            true => RouteHealthState.Up,
            false => RouteHealthState.Down,
            null => RouteHealthState.Unknown,
        };
        if (Items.FirstOrDefault(i => i.Id == routeId) is { Enabled: true } item)
            item.Health = state;
        else if (ServiceGroups.SelectMany(g => g.Items).FirstOrDefault(i => i.Id == routeId) is { Enabled: true } service)
            service.Health = state;
    }

    private static string Plural(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count} {many}";

    private static void Dispatch(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);
}
