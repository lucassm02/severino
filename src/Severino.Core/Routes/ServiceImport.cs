using Severino.Core.Configuration;
using Severino.Core.Discovery;

namespace Severino.Core.Routes;

/// <summary>A discovered service picked for import, with where it came from.</summary>
public sealed record ServiceCandidate(DiscoveredService Service, ServiceOrigin Origin);

/// <param name="Route">What will be saved; null when <paramref name="Error"/> says why not.</param>
/// <param name="Replaces">The route already imported from the same origin, which gets the new ports and keeps its names.</param>
/// <param name="DroppedNames">Names left out because another route, or an earlier candidate, has them.</param>
public sealed record PlannedService(ServiceCandidate Candidate, ServiceRoute? Route, ServiceRoute? Replaces, IReadOnlyList<string> DroppedNames, string? Error)
{
    /// <summary>"redis já está em outra rota" style note for the list; null when nothing to say.</summary>
    public string? Warning => Error ?? (DroppedNames.Count == 0 ? null
        : DroppedNames.Count == 1 ? $"{DroppedNames[0]} já está em outra rota e fica de fora."
        : $"{string.Join(", ", DroppedNames)} já estão em outras rotas e ficam de fora.");
}

public sealed record RefreshResult(IReadOnlyList<ServiceRoute> Updated, IReadOnlyList<ServiceRoute> Missing);

/// <summary>Turns discovered services into service routes, without clashing with what exists.</summary>
public static class ServiceImport
{
    /// <summary>Origins that "Atualizar" can ask again: same tool, same place, same context.</summary>
    public static bool SameSource(ServiceOrigin a, ServiceOrigin b) =>
        a.Kind == b.Kind && a.Source == b.Source && a.Context == b.Context;

    /// <summary>The same service of the same source.</summary>
    public static bool SameService(ServiceOrigin a, ServiceOrigin b) =>
        SameSource(a, b) && a.Namespace == b.Namespace && a.Name == b.Name;

    /// <summary>
    /// What importing <paramref name="candidates"/> would do, in order. A service imported before
    /// from the same origin is updated in place. A new one takes the next address and the names
    /// nobody has yet, so two namespaces with a <c>redis</c> each still both import: the second
    /// keeps <c>redis.ns</c> and the longer names. Names of DNS entries and of hosts lines outside
    /// Severino (<paramref name="otherNames"/>) are taken too.
    /// </summary>
    public static IReadOnlyList<PlannedService> Plan(IReadOnlyList<ServiceCandidate> candidates, IReadOnlyList<ServiceRoute> services, IReadOnlyList<RouteEntry> webRoutes,
        IEnumerable<string>? otherNames = null)
    {
        var taken = services.SelectMany(s => s.Names)
            .Concat(webRoutes.Select(r => RouteRules.Normalize(r.Domain)).OfType<string>())
            .Concat(otherNames ?? [])
            .ToHashSet(StringComparer.Ordinal);
        var addresses = services.ToList();
        var plan = new List<PlannedService>();

        foreach (var candidate in candidates)
        {
            var service = candidate.Service;
            if (!service.CanImport)
            {
                plan.Add(new(candidate, null, null, [], service.Unreachable ?? "Sem acesso de fora."));
                continue;
            }

            if (services.FirstOrDefault(s => s.Origin is { } o && SameService(o, candidate.Origin)) is { } existing)
            {
                plan.Add(new(candidate, existing with { Ports = service.Ports, Origin = candidate.Origin }, existing, [], null));
                continue;
            }

            var names = service.Names.Where(n => !taken.Contains(n)).ToList();
            var dropped = service.Names.Where(taken.Contains).ToList();
            if (names.Count == 0)
            {
                plan.Add(new(candidate, null, null, dropped, "Todos os nomes já estão em outras rotas."));
                continue;
            }

            var route = new ServiceRoute
            {
                Names = names,
                Address = ServiceRules.NextAddress(addresses),
                Ports = service.Ports,
                Origin = candidate.Origin,
            };
            addresses.Add(route);
            taken.UnionWith(names);
            plan.Add(new(candidate, route, null, dropped, null));
        }
        return plan;
    }

    /// <summary>
    /// New ports for the routes that came from <paramref name="source"/>, matched by namespace and
    /// name. Names stay as the person left them. Routes whose service is gone are reported, not removed.
    /// </summary>
    public static RefreshResult Refresh(IReadOnlyList<ServiceRoute> services, ServiceOrigin source, IReadOnlyList<DiscoveredService> found)
    {
        var updated = new List<ServiceRoute>();
        var missing = new List<ServiceRoute>();
        foreach (var route in services.Where(s => s.Origin is { } o && SameSource(o, source)))
        {
            var match = found.FirstOrDefault(f => f.Namespace == route.Origin!.Namespace && f.Name == route.Origin.Name && f.CanImport);
            if (match is null)
                missing.Add(route);
            else if (!match.Ports.SequenceEqual(route.Ports))
                updated.Add(route with { Ports = match.Ports });
        }
        return new(updated, missing);
    }
}
