using System.Windows;
using Severino.App.Resources;
using Severino.App.Tray;
using Severino.Core.Configuration;
using Severino.Core.Routes;
using Severino.Proxy;

namespace Severino.App.Services;

/// <summary>
/// The tray notices for routes and services with <see cref="RouteEntry.NotifyHealth"/>: one when
/// a target stops answering for <see cref="Grace"/>, one when it is back. Several at once share a
/// notice. Quiet while paused, and when the setting is off.
/// </summary>
public sealed class HealthNotifier(ConfigService config, HealthMonitor health, ProxyCoordinator coordinator, TrayService tray,
    Navigation navigation, Lazy<ShellService> shell, TimeProvider time) : IDisposable
{
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    /// <summary>Names listed in a notice that covers several; the rest are counted.</summary>
    private const int Listed = 4;

    private readonly HealthAlerts _alerts = new(Grace);
    private ITimer? _timer;

    public void Start()
    {
        health.Changed += OnChanged;
        _timer = time.CreateTimer(_ => Tick(), null, Interval, Interval);
    }

    public void Dispose()
    {
        health.Changed -= OnChanged;
        _timer?.Dispose();
    }

    private void OnChanged(object? sender, RouteHealth change) => _alerts.Observe(change.RouteId, change.IsUp, time.GetUtcNow());

    private void Tick()
    {
        if (coordinator.IsPaused || coordinator.IsStopped)
        {
            _alerts.Reset();
            return;
        }
        var current = Environments.Effective(config.Current);
        var (down, back) = _alerts.Take(time.GetUtcNow(), id => Watched(current, id) is not null && health.IsUp(id) != true);
        // A fall and a return in the same few seconds: the fall is the news.
        var (ids, falling) = down.Count > 0 ? (down, true) : (back, false);
        var items = ids.Select(id => Watched(current, id)).OfType<Watch>().ToList();
        if (items.Count > 0)
            Application.Current?.Dispatcher.BeginInvoke(() => Show(items, falling));
    }

    private sealed record Watch(string Name, string Target, AppTab Tab, string Search);

    /// <summary>The route or service behind <paramref name="id"/>, when it asked for notices and is on.</summary>
    private static Watch? Watched(SeverinoConfig current, Guid id)
    {
        if (!current.Settings.HealthNotifications)
            return null;
        if (current.Routes.FirstOrDefault(r => r.Id == id) is { Enabled: true, NotifyHealth: true } route)
        {
            var target = RouteRules.FolderOf(route.Target) ?? (RouteRules.TryParseTarget(route.Target, out var uri, out _) ? uri.Authority : route.Target);
            return new(route.Domain + route.Path, target, AppTab.Routes, route.Domain);
        }
        if (current.Services.FirstOrDefault(s => s.Id == id) is { Enabled: true, NotifyHealth: true, Names.Count: > 0, Ports.Count: > 0 } service)
            return new(service.Names[0], $"{service.Ports[0].TargetHost}:{service.Ports[0].TargetPort}", AppTab.Services, service.Names[0]);
        return null;
    }

    private void Show(IReadOnlyList<Watch> items, bool falling)
    {
        string title, message;
        if (items.Count == 1)
        {
            var item = items[0];
            title = string.Format(falling ? Strings.Health_DownOne : Strings.Health_BackOne, item.Name);
            message = string.Format(falling ? Strings.Health_DownOneText : Strings.Health_BackOneText, item.Target, (int)Grace.TotalSeconds);
        }
        else
        {
            title = string.Format(falling ? Strings.Health_DownMany : Strings.Health_BackMany, items.Count);
            var names = string.Join(", ", items.Take(Listed).Select(i => i.Name));
            message = items.Count > Listed ? string.Format(Strings.Health_AndMore, names, items.Count - Listed) : names;
        }

        // The click goes to the tab of the first one, filtered by it.
        var first = items[0];
        void Open()
        {
            shell.Value.ShowMainWindow();
            navigation.Show(first.Tab, items.Count == 1 ? first.Search : null);
        }
        if (falling)
            tray.ShowWarning(title, message, Open);
        else
            tray.ShowInfo(title, message, Open);
    }
}
