using System.Net;
using Severino.Contracts;
using Severino.Core.Configuration;

namespace Severino.Core.Routes;

public sealed record RouteErrors(string? Domain, string? Target)
{
    public bool IsValid => Domain is null && Target is null;
}

/// <summary>Validation shared by the route form and the proxy.</summary>
public static class RouteRules
{
    /// <summary>Checks <paramref name="route"/> against the other routes; ignores the one with the same id.</summary>
    public static RouteErrors Validate(RouteEntry route, IReadOnlyList<RouteEntry> routes, int proxyPort)
    {
        string? domainError = null;
        if (!DomainName.TryNormalize(route.Domain, out var domain, out var reason))
            domainError = reason;
        else if (routes.Any(r => r.Id != route.Id && Normalize(r.Domain) == domain))
            domainError = "Já existe uma rota para este domínio.";

        string? targetError = null;
        if (!TryParseTarget(route.Target, out var target, out var targetReason))
            targetError = targetReason;
        else if (IsLoop(target, domain, routes, proxyPort))
            targetError = "O destino aponta para o próprio Severino e criaria um loop.";

        return new RouteErrors(domainError, targetError);
    }

    public static bool TryParseTarget(string? value, out Uri target, out string? error)
    {
        target = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.HostNameType == UriHostNameType.Unknown || uri.Host.Length == 0)
        {
            error = "Informe um destino como http://127.0.0.1:3000.";
            return false;
        }
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            error = "O destino deve começar com http:// ou https://.";
            return false;
        }
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
        {
            error = "O destino não pode ter usuário, query ou fragmento.";
            return false;
        }

        target = uri;
        error = null;
        return true;
    }

    /// <summary>
    /// True when the target would come back into the proxy: same port, and a host that resolves
    /// to it (loopback, *.localhost, or any routed domain, which the hosts file sends to loopback).
    /// </summary>
    public static bool IsLoop(Uri target, string? routeDomain, IReadOnlyList<RouteEntry> routes, int proxyPort)
    {
        if (target.Port != proxyPort)
            return false;

        var host = target.IdnHost.ToLowerInvariant();
        if (IPAddress.TryParse(host, out var ip))
            return IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any);

        return host == "localhost"
            || host.EndsWith(".localhost", StringComparison.Ordinal)
            || host == routeDomain
            || routes.Any(r => Normalize(r.Domain) == host);
    }

    /// <summary>The distinct, normalized domains of enabled routes: what the hosts block should hold.</summary>
    public static IReadOnlyList<string> ActiveDomains(IEnumerable<RouteEntry> routes) =>
        [.. routes
            .Where(r => r.Enabled)
            .Select(r => Normalize(r.Domain))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    public static string? Normalize(string domain) =>
        DomainName.TryNormalize(domain, out var normalized, out _) ? normalized : null;
}
