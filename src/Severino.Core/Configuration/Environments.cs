namespace Severino.Core.Configuration;

/// <summary>
/// Environments, like "producao" and "staging": a route, service or DNS entry may belong to one,
/// and only those of the active environment (and those of none) count. The others keep their
/// switch as it was and come back with their environment. There is no list of environments
/// apart: one exists while some item uses it.
/// </summary>
public static class Environments
{
    public static bool Same(string? a, string? b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Two items with the same name get in each other's way unless both have an environment and
    /// the environments differ: <c>sql.banco</c> in producao and in staging is fine.
    /// </summary>
    public static bool Clash(string? a, string? b) => (a ?? "").Trim().Length == 0 || (b ?? "").Trim().Length == 0 || Same(a, b);

    /// <summary>Of none, or of the active one.</summary>
    public static bool IsActive(string? environment, SeverinoConfig config) =>
        (environment ?? "").Trim().Length == 0 || Same(environment, config.Settings.ActiveEnvironment);

    /// <summary>The environments in use, in alphabetical order, each as first written.</summary>
    public static IReadOnlyList<string> List(SeverinoConfig config) =>
        [.. config.Routes.Select(r => r.Environment)
            .Concat(config.Services.Select(s => s.Environment))
            .Concat(config.DnsEntries.Select(d => d.Environment))
            .Select(e => (e ?? "").Trim())
            .Where(e => e.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// The config as the proxy, the hosts block and the rest should apply it: items of another
    /// environment switched off. The same instance when no item has an environment.
    /// </summary>
    public static SeverinoConfig Effective(SeverinoConfig config)
    {
        if (List(config).Count == 0)
            return config;
        return config with
        {
            Routes = [.. config.Routes.Select(r => !r.Enabled || IsActive(r.Environment, config) ? r : r with { Enabled = false })],
            Services = [.. config.Services.Select(s => !s.Enabled || IsActive(s.Environment, config) ? s : s with { Enabled = false })],
            DnsEntries = [.. config.DnsEntries.Select(d => !d.Enabled || IsActive(d.Environment, config) ? d : d with { Enabled = false })],
        };
    }

    /// <summary>
    /// Keeps the active environment sensible after a change. The first environment ever used
    /// becomes the active one, so the item just tagged does not go dark. One no item uses any
    /// more leaves nothing active: switching to another on its own could point names at production.
    /// </summary>
    public static SeverinoConfig Settle(SeverinoConfig before, SeverinoConfig after)
    {
        var active = after.Settings.ActiveEnvironment.Trim();
        var list = List(after);
        if (active.Length == 0 && list.Count > 0 && List(before).Count == 0)
            return after with { Settings = after.Settings with { ActiveEnvironment = list[0] } };
        if (active.Length > 0 && !list.Contains(active, StringComparer.OrdinalIgnoreCase))
            return after with { Settings = after.Settings with { ActiveEnvironment = "" } };
        return after;
    }
}
