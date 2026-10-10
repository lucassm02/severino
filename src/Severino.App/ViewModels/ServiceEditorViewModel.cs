using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    public static ServicePortRow From(ServicePort port) => new()
    {
        Port = port.Port.ToString(),
        Host = port.TargetHost,
        TargetPort = port.TargetPort.ToString(),
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

/// <summary>The "Novo serviço" / "Editar serviço" form: names, one per line, and a row per port.</summary>
public sealed partial class ServiceEditorViewModel : ObservableObject
{
    private readonly ServiceRouteService _services;
    private readonly ServiceRoute _original;

    /// <param name="draft">For a new service, the names and ports to start with, such as those of a route form.</param>
    public ServiceEditorViewModel(ServiceRouteService services, ServiceRoute? existing, IReadOnlyList<Core.Dns.DnsDestination>? destinations = null, ServiceRoute? draft = null)
    {
        _services = services;
        IsNew = existing is null;
        _original = existing ?? (draft ?? new ServiceRoute()) with { Address = ServiceRules.NextAddress(services.Services) };
        NamesText = string.Join(Environment.NewLine, _original.Names);
        foreach (var port in _original.Ports)
            Ports.Add(ServicePortRow.From(port));
        if (Ports.Count == 0)
            Ports.Add(new ServicePortRow());
        Notes = _original.Notes;
        HostSuggestions = [new Core.Dns.DnsDestination("127.0.0.1", "esta máquina", Outside: false), .. destinations ?? []];
    }

    /// <summary>
    /// Destinations to pick from: this machine and the names in the hosts. A port that points at a
    /// DNS name follows it when its address changes in the DNS tab.
    /// </summary>
    public IReadOnlyList<Core.Dns.DnsDestination> HostSuggestions { get; }

    public bool IsNew { get; }
    public string Title => IsNew ? "Novo serviço" : "Editar serviço";
    public string SaveLabel => IsNew ? "Criar" : "Salvar";

    /// <summary>The loopback address the names point to; given once and kept.</summary>
    public string AddressText => $"Os nomes apontam para {_original.Address}, onde o Severino escuta essas portas.";

    /// <summary>Where it was imported from, and what "Atualizar" will change.</summary>
    public string? OriginText => _original.Origin is { } origin
        ? $"Importado de {ServiceGroupViewModel.Describe(origin)} ({(origin.Namespace.Length > 0 ? origin.Namespace + "/" : "")}{origin.Name}). " +
          "\"Atualizar\" troca as portas pelas do momento e mantém os nomes."
        : null;

    [ObservableProperty]
    public partial string NamesText { get; set; }

    public ObservableCollection<ServicePortRow> Ports { get; } = [];

    [ObservableProperty]
    public partial string Notes { get; set; }

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
        var names = NamesText.Split(['\n', '\r', ',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var ports = new List<ServicePort>();
        foreach (var row in Ports.Where(r => !r.IsEmpty))
        {
            if (!ServiceRules.TryParsePort(row.Line, out var port))
            {
                Error = $"A porta {row.Line} está incompleta: diga a porta que o app chama, o destino e a porta do destino.";
                return;
            }
            ports.Add(port);
        }

        var route = _original with { Names = names, Ports = ports, Notes = Notes.Trim() };
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
