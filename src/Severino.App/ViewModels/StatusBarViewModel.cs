using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Severino.App.Services;
using Severino.Core.Routes;
using Severino.Proxy;

namespace Severino.App.ViewModels;

/// <summary>The line at the bottom of the window: proxy and hosts state, with a way to fix each.</summary>
public sealed partial class StatusBarViewModel : ObservableObject
{
    private readonly ProxyServer _proxy;
    private readonly HostsSync _hosts;
    private readonly ProxyCoordinator _coordinator;

    public StatusBarViewModel(ProxyServer proxy, HostsSync hosts, ProxyCoordinator coordinator)
    {
        _proxy = proxy;
        _hosts = hosts;
        _coordinator = coordinator;
        proxy.StatusChanged += (_, _) => Dispatch(Refresh);
        hosts.StatusChanged += (_, _) => Dispatch(Refresh);
        Refresh();
    }

    /// <summary>Asks the window to show the Settings tab.</summary>
    public event EventHandler? OpenSettingsRequested;

    [ObservableProperty]
    public partial string ProxyText { get; set; } = "";

    [ObservableProperty]
    public partial bool ProxyHasProblem { get; set; }

    [ObservableProperty]
    public partial string HostsText { get; set; } = "";

    [ObservableProperty]
    public partial bool HostsHasProblem { get; set; }

    [RelayCommand]
    private async Task FixProxyAsync()
    {
        if (_proxy.Status.State == ProxyState.PortInUse)
            OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
        else
            await _coordinator.RetryAsync();
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
            ProxyState.Running => ($"Proxy ativo :{proxy.Port}", false),
            ProxyState.PortInUse => ($"{proxy.Detail} Trocar porta", true),
            ProxyState.Failed => ($"Proxy parado: {proxy.Detail} Tentar de novo", true),
            _ => ("Proxy iniciando…", false),
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
