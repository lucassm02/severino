using Severino.Core.Configuration;
using Severino.Core.Dns;

namespace Severino.Core.Routes;

/// <param name="Routes">The routes, as <see cref="RouteTransfer.Import(RouteFile, IReadOnlyList{RouteEntry}, int, int?, IReadOnlyList{ServiceRoute}?)"/> sorted them.</param>
/// <param name="Services">The first name of each service that came in.</param>
/// <param name="Dns">The first name of each DNS entry that came in.</param>
/// <param name="Refused">Services and DNS entries left out, with why: usually a name already in use here.</param>
public sealed record BackupResult(ImportResult Routes, IReadOnlyList<string> Services, IReadOnlyList<string> Dns, IReadOnlyList<InvalidImport> Refused);

/// <summary>
/// Everything a person made by hand or imported, in one file to take to another machine: routes,
/// services and DNS entries. Importing only adds; what is already here stays as it is.
/// </summary>
public sealed class Backup(ConfigService config, ServiceRouteService services, DnsService dns)
{
    public string Export() => RouteTransfer.Export(config.Current.Routes, config.Current.Services, config.Current.DnsEntries);

    /// <exception cref="InvalidRouteFileException">Not a backup file this version can read.</exception>
    public BackupResult Import(string json)
    {
        var file = RouteTransfer.Read(json);
        var refused = new List<InvalidImport>();

        // DNS first: routes and services in the file may go to these names.
        var dnsAdded = new List<string>();
        foreach (var entry in file.DnsEntries)
        {
            var (normalized, error) = dns.Validate(entry with { Id = Guid.NewGuid() });
            if (normalized is null)
            {
                refused.Add(new(Describe(entry.Names), error ?? ""));
                continue;
            }
            dns.Save(normalized);
            dnsAdded.Add(normalized.Names[0]);
        }

        var settings = config.Current.Settings;
        var routes = RouteTransfer.Import(file, config.Current.Routes, settings.HttpPort, settings.HttpsPort, config.Current.Services);
        if (routes.Added.Count > 0)
            config.Update(c => c with { Routes = [.. c.Routes, .. routes.Added] });

        var servicesAdded = new List<string>();
        foreach (var service in file.Services)
        {
            // Addresses and port-forward ports are per machine: the import picks free ones here.
            var ports = service.PortForward
                ? [.. service.Ports.Zip(ServiceRules.NextForwardPorts(services.Services, service.Ports.Count), (p, local) => p with { TargetPort = local })]
                : service.Ports;
            var (normalized, error) = services.Validate(service with
            {
                Id = Guid.NewGuid(),
                Address = ServiceRules.NextAddress(services.Services),
                Ports = ports,
            });
            if (normalized is null)
            {
                refused.Add(new(Describe(service.Names), error ?? ""));
                continue;
            }
            services.Save(normalized);
            servicesAdded.Add(normalized.Names[0]);
        }

        return new(routes, servicesAdded, dnsAdded, refused);
    }

    private static string Describe(IReadOnlyList<string> names) => names.Count > 0 ? names[0] : "(sem nome)";
}
