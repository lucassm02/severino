using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Severino.App.Resources;
using Severino.App.Services;
using Severino.Core.Configuration;
using Severino.Core.Routes;

namespace Severino.App.ViewModels;

/// <summary>One port of a service: the one the app calls, and where the service answers.</summary>
public sealed partial class ServicePortRow : ObservableObject
{
    [ObservableProperty]
    public partial string Port { get; set; } = "";

    [ObservableProperty]
    public partial string Host { get; set; } = "";

    [ObservableProperty]
    public partial string TargetPort { get; set; } = "";

    /// <summary>Goes through the proxy, with the service's HTTPS and rules.</summary>
    [ObservableProperty]
    public partial bool Http { get; set; }

    public static ServicePortRow From(ServicePort port) => new()
    {
        Port = port.Port.ToString(),
        Host = port.TargetHost,
        TargetPort = port.TargetPort.ToString(),
        Http = port.Http,
    };

    /// <summary>The row as a port line, so it goes through the same parsing as everywhere else.</summary>
    public string Line
    {
        get
        {
            var host = Host.Trim();
            if (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6)
                host = $"[{host}]";
            return $"{Port.Trim()} → {host}:{TargetPort.Trim()}";
        }
    }

    public bool IsEmpty => Port.Trim().Length == 0 && Host.Trim().Length == 0 && TargetPort.Trim().Length == 0;
}

/// <summary>The Strings.ServiceEditor_New / Strings.ServiceEditor_Edit form: names, one per line, and a row per port.</summary>
public sealed partial class ServiceEditorViewModel : ObservableObject
{
    private readonly ServiceRouteService _services;
    private readonly HttpsService? _https;
    private readonly ServiceRoute _original;

    /// <param name="draft">For a new service, the names and ports to start with, such as those of a route form.</param>
    public ServiceEditorViewModel(ServiceRouteService services, ServiceRoute? existing, IReadOnlyList<Core.Dns.DnsDestination>? destinations = null, ServiceRoute? draft = null,
        HttpsService? https = null)
    {
        _services = services;
        _https = https;
        HttpsAvailable = https?.IsActive ?? false;
        IsNew = existing is null;
        _original = existing ?? (draft ?? new ServiceRoute()) with { Address = ServiceRules.NextAddress(services.Services) };
        Rules = new RulesEditorViewModel(_original.Rules);
        ShowRules = _original.Rules.HasAny();
        NamesText = string.Join(System.Environment.NewLine, _original.Names);
        Ports.CollectionChanged += OnPortsChanged;
        foreach (var port in _original.Ports)
            Ports.Add(ServicePortRow.From(port));
        if (Ports.Count == 0)
            Ports.Add(new ServicePortRow());
        Https = _original.Https;
        Notes = _original.Notes;
        NotifyHealth = _original.NotifyHealth;
        Environment = _original.Environment;
        EnvironmentSuggestions = services.EnvironmentNames;
        HostSuggestions = [new Core.Dns.DnsDestination("127.0.0.1", Strings.Common_ThisMachine, Outside: false), .. destinations ?? []];
    }

    /// <summary>
    /// Destinations to pick from: this machine and the names in the hosts. A port that points at a
    /// DNS name follows it when its address changes in the DNS tab.
    /// </summary>
    public IReadOnlyList<Core.Dns.DnsDestination> HostSuggestions { get; }

    public bool IsNew { get; }
    public string Title => IsNew ? Strings.ServiceEditor_New : Strings.ServiceEditor_Edit;
    public string SaveLabel => IsNew ? Strings.Common_Create : Strings.Common_Save;

    /// <summary>The loopback address the names point to; given once and kept.</summary>
    public string AddressText => string.Format(Strings.ServiceEditor_Address, _original.Address);

    /// <summary>Where it was imported from, and what "Atualizar" will change.</summary>
    public string? OriginText => _original.Origin is { } origin
        ? string.Format(Strings.ServiceEditor_Origin, ServiceGroupViewModel.Describe(origin), (origin.Namespace.Length > 0 ? origin.Namespace + "/" : "") + origin.Name)
        : null;

    [ObservableProperty]
    public partial string NamesText { get; set; }

    public ObservableCollection<ServicePortRow> Ports { get; } = [];

    /// <summary>The local CA is active, so HTTPS can be turned on.</summary>
    public bool HttpsAvailable { get; }

