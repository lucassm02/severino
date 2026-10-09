using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Severino.App.Services;
using Severino.Core.Configuration;
using Severino.Core.Routes;
using Severino.Proxy;

namespace Severino.App.ViewModels;

public sealed partial class RoutesViewModel : ObservableObject
{
    private static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(5);

    private readonly RouteService _routes;
    private readonly ConfigService _config;
    private readonly HealthMonitor _health;
    private readonly DialogService _dialogs;
    private readonly HttpsService _https;
    private readonly DispatcherTimer _undoTimer;
    private RemovedRoute? _removed;

    public RoutesViewModel(RouteService routes, ConfigService config, HealthMonitor health, DialogService dialogs, HttpsService https)
    {
        _routes = routes;
        _config = config;
        _health = health;
        _dialogs = dialogs;
        _https = https;
        _undoTimer = new DispatcherTimer { Interval = UndoWindow };
        _undoTimer.Tick += (_, _) => DismissUndo();

        Reconcile(config.Current.Routes);
        config.Changed += (_, c) => Dispatch(() => Reconcile(c.Routes));
        health.Changed += (_, h) => Dispatch(() => ApplyHealth(h.RouteId));
        https.Changed += (_, _) => Dispatch(() => Reconcile(_config.Current.Routes));
    }

    public ObservableCollection<RouteItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool HasRoutes { get; set; }

    public bool IsEmpty => !HasRoutes;

    /// <summary>"3 rotas · 2 ligadas".</summary>
    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    [ObservableProperty]
    public partial string? UndoMessage { get; set; }

    [ObservableProperty]
    public partial string? Toast { get; set; }

    partial void OnSearchChanged(string value) => Reconcile(_config.Current.Routes, force: true);

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
        _removed = _routes.Remove(item.Id);
        if (_removed is null)
            return;
        UndoMessage = $"{item.Domain} removido";
        _undoTimer.Stop();
        _undoTimer.Start();
    }

    [RelayCommand]
    private void Undo()
    {
        if (_removed is not null && !_routes.Restore(_removed))
            ShowToast("Não deu para desfazer: o domínio já está em outra rota.");
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
        _removed = null;
        UndoMessage = null;
    }

    private async void ShowToast(string message)
    {
        Toast = message;
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (Toast == message)
            Toast = null;
    }

    private void Reconcile(IReadOnlyList<RouteEntry> routes, bool force = false)
    {
        HasRoutes = routes.Count > 0;
        var enabled = routes.Count(r => r.Enabled);
        Summary = $"{routes.Count} {(routes.Count == 1 ? "rota" : "rotas")} · {enabled} {(enabled == 1 ? "ligada" : "ligadas")}";
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
    }

    private void ApplyHealth(Guid routeId)
    {
        var item = Items.FirstOrDefault(i => i.Id == routeId);
        if (item is null || !item.Enabled)
            return;
        item.Health = _health.IsUp(routeId) switch
        {
            true => RouteHealthState.Up,
            false => RouteHealthState.Down,
            null => RouteHealthState.Unknown,
        };
    }

    private static void Dispatch(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);
}
