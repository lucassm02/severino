using System.Text.Json;
using Severino.Contracts;
using Severino.Core.Configuration;

namespace Severino.Core.Discovery;

/// <summary>Reads kubectl's JSON into service route candidates.</summary>
public static class KubernetesDiscovery
{
    /// <summary>The four names a service answers to inside the cluster, short first.</summary>
    public static IReadOnlyList<string> NameVariants(string name, string @namespace) =>
        [.. new[] { name, $"{name}.{@namespace}", $"{name}.{@namespace}.svc", $"{name}.{@namespace}.svc.cluster.local" }
            .Select(n => DomainName.TryNormalize(n, out var normalized, out _, allowSingleLabel: true) ? normalized : null)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)];

    /// <summary>Internal IPs of the nodes, in the order kubectl lists them.</summary>
    public static IReadOnlyList<string> NodeAddresses(string nodesJson)
    {
        using var doc = JsonDocument.Parse(nodesJson);
        var result = new List<string>();
        foreach (var node in Items(doc.RootElement))
        {
            if (!node.TryGetProperty("status", out var status) || !status.TryGetProperty("addresses", out var addresses))
                continue;
            foreach (var address in addresses.EnumerateArray())
            {
                if (address.GetProperty("type").GetString() == "InternalIP" && address.GetProperty("address").GetString() is { } ip)
                {
                    result.Add(ip);
                    break;
                }
            }
        }
        return result;
    }

    /// <summary>"namespace/name" of the services with at least one ready endpoint.</summary>
    public static IReadOnlySet<string> ReadyServices(string endpointSlicesJson)
    {
        using var doc = JsonDocument.Parse(endpointSlicesJson);
        var ready = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slice in Items(doc.RootElement))
        {
            var metadata = slice.GetProperty("metadata");
            if (!metadata.TryGetProperty("labels", out var labels) || !labels.TryGetProperty("kubernetes.io/service-name", out var service))
                continue;
            if (!slice.TryGetProperty("endpoints", out var endpoints) || endpoints.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var endpoint in endpoints.EnumerateArray())
            {
                if (endpoint.TryGetProperty("conditions", out var conditions) && conditions.TryGetProperty("ready", out var isReady) && isReady.ValueKind == JsonValueKind.True)
                {
                    ready.Add($"{metadata.GetProperty("namespace").GetString()}/{service.GetString()}");
                    break;
                }
            }
        }
        return ready;
    }

    /// <summary>The host of the API server URL, like 192.168.203.100 for https://192.168.203.100:6443.</summary>
    public static string? ServerHost(string serverUrl) =>
        Uri.TryCreate(serverUrl.Trim(), UriKind.Absolute, out var uri) ? uri.Host : null;

    /// <summary>
    /// Every service, importable or not. The destination of each TCP port, in order of preference:
    /// a load balancer IP, an external IP (as an ingress controller often has), or the NodePort
    /// on <paramref name="nodeAddress"/>. Only ClusterIP, with neither, cannot be reached.
    /// </summary>
    public static IReadOnlyList<DiscoveredService> Services(string servicesJson, string? nodeAddress, IReadOnlySet<string>? ready)
    {
        using var doc = JsonDocument.Parse(servicesJson);
        var result = new List<DiscoveredService>();
        foreach (var item in Items(doc.RootElement))
        {
            var metadata = item.GetProperty("metadata");
            var name = metadata.GetProperty("name").GetString() ?? "";
            var @namespace = metadata.TryGetProperty("namespace", out var ns) ? ns.GetString() ?? "default" : "default";
            var spec = item.GetProperty("spec");
            var type = spec.TryGetProperty("type", out var t) ? t.GetString() ?? "ClusterIP" : "ClusterIP";
            var isReady = ready is null ? (bool?)null : ready.Contains($"{@namespace}/{name}");

            var loadBalancer = FirstLoadBalancerAddress(item);
            var external = spec.TryGetProperty("externalIPs", out var ips) && ips.ValueKind == JsonValueKind.Array
                ? ips.EnumerateArray().Select(i => i.GetString()).FirstOrDefault(i => !string.IsNullOrEmpty(i))
                : null;

            var ports = new List<ServicePort>();
            var kind = type;
            if (spec.TryGetProperty("ports", out var portList) && portList.ValueKind == JsonValueKind.Array)
            {
                foreach (var port in portList.EnumerateArray())
                {
                    var protocol = port.TryGetProperty("protocol", out var p) ? p.GetString() : "TCP";
                    if (!string.Equals(protocol, "TCP", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var number = port.GetProperty("port").GetInt32();
                    if (loadBalancer is not null)
                        ports.Add(new() { Port = number, TargetHost = loadBalancer, TargetPort = number });
                    else if (external is not null)
                    {
                        ports.Add(new() { Port = number, TargetHost = external, TargetPort = number });
                        kind = "externalIPs";
                    }
                    else if (nodeAddress is not null && port.TryGetProperty("nodePort", out var nodePort))
                    {
                        ports.Add(new() { Port = number, TargetHost = nodeAddress, TargetPort = nodePort.GetInt32() });
                        kind = "NodePort"; // also a LoadBalancer still waiting for its address
                    }
                }
            }

            var unreachable = ports.Count > 0 ? null
                : type == "ExternalName" ? "ExternalName: aponta para fora do cluster."
                : type is "NodePort" or "LoadBalancer" && nodeAddress is null ? "Escolha um nó para usar a NodePort."
                : "Só ClusterIP: não tem acesso de fora do cluster.";
            result.Add(new(ServiceKind.Kubernetes, @namespace, name, NameVariants(name, @namespace), ports, isReady, kind, unreachable));
        }
        return [.. result.OrderBy(s => s.Namespace, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal)];
    }

    private static string? FirstLoadBalancerAddress(JsonElement service)
    {
        if (!service.TryGetProperty("status", out var status) || !status.TryGetProperty("loadBalancer", out var lb)
            || !lb.TryGetProperty("ingress", out var ingress) || ingress.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var entry in ingress.EnumerateArray())
        {
            if (entry.TryGetProperty("ip", out var ip) && ip.GetString() is { Length: > 0 } address)
                return address;
            if (entry.TryGetProperty("hostname", out var host) && host.GetString() is { Length: > 0 } hostname)
                return hostname;
        }
        return null;
    }

    private static IEnumerable<JsonElement> Items(JsonElement root) =>
        root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray() : [];
}
