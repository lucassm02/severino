using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Severino.App.Resources;
using Severino.App.Services;
using Severino.Core.Configuration;
using Severino.Core.Routes;
using Severino.Proxy;
using Severino.Proxy.Certificates;

namespace Severino.App.ViewModels;

/// <summary>The line at the bottom of the window: proxy, HTTPS and hosts state, with a way to fix each.</summary>
public sealed partial class StatusBarViewModel : ObservableObject
{
    private readonly ProxyServer _proxy;
    private readonly HostsSync _hosts;
    private readonly ProxyCoordinator _coordinator;
    private readonly HttpsService _https;
    private readonly Navigation _navigation;
    private readonly SystemProxy _systemProxy;
    private readonly ServiceForwarder _services;

    public StatusBarViewModel(ProxyServer proxy, HostsSync hosts, ProxyCoordinator coordinator, HttpsService https, ConfigService config,
        Navigation navigation, SystemProxy systemProxy, ServiceForwarder services)
    {
        _systemProxy = systemProxy;
        _services = services;
        services.StatusChanged += (_, _) => Dispatch(Refresh);
        systemProxy.Changed += (_, _) => Dispatch(Refresh);
        _proxy = proxy;
        _hosts = hosts;
        _coordinator = coordinator;
        _https = https;
        _navigation = navigation;
        proxy.StatusChanged += (_, _) => Dispatch(Refresh);
        hosts.StatusChanged += (_, _) => Dispatch(Refresh);
        https.Changed += (_, _) => Dispatch(Refresh);
        config.Changed += (_, _) => Dispatch(Refresh);
        coordinator.PausedChanged += (_, _) => Dispatch(Refresh);
        Refresh();
    }

    [ObservableProperty]
    public partial string ProxyText { get; set; } = "";

    [ObservableProperty]
    public partial bool ProxyHasProblem { get; set; }

    [ObservableProperty]
    public partial string HttpsText { get; set; } = "";

    [ObservableProperty]
    public partial bool HttpsHasProblem { get; set; }

    [ObservableProperty]
    public partial string HostsText { get; set; } = "";

    [ObservableProperty]
    public partial bool HostsHasProblem { get; set; }

    /// <summary>Null when the system proxy is out of the way; then the segment is hidden.</summary>
    [ObservableProperty]
    public partial string? SystemProxyText { get; set; }

    /// <summary>Null while every service route port is listening; then the segment is hidden.</summary>
    [ObservableProperty]
    public partial string? ServicesText { get; set; }

    /// <summary>A fixed proxy takes route domains. A PAC script only might, so it is shown but not counted.</summary>
    [ObservableProperty]
    public partial bool SystemProxyHasProblem { get; set; }

    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    /// <summary>Something needs the user; pausing on purpose does not count.</summary>
    [ObservableProperty]
    public partial bool HasProblem { get; set; }

    /// <summary>One line for the tray tooltip: the first problem, or that all is well.</summary>
    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [RelayCommand]
    private async Task FixProxyAsync()
    {
        if (_coordinator.IsPaused)
            await _coordinator.ResumeAsync();
        else if (_proxy.Status.State == ProxyState.PortInUse)
            _navigation.ShowSettings();
        else
            await _coordinator.RetryAsync();
    }

    [RelayCommand]
    private async Task FixHttpsAsync()
    {
        if (_https.IsActive && _proxy.HttpsStatus.State == ProxyState.Failed)
            await _coordinator.RetryAsync();
        else
            _navigation.ShowSettings();
    }

    [RelayCommand]
    private async Task FixSystemProxyAsync()
    {
        var settings = _systemProxy.Read();
        var taken = _systemProxy.Uncovered();
        if (taken.Count == 0)
        {
            await DialogService.ShowInfoAsync(Strings.Status_SystemProxyTitle,
                string.Format(Strings.Status_PacInfo, settings.AutoConfigUrl));
            return;
        }

        if (!await DialogService.ConfirmAsync(Strings.Status_SystemProxyTitle,
                string.Format(Strings.Status_ProxyConfirm, settings.Server, string.Join(", ", taken)),
                Strings.Status_AddExceptions))
            return;

        try
        {
            var added = await _systemProxy.AddExceptionsAsync();
            Refresh();
            await DialogService.ShowInfoAsync(Strings.Status_SystemProxyTitle, added.Count == 0
                ? Strings.Status_ExceptionsThere
                : string.Format(Strings.Status_ExceptionsAdded, string.Join(", ", added)));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Company policy can lock the proxy settings.
            await DialogService.ShowInfoAsync(Strings.Status_SystemProxyTitle,
                string.Format(Strings.Status_ExceptionsLocked, string.Join(", ", taken), ex.Message));
        }
    }

