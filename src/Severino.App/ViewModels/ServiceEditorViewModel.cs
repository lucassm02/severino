using System.Net;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Severino.Core.Configuration;
using Severino.Core.Routes;

namespace Severino.App.ViewModels;

/// <summary>The "Novo serviço" / "Editar serviço" form: names, one per line, and port lines.</summary>
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
        PortsText = string.Join(Environment.NewLine, _original.Ports.Select(Format));
        Notes = _original.Notes;
        var named = (destinations ?? []).Where(d => !d.Outside).Take(6).ToList();
        DestinationsHint = named.Count == 0 ? null
            : "Destinos por nome, do DNS: " + string.Join(", ", named.Select(d => $"{d.Name} ({d.Address})")) +
              ". Com o nome, mudar o IP na aba DNS muda o destino.";
    }

    /// <summary>The DNS names that can stand for a destination host, so the IP lives in one place.</summary>
    public string? DestinationsHint { get; }

    public bool IsNew { get; }
    public string Title => IsNew ? "Novo serviço" : "Editar serviço";
    public string SaveLabel => IsNew ? "Criar" : "Salvar";

    /// <summary>The loopback address the names point to; given once and kept.</summary>
    public string Address => _original.Address;

    /// <summary>Where it was imported from, and what "Atualizar" will change.</summary>
    public string? OriginText => _original.Origin is { } origin
        ? $"Importado de {ServiceGroupViewModel.Describe(origin)} ({(origin.Namespace.Length > 0 ? origin.Namespace + "/" : "")}{origin.Name}). " +
          "\"Atualizar\" troca as portas pelas do momento e mantém os nomes."
        : null;

    [ObservableProperty]
    public partial string NamesText { get; set; }

    [ObservableProperty]
    public partial string PortsText { get; set; }

    [ObservableProperty]
    public partial string Notes { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public ServiceRoute? Saved { get; private set; }

    public event EventHandler<bool>? CloseRequested;

    [RelayCommand]
    private void Save()
    {
        var names = NamesText.Split(['\n', '\r', ',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var ports = new List<ServicePort>();
        foreach (var line in PortsText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!ServiceRules.TryParsePort(line, out var port))
            {
                Error = $"Não entendi a porta \"{line}\". Use uma linha por porta, como 80 → 192.168.0.10:30080.";
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

    private static string Format(ServicePort port) =>
        IPAddress.TryParse(port.TargetHost, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6
            ? $"{port.Port} → [{port.TargetHost}]:{port.TargetPort}"
            : port.ToString();
}
