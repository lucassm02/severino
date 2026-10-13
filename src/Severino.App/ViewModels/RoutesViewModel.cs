using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Severino.App.Resources;
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
    private Task NewRouteAsync() => CreateRouteAsync(null);

    [RelayCommand]
    private Task NewRouteInGroupAsync(RouteGroupViewModel group) => CreateRouteAsync(group.Name);

    private async Task CreateRouteAsync(string? group)
    {
        var saved = _dialogs.EditRoute(null, group: group, madeInstead: MadeInstead);
        if (saved is not null && !await OfferReissueAsync(saved))
            ShowToast(string.Format(Strings.Routes_Ready, saved.Domain));
    }

    /// <summary>The route form became a service or DNS entry, which lives in another tab.</summary>
    private void MadeInstead(AppTab tab, string name) =>
        ShowToast(string.Format(tab == AppTab.Services ? Strings.Routes_BecameService : Strings.Routes_BecameDns, name),
            tab == AppTab.Services ? Strings.Common_SeeInServices : Strings.Common_SeeInDns,
            () => _navigation.Show(tab, name));

    [RelayCommand]
    private async Task RenameGroupAsync(RouteGroupViewModel group)
    {
        if (await DialogService.PromptAsync(Strings.Routes_RenameGroup, Strings.Routes_RenameGroupHint, group.Name, Strings.Common_Rename) is not { } name)
            return;
        if (name.Trim().Length == 0)
        {
            ShowToast(Strings.Routes_RenameEmpty);
            return;
        }
        _routes.RenameGroup(group.Name, name);
    }

    [RelayCommand]
    private void Ungroup(RouteGroupViewModel group)
    {
        var name = group.Name;
        _routes.RenameGroup(name, "");
        ShowToast(string.Format(Strings.Routes_Ungrouped, name));
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
            var lead = string.Format(Strings.Routes_ReissueLead, saved.Domain);
            ShowToast((await _https.ReissueAsync(prompt => DialogService.ConfirmTrustAsync(Strings.Settings_ReissueTitle, prompt, lead))) switch
            {
                HttpsActionResult.Done or HttpsActionResult.DoneOldRootKept => string.Format(Strings.Routes_Reissued, saved.Domain),
                HttpsActionResult.Cancelled => string.Format(Strings.Routes_ReissueCancelled, saved.Domain),
                _ => string.Format(Strings.Routes_ReissueDeclined, saved.Domain),
            });
        }
        catch (Exception ex)
        {
            ShowToast(string.Format(Strings.Routes_ReissueFailed, ex.Message));
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
            ShowToast(Strings.Routes_MakeDnsNeedsIp);
            return;
        }
        if (!await DialogService.ConfirmAsync(Strings.Routes_MakeDnsTitle,
                string.Format(Strings.Routes_MakeDnsConfirm, item.Domain, ip, target.Port),
                Strings.Routes_MakeDnsButton))
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
        ShowToast(string.Format(Strings.Routes_NowDns, item.Domain), Strings.Common_SeeInDns, () => _navigation.Show(AppTab.Dns, item.Domain));
    }

    /// <summary>"*.meuapp.sev espera aprovação…", while a wildcard's suffix is not approved.</summary>
    [ObservableProperty]
    public partial string? WildcardPendingText { get; set; }

    private void UpdateWildcardPending()
    {
        var names = _hosts?.Status.PendingEntries.Select(e => e.Name).Distinct().ToList() ?? [];
        WildcardPendingText = names.Count == 0 ? null
            : string.Format(names.Count == 1 ? Strings.Routes_WildcardPendingOne : Strings.Routes_WildcardPending, string.Join(", ", names));
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
        ShowToast(Strings.Routes_WildcardApproved);
    }

    [RelayCommand]
    private void Open(RouteItemViewModel item) => Browser.Open(Url(item));

    [RelayCommand]
    private void CopyUrl(RouteItemViewModel item)
    {
        Clipboard.SetText(Url(item));
        ShowToast(Strings.Routes_UrlCopied);
    }

    [RelayCommand]
    private void Remove(RouteItemViewModel item)
    {
        if (_routes.Remove(item.Id) is not { } removed)
            return;
        ShowUndo(string.Format(Strings.Common_Removed, item.Domain), () => _routes.Restore(removed) ? null : Strings.Routes_UndoTaken);
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
        Summary = $"{Plural(routes.Count, Strings.Count_Route, Strings.Count_Routes)} · {Plural(enabled, Strings.Routes_EnabledOne, Strings.Routes_EnabledMany)}";
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
            item.OtherEnvironment = !Environments.IsActive(item.Route.Environment, config);
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
        || (RouteRules.FolderOf(route.Target) is { } folder && folder.Contains(search, StringComparison.OrdinalIgnoreCase))
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
