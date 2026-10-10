using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Severino.App.Services;
using Severino.Core.Configuration;
using Severino.Core.Domains;
using Severino.Core.Network;
using Severino.Core.Routes;
using Severino.Proxy;

namespace Severino.App.ViewModels;

public enum CheckState
{
    Pending,
    Ok,
    /// <summary>Probably fine, but worth knowing; does not hold anything up.</summary>
    Warning,
    Problem,
}

/// <summary>One line of the first-run check: what was checked, how it went, and a fix when there is one.</summary>
public sealed partial class SetupCheck(string title) : ObservableObject
{
    public string Title { get; } = title;

    [ObservableProperty]
    public partial CheckState State { get; set; }

    [ObservableProperty]
    public partial string Detail { get; set; } = "Verificando…";

    /// <summary>Null hides the fix button.</summary>
    [ObservableProperty]
    public partial string? FixLabel { get; set; }

    [ObservableProperty]
    public partial ICommand? FixCommand { get; set; }

    public void Set(CheckState state, string detail, string? fixLabel = null, ICommand? fix = null)
    {
        State = state;
        Detail = detail;
        FixLabel = fixLabel;
        FixCommand = fix;
    }
}

/// <summary>
/// The first-run wizard: a check of what Severino needs, each problem with its fix, then the
/// first route, with HTTPS as an option. Shown once; Settings can open it again.
/// </summary>
public sealed partial class FirstRunViewModel : ObservableObject
{
    /// <summary>A name under the suggested TLD, to ask the system proxy about before any route exists.</summary>
    private const string SampleDomain = "meuapp.sev";

    private readonly ConfigService _config;
    private readonly ProxyServer _proxy;
    private readonly HostsSync _hosts;
    private readonly SystemProxy _systemProxy;
    private readonly ProxyCoordinator _coordinator;
    private readonly HttpsService _https;

