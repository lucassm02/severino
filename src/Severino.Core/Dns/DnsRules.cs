using Severino.Contracts;
using Severino.Core.Configuration;
using Severino.Core.Routes;

namespace Severino.Core.Dns;

/// <summary>Validation for DNS entries, and the one list of names that entries, routes and services share.</summary>
public static class DnsRules
{
    public const string ExternalClash = "já está no hosts, fora do Severino. Edite essa linha na aba DNS ou escolha outro nome.";

    /// <summary>
    /// The entry with its names and address normalized, or the first reason it cannot be used.
    /// A name may not belong to another entry, a route, a service, or a hosts line outside Severino.
    /// </summary>
    public static (DnsEntry? Normalized, string? Error) Validate(DnsEntry entry, SeverinoConfig config, IReadOnlySet<string>? external = null)
    {
        if (entry.Names.Count == 0)
            return (null, "Informe pelo menos um nome.");

        var names = new List<string>();
        foreach (var raw in entry.Names)
        {
            if (!(DomainName.IsWildcard(raw) ? DomainName.TryNormalizeWildcard(raw, out var name, out var reason) : DomainName.TryNormalize(raw, out name, out reason, allowSingleLabel: true)))
                return (null, $"Nome inválido '{raw}': {reason}");
            if (!names.Contains(name))
                names.Add(name);
        }

        if (!DnsAddress.TryClassify(entry.Address?.Trim(), out var address, out _))
            return (null, "Informe um IPv4 ou IPv6 de um computador, como 10.0.0.8.");

        foreach (var name in names)
        {
            if (config.DnsEntries.Any(d => d.Id != entry.Id && d.Names.Contains(name)))
                return (null, $"{name} já está em outra entrada DNS.");
            if (config.Routes.Any(r => RouteRules.Normalize(r.Domain) == name))
                return (null, $"{name} já é uma rota.");
            if (config.Services.Any(s => s.Names.Contains(name)))
                return (null, $"{name} já é um serviço.");
            if (external?.Contains(name) == true)
                return (null, $"{name} {ExternalClash}");
        }

        return (entry with { Names = names, Address = address, Notes = entry.Notes.Trim() }, null);
    }

    /// <summary>What goes in the DNS block: the enabled entries, one line per name.</summary>
    public static IReadOnlyList<HostEntry> BlockEntries(IEnumerable<DnsEntry> entries) =>
        [.. entries.Where(e => e.Enabled).SelectMany(e => e.Names.Select(n => new HostEntry(n, e.Address)))];

    /// <summary>The routes and services whose destination is one of <paramref name="names"/>: they stop working without it.</summary>
    public static (IReadOnlyList<RouteEntry> Routes, IReadOnlyList<ServiceRoute> Services) UsedBy(IEnumerable<string> names, SeverinoConfig config)
    {
        var set = names.Select(n => n.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var routes = config.Routes
            .Where(r => Uri.TryCreate(r.Target, UriKind.Absolute, out var target) && set.Contains(target.IdnHost.ToLowerInvariant()))
            .ToList();
        var services = config.Services
            .Where(s => s.Ports.Any(p => set.Contains(p.TargetHost.ToLowerInvariant())))
            .ToList();
        return (routes, services);
    }

    /// <summary>The names of every DNS entry, enabled or not: they stay reserved while switched off.</summary>
    public static IReadOnlySet<string> Names(IEnumerable<DnsEntry> entries) =>
        entries.SelectMany(e => e.Names).ToHashSet(StringComparer.Ordinal);
}
