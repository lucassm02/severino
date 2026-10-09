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
    private readonly DispatcherTimer _undoTimer;
    private RemovedRoute? _removed;

    public RoutesViewModel(RouteService routes, ConfigService config, HealthMonitor health, DialogService dialogs)
    {
        _routes = routes;
        _config = config;
        _health = health;
        _dialogs = dialogs;
        _undoTimer = new DispatcherTimer { Interval = UndoWindow };
        _undoTimer.Tick += (_, _) => DismissUndo();

        Reconcile(config.Current.Routes);
        config.Changed += (_, c) => Dispatch(() => Reconcile(c.Routes));
        health.Changed += (_, h) => Dispatch(() => ApplyHealth(h.RouteId));
    }

    public ObservableCollection<RouteItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool HasRoutes { get; set; }

    public bool IsEmpty => !HasRoutes;

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    [ObservableProperty]
    public partial string? UndoMessage { get; set; }

    [ObservableProperty]
    public partial string? Toast { get; set; }

    partial void OnSearchChanged(string value) => Reconcile(_config.Current.Routes, force: true);

    [RelayCommand]
    private void NewRoute()
    {
        var saved = _dialogs.EditRoute(null);
        if (saved is not null)
            ShowToast($"{saved.Domain} pronto");
    }

    [RelayCommand]
    private void Edit(RouteItemViewModel item) => _dialogs.EditRoute(item.Route);

    [RelayCommand]
    private void Duplicate(RouteItemViewModel item) =>
        _dialogs.EditRoute(item.Route with { Id = Guid.NewGuid(), Domain = "" }, isCopy: true);

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

    private string Url(RouteItemViewModel item) => Browser.UrlFor(item.Domain, _config.Current.Settings.HttpPort);

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
            ApplyHealth(item.Id);
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
