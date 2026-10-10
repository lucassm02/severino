using System.Text.Json;

namespace Severino.Core.Discovery;

/// <param name="Context">The kubectl context the services came from.</param>
/// <param name="Nodes">Node internal IPs; any of them answers any NodePort.</param>
/// <param name="Node">The node used for NodePorts: the API server's host when it is a node, else the first.</param>
public sealed record KubernetesResult(string Context, IReadOnlyList<string> Nodes, string? Node, IReadOnlyList<DiscoveredService> Services, string? Error = null)
{
    public static KubernetesResult Failed(string error) => new("", [], null, [], error);
}

/// <param name="Engine">Docker's engine ID: the same engine reached from Windows and from WSL is shown once.</param>
public sealed record DockerResult(string Engine, IReadOnlyList<DiscoveredService> Containers, string? Error = null)
{
    public static DockerResult Failed(string error) => new("", [], error);
}

/// <summary>
/// Finds services with the tools already on the machine: kubectl and docker, on Windows or
/// inside a running WSL distro. Read-only: every command only lists.
/// </summary>
public sealed class ServiceDiscovery(ICommandRunner runner)
{
    /// <summary>Windows, then every running distro. Stopped distros are left alone: listing must not start them.</summary>
    public async Task<IReadOnlyList<CommandSource>> SourcesAsync(CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(CommandSource.Windows, "wsl.exe", ["--list", "--running", "--quiet"], cancellationToken);
        var distros = result.Succeeded ? ParseDistros(result.Output) : [];
        return [CommandSource.Windows, .. distros.Select(CommandSource.Wsl)];
    }

    /// <summary>Installed distros that are not running: offered apart, since asking them starts them.</summary>
    public async Task<IReadOnlyList<CommandSource>> StoppedSourcesAsync(CancellationToken cancellationToken)
    {
        var all = await runner.RunAsync(CommandSource.Windows, "wsl.exe", ["--list", "--quiet"], cancellationToken);
        var running = await runner.RunAsync(CommandSource.Windows, "wsl.exe", ["--list", "--running", "--quiet"], cancellationToken);
        if (!all.Succeeded)
            return [];
        var up = running.Succeeded ? ParseDistros(running.Output) : [];
        return [.. ParseDistros(all.Output).Except(up, StringComparer.OrdinalIgnoreCase).Select(CommandSource.Wsl)];
    }

    public static IReadOnlyList<string> ParseDistros(string output) =>
        // Old wsl.exe versions pad with NULs even in UTF-8 mode.
        [.. output.Replace("\0", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(d => d.Length > 0 && !d.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase))];

    /// <param name="node">The node to use for NodePorts when it is one of the cluster's; else the API server's.</param>
    public async Task<KubernetesResult> KubernetesAsync(CommandSource source, CancellationToken cancellationToken, string? node = null)
    {
        var context = await RunAsync(source, "kubectl", ["config", "current-context"], cancellationToken);
        if (context.Error is { } error)
            return KubernetesResult.Failed(error);
        var contextName = context.Output.Trim();

        // In parallel: each one waits on the cluster.
        var server = RunAsync(source, "kubectl", ["config", "view", "--minify", "-o", "jsonpath={.clusters[0].cluster.server}"], cancellationToken);
        var nodes = RunAsync(source, "kubectl", ["get", "nodes", "-o", "json", "--request-timeout=8s"], cancellationToken);
        var services = RunAsync(source, "kubectl", ["get", "services", "-A", "-o", "json", "--request-timeout=8s"], cancellationToken);
        var slices = RunAsync(source, "kubectl", ["get", "endpointslices", "-A", "-o", "json", "--request-timeout=8s"], cancellationToken);
        await Task.WhenAll(server, nodes, services, slices);

        if ((await services).Error is { } servicesError)
            return KubernetesResult.Failed(servicesError) with { Context = contextName };

        try
        {
            var nodeList = (await nodes).Error is null ? KubernetesDiscovery.NodeAddresses((await nodes).Output) : [];
            var apiHost = KubernetesDiscovery.ServerHost((await server).Output);
            var chosen = node is not null && nodeList.Contains(node) ? node
                : nodeList.Contains(apiHost) ? apiHost
                : nodeList.FirstOrDefault();
            var ready = (await slices).Error is null ? KubernetesDiscovery.ReadyServices((await slices).Output) : null;
            return new(contextName, nodeList, chosen, KubernetesDiscovery.Services((await services).Output, chosen, ready));
        }
        catch (JsonException ex)
        {
            return KubernetesResult.Failed($"O kubectl respondeu algo que não é JSON: {ex.Message}") with { Context = contextName };
        }
    }

