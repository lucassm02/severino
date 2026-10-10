using Severino.Core.Configuration;

namespace Severino.Core.Discovery;

/// <summary>
/// A Kubernetes service or a Docker container that could become a service route: the names the
/// app would call it by, and where each port really answers.
/// </summary>
/// <param name="Namespace">Kubernetes namespace, or Compose project ("" for a plain container).</param>
/// <param name="Name">Service name, or Compose service (container name without Compose).</param>
/// <param name="Names">The route names, normalized: for Kubernetes the four variants, short to .svc.cluster.local.</param>
/// <param name="Ports">Empty when it cannot be reached from outside; <paramref name="Unreachable"/> says why.</param>
/// <param name="Ready">Kubernetes: whether any pod is ready; null when unknown or for Docker.</param>
/// <param name="Kind">How it is reached, as shown in the list: "NodePort", "LoadBalancer", "Docker"…</param>
public sealed record DiscoveredService(
    ServiceKind Source,
    string Namespace,
    string Name,
    IReadOnlyList<string> Names,
    IReadOnlyList<ServicePort> Ports,
    bool? Ready,
    string Kind,
    string? Unreachable = null)
{
    public bool CanImport => Ports.Count > 0;
}
