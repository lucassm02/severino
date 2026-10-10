using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Severino.App.Services;
using Severino.Core.Configuration;
using Severino.Core.Dns;
using Severino.Core.Routes;
using Severino.Proxy;

namespace Severino.App.ViewModels;

/// <summary>The Rotas tab: names opened in the browser, through the proxy, by group.</summary>
public sealed partial class RoutesViewModel : ListPageViewModel
{
    private readonly RouteService _routes;
    private readonly DnsService? _dns;
    private readonly HostsSync? _hosts;
    private readonly ConfigService _config;
    private readonly HealthMonitor _health;
    private readonly DialogService _dialogs;
    private readonly HttpsService _https;
    private readonly Navigation _navigation;

    public RoutesViewModel(RouteService routes, ConfigService config, HealthMonitor health, DialogService dialogs, HttpsService https, Navigation navigation,
        DnsService? dns = null, HostsSync? hosts = null)
    {
        _routes = routes;
        _dns = dns;
        _hosts = hosts;
        _config = config;
        _health = health;
        _dialogs = dialogs;
        _https = https;
        _navigation = navigation;
        if (hosts is not null)
        {
            hosts.StatusChanged += (_, _) => Dispatch(UpdateWildcardPending);
            UpdateWildcardPending();
        }

        Reconcile(config.Current);
        config.Changed += (_, c) => Dispatch(() => Reconcile(c));
        health.Changed += (_, h) => Dispatch(() => ApplyHealth(h.RouteId));
        https.Changed += (_, _) => Dispatch(() => Reconcile(_config.Current));
    }

    public ObservableCollection<RouteItemViewModel> Items { get; } = [];

