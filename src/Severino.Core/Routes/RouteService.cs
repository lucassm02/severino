using Severino.Core.Configuration;

namespace Severino.Core.Routes;

/// <summary>Create, edit and remove routes; every change is saved through <see cref="ConfigService"/>.</summary>
public sealed class RouteService(ConfigService config, Dns.ExternalHosts? external = null)
{
    public IReadOnlyList<RouteEntry> Routes => config.Current.Routes;

    public RouteErrors Validate(RouteEntry route) =>
        RouteRules.Validate(route, Routes, config.Current.Settings.HttpPort, config.Current.Settings.HttpsPort, config.Current.Services,
            Dns.DnsRules.Names(config.Current.DnsEntries), external?.Names);

    /// <summary>Adds the route, or replaces the one with the same id. Domain is stored normalized.</summary>
    /// <exception cref="ArgumentException">The route does not pass <see cref="Validate"/>.</exception>
    public RouteEntry Save(RouteEntry route)
    {
        var errors = Validate(route);
        if (!errors.IsValid)
            throw new ArgumentException(errors.Domain ?? errors.Path ?? errors.Target, nameof(route));

        var saved = route with { Domain = RouteRules.Normalize(route.Domain)!, Path = RouteRules.NormalizePath(route.Path)!, Group = route.Group.Trim() };
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

    public string Export() => RouteTransfer.Export(Routes);

    /// <summary>Adds the file's new routes in one change; see <see cref="RouteTransfer.Import"/>.</summary>
    /// <exception cref="InvalidRouteFileException">Not a routes file this version can read.</exception>
    public ImportResult Import(string json)
    {
        var settings = config.Current.Settings;
        var result = RouteTransfer.Import(json, Routes, settings.HttpPort, settings.HttpsPort, config.Current.Services);
        if (result.Added.Count > 0)
            config.Update(c => c with { Routes = [.. c.Routes, .. result.Added] });
        return result;
    }

    public void SetEnabled(Guid id, bool enabled) =>
        Change(id, r => r with { Enabled = enabled });

    /// <summary>Every route of <paramref name="group"/> on or off, in one change: the hosts follows at once.</summary>
    public void SetGroupEnabled(string group, bool enabled) =>
        config.Update(c => c with { Routes = [.. c.Routes.Select(r => r.Group == group ? r with { Enabled = enabled } : r)] });

    /// <summary>
    /// Gives the group's routes another group name; an empty one takes them out of any group, and
    /// the name of a group in use joins the two.
    /// </summary>
    public void RenameGroup(string group, string newName)
    {
        var name = newName.Trim();
        config.Update(c => c with { Routes = [.. c.Routes.Select(r => r.Group == group ? r with { Group = name } : r)] });
    }

    /// <summary>The groups in use, in the order they first appear.</summary>
    public IReadOnlyList<string> Groups => [.. Routes.Select(r => r.Group).Where(g => g.Length > 0).Distinct(StringComparer.Ordinal)];

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
