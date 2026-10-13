using Severino.Core.Configuration;

namespace Severino.Core.Routes;

/// <summary>
/// The HTTP ports of a service: they go through the proxy, which picks the service's internal
/// route by name and port. With HTTPS on, the service also answers on 443 unless it has a 443 of
/// its own, and the other HTTP ports take TLS on the same port.
/// </summary>
public static class ServiceHttp
{
    public const int TlsPort = 443;

    public static bool HasHttp(ServiceRoute service) => service.Ports.Any(p => p.Http);

    /// <summary>Where https://name and http://name go: the HTTP port 80 when there is one, else the first HTTP port.</summary>
    public static ServicePort? Main(ServiceRoute service) =>
        service.Ports.FirstOrDefault(p => p.Http && p.Port == 80) ?? service.Ports.FirstOrDefault(p => p.Http);

    /// <summary>443, when HTTPS is on, some port is HTTP and the service has no 443 of its own.</summary>
    public static int? ExtraTlsPort(ServiceRoute service) =>
        service.Https && HasHttp(service) && service.Ports.All(p => p.Port != TlsPort) ? TlsPort : null;

    /// <summary>
    /// New ports from Docker or Kubernetes, keeping the HTTP mark of each port that is still
    /// there, by the number the app calls.
    /// </summary>
    public static IReadOnlyList<ServicePort> KeepHttp(IReadOnlyList<ServicePort> fresh, IReadOnlyList<ServicePort> old) =>
        [.. fresh.Select(p => p with { Http = old.Any(o => o.Port == p.Port && o.Http) })];
}
