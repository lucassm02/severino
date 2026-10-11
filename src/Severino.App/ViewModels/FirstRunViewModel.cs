using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Severino.App.Resources;
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
    public partial string Detail { get; set; } = Strings.Health_Checking;

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

    public SetupCheck Helper { get; } = new(Strings.Status_HelperTitle);
    public SetupCheck Port { get; } = new(Strings.FirstRun_ProxyPort);
    public SetupCheck SystemProxyCheck { get; } = new(Strings.Status_SystemProxyTitle);

    public IReadOnlyList<SetupCheck> Checks => [Helper, Port, SystemProxyCheck];

    public RouteEditorViewModel Route { get; }

    /// <summary>Raised when the wizard is done, finished or skipped.</summary>
    public event EventHandler? Finished;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRouteStep), nameof(StepText), nameof(Subtitle))]
    public partial bool IsCheckStep { get; set; } = true;

    public bool IsRouteStep => !IsCheckStep;

    public string StepText => IsCheckStep ? Strings.FirstRun_Step1 : Strings.FirstRun_Step2;

    public string Subtitle => IsCheckStep
        ? Strings.FirstRun_CheckSubtitle
        : Strings.FirstRun_RouteSubtitle;

    /// <summary>HTTPS for the first route. Off by default: Windows asks to trust the CA, and http already works.</summary>
    [ObservableProperty]
    public partial bool UseHttps { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>"Criar e abrir", or the form's "Criar mesmo assim" when the name exists on the internet.</summary>
    public string CreateLabel => Route.SaveLabel == Strings.Common_Create ? Strings.FirstRun_CreateAndOpen : Route.SaveLabel;

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
                var result = await _https.ActivateAsync(prompt => DialogService.ConfirmTrustAsync(Strings.Settings_HttpsActivate, prompt));
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
                Helper.Set(CheckState.Ok, Strings.FirstRun_HelperOk);
                break;
            case HostsSyncState.HelperUnavailable:
                Helper.Set(CheckState.Problem,
                    Strings.FirstRun_HelperDown,
                    Strings.Common_TryAgain, retry);
                break;
            case HostsSyncState.Failed:
                Helper.Set(CheckState.Problem, string.Format(Strings.FirstRun_HelperFailed, status.Detail), Strings.Common_TryAgain, retry);
                break;
            default:
                Helper.Set(CheckState.Pending, Strings.Health_Checking);
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
                    ? Strings.FirstRun_Port80
                    : string.Format(Strings.FirstRun_PortOther, status.Port));
                break;
            case ProxyState.PortInUse when status.Port == 80:
                Port.Set(CheckState.Problem, string.Format(Strings.FirstRun_Port80Taken, status.Detail),
                    Strings.FirstRun_Use8080, new RelayCommand(() => _config.Update(c => c with { Settings = c.Settings with { HttpPort = 8080 } })));
                break;
            case ProxyState.PortInUse or ProxyState.Failed:
                Port.Set(CheckState.Problem, status.Detail ?? Strings.FirstRun_ProxyDidNotStart, Strings.Common_TryAgain, new AsyncRelayCommand(_coordinator.RetryAsync));
                break;
            default:
                Port.Set(CheckState.Pending, Strings.Health_Checking);
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
                string.Format(Strings.FirstRun_SystemProxy, settings.Server),
                taken.All(d => d.EndsWith(".sev", StringComparison.Ordinal)) ? Strings.FirstRun_AddSevException : Strings.Status_AddExceptions,
                new AsyncRelayCommand(async () =>
                {
                    await _systemProxy.AddExceptionsForAsync(domains);
                    EvaluateSystemProxy();
                }));
        }
        else if (settings.HasScript)
        {
            SystemProxyCheck.Set(CheckState.Warning,
                Strings.FirstRun_Pac);
        }
        else
        {
            SystemProxyCheck.Set(CheckState.Ok, settings.HasFixedProxy
                ? Strings.FirstRun_ProxyExcepted
                : Strings.FirstRun_NoProxy);
        }
    }
}
