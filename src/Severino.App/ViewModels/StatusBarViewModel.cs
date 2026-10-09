using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    public StatusBarViewModel(ProxyServer proxy, HostsSync hosts, ProxyCoordinator coordinator, HttpsService https, ConfigService config, Navigation navigation)
    {
        _proxy = proxy;
        _hosts = hosts;
        _coordinator = coordinator;
        _https = https;
        _navigation = navigation;
        proxy.StatusChanged += (_, _) => Dispatch(Refresh);
        hosts.StatusChanged += (_, _) => Dispatch(Refresh);
        https.Changed += (_, _) => Dispatch(Refresh);
        config.Changed += (_, _) => Dispatch(Refresh);
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

    [RelayCommand]
    private async Task FixProxyAsync()
    {
        if (_proxy.Status.State == ProxyState.PortInUse)
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
    private void FixHosts() => DialogService.ShowMessage(
        "Serviço auxiliar",
        _hosts.Status.State == HostsSyncState.HelperUnavailable
            ? "O Severino precisa do serviço auxiliar para gravar o arquivo hosts.\n\n" +
              "Enquanto não há instalador, rode num terminal como administrador, na pasta do projeto:\n\n" +
              "    ./scripts/dev-helper.ps1 install\n\n" +
              $"Detalhe: {_hosts.Status.Detail}"
            : $"O serviço auxiliar não conseguiu atualizar o hosts.\n\nDetalhe: {_hosts.Status.Detail}");

    private void Refresh()
    {
        var proxy = _proxy.Status;
        (ProxyText, ProxyHasProblem) = proxy.State switch
        {
            ProxyState.Running when _proxy.HttpsStatus.State == ProxyState.Running => ($"Proxy ativo :{proxy.Port} :{_proxy.HttpsStatus.Port}", false),
            ProxyState.Running => ($"Proxy ativo :{proxy.Port}", false),
            ProxyState.PortInUse => ($"{proxy.Detail} Trocar porta", true),
            ProxyState.Failed => ($"Proxy parado: {proxy.Detail} Tentar de novo", true),
            _ => ("Proxy iniciando…", false),
        };

        var https = _proxy.HttpsStatus;
        var uncovered = _https.Uncovered().Count;
        (HttpsText, HttpsHasProblem) = _https.Status.State switch
        {
            LocalCaState.Disabled => ("HTTPS desativado", false),
            LocalCaState.NotTrusted or LocalCaState.Unreadable => ("HTTPS: CA inválida", true),
            _ => https.State switch
            {
                ProxyState.PortInUse => ($"HTTPS: {https.Detail} Trocar porta", true),
                ProxyState.Failed => ($"HTTPS parado: {https.Detail} Tentar de novo", true),
                ProxyState.Running when uncovered > 0 => (uncovered == 1 ? "HTTPS: 1 domínio fora da CA" : $"HTTPS: {uncovered} domínios fora da CA", true),
                ProxyState.Running => ("HTTPS ok", false),
                _ => ("HTTPS iniciando…", false),
            },
        };

        var hosts = _hosts.Status;
        (HostsText, HostsHasProblem) = hosts.State switch
        {
            HostsSyncState.Synced => ("hosts ok", false),
            HostsSyncState.HelperUnavailable => ("hosts: serviço auxiliar indisponível", true),
            HostsSyncState.Failed => ("hosts: falha ao gravar", true),
            _ => ("hosts: sincronizando…", false),
        };
    }

    private static void Dispatch(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);
}
