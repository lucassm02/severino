using System.IO;
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
    private readonly SystemProxy _systemProxy;

    public StatusBarViewModel(ProxyServer proxy, HostsSync hosts, ProxyCoordinator coordinator, HttpsService https, ConfigService config,
        Navigation navigation, SystemProxy systemProxy)
    {
        _systemProxy = systemProxy;
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
            await DialogService.ShowInfoAsync("Proxy do sistema",
                "O Windows usa um script de proxy (PAC), que decide sozinho para onde cada endereço vai. O Severino não consegue saber se ele desvia as suas rotas.\n\n" +
                "Se uma rota não abrir no navegador, peça para o script ignorar esses domínios, ou teste com a VPN desligada.\n\n" +
                $"Script: {settings.AutoConfigUrl}");
            return;
        }

        if (!await DialogService.ConfirmAsync("Proxy do sistema",
                $"O Windows está configurado para usar o proxy {settings.Server}. O Edge e o Chrome mandariam {string.Join(", ", taken)} para ele, e não para o Severino.\n\n" +
                "Adicionar esses domínios às exceções do proxy? Eles entram na mesma lista de Opções da Internet, e o Severino tira só o que adicionou quando você usar \"Limpar tudo\".",
                "Adicionar exceções"))
            return;

        try
        {
            var added = await _systemProxy.AddExceptionsAsync();
            Refresh();
            await DialogService.ShowInfoAsync("Proxy do sistema", added.Count == 0
                ? "As exceções já estavam lá."
                : $"Exceções adicionadas: {string.Join(", ", added)}. Recarregue a página no navegador.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Company policy can lock the proxy settings.
            await DialogService.ShowInfoAsync("Proxy do sistema",
                $"O Windows não deixou alterar as exceções, talvez por uma política da empresa. Peça para incluir {string.Join(", ", taken)} nas exceções do proxy.\n\nDetalhe: {ex.Message}");
        }
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
        IsPaused = _coordinator.IsPaused;
        var proxy = _proxy.Status;
        (ProxyText, ProxyHasProblem) = proxy.State switch
        {
            // A link, so it reads as something to undo; it is not counted as a problem below.
            _ when IsPaused => ("Severino pausado. Retomar", true),
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
            _ when IsPaused => ("HTTPS pausado", false),
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
            HostsSyncState.Synced when IsPaused => ("hosts sem as rotas", false),
            HostsSyncState.Synced => ("hosts ok", false),
            HostsSyncState.HelperUnavailable => ("hosts: serviço auxiliar indisponível", true),
            HostsSyncState.Failed => ("hosts: falha ao gravar", true),
            _ => ("hosts: sincronizando…", false),
        };

        var systemProxy = _systemProxy.Read();
        var taken = _systemProxy.Uncovered().Count;
        (SystemProxyText, SystemProxyHasProblem) = (taken, systemProxy.HasScript) switch
        {
            ( > 0, _) => (taken == 1 ? "proxy do sistema no caminho de 1 domínio" : $"proxy do sistema no caminho de {taken} domínios", true),
            (_, true) => ("proxy por script ativo", false),
            _ => ((string?)null, false),
        };

        HasProblem = !IsPaused && (ProxyHasProblem || HttpsHasProblem || HostsHasProblem || SystemProxyHasProblem);
        Summary = IsPaused ? "pausado"
            : ProxyHasProblem ? ProxyText
            : HttpsHasProblem ? HttpsText
            : HostsHasProblem ? HostsText
            : SystemProxyHasProblem ? SystemProxyText!
            : "tudo certo";
    }

    private static void Dispatch(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);
}