    public async Task<DockerResult> DockerAsync(CommandSource source, CancellationToken cancellationToken)
    {
        var engine = await RunAsync(source, "docker", ["info", "--format", "{{.ID}}"], cancellationToken);
        if (engine.Error is { } error)
            return DockerResult.Failed(error);
        var containers = await RunAsync(source, "docker", ["ps", "--format", "json"], cancellationToken);
        if (containers.Error is { } psError)
            return DockerResult.Failed(psError);
        try
        {
            return new(engine.Output.Trim(), DockerDiscovery.Containers(containers.Output));
        }
        catch (JsonException ex)
        {
            return DockerResult.Failed($"O docker respondeu algo que não é JSON: {ex.Message}");
        }
    }

    /// <summary>
    /// Pasted output: <c>kubectl get services -A -o json</c> (which needs <paramref name="node"/> for
    /// NodePorts) or <c>docker ps --format json</c>. Null when it is neither.
    /// </summary>
    public static IReadOnlyList<DiscoveredService>? FromPaste(string text, string? node)
    {
        var trimmed = text.Trim();
        try
        {
            if (trimmed.StartsWith('{') && trimmed.Contains("\"kind\"", StringComparison.Ordinal) && trimmed.Contains("\"Service\"", StringComparison.Ordinal))
                return KubernetesDiscovery.Services(trimmed, string.IsNullOrWhiteSpace(node) ? null : node.Trim(), ready: null);
            if (trimmed.StartsWith('{') && trimmed.Contains("\"Ports\"", StringComparison.Ordinal) && trimmed.Contains("\"Names\"", StringComparison.Ordinal))
                return DockerDiscovery.Containers(trimmed);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
        }
        return null;
    }

    private async Task<(string Output, string? Error)> RunAsync(CommandSource source, string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(source, program, arguments, cancellationToken);
        if (result.Succeeded)
            return (result.Output, null);
        return (result.Output, Explain(program, source, result));
    }

    /// <summary>What went wrong, in words the user can act on.</summary>
    public static string Explain(string program, CommandSource source, CommandResult result)
    {
        // 124 is Linux's timeout giving up first, inside the distro.
        if (result.TimedOut || (source.IsWsl && result.ExitCode == 124))
            return program == "kubectl"
                ? $"O cluster não respondeu em {CommandRunner.DefaultTimeout.TotalSeconds:0} s. A VPN está conectada?"
                : $"O {program} não respondeu em {CommandRunner.DefaultTimeout.TotalSeconds:0} s.";
        var message = result.Error.Trim();
        // bash says "command not found" with 127; a missing exe on Windows never starts (-1).
        if (result.ExitCode is 127 or -1)
            return $"O {program} não está instalado em {source}.";
        if (program == "docker" && message.Contains("permission denied", StringComparison.OrdinalIgnoreCase))
            return $"Sem permissão para falar com o Docker em {source}. O usuário precisa estar no grupo docker.";
        if (program == "docker" && message.Contains("Cannot connect to the Docker daemon", StringComparison.OrdinalIgnoreCase))
            return $"O Docker não está rodando em {source}.";
        var firstLine = message.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
        return string.IsNullOrEmpty(firstLine) ? $"O {program} terminou com o código {result.ExitCode}." : firstLine;
    }
}
