using System.Net;
using Severino.Core.Configuration;
using Severino.Core.Discovery;
using Severino.Core.Dns;

namespace Severino.Core.Routes;

/// <param name="Result">What changed; null when the source could not be asked.</param>
/// <param name="Error">Why the source could not be asked, in words for the person.</param>
public sealed record RefreshOutcome(RefreshResult? Result, string? Error = null)
{
    /// <summary>"2 serviços com portas novas · não encontrados agora: velho".</summary>
    public string Describe()
    {
        if (Error is not null || Result is null)
            return Error ?? "";
        var parts = new List<string>
        {
            Result.Updated.Count switch
            {
                0 => "Nada mudou",
                1 => "1 serviço com portas novas",
                var n => $"{n} serviços com portas novas",
            },
        };
        if (Result.Missing.Count > 0)
            parts.Add($"não encontrados agora: {string.Join(", ", Result.Missing.Select(m => m.Names[0]))}");
        return string.Join(" · ", parts);
    }
}

/// <summary>
/// "Atualizar" for one source: asks kubectl or docker again and moves the routes from it to the
/// ports of now, keeping their names. Used by the button and by <see cref="ServiceWatcher"/>.
/// </summary>
public sealed class ServiceRefresher(ServiceDiscovery discovery, ServiceRouteService services, DnsService? dns = null)
{
    public async Task<RefreshOutcome> RefreshAsync(ServiceOrigin origin, CancellationToken cancellationToken)
    {
        var source = CommandSource.FromId(origin.Source);
        var routes = services.Services.Where(s => s.Origin is { } o && ServiceImport.SameSource(o, origin)).ToList();
        IReadOnlyList<DiscoveredService> found;
        if (origin.Kind == ServiceKind.Kubernetes)
        {
            // Keep the node the routes use, when it is still one of the cluster's; a node kept by
            // its DNS name is asked by its IP and written back by name.
            var node = routes.Where(r => !r.PortForward).SelectMany(r => r.Ports).Select(p => p.TargetHost).FirstOrDefault();
            string? nodeName = null;
            if (node is not null && !IPAddress.TryParse(node, out _) && dns?.Destinations().FirstOrDefault(d => d.Name == node) is { } destination)
            {
                nodeName = node;
                node = destination.Address;
            }
            var result = await discovery.KubernetesAsync(source, cancellationToken, node);
            if (result.Error is { } error)
                return new(null, error);
            if (result.Context != origin.Context)
                return new(null, $"O kubectl em {source} está no contexto {result.Context}, não em {origin.Context}. Troque o contexto e atualize de novo.");
            found = nodeName is not null && result.Node is { } address
                ? DiscoveredService.UseName(result.Services, address, nodeName)
                : result.Services;
        }
        else
        {
            var result = await discovery.DockerAsync(source, cancellationToken);
            if (result.Error is { } error)
                return new(null, error);
            found = result.Containers;
        }
        return new(services.Refresh(origin, found));
    }
}