    /// <summary>The web routes, by group; the first group, with no name, holds the routes without one.</summary>
    public ObservableCollection<RouteGroupViewModel> RouteGroups { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool HasRoutes { get; set; }

    public bool IsEmpty => !HasRoutes;

    /// <summary>"3 rotas · 2 ligadas".</summary>
    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    protected override void OnSearch() => Reconcile(_config.Current, force: true);

    [RelayCommand]
    private async Task NewRouteAsync()
    {
        var saved = _dialogs.EditRoute(null);
        if (saved is not null && !await OfferReissueAsync(saved))
            ShowToast($"{saved.Domain} pronto");
    }

    [RelayCommand]
    private async Task NewRouteInGroupAsync(RouteGroupViewModel group)
    {
        var saved = _dialogs.EditRoute(null, group: group.Name);
        if (saved is not null && !await OfferReissueAsync(saved))
            ShowToast($"{saved.Domain} pronto");
    }

    [RelayCommand]
    private async Task RenameGroupAsync(RouteGroupViewModel group)
    {
        if (await DialogService.PromptAsync("Renomear grupo", "Com o nome de outro grupo, os dois viram um só.", group.Name, "Renomear") is not { } name)
            return;
        if (name.Trim().Length == 0)
        {
            ShowToast("Para tirar as rotas do grupo, use Desfazer o grupo.");
            return;
        }
        _routes.RenameGroup(group.Name, name);
    }

    [RelayCommand]
    private void Ungroup(RouteGroupViewModel group)
    {
        var name = group.Name;
        _routes.RenameGroup(name, "");
        ShowToast($"As rotas de {name} ficaram sem grupo. Para juntar de novo, edite cada uma.");
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

    [RelayCommand]
    private void ShowDns(string name) => _navigation.Show(AppTab.Dns, name);

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

    /// <summary>
    /// A route whose destination is an IP becomes a plain DNS entry: the name then goes straight
    /// to that IP, without the proxy, so the port, HTTPS and the log no longer apply.
    /// </summary>
    [RelayCommand]
    private async Task MakeDnsAsync(RouteItemViewModel item)
    {
        if (_dns is null)
            return;
        if (!Uri.TryCreate(item.Route.Target, UriKind.Absolute, out var target) || !System.Net.IPAddress.TryParse(target.Host.Trim('[', ']'), out var ip))
        {
            ShowToast("Só uma rota cujo destino é um IP vira entrada DNS.");
            return;
        }
        if (!await DialogService.ConfirmAsync("Transformar em entrada DNS",
                $"{item.Domain} passa a apontar direto para {ip}, sem passar pelo Severino. " +
                $"A porta {target.Port}, o HTTPS da CA local e o log de requisições deixam de valer: o navegador vai ao IP, na porta que a URL disser.",
                "Transformar"))
            return;

        if (_routes.Remove(item.Id) is not { } removed)
            return;
        var draft = new DnsEntry { Names = [item.Domain], Address = ip.ToString(), Notes = item.Route.Notes };
        if (_dns.Validate(draft).Error is { } error)
        {
            _routes.Restore(removed);
            ShowToast(error);
            return;
        }
        _dns.Save(draft);
        ShowToast($"{item.Domain} agora é uma entrada DNS.", "Ver no DNS", () => _navigation.Show(AppTab.Dns, item.Domain));
    }

    /// <summary>"*.callfred.sev espera aprovação…", while a wildcard's suffix is not approved.</summary>
    [ObservableProperty]
    public partial string? WildcardPendingText { get; set; }

    private void UpdateWildcardPending()
    {
        var names = _hosts?.Status.PendingEntries.Select(e => e.Name).Distinct().ToList() ?? [];
        WildcardPendingText = names.Count == 0 ? null
            : $"{string.Join(", ", names)} {(names.Count == 1 ? "só vale" : "só valem")} depois de aprovado: o curinga manda um sufixo inteiro para o Severino responder. O Windows pede confirmação de administrador.";
    }

    [RelayCommand]
    private async Task ApproveWildcardsAsync()
    {
        if (_hosts is null || _hosts.Status.PendingEntries is not { Count: > 0 } pending)
            return;
        if (await HelperApproval.ApproveAsync(pending) is { } error)
        {
            ShowToast(error);
            return;
        }
        await _hosts.SyncAsync();
        ShowToast("Aprovado: o curinga já responde.");
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

    private string Url(RouteItemViewModel item)
    {
        var settings = _config.Current.Settings;
        return Browser.UrlFor(item.Domain, settings.HttpPort, item.HttpsState == RouteHttpsState.On ? settings.HttpsPort : null, item.Route.Path);
    }

    private void Reconcile(SeverinoConfig config, bool force = false)
    {
        var routes = config.Routes;
        HasRoutes = routes.Count > 0;
        var enabled = routes.Count(r => r.Enabled);
        Summary = $"{Plural(routes.Count, "rota", "rotas")} · {enabled} {(enabled == 1 ? "ligada" : "ligadas")}";
        // Routes without a group first, then each group in the order it first appears.
        var groupOrder = routes.Select(r => r.Group).Distinct(StringComparer.Ordinal).OrderBy(g => g.Length == 0 ? 0 : 1).ToList();
        var search = Search.Trim();
        var visible = routes
            .Where(r => Matches(r, search))
            .OrderBy(r => groupOrder.IndexOf(r.Group))
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
                Items.Add(new RouteItemViewModel(route, (row, on) => _routes.SetEnabled(row.Id, on)));
        }

        var destinations = _dns?.Destinations() ?? [];
        foreach (var item in Items)
        {
            ApplyHealth(item.Id);
            item.HttpsState = !item.Route.Https || !_https.IsActive ? RouteHttpsState.Off
                : _https.Covers(item.Domain) ? RouteHttpsState.On
                : RouteHttpsState.Uncovered;
            item.Destination = Uri.TryCreate(item.Route.Target, UriKind.Absolute, out var target)
                ? destinations.FirstOrDefault(d => d.Name.Equals(target.IdnHost, StringComparison.OrdinalIgnoreCase))
                : null;
        }

        // The groups reuse the rows, so a toggle keeps its state; rebuilt only when membership changes.
        var signature = string.Join("|", Items.Select(i => i.Route.Group + ":" + i.Id));
        if (force || signature != _groupSignature)
        {
            _groupSignature = signature;
            RouteGroups.Clear();
            foreach (var group in Items.GroupBy(i => i.Route.Group))
                RouteGroups.Add(new RouteGroupViewModel(group.Key, [.. group], (name, on) => _routes.SetGroupEnabled(name, on)));
        }
        else
        {
            foreach (var group in RouteGroups)
                group.Refresh();
        }
    }

    private string? _groupSignature;

    /// <summary>By domain, group or destination host, so "gateway.k8s" finds what goes through it.</summary>
    private static bool Matches(RouteEntry route, string search) =>
        search.Length == 0
        || (route.Domain + route.Path).Contains(search, StringComparison.OrdinalIgnoreCase)
        || route.Group.Contains(search, StringComparison.OrdinalIgnoreCase)
        || (Uri.TryCreate(route.Target, UriKind.Absolute, out var target) && target.Host.Contains(search, StringComparison.OrdinalIgnoreCase));

    private void ApplyHealth(Guid routeId)
    {
        if (Items.FirstOrDefault(i => i.Id == routeId) is { Enabled: true } item)
            item.Health = _health.IsUp(routeId) switch
            {
                true => RouteHealthState.Up,
                false => RouteHealthState.Down,
                null => RouteHealthState.Unknown,
            };
    }
}
