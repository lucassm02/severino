using System.Net;
using Severino.Contracts;
using Severino.Core.Configuration;

namespace Severino.Core.Routes;

/// <summary>Validation and addressing for service routes.</summary>
public static partial class ServiceRules
{
    /// <summary>Every service route gets an address here, so services on the same port never mix.</summary>
    public const string AddressPrefix = "127.77.";

    /// <summary>
    /// The first free address in 127.77.0.0/16, skipping .0, .1 and .255 in the last octet so no
    /// one mistakes one for a network or broadcast address.
    /// </summary>
    public static string NextAddress(IEnumerable<ServiceRoute> existing)
    {
        var taken = existing.Select(s => s.Address).ToHashSet(StringComparer.Ordinal);
        for (var third = 0; third <= 255; third++)
        {
            for (var fourth = 2; fourth <= 254; fourth++)
            {
                var address = $"{AddressPrefix}{third}.{fourth}";
                if (!taken.Contains(address))
                    return address;
            }
        }
        throw new InvalidOperationException("Os endereços de 127.77.0.0/16 acabaram.");
    }

    /// <summary>
    /// One port line as the editor shows it, "80 → 192.168.203.100:32359"; "->", "=" or a space
    /// work as the arrow too, and an IPv6 destination goes in brackets.
    /// </summary>
    public static bool TryParsePort(string line, out ServicePort port)
    {
        port = null!;
        var match = PortLine().Match(line);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var number) || !int.TryParse(match.Groups[3].Value, out var targetPort))
            return false;
        port = new ServicePort { Port = number, TargetHost = match.Groups[2].Value.Trim('[', ']'), TargetPort = targetPort };
        return true;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*(\d{1,5})\s*(?:→|->|=|\s)\s*(\[[0-9A-Fa-f:.]+\]|[^\s:\[\]]+):(\d{1,5})\s*$")]
    private static partial System.Text.RegularExpressions.Regex PortLine();

    public static bool IsServiceAddress(string? address) =>
        IPAddress.TryParse(address, out var ip) && ip.ToString().StartsWith(AddressPrefix, StringComparison.Ordinal);

    /// <summary>
    /// The route with its names normalized, or the first reason in Portuguese it cannot be used.
    /// A name may not belong to another service or to a web route.
    /// </summary>
    public static (ServiceRoute? Normalized, string? Error) Validate(ServiceRoute route, IReadOnlyList<ServiceRoute> services, IReadOnlyList<RouteEntry> webRoutes)
    {
        if (route.Names.Count == 0)
            return (null, "Informe pelo menos um nome.");

        var names = new List<string>();
        foreach (var raw in route.Names)
        {
            if (!DomainName.TryNormalize(raw, out var name, out var reason, allowSingleLabel: true))
                return (null, $"Nome inválido '{raw}': {reason}");
            if (!names.Contains(name))
                names.Add(name);
        }

        var others = services.Where(s => s.Id != route.Id).SelectMany(s => s.Names)
            .Concat(webRoutes.Select(r => RouteRules.Normalize(r.Domain)).OfType<string>())
            .ToHashSet(StringComparer.Ordinal);
        if (names.FirstOrDefault(others.Contains) is { } taken)
            return (null, $"{taken} já está em outra rota.");

        if (!IsServiceAddress(route.Address))
            return (null, "Endereço de loopback inválido.");

        if (route.Ports.Count == 0)
            return (null, "Informe pelo menos uma porta.");
        if (route.Ports.GroupBy(p => p.Port).FirstOrDefault(g => g.Count() > 1) is { } repeated)
            return (null, $"A porta {repeated.Key} aparece duas vezes.");
        foreach (var port in route.Ports)
        {
            if (port.Port is < 1 or > 65535 || port.TargetPort is < 1 or > 65535)
                return (null, "Use portas de 1 a 65535.");
            if (string.IsNullOrWhiteSpace(port.TargetHost) || Uri.CheckHostName(port.TargetHost) == UriHostNameType.Unknown)
                return (null, $"Destino inválido: {port.TargetHost}");
            // A service forwarding to another service's address would loop through Severino.
            if (IsServiceAddress(port.TargetHost))
                return (null, "O destino não pode ser um endereço de outra rota de serviço.");
        }

        return (route with { Names = names }, null);
    }
}

/// <summary>What the hosts block should hold for a config.</summary>
public static class HostsEntries
{
    /// <summary>Enabled web routes on both loopbacks, then enabled services on their own address.</summary>
    public static IReadOnlyList<HostEntry> For(SeverinoConfig config) =>
        [
            .. HostEntry.ForDomains(RouteRules.ActiveDomains(config.Routes)),
            .. config.Services.Where(s => s.Enabled).SelectMany(s => s.Names.Select(n => new HostEntry(n, s.Address))),
        ];
}