    /// <summary>Some port is marked HTTP: HTTPS and the rules apply.</summary>
    public bool HasHttp => Ports.Any(p => p.Http);

    public bool CanToggleHttps => HttpsAvailable && HasHttp;

    [ObservableProperty]
    public partial bool Https { get; set; }

    /// <summary>Where https:// goes: "https://catalogoapi vai para a porta 8080".</summary>
    public string? HttpsHint
    {
        get
        {
            if (!HttpsAvailable)
                return null;
            if (!HasHttp)
                return Strings.ServiceEditor_NoHttpPort;
            var name = Names().FirstOrDefault() ?? "";
            var main = Ports.FirstOrDefault(p => p.Http && p.Port.Trim() == "80") ?? Ports.First(p => p.Http);
            return Ports.Any(p => p.Port.Trim() == "443")
                ? string.Format(Strings.ServiceEditor_HttpsEachPort, name)
                : string.Format(Strings.ServiceEditor_HttpsMain, name, main.Port.Trim());
        }
    }

    /// <summary>A name outside the CA's Name Constraints; the status bar then offers to reissue.</summary>
    public string? CoverageWarning => HttpsAvailable && Https && HasHttp && _https is not null
        && Names().FirstOrDefault(n => !_https.Covers(n)) is { } name
            ? string.Format(Strings.RouteEditor_Uncovered, name)
            : null;

    /// <summary>CORS, headers, delay and simulated failure, on the HTTP ports.</summary>
    public RulesEditorViewModel Rules { get; }

    [ObservableProperty]
    public partial bool ShowRules { get; set; }

    partial void OnHttpsChanged(bool value) => OnPropertyChanged(nameof(CoverageWarning));

    partial void OnNamesTextChanged(string value)
    {
        OnPropertyChanged(nameof(HttpsHint));
        OnPropertyChanged(nameof(CoverageWarning));
    }

    private IEnumerable<string> Names() =>
        (NamesText ?? "").Split(['\n', '\r', ',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void OnPortsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        foreach (var row in e.NewItems?.OfType<ServicePortRow>() ?? [])
            row.PropertyChanged += (_, _) => OnHttpChanged();
        OnHttpChanged();
    }

    private void OnHttpChanged()
    {
        OnPropertyChanged(nameof(HasHttp));
        OnPropertyChanged(nameof(CanToggleHttps));
        OnPropertyChanged(nameof(HttpsHint));
        OnPropertyChanged(nameof(CoverageWarning));
    }

    [ObservableProperty]
    public partial string Notes { get; set; }

    [ObservableProperty]
    public partial bool NotifyHealth { get; set; }
    /// <summary>Empty: counts in every environment.</summary>
    [ObservableProperty]
    public partial string Environment { get; set; } = "";

    public IReadOnlyList<string> EnvironmentSuggestions { get; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public ServiceRoute? Saved { get; private set; }

    public event EventHandler<bool>? CloseRequested;

    [RelayCommand]
    private void AddPort() => Ports.Add(new ServicePortRow());

    [RelayCommand]
    private void RemovePort(ServicePortRow row)
    {
        Ports.Remove(row);
        if (Ports.Count == 0)
            Ports.Add(new ServicePortRow());
    }

    [RelayCommand]
    private void Save()
    {
        var names = Names().ToList();
        var ports = new List<ServicePort>();
        foreach (var row in Ports.Where(r => !r.IsEmpty))
        {
            if (!ServiceRules.TryParsePort(row.Line, out var port))
            {
                Error = string.Format(Strings.ServiceEditor_PortIncomplete, row.Line);
                return;
            }
            ports.Add(port with { Http = row.Http });
        }

        var rules = Rules.Build();
        Rules.Error = RouteRules.ValidateRules(rules);
        if (Rules.Error is not null)
        {
            ShowRules = true;
            return;
        }
        var http = ports.Any(p => p.Http);
        var route = _original with
        {
            Names = names,
            Ports = ports,
            Notes = Notes.Trim(),
            NotifyHealth = NotifyHealth,
            Environment = (Environment ?? "").Trim(),
            // Without an HTTP port HTTPS has nothing to apply to: unticking the last one turns it off.
            Https = http && Https,
            Rules = rules,
        };
        var (_, error) = _services.Validate(route);
        if (error is not null)
        {
            Error = error;
            return;
        }
        Saved = _services.Save(route);
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
