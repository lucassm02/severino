using Severino.Core.Configuration;
using Severino.Core.Routes;

namespace Severino.Core.Certificates;

/// <summary>
/// Which names the local CA may sign for: the Name Constraints of its root. A whole TLD when it
/// does not exist on the internet (so new routes under it need no new CA), the exact name
/// otherwise (so the CA cannot sign for anyone else's site).
/// </summary>
public static class CaCoverage
{
    /// <summary>Reserved by RFC 2606/6761 and ICANN; never delegated.</summary>
    public static readonly IReadOnlySet<string> ReservedTlds =
        new HashSet<string>(StringComparer.Ordinal) { "test", "localhost", "internal", "example", "invalid" };

    /// <summary>Normalized domains of every route with HTTPS on, enabled or not.</summary>
    public static IReadOnlyList<string> RequiredDomains(IEnumerable<RouteEntry> routes) =>
        [.. routes
            .Where(r => r.Https)
            .Select(r => RouteRules.Normalize(r.Domain))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    /// <param name="domains">Normalized domains.</param>
    /// <param name="tldExists">True or false when known; null when the DNS could not tell, which
    /// keeps the exact name.</param>
    public static IReadOnlyList<string> Compute(IEnumerable<string> domains, Func<string, bool?> tldExists)
    {
        var names = domains
            .Select(domain =>
            {
                var tld = Tld(domain);
                return ReservedTlds.Contains(tld) || tldExists(tld) == false ? tld : domain;
            })
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return [.. names
            .Where(name => !names.Any(other => other != name && IsCovered(name, [other])))
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// True when <paramref name="domain"/> is one of <paramref name="names"/> or below one of
    /// them, label by label: "empresa.com" covers "api.empresa.com" but not "xempresa.com".
    /// </summary>
    public static bool IsCovered(string domain, IEnumerable<string> names) =>
        names.Any(name => domain == name || domain.EndsWith("." + name, StringComparison.Ordinal));

    public static string Tld(string domain) => domain[(domain.LastIndexOf('.') + 1)..];
}
