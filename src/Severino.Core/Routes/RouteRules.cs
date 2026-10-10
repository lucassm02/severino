using System.Net;
using Severino.Contracts;
using Severino.Core.Configuration;

namespace Severino.Core.Routes;

public sealed record RouteErrors(string? Domain, string? Target, string? Path = null)
{
    public bool IsValid => Domain is null && Target is null && Path is null;
}

/// <summary>Validation shared by the route form and the proxy.</summary>
public static class RouteRules
{
    /// <summary>
    /// Checks <paramref name="route"/> against the other routes; ignores the one with the same id.
    /// <paramref name="services"/>, <paramref name="dnsNames"/> and <paramref name="external"/> (hosts
    /// lines outside Severino) hold names a web route cannot take.
    /// </summary>
    public static RouteErrors Validate(RouteEntry route, IReadOnlyList<RouteEntry> routes, int proxyPort, int? httpsPort = null, IReadOnlyList<ServiceRoute>? services = null,
        IReadOnlySet<string>? dnsNames = null, IReadOnlySet<string>? external = null)
    {
        string? domainError = null;
        string? pathError = null;
        if (!TryNormalizePath(route.Path, out var path, out var pathReason))
            pathError = pathReason;
        if (!TryNormalize(route.Domain, out var domain, out var reason))
            domainError = reason;
        else if (path is not null && routes.Any(r => r.Id != route.Id && Normalize(r.Domain) == domain && NormalizePath(r.Path) == path))
            domainError = path.Length == 0 ? "Já existe uma rota para este domínio." : $"Já existe uma rota para {domain}{path}.";
        else if (services?.Any(s => s.Names.Contains(domain)) == true)
            domainError = "Já existe uma rota de serviço com este nome.";
        else if (dnsNames?.Contains(domain) == true)
            domainError = "Já existe uma entrada DNS com este nome.";
        else if (external?.Contains(domain) == true)
            domainError = $"Este nome {Dns.DnsRules.ExternalClash}";

        string? targetError = null;
        if (!TryParseTarget(route.Target, out var target, out var targetReason))
            targetError = targetReason;
        else if (IsLoop(target, domain, routes, proxyPort, httpsPort))
            targetError = "O destino aponta para o próprio Severino e criaria um loop.";

        return new RouteErrors(domainError, targetError, pathError);
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
    /// True when the target would come back into the proxy: one of its ports, and a host that resolves
    /// to it (loopback, *.localhost, or any routed domain, which the hosts file sends to loopback).
    /// </summary>
    public static bool IsLoop(Uri target, string? routeDomain, IReadOnlyList<RouteEntry> routes, int proxyPort, int? httpsPort = null)
    {
        if (target.Port != proxyPort && target.Port != httpsPort)
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
        TryNormalize(domain, out var normalized, out _) ? normalized : null;

    /// <summary>A domain, or a wildcard like *.meuapp.sev that covers every name below it.</summary>
    public static bool TryNormalize(string? domain, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? normalized,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error) =>
        DomainName.IsWildcard(domain)
            ? DomainName.TryNormalizeWildcard(domain, out normalized, out error)
            : DomainName.TryNormalize(domain, out normalized, out error);

    /// <summary>
    /// A route's path: empty for the whole domain, else "/api" style, segments of letters, digits
    /// and <c>-._~</c>, no trailing slash. Null when it cannot be one.
    /// </summary>
    public static string? NormalizePath(string? path) => TryNormalizePath(path, out var normalized, out _) ? normalized : null;

    public static bool TryNormalizePath(string? path, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? normalized,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        normalized = null;
        var value = (path ?? "").Trim().TrimEnd('/');
        if (value.Length == 0)
        {
            normalized = "";
            error = null;
            return true;
        }
        if (!value.StartsWith('/'))
            value = "/" + value;
        var segments = value[1..].Split('/');
        if (segments.Any(s => s.Length == 0 || !s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~')))
        {
            error = "Use um caminho como /api ou /v1/pedidos: letras, números, - . _ e ~.";
            return false;
        }
        normalized = value;
        error = null;
        return true;
    }

    /// <summary>
    /// The routes for a request's host: those of the exact domain first, else those of the most
    /// specific wildcard above it, so api.meuapp.sev beats *.meuapp.sev.
    /// </summary>
    public static IReadOnlyList<RouteEntry> ForHost(IEnumerable<RouteEntry> routes, string host)
    {
        if (Normalize(host) is not { } name)
            return [];
        var enabled = routes.Where(r => r.Enabled).Select(r => (Route: r, Domain: Normalize(r.Domain))).Where(r => r.Domain is not null).ToList();
        var exact = enabled.Where(r => r.Domain == name).Select(r => r.Route).ToList();
        if (exact.Count > 0)
            return exact;
        var wildcard = enabled.Where(r => DomainName.IsWildcard(r.Domain) && DomainName.MatchesWildcard(r.Domain!, name))
            .OrderByDescending(r => r.Domain!.Length)
            .FirstOrDefault().Domain;
        return wildcard is null ? [] : [.. enabled.Where(r => r.Domain == wildcard).Select(r => r.Route)];
    }

    /// <summary>
    /// The route for a request: by host (see <see cref="ForHost"/>), then the longest path that
    /// <paramref name="path"/> starts with, segment by segment. Without a path, the domain's root route.
    /// </summary>
    public static RouteEntry? Find(IEnumerable<RouteEntry> routes, string host, string? path = null)
    {
        var candidates = ForHost(routes, host);
        var requested = path ?? "/";
        return candidates
            .Select(r => (Route: r, Path: NormalizePath(r.Path)))
            .Where(r => r.Path is not null && (r.Path.Length == 0 || requested.Equals(r.Path, StringComparison.OrdinalIgnoreCase)
                || requested.StartsWith(r.Path + "/", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(r => r.Path!.Length)
            .FirstOrDefault().Route;
    }
}
