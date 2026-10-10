using Severino.Core.Configuration;
using Severino.Core.Discovery;

namespace Severino.Core.Routes;

/// <summary>Create, import, refresh and remove service routes; every change goes through <see cref="ConfigService"/>.</summary>
public sealed class ServiceRouteService(ConfigService config, Dns.ExternalHosts? external = null)
{
    public IReadOnlyList<ServiceRoute> Services => config.Current.Services;

    /// <summary>Names an import must leave alone: DNS entries and hosts lines outside Severino.</summary>
    private IEnumerable<string> OtherNames(SeverinoConfig c) =>
        Dns.DnsRules.Names(c.DnsEntries).Concat(external?.Names ?? Enumerable.Empty<string>());

    public IReadOnlyList<PlannedService> Plan(IReadOnlyList<ServiceCandidate> candidates) =>
        ServiceImport.Plan(candidates, Services, config.Current.Routes, OtherNames(config.Current));

    /// <summary>Saves the plan's routes in one change, planned again against the config as it is now.</summary>
    public IReadOnlyList<PlannedService> Import(IReadOnlyList<ServiceCandidate> candidates)
    {
        IReadOnlyList<PlannedService> plan = [];
        config.Update(c =>
        {
            plan = ServiceImport.Plan(candidates, c.Services, c.Routes, OtherNames(c));
            var services = c.Services.ToList();
            foreach (var planned in plan.Where(p => p.Route is not null))
            {
                var index = services.FindIndex(s => s.Id == planned.Route!.Id);
                if (index >= 0)
                    services[index] = planned.Route!;
                else
                    services.Add(planned.Route!);
            }
            return c with { Services = services };
        });
        return plan;
    }

    public (ServiceRoute? Normalized, string? Error) Validate(ServiceRoute route) =>
        ServiceRules.Validate(route, Services, config.Current.Routes, Dns.DnsRules.Names(config.Current.DnsEntries), external?.Names);

    /// <summary>Adds the route, or replaces the one with the same id.</summary>
    /// <exception cref="ArgumentException">The route does not pass <see cref="Validate"/>.</exception>
    public ServiceRoute Save(ServiceRoute route)
    {
        var (saved, error) = Validate(route);
        if (saved is null)
            throw new ArgumentException(error, nameof(route));
        Replace([saved]);
        return saved;
    }

    /// <summary>Applies <see cref="ServiceImport.Refresh"/> for the routes from <paramref name="source"/>.</summary>
    public RefreshResult Refresh(ServiceOrigin source, IReadOnlyList<DiscoveredService> found)
    {
        var result = ServiceImport.Refresh(Services, source, found);
        Replace(result.Updated);
        return result;
    }

    public void SetEnabled(Guid id, bool enabled)
    {
        if (Services.FirstOrDefault(s => s.Id == id) is { } route && route.Enabled != enabled)
            Replace([route with { Enabled = enabled }]);
    }

    /// <summary>Removes the routes and returns them, for <see cref="Restore"/>.</summary>
    public IReadOnlyList<ServiceRoute> Remove(IReadOnlyCollection<Guid> ids)
    {
        IReadOnlyList<ServiceRoute> removed = [];
        config.Update(c =>
        {
            removed = [.. c.Services.Where(s => ids.Contains(s.Id))];
            return removed.Count == 0 ? c : c with { Services = [.. c.Services.Where(s => !ids.Contains(s.Id))] };
        });
        return removed;
    }

    /// <summary>
    /// Puts removed routes back, except those whose names were taken meanwhile. One whose address
    /// went to another route gets a new one.
    /// </summary>
    public int Restore(IReadOnlyList<ServiceRoute> removed)
    {
        var restored = 0;
        config.Update(c =>
        {
            var services = c.Services.ToList();
            foreach (var original in removed)
            {
                var route = services.Any(s => s.Address == original.Address)
                    ? original with { Address = ServiceRules.NextAddress(services) }
                    : original;
                if (services.Any(s => s.Id == route.Id) || ServiceRules.Validate(route, services, c.Routes, Dns.DnsRules.Names(c.DnsEntries), external?.Names).Error is not null)
                    continue;
                services.Add(route);
                restored++;
            }
            return c with { Services = services };
        });
        return restored;
    }

    private void Replace(IReadOnlyList<ServiceRoute> routes)
    {
        if (routes.Count == 0)
            return;
        config.Update(c =>
        {
            var services = c.Services.ToList();
            foreach (var route in routes)
            {
                var index = services.FindIndex(s => s.Id == route.Id);
                if (index >= 0)
                    services[index] = route;
                else
                    services.Add(route);
            }
            return c with { Services = services };
        });
    }
}
