using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Severino.App.Services;
using Severino.Contracts;
using Severino.Core.Configuration;
using Severino.Core.Domains;
using Severino.Core.Network;
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
    private readonly HttpsService _https;
    private readonly Navigation _navigation;
    private readonly RouteEntry _original;
    private CancellationTokenSource? _domainCheck;
    private CancellationTokenSource? _portCheck;
    private Task _pendingDomainCheck = Task.CompletedTask;
    private string? _confirmedExistingDomain;

    public RouteEditorViewModel(RouteService routes, DomainInspector inspector, HttpsService https, Navigation navigation, RouteEntry? existing, bool isCopy)
    {
        _routes = routes;
        _inspector = inspector;
        _https = https;
        _navigation = navigation;
        IsNew = existing is null || isCopy;
        HttpsAvailable = https.IsActive;
        // New routes get HTTPS and the redirect while the CA is active.
        _original = existing ?? new RouteEntry
        {
            Domain = "",
            Target = "http://localhost:3000",
            Https = HttpsAvailable,
            RedirectToHttps = HttpsAvailable,
        };

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
        Https = _original.Https;
        RedirectToHttps = _original.RedirectToHttps;
        PreserveHost = _original.PreserveHost;
        IgnoreTargetCertErrors = _original.IgnoreTargetCertErrors;
        Notes = _original.Notes;
        ShowAdvanced = PreserveHost || IgnoreTargetCertErrors || Notes.Length > 0;
        UpdateHttpsRules();
        IsReady = true;
        ScheduleChecks();
        _ = RefreshPortsAsync();
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

    /// <summary>The local CA is active, so the HTTPS options apply.</summary>
    public bool HttpsAvailable { get; }

    /// <summary>False in the first-run wizard, which offers HTTPS its own way, before any CA exists.</summary>
    public bool ShowHttpsOptions { get; init; } = true;

    /// <summary>The TLD is HSTS-preloaded: browsers only open it over HTTPS.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleHttps), nameof(HttpsRequiredText))]
    public partial bool HstsRequired { get; set; }

    public bool CanToggleHttps => HttpsAvailable && !HstsRequired;

    public bool CanToggleRedirect => HttpsAvailable && Https;

    public string? HttpsRequiredText => HttpsAvailable && HstsRequired
        ? "HTTPS obrigatório: navegadores só abrem este domínio com HTTPS (HSTS preload)."
        : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleRedirect))]
    public partial bool Https { get; set; }

    [ObservableProperty]
    public partial bool RedirectToHttps { get; set; }

    /// <summary>The domain is outside the CA's Name Constraints; saving offers to reissue.</summary>
    [ObservableProperty]
    public partial string? CoverageWarning { get; set; }

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

    /// <summary>Ports something listens on right now, for the port field's list.</summary>
    public ObservableCollection<ListeningPort> Ports { get; } = [];

    public async Task RefreshPortsAsync()
    {
        // GetExtendedTcpTable plus a command line per process: cheap, but not for the UI thread.
        var ports = await Task.Run(() => ListeningPorts.List(excludeProcessId: Environment.ProcessId));
        Ports.Clear();
        foreach (var port in ports)
            Ports.Add(port);
    }

    private string? NormalizedDomain => RouteRules.Normalize(Domain);

    partial void OnHttpsChanged(bool value) => UpdateCoverageWarning();

    partial void OnDomainChanged(string value)
    {
        DomainError = null;
        UpdateHttpsRules();
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

    /// <summary>Closes the form and shows Settings, where HTTPS is turned on.</summary>
    [RelayCommand]
    private void OpenHttpsSettings()
    {
        CloseRequested?.Invoke(this, false);
        _navigation.ShowSettings();
    }

    private void UpdateHttpsRules()
    {
        HstsRequired = NormalizedDomain is { } domain && DomainInspector.IsHstsPreloaded(domain);
        if (HstsRequired && HttpsAvailable)
            Https = true;
        UpdateCoverageWarning();
    }

    private void UpdateCoverageWarning() =>
        CoverageWarning = HttpsAvailable && Https && NormalizedDomain is { } domain && !_https.Covers(domain)
            ? $"{domain} está fora da CA atual. Ao salvar, o Severino oferece reemitir a CA, e o Windows pede confirmação."
            : null;

    private RouteEntry Build() => _original with
    {
        Domain = Domain ?? "",
        Target = $"{Scheme}://{FormatHost(TargetHost?.Trim() ?? "")}:{TargetPort?.Trim()}",
        Https = Https,
        RedirectToHttps = Https && RedirectToHttps,
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

        // With the CA active, the HSTS warning gives way to the locked HTTPS box.
        foreach (var warning in _inspector.CheckLocal(domain).Where(w => !(HttpsAvailable && w.Kind == DomainWarningKind.HstsPreload)))
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
