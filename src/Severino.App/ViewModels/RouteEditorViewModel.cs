using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Severino.Contracts;
using Severino.Core.Configuration;
using Severino.Core.Domains;
using Severino.Core.Routes;
using Severino.Proxy;

namespace Severino.App.ViewModels;

/// <summary>The "Nova rota" / "Editar rota" form.</summary>
public sealed partial class RouteEditorViewModel : ObservableObject
{
    private static readonly TimeSpan DomainCheckDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PortCheckDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan SaveWaitForDomainCheck = TimeSpan.FromSeconds(1.5);

    private readonly RouteService _routes;
    private readonly DomainInspector _inspector;
    private readonly RouteEntry _original;
    private CancellationTokenSource? _domainCheck;
    private CancellationTokenSource? _portCheck;
    private Task _pendingDomainCheck = Task.CompletedTask;
    private string? _confirmedExistingDomain;

    public RouteEditorViewModel(RouteService routes, DomainInspector inspector, RouteEntry? existing, bool isCopy)
    {
        _routes = routes;
        _inspector = inspector;
        IsNew = existing is null || isCopy;
        _original = existing ?? new RouteEntry { Domain = "", Target = "http://localhost:3000" };

        Domain = _original.Domain;
        if (Uri.TryCreate(_original.Target, UriKind.Absolute, out var target))
        {
            Scheme = target.Scheme;
            TargetHost = target.Host;
            TargetPort = target.Port.ToString();
        }
        else
        {
            Scheme = Uri.UriSchemeHttp;
            TargetHost = "localhost";
            TargetPort = "3000";
        }
        PreserveHost = _original.PreserveHost;
        IgnoreTargetCertErrors = _original.IgnoreTargetCertErrors;
        Notes = _original.Notes;
        ShowAdvanced = PreserveHost || IgnoreTargetCertErrors || Notes.Length > 0;
        IsReady = true;
        ScheduleChecks();
    }

    public bool IsNew { get; }
    public string Title => IsNew ? "Nova rota" : "Editar rota";
    public string SaveLabel => _confirmedExistingDomain is not null && _confirmedExistingDomain == NormalizedDomain
        ? "Criar mesmo assim"
        : IsNew ? "Criar" : "Salvar";

    public IReadOnlyList<string> Schemes { get; } = [Uri.UriSchemeHttp, Uri.UriSchemeHttps];

    /// <summary>The route that was saved, once <see cref="SaveCommand"/> succeeds.</summary>
    public RouteEntry? Saved { get; private set; }

    /// <summary>Raised when the form should close; true when a route was saved.</summary>
    public event EventHandler<bool>? CloseRequested;

    private bool IsReady { get; }

    [ObservableProperty]
    public partial string Domain { get; set; }

    [ObservableProperty]
    public partial string Scheme { get; set; }

    [ObservableProperty]
    public partial string TargetHost { get; set; }

    [ObservableProperty]
    public partial string TargetPort { get; set; }

    [ObservableProperty]
    public partial bool PreserveHost { get; set; }

    [ObservableProperty]
    public partial bool IgnoreTargetCertErrors { get; set; }

    [ObservableProperty]
    public partial string Notes { get; set; }

    [ObservableProperty]
    public partial bool ShowAdvanced { get; set; }

    [ObservableProperty]
    public partial string? DomainError { get; set; }

    [ObservableProperty]
    public partial string? TargetError { get; set; }

    [ObservableProperty]
    public partial string? PortWarning { get; set; }

    public ObservableCollection<DomainWarning> DomainWarnings { get; } = [];

    private string? NormalizedDomain => RouteRules.Normalize(Domain);

    partial void OnDomainChanged(string value)
    {
        DomainError = null;
        OnPropertyChanged(nameof(SaveLabel));
        if (IsReady)
            ScheduleDomainCheck();
    }

    partial void OnTargetHostChanged(string value) => OnTargetChanged();
    partial void OnTargetPortChanged(string value) => OnTargetChanged();
    partial void OnSchemeChanged(string value) => OnTargetChanged();

    [RelayCommand]
    private async Task SaveAsync()
    {
        var route = Build();
        var errors = _routes.Validate(route);
        DomainError = errors.Domain;
        TargetError = errors.Target;
        if (!errors.IsValid)
            return;

        // The lookup may still be running if the user typed and saved quickly; a name that turns
        // out to exist gets one more look before it is saved. Slow DNS does not hold the button.
        await _pendingDomainCheck.WaitAsync(SaveWaitForDomainCheck)
            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        var domain = NormalizedDomain!;
        var existsOnline = DomainWarnings.Any(w => w.Kind == DomainWarningKind.ExistsOnInternet);
        if (existsOnline && IsNew && _confirmedExistingDomain != domain)
        {
            _confirmedExistingDomain = domain;
            OnPropertyChanged(nameof(SaveLabel));
            return;
        }

        Saved = _routes.Save(route);
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    private RouteEntry Build() => _original with
    {
        Domain = Domain ?? "",
        Target = $"{Scheme}://{FormatHost(TargetHost?.Trim() ?? "")}:{TargetPort?.Trim()}",
        PreserveHost = PreserveHost,
        IgnoreTargetCertErrors = IgnoreTargetCertErrors,
        Notes = Notes ?? "",
    };

    private static string FormatHost(string host) =>
        host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;

    private void OnTargetChanged()
    {
        TargetError = null;
        if (IsReady)
            SchedulePortCheck();
    }

    private void ScheduleChecks()
    {
        ScheduleDomainCheck();
        SchedulePortCheck();
    }

    private void ScheduleDomainCheck()
    {
        _domainCheck?.Cancel();
        DomainWarnings.Clear();
        var domain = NormalizedDomain;
        if (domain is null)
        {
            _pendingDomainCheck = Task.CompletedTask;
            return;
        }

        foreach (var warning in _inspector.CheckLocal(domain))
            DomainWarnings.Add(warning);

        var cts = _domainCheck = new CancellationTokenSource();
        _pendingDomainCheck = CheckInternetAsync(domain, cts.Token);
    }

    private async Task CheckInternetAsync(string domain, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(DomainCheckDelay, cancellationToken);
            var warning = await _inspector.CheckInternetAsync(domain, cancellationToken);
            if (warning is not null && !cancellationToken.IsCancellationRequested)
                DomainWarnings.Add(warning);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async void SchedulePortCheck()
    {
        _portCheck?.Cancel();
        PortWarning = null;
        if (!RouteRules.TryParseTarget(Build().Target, out var target, out _))
            return;

        var cts = _portCheck = new CancellationTokenSource();
        try
        {
            await Task.Delay(PortCheckDelay, cts.Token);
            var up = await HealthMonitor.CanConnectAsync(target.IdnHost, target.Port, cts.Token);
            if (!cts.IsCancellationRequested)
                PortWarning = up ? null : $"Nada escutando em {target.Authority} agora. A rota fica esperando seu servidor subir.";
        }
        catch (OperationCanceledException)
        {
        }
    }
}