    [RelayCommand]
    private async Task FixServicesAsync()
    {
        var failing = _services.Statuses.Where(s => s.State != ProxyState.Running).ToList();
        if (failing.Count == 0)
            return;
        var lines = string.Join("\n", failing.Select(s => $"• {s.Name} ({s.Address}:{s.Port}): {s.Detail}"));
        if (await DialogService.ConfirmAsync(Strings.Tab_Services,
                string.Format(Strings.Status_PortsDidNotOpen, lines),
                Strings.Common_TryAgain))
            await _coordinator.RetryAsync();
    }

    [RelayCommand]
    private void FixHosts() => DialogService.ShowMessage(
        Strings.Status_HelperTitle,
        _hosts.Status.State == HostsSyncState.HelperUnavailable
            ? string.Format(Strings.Status_HelperMissing, _hosts.Status.Detail)
            : string.Format(Strings.Status_HelperFailed, _hosts.Status.Detail));

    private void Refresh()
    {
        IsPaused = _coordinator.IsPaused;
        var proxy = _proxy.Status;
        (ProxyText, ProxyHasProblem) = proxy.State switch
        {
            // A link, so it reads as something to undo; it is not counted as a problem below.
            _ when IsPaused => (Strings.Status_PausedResume, true),
            ProxyState.Running when _proxy.HttpsStatus.State == ProxyState.Running => (string.Format(Strings.Status_ProxyOnBoth, proxy.Port, _proxy.HttpsStatus.Port), false),
            ProxyState.Running => (string.Format(Strings.Status_ProxyOn, proxy.Port), false),
            ProxyState.PortInUse => (string.Format(Strings.Status_ChangePort, proxy.Detail), true),
            ProxyState.Failed => (string.Format(Strings.Status_ProxyStopped, proxy.Detail), true),
            _ => (Strings.Status_ProxyStarting, false),
        };

        var https = _proxy.HttpsStatus;
        var uncovered = _https.Uncovered().Count;
        (HttpsText, HttpsHasProblem) = _https.Status.State switch
        {
            LocalCaState.Disabled => (Strings.Status_HttpsOff, false),
            _ when IsPaused => (Strings.Status_HttpsPaused, false),
            LocalCaState.NotTrusted or LocalCaState.Unreadable => (Strings.Status_HttpsInvalidCa, true),
            _ => https.State switch
            {
                ProxyState.PortInUse => (string.Format(Strings.Status_HttpsChangePort, https.Detail), true),
                ProxyState.Failed => (string.Format(Strings.Status_HttpsStopped, https.Detail), true),
                ProxyState.Running when uncovered > 0 => (uncovered == 1 ? Strings.Status_HttpsUncoveredOne : string.Format(Strings.Status_HttpsUncoveredMany, uncovered), true),
                ProxyState.Running => (Strings.Status_HttpsOk, false),
                _ => (Strings.Status_HttpsStarting, false),
            },
        };

        var hosts = _hosts.Status;
        (HostsText, HostsHasProblem) = hosts.State switch
        {
            HostsSyncState.Synced when IsPaused => (Strings.Status_HostsWithoutRoutes, false),
            HostsSyncState.Synced => (Strings.Status_HostsOk, false),
            HostsSyncState.HelperUnavailable => (Strings.Status_HostsHelperUnavailable, true),
            HostsSyncState.Failed => (Strings.Status_HostsFailed, true),
            _ => (Strings.Status_HostsSyncing, false),
        };

        var failing = _services.Statuses.Where(s => s.State != ProxyState.Running).ToList();
        ServicesText = failing switch
        {
            [] => null,
            [var one] => string.Format(Strings.Status_ServicePortDown, one.Name, one.Port),
            _ => string.Format(Strings.Status_ServicePortsDown, failing.Count),
        };

        var systemProxy = _systemProxy.Read();
        var taken = _systemProxy.Uncovered().Count;
        (SystemProxyText, SystemProxyHasProblem) = (taken, systemProxy.HasScript) switch
        {
            ( > 0, _) => (taken == 1 ? Strings.Status_SystemProxyInWayOne : string.Format(Strings.Status_SystemProxyInWayMany, taken), true),
            (_, true) => (Strings.Status_PacActive, false),
            _ => ((string?)null, false),
        };

        HasProblem = !IsPaused && (ProxyHasProblem || HttpsHasProblem || HostsHasProblem || SystemProxyHasProblem || ServicesText is not null);
        Summary = IsPaused ? Strings.Status_Paused
            : ProxyHasProblem ? ProxyText
            : HttpsHasProblem ? HttpsText
            : HostsHasProblem ? HostsText
            : SystemProxyHasProblem ? SystemProxyText!
            : ServicesText ?? Strings.Status_AllGood;
    }

    private static void Dispatch(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);
}
