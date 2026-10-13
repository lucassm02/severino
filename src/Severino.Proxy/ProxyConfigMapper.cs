using Severino.Core.Configuration;
using Severino.Core.Routes;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace Severino.Proxy;

/// <summary>Turns Severino routes, and the HTTP ports of services, into YARP routes and clusters, one cluster per route.</summary>
public static class ProxyConfigMapper
{
    public const string DomainKey = "severino.domain";
    public const string TargetKey = "severino.target";

    /// <summary>On a service's internal routes: the service's id, for the request log.</summary>
    public const string ServiceKey = "severino.service";

    /// <summary>Long enough to sit on a breakpoint in the backend without a 504.</summary>
    public static readonly TimeSpan ActivityTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Enabled routes only; invalid ones and ones that would loop back into the proxy are skipped.</summary>
    public static (IReadOnlyList<RouteConfig> Routes, IReadOnlyList<ClusterConfig> Clusters) Map(IReadOnlyList<RouteEntry> routes, int proxyPort, int? httpsPort = null,
        IReadOnlyList<ServiceRoute>? services = null)
    {
        var yarpRoutes = new List<RouteConfig>();
        var clusters = new List<ClusterConfig>();

        foreach (var route in routes.Where(r => r.Enabled))
        {
            // A folder is served by the proxy itself, not forwarded: no cluster (see ProxyServer.ServeFolderAsync).
            if (RouteRules.FolderOf(route.Target) is not null)
                continue;
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
            // The route's own headers.
            transforms.AddRange(HeaderTransforms(route.Rules));

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

        foreach (var service in (services ?? []).Where(s => s.Enabled))
        {
            foreach (var port in service.Ports.Where(p => p.Http))
            {
                var id = ServiceRouteId(service, port.Port);
                var target = $"http://{(port.TargetHost.Contains(':') ? $"[{port.TargetHost}]" : port.TargetHost)}:{port.TargetPort}/";
                // Host with its port: the forwarder brought the app's own Host here, so the
                // service's ports tell themselves apart. https://name (443) is the main port's.
                var hosts = service.Names.Select(n => $"{n}:{port.Port}").ToList();
                if (ServiceHttp.ExtraTlsPort(service) is { } tls && ServiceHttp.Main(service) == port)
                    hosts.AddRange(service.Names.Select(n => $"{n}:{tls}"));

                // The app's Host goes on to the destination, as it does through the tunnel.
                var transforms = new List<IReadOnlyDictionary<string, string>> { new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" } };
                transforms.AddRange(HeaderTransforms(service.Rules));

                yarpRoutes.Add(new RouteConfig
                {
                    RouteId = id,
                    ClusterId = id,
                    Match = new RouteMatch { Hosts = hosts, Path = "/{**catch-all}" },
                    Metadata = new Dictionary<string, string> { [DomainKey] = service.Names[0], [TargetKey] = target, [ServiceKey] = service.Id.ToString("N") },
                    Transforms = transforms,
                });
                clusters.Add(new ClusterConfig
                {
                    ClusterId = id,
                    Destinations = new Dictionary<string, DestinationConfig> { ["target"] = new() { Address = target } },
                    HttpRequest = new ForwarderRequestConfig { ActivityTimeout = ActivityTimeout },
                });
            }
        }

        return (yarpRoutes, clusters);
    }

    /// <summary>The internal route of one HTTP port of a service.</summary>
    public static string ServiceRouteId(ServiceRoute service, int port) => $"svc-{service.Id:N}-{port}";

    /// <summary>The rules' headers as YARP transforms: an empty value takes the header out.</summary>
    private static IEnumerable<IReadOnlyDictionary<string, string>> HeaderTransforms(TrafficRules rules)
    {
        foreach (var header in rules.RequestHeaders)
        {
            yield return header.Value.Length == 0
                ? new Dictionary<string, string> { ["RequestHeaderRemove"] = header.Name.Trim() }
                : new Dictionary<string, string> { ["RequestHeader"] = header.Name.Trim(), ["Set"] = header.Value };
        }
        foreach (var header in rules.ResponseHeaders)
        {
            yield return header.Value.Length == 0
                ? new Dictionary<string, string> { ["ResponseHeaderRemove"] = header.Name.Trim(), ["When"] = "Always" }
                : new Dictionary<string, string> { ["ResponseHeader"] = header.Name.Trim(), ["Set"] = header.Value, ["When"] = "Always" };
        }
    }
}
