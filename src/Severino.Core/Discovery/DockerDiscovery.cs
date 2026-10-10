using System.Text.Json;
using System.Text.RegularExpressions;
using Severino.Contracts;
using Severino.Core.Configuration;

namespace Severino.Core.Discovery;

/// <summary>
/// Reads <c>docker ps --format json</c> into service route candidates. Only ports published on
/// the host count: Docker's internal DNS is not used (decided 2026-10-10).
/// </summary>
public static partial class DockerDiscovery
{
    private const string ProjectLabel = "com.docker.compose.project";
    private const string ServiceLabel = "com.docker.compose.service";

    /// <summary>One container per line, as <c>docker ps --format json</c> prints them.</summary>
    public static IReadOnlyList<DiscoveredService> Containers(string psJsonLines)
    {
        var result = new List<DiscoveredService>();
        foreach (var line in psJsonLines.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var container = (String(root, "Names") ?? "").Split(',')[0];
            var labels = Labels(String(root, "Labels") ?? "");
            labels.TryGetValue(ProjectLabel, out var project);
            labels.TryGetValue(ServiceLabel, out var service);

            var names = new[] { service, container }
                .Select(n => n is not null && DomainName.TryNormalize(n, out var normalized, out _, allowSingleLabel: true) ? normalized : null)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var ports = PublishedPorts(String(root, "Ports") ?? "");
            var unreachable = ports.Count > 0 ? null : "Nenhuma porta publicada no host.";
            if (names.Count == 0)
                (ports, unreachable) = ([], "O nome do container não serve como nome de host.");
            result.Add(new(ServiceKind.Docker, project ?? "", service ?? container, names, ports, null, "Docker", unreachable));
        }
        return [.. result.OrderBy(c => c.Namespace, StringComparer.Ordinal).ThenBy(c => c.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The TCP ports published on the host, as "container port → 127.0.0.1:host port". IPv4
    /// and IPv6 bindings of the same port count once; ranges are expanded.
    /// </summary>
    public static IReadOnlyList<ServicePort> PublishedPorts(string ports)
    {
        var result = new List<ServicePort>();
        foreach (Match match in PortMapping().Matches(ports))
        {
            if (!string.Equals(match.Groups["proto"].Value, "tcp", StringComparison.OrdinalIgnoreCase))
                continue;
            var hostStart = int.Parse(match.Groups["host"].Value);
            var containerStart = int.Parse(match.Groups["container"].Value);
            var count = match.Groups["hostEnd"].Success ? int.Parse(match.Groups["hostEnd"].Value) - hostStart + 1 : 1;
            for (var i = 0; i < count; i++)
            {
                var mapping = new ServicePort { Port = containerStart + i, TargetHost = "127.0.0.1", TargetPort = hostStart + i };
                if (!result.Any(p => p.Port == mapping.Port))
                    result.Add(mapping);
            }
        }
        return result;
    }

    /// <summary>
    /// docker ps joins labels with commas, and a value may hold commas itself (a Compose file
    /// list): a piece without '=' belongs to the value before it.
    /// </summary>
    private static Dictionary<string, string> Labels(string text)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        string? last = null;
        foreach (var piece in text.Split(','))
        {
            var equals = piece.IndexOf('=');
            if (equals > 0 && !piece[..equals].Contains(' '))
            {
                last = piece[..equals];
                labels[last] = piece[(equals + 1)..];
            }
            else if (last is not null)
                labels[last] += "," + piece;
        }
        return labels;
    }

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // "0.0.0.0:24600->4000/tcp", "[::]:8000-8001->80-81/tcp"; a bare "5432/tcp" is not published.
    [GeneratedRegex(@"(?:\[[^\]]*\]|[\d.]+):(?<host>\d+)(?:-(?<hostEnd>\d+))?->(?<container>\d+)(?:-\d+)?/(?<proto>\w+)")]
    private static partial Regex PortMapping();
}
