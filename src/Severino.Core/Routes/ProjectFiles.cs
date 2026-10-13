using System.Text.Json;
using System.Text.Json.Nodes;
using Severino.Core.Configuration;
using Severino.Core.Dns;
using Severino.Core.Resources;

namespace Severino.Core.Routes;

public enum ProjectChange
{
    Added,
    Changed,
    Removed,
    Unchanged,
}

public enum ProjectItemKind
{
    Route,
    Service,
    Dns,
}

/// <summary>One line of a project's preview: what it is, where it points, and what applying does to it.</summary>
public sealed record ProjectItem(ProjectItemKind Kind, string Name, string Detail, ProjectChange Change);

/// <param name="Next">The config as it is once the project is applied.</param>
/// <param name="Refused">Items of the file that cannot come in, with why: usually a name in use here outside the project.</param>
public sealed record ProjectPlan(string Name, SeverinoConfig Next, IReadOnlyList<ProjectItem> Items, IReadOnlyList<InvalidImport> Refused)
{
    public bool HasChanges => Items.Any(i => i.Change != ProjectChange.Unchanged);
}

/// <summary>
/// Project files, a severino.json in a repository: the routes, services and DNS entries of a
/// project, in the backup format with a name. Applying one is always a gesture of the person,
/// after a preview: it adds what is new, updates what changed and removes what left the file,
/// touching only the items marked with the project's name. Ids, loopback addresses, port-forward
/// ports and switches stay as they were here.
/// </summary>
public static class ProjectFiles
{
    public const string FileName = "severino.json";

    /// <summary>A backup file with a name.</summary>
    /// <exception cref="InvalidRouteFileException">Not a Severino file, one from a newer version, or one without a name.</exception>
    public static RouteFile Read(string json)
    {
        var file = RouteTransfer.Read(json);
        if (string.IsNullOrWhiteSpace(file.Name))
            throw new InvalidRouteFileException(Strings.Project_NoName);
        return file with { Name = file.Name.Trim() };
    }

    private static bool Mine(string project, string name) => string.Equals(project.Trim(), name, StringComparison.OrdinalIgnoreCase);

    /// <summary>What applying <paramref name="file"/> to <paramref name="config"/> would do, and the config after it.</summary>
    /// <param name="external">Hosts lines outside Severino, whose names no item may take.</param>
    public static ProjectPlan Plan(RouteFile file, SeverinoConfig config, IReadOnlySet<string>? external = null)
    {
        var name = file.Name.Trim();
        var oldRoutes = config.Routes.Where(r => Mine(r.Project, name)).ToList();
        var oldServices = config.Services.Where(s => Mine(s.Project, name)).ToList();
        var oldDns = config.DnsEntries.Where(d => Mine(d.Project, name)).ToList();
        var routes = config.Routes.Where(r => !Mine(r.Project, name)).ToList();
        var services = config.Services.Where(s => !Mine(s.Project, name)).ToList();
        var dns = config.DnsEntries.Where(d => !Mine(d.Project, name)).ToList();
        var refused = new List<InvalidImport>();
        var settings = config.Settings;

        // DNS first: routes and services in the file may go to these names.
        foreach (var entry in file.DnsEntries)
        {
            var previous = oldDns.FirstOrDefault(o => SameFirstName(o.Names, entry.Names) && Environments.Same(o.Environment, entry.Environment));
            var candidate = entry with { Id = previous?.Id ?? Guid.NewGuid(), Enabled = previous?.Enabled ?? entry.Enabled, Project = name };
            var (normalized, error) = DnsRules.Validate(candidate, config with { Routes = routes, Services = services, DnsEntries = dns }, external);
            if (normalized is null)
                refused.Add(new(Describe(entry.Names), error ?? ""));
            else
                dns.Add(normalized);
        }

        foreach (var exported in file.Routes)
        {
            var route = RouteTransfer.FromExported(exported);
            var domain = RouteRules.Normalize(route.Domain);
            var path = RouteRules.NormalizePath(route.Path);
            var previous = oldRoutes.FirstOrDefault(o => RouteRules.Normalize(o.Domain) == domain && RouteRules.NormalizePath(o.Path) == path
                && Environments.Same(o.Environment, route.Environment));
            route = route with { Id = previous?.Id ?? Guid.NewGuid(), Enabled = previous?.Enabled ?? route.Enabled, Project = name };
            var errors = RouteRules.Validate(route, routes, settings.HttpPort, settings.HttpsPort, services, DnsRules.Names(dns, route.Environment), external);
            if (!errors.IsValid)
            {
                refused.Add(new(route.Domain.Length > 0 ? route.Domain + route.Path : Strings.Transfer_NoDomain, errors.Domain ?? errors.Path ?? errors.Target ?? errors.Rules ?? errors.Access ?? ""));
                continue;
            }
            routes.Add(route with { Domain = domain!, Path = path! });
        }

        foreach (var service in file.Services)
        {
            var previous = oldServices.FirstOrDefault(o => SameFirstName(o.Names, service.Names) && Environments.Same(o.Environment, service.Environment));
            // Addresses and port-forward ports are this machine's: kept when the service was here, picked when new.
            var ports = !service.PortForward ? service.Ports
                : previous is { PortForward: true } && previous.Ports.Count == service.Ports.Count
                    ? [.. service.Ports.Zip(previous.Ports, (p, local) => p with { TargetPort = local.TargetPort })]
                    : [.. service.Ports.Zip(ServiceRules.NextForwardPorts([.. services, .. oldServices], service.Ports.Count), (p, local) => p with { TargetPort = local })];
            var candidate = service with
            {
                Id = previous?.Id ?? Guid.NewGuid(),
                Address = previous?.Address ?? ServiceRules.NextAddress([.. services, .. oldServices]),
                Ports = ports,
                Enabled = previous?.Enabled ?? service.Enabled,
                Project = name,
            };
            var (normalized, error) = ServiceRules.Validate(candidate, services, routes, DnsRules.Names(dns, candidate.Environment), external);
            if (normalized is null)
                refused.Add(new(Describe(service.Names), error ?? ""));
            else
                services.Add(normalized);
        }

        var next = config with { Routes = routes, Services = services, DnsEntries = dns };
        var items = new List<ProjectItem>();
        items.AddRange(Diff(ProjectItemKind.Dns, oldDns, dns.Where(d => Mine(d.Project, name)), d => d.Id,
            d => Describe(d.Names), d => d.Address, ConfigJsonContext.Default.DnsEntry));
        items.AddRange(Diff(ProjectItemKind.Route, oldRoutes, routes.Where(r => Mine(r.Project, name)), r => r.Id,
            r => r.Domain + r.Path, r => RouteRules.FolderOf(r.Target) ?? r.Target, ConfigJsonContext.Default.RouteEntry));
        items.AddRange(Diff(ProjectItemKind.Service, oldServices, services.Where(s => Mine(s.Project, name)), s => s.Id,
            s => Describe(s.Names), s => string.Join("  ", s.Ports), ConfigJsonContext.Default.ServiceRoute));
        return new(name, next, items, refused);
    }