    public FirstRunViewModel(ConfigService config, ProxyServer proxy, HostsSync hosts, SystemProxy systemProxy,
        ProxyCoordinator coordinator, HttpsService https, RouteService routes, DomainInspector inspector, Navigation navigation)
    {
        _config = config;
        _proxy = proxy;
        _hosts = hosts;
        _systemProxy = systemProxy;
        _coordinator = coordinator;
        _https = https;

        Route = new RouteEditorViewModel(routes, inspector, https, navigation,
            new RouteEntry { Domain = SampleDomain, Target = "http://localhost:3000" }, isCopy: true)
        {
            ShowHttpsOptions = false,
            IsWizard = true,
        };
        Route.CloseRequested += (_, saved) =>
        {
            if (saved)
                _ = OnRouteCreatedAsync();
        };
        Route.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RouteEditorViewModel.SaveLabel))
                OnPropertyChanged(nameof(CreateLabel));
        };

        proxy.StatusChanged += OnSomethingChanged;
        hosts.StatusChanged += OnSomethingChanged;
        systemProxy.Changed += OnSomethingChanged;
        Evaluate();
    }

    public SetupCheck Helper { get; } = new("Serviço auxiliar");
    public SetupCheck Port { get; } = new("Porta do proxy");
    public SetupCheck SystemProxyCheck { get; } = new("Proxy do sistema");

    public IReadOnlyList<SetupCheck> Checks => [Helper, Port, SystemProxyCheck];

    public RouteEditorViewModel Route { get; }

    /// <summary>Raised when the wizard is done, finished or skipped.</summary>
    public event EventHandler? Finished;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRouteStep), nameof(StepText), nameof(Subtitle))]
    public partial bool IsCheckStep { get; set; } = true;

    public bool IsRouteStep => !IsCheckStep;

    public string StepText => IsCheckStep ? "Passo 1 de 2" : "Passo 2 de 2";

    public string Subtitle => IsCheckStep
        ? "Antes de começar, uma checagem rápida do que o Severino precisa."
        : "Agora diga qual domínio leva a qual servidor. Dá para mudar tudo depois.";

    /// <summary>HTTPS for the first route. Off by default: Windows asks to trust the CA, and http already works.</summary>
    [ObservableProperty]
    public partial bool UseHttps { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>"Criar e abrir", or the form's "Criar mesmo assim" when the name exists on the internet.</summary>
    public string CreateLabel => Route.SaveLabel == "Criar" ? "Criar e abrir" : Route.SaveLabel;

    [RelayCommand]
    private void Continue() => IsCheckStep = false;

    [RelayCommand]
    private void Skip() => Finish();

    [RelayCommand]
    private Task Create() => Route.SaveCommand.ExecuteAsync(null);

    private async Task OnRouteCreatedAsync()
    {
        var saved = Route.Saved!;
        IsBusy = true;
        try
        {
            var https = false;
            if (UseHttps)
            {
                var result = await _https.ActivateAsync(prompt => DialogService.ConfirmTrustAsync("Ativar HTTPS", prompt));
                https = result is HttpsActionResult.Done or HttpsActionResult.DoneOldRootKept;
            }
            var settings = _config.Current.Settings;
            Browser.Open(Browser.UrlFor(saved.Domain, settings.HttpPort, https ? settings.HttpsPort : null, saved.Path));
        }
        finally
        {
            IsBusy = false;
            Finish();
        }
    }

    private void Finish()
    {
        _proxy.StatusChanged -= OnSomethingChanged;
        _hosts.StatusChanged -= OnSomethingChanged;
        _systemProxy.Changed -= OnSomethingChanged;
        _config.Update(c => c with { State = c.State with { FirstRunCompleted = true } });
        Finished?.Invoke(this, EventArgs.Empty);
    }

    private void OnSomethingChanged<T>(object? sender, T e) => Application.Current?.Dispatcher.BeginInvoke(Evaluate);

    /// <summary>Reads every check again. Public for tests, which have no dispatcher.</summary>
    public void Evaluate()
    {
        EvaluateHelper();
        EvaluatePort();
        EvaluateSystemProxy();
    }

    private void EvaluateHelper()
    {
        var status = _hosts.Status;
        var retry = new AsyncRelayCommand(() => _hosts.SyncAsync());
        switch (status.State)
        {
            case HostsSyncState.Synced:
                Helper.Set(CheckState.Ok, "Respondendo. O Severino consegue gravar o arquivo hosts.");
                break;
            case HostsSyncState.HelperUnavailable:
                Helper.Set(CheckState.Problem,
                    "Não respondeu. Sem ele, os domínios não chegam ao Severino. Reinstalar o Severino instala o serviço de novo.",
                    "Tentar de novo", retry);
                break;
            case HostsSyncState.Failed:
                Helper.Set(CheckState.Problem, $"Respondeu, mas não conseguiu gravar o hosts: {status.Detail}", "Tentar de novo", retry);
                break;
            default:
                Helper.Set(CheckState.Pending, "Verificando…");
                break;
        }
    }

    private void EvaluatePort()
    {
        var status = _proxy.Status;
        switch (status.State)
        {
            case ProxyState.Running:
                Port.Set(CheckState.Ok, status.Port == 80
                    ? "O Severino está escutando na porta 80."
                    : $"O Severino está escutando na porta {status.Port}. As URLs levam :{status.Port}, como http://meuapp.sev:{status.Port}.");
                break;
            case ProxyState.PortInUse when status.Port == 80:
                Port.Set(CheckState.Problem, $"{status.Detail} Dá para liberar a porta ou usar a 8080, e aí as URLs levam :8080.",
                    "Usar a porta 8080", new RelayCommand(() => _config.Update(c => c with { Settings = c.Settings with { HttpPort = 8080 } })));
                break;
            case ProxyState.PortInUse or ProxyState.Failed:
                Port.Set(CheckState.Problem, status.Detail ?? "O proxy não subiu.", "Tentar de novo", new AsyncRelayCommand(_coordinator.RetryAsync));
                break;
            default:
                Port.Set(CheckState.Pending, "Verificando…");
                break;
        }
    }

    private void EvaluateSystemProxy()
    {
        var settings = _systemProxy.Read();
        // The sample stands for the routes to come; existing routes count too.
        var domains = RouteRules.ActiveDomains(_config.Current.Routes).Append(SampleDomain).ToList();
        var taken = ProxyBypass.Uncovered(settings, domains);
        if (taken.Count > 0)
        {
            SystemProxyCheck.Set(CheckState.Problem,
                $"O Windows usa o proxy {settings.Server}, e o Edge e o Chrome mandariam os domínios do Severino para ele. Uma exceção resolve.",
                taken.All(d => d.EndsWith(".sev", StringComparison.Ordinal)) ? "Adicionar exceção para .sev" : "Adicionar exceções",
                new AsyncRelayCommand(async () =>
                {
                    await _systemProxy.AddExceptionsForAsync(domains);
                    EvaluateSystemProxy();
                }));
        }
        else if (settings.HasScript)
        {
            SystemProxyCheck.Set(CheckState.Warning,
                "O Windows usa um script de proxy, que decide sozinho para onde cada endereço vai. Se uma rota não abrir, a barra inferior do Severino ajuda.");
        }
        else
        {
            SystemProxyCheck.Set(CheckState.Ok, settings.HasFixedProxy
                ? "Há um proxy, mas os domínios do Severino já estão nas exceções."
                : "Nenhum proxy no caminho.");
        }
    }
}
