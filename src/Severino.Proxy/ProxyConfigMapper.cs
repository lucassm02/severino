using Severino.Core.Configuration;
using Severino.Core.Routes;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace Severino.Proxy;

/// <summary>Turns Severino routes into YARP routes and clusters, one cluster per route.</summary>
public static class ProxyConfigMapper
{
    public const string DomainKey = "severino.domain";
    public const string TargetKey = "severino.target";

    /// <summary>Long enough to sit on a breakpoint in the backend without a 504.</summary>
    public static readonly TimeSpan ActivityTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Enabled routes only; invalid ones and ones that would loop back into the proxy are skipped.</summary>
    public static (IReadOnlyList<RouteConfig> Routes, IReadOnlyList<ClusterConfig> Clusters) Map(IReadOnlyList<RouteEntry> routes, int proxyPort, int? httpsPort = null)
    {
        var yarpRoutes = new List<RouteConfig>();
        var clusters = new List<ClusterConfig>();

        foreach (var route in routes.Where(r => r.Enabled))
        {
            if (!RouteRules.Validate(route, routes, proxyPort, httpsPort).IsValid || !RouteRules.TryParseTarget(route.Target, out var target, out _))
                continue;

            var domain = RouteRules.Normalize(route.Domain)!;
            var id = route.Id.ToString("N");

            // "/api" takes "/api" and everything below it; routing precedence puts the longer,
            // literal path ahead of the domain's catch-all.
            var path = RouteRules.NormalizePath(route.Path) ?? "";
            var transforms = new List<IReadOnlyDictionary<string, string>>();
            // Off by default YARP sends the destination's Host, which keeps Vite and webpack-dev-server
            // happy; X-Forwarded-Host still carries the original.
            if (route.PreserveHost)
                transforms.Add(new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" });
            if (route.StripPath && path.Length > 0)
                transforms.Add(new Dictionary<string, string> { ["PathRemovePrefix"] = path });

            yarpRoutes.Add(new RouteConfig
            {
                RouteId = id,
                ClusterId = id,
                Match = new RouteMatch { Hosts = [domain], Path = path + "/{**catch-all}" },
                Metadata = new Dictionary<string, string> { [DomainKey] = domain, [TargetKey] = target.ToString() },
                Transforms = transforms.Count > 0 ? transforms : null,
            });

            clusters.Add(new ClusterConfig
            {
                ClusterId = id,
                Destinations = new Dictionary<string, DestinationConfig> { ["target"] = new() { Address = target.ToString() } },
                HttpRequest = new ForwarderRequestConfig { ActivityTimeout = ActivityTimeout },
                HttpClient = new HttpClientConfig { DangerousAcceptAnyServerCertificate = route.IgnoreTargetCertErrors },
            });
        }

        return (yarpRoutes, clusters);
    }
}
