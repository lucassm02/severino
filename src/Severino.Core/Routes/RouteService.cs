using Severino.Core.Configuration;

namespace Severino.Core.Routes;

/// <summary>Create, edit and remove routes; every change is saved through <see cref="ConfigService"/>.</summary>
public sealed class RouteService(ConfigService config)
{
    public IReadOnlyList<RouteEntry> Routes => config.Current.Routes;

    public RouteErrors Validate(RouteEntry route) =>
        RouteRules.Validate(route, Routes, config.Current.Settings.HttpPort);

    /// <summary>Adds the route, or replaces the one with the same id. Domain is stored normalized.</summary>
    /// <exception cref="ArgumentException">The route does not pass <see cref="Validate"/>.</exception>
    public RouteEntry Save(RouteEntry route)
    {
        var errors = Validate(route);
        if (!errors.IsValid)
            throw new ArgumentException(errors.Domain ?? errors.Target, nameof(route));

        var saved = route with { Domain = RouteRules.Normalize(route.Domain)! };
        config.Update(c =>
        {
            var routes = c.Routes.ToList();
            var index = routes.FindIndex(r => r.Id == saved.Id);
            if (index >= 0)
                routes[index] = saved;
            else
                routes.Add(saved);
            return c with { Routes = routes };
        });
        return saved;
    }

    public void SetEnabled(Guid id, bool enabled) =>
        Change(id, r => r with { Enabled = enabled });

    /// <summary>Removes the route and returns what <see cref="Restore"/> needs to undo it.</summary>
    public RemovedRoute? Remove(Guid id)
    {
        RemovedRoute? removed = null;
        config.Update(c =>
        {
            var routes = c.Routes.ToList();
            var index = routes.FindIndex(r => r.Id == id);
            if (index < 0)
                return c;
            removed = new RemovedRoute(routes[index], index);
            routes.RemoveAt(index);
            return c with { Routes = routes };
        });
        return removed;
    }

    /// <summary>Puts a removed route back where it was, unless its domain was taken meanwhile.</summary>
    public bool Restore(RemovedRoute removed)
    {
        if (Routes.Any(r => r.Id == removed.Route.Id) || !Validate(removed.Route).IsValid)
            return false;

        config.Update(c =>
        {
            var routes = c.Routes.ToList();
            routes.Insert(Math.Min(removed.Index, routes.Count), removed.Route);
            return c with { Routes = routes };
        });
        return true;
    }

    private void Change(Guid id, Func<RouteEntry, RouteEntry> change) =>
        config.Update(c => c with { Routes = [.. c.Routes.Select(r => r.Id == id ? change(r) : r)] });
}

public sealed record RemovedRoute(RouteEntry Route, int Index);