    /// <summary>Takes out every item of the project and forgets where its file was.</summary>
    public static SeverinoConfig Remove(SeverinoConfig config, string name) => config with
    {
        Routes = [.. config.Routes.Where(r => !Mine(r.Project, name))],
        Services = [.. config.Services.Where(s => !Mine(s.Project, name))],
        DnsEntries = [.. config.DnsEntries.Where(d => !Mine(d.Project, name))],
        State = config.State with { Projects = [.. config.State.Projects.Where(p => !Mine(p.Name, name))] },
    };

    /// <summary>Keeps where the project's file is, for "Aplicar de novo".</summary>
    public static SeverinoConfig Remember(SeverinoConfig config, string name, string path) => config with
    {
        State = config.State with
        {
            Projects = [.. config.State.Projects.Where(p => !Mine(p.Name, name)), new ProjectRef { Name = name, Path = path }],
        },
    };

    /// <summary>How many routes, services and DNS entries a project has here.</summary>
    public static int Count(SeverinoConfig config, string name) =>
        config.Routes.Count(r => Mine(r.Project, name)) + config.Services.Count(s => Mine(s.Project, name)) + config.DnsEntries.Count(d => Mine(d.Project, name));

    /// <summary>
    /// A project file with <paramref name="routes"/> and the services and DNS entries they go to,
    /// without what belongs to this machine: ids, loopback addresses and the project marks.
    /// </summary>
    public static string Export(string name, IReadOnlyList<RouteEntry> routes, SeverinoConfig config)
    {
        var hosts = routes.Select(r => Uri.TryCreate(r.Target, UriKind.Absolute, out var uri) ? uri.IdnHost.ToLowerInvariant() : "")
            .Where(h => h.Length > 0).ToHashSet(StringComparer.Ordinal);
        var dns = config.DnsEntries.Where(d => d.Names.Any(hosts.Contains)).ToList();
        var services = config.Services.Where(s => s.Names.Any(hosts.Contains)).ToList();

        var file = JsonNode.Parse(RouteTransfer.Export(routes, services, dns))!.AsObject();
        file["name"] = name.Trim();
        foreach (var item in new[] { "services", "dnsEntries" }.SelectMany(list => file[list]?.AsArray() ?? []).OfType<JsonObject>())
        {
            item.Remove("id");
            item.Remove("project");
            if (item.ContainsKey("ports"))
                item.Remove("address");
        }
        // The version first, then the name, as a person would write the file.
        var ordered = new JsonObject { ["severino"] = RouteFile.CurrentVersion, ["name"] = name.Trim() };
        foreach (var (key, value) in file.ToList())
        {
            if (key is "severino" or "name")
                continue;
            file.Remove(key);
            ordered[key] = value;
        }
        return ordered.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    private static IEnumerable<ProjectItem> Diff<T>(ProjectItemKind kind, IReadOnlyList<T> before, IEnumerable<T> after, Func<T, Guid> id,
        Func<T, string> name, Func<T, string> detail, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> json)
    {
        var now = after.ToList();
        foreach (var item in now)
        {
            var old = before.FirstOrDefault(b => id(b) == id(item));
            var change = old is null ? ProjectChange.Added
                : JsonSerializer.Serialize(old, json) == JsonSerializer.Serialize(item, json) ? ProjectChange.Unchanged
                : ProjectChange.Changed;
            yield return new(kind, name(item), detail(item), change);
        }
        foreach (var old in before.Where(b => now.All(n => id(n) != id(b))))
            yield return new(kind, name(old), detail(old), ProjectChange.Removed);
    }

    private static bool SameFirstName(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Count > 0 && b.Count > 0 && string.Equals(a[0].Trim().TrimEnd('.'), b[0].Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    private static string Describe(IReadOnlyList<string> names) => names.Count > 0 ? names[0] : Strings.Common_NoName;
}
