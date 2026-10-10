using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Severino.Core.Configuration;

namespace Severino.Core.Discovery;

/// <summary>One kubectl port-forward: a ClusterIP service's ports on local ports of 127.0.0.1.</summary>
public sealed record PortForwardSpec(Guid RouteId, CommandSource Source, string Context, string Namespace, string Service, IReadOnlyList<(int Local, int Remote)> Ports)
{
    /// <summary>The process name it runs under inside WSL, so it can be found and stopped there.</summary>
    public string Marker => $"severino-pf-{RouteId:N}";

    /// <summary>The spec for a port-forwarded service route, or null when it cannot have one.</summary>
    public static PortForwardSpec? For(ServiceRoute route) =>
        route is { PortForward: true, Origin: { Kind: ServiceKind.Kubernetes, Context.Length: > 0 } origin } && origin.Source != "paste"
            ? new(route.Id, CommandSource.FromId(origin.Source), origin.Context, origin.Namespace, origin.Name,
                [.. route.Ports.Select(p => (p.TargetPort, p.Port))])
            : null;

    /// <summary>The program and arguments: kubectl itself on Windows, or through wsl.exe with the marker as its name.</summary>
    public (string Program, IReadOnlyList<string> Arguments) Command()
    {
        IReadOnlyList<string> kubectl =
        [
            "--context", Context, "-n", Namespace, "port-forward", $"svc/{Service}", "--address", "127.0.0.1",
            .. Ports.Select(p => $"{p.Local}:{p.Remote}"),
        ];
        if (Source.Distro is not { } distro)
            return ("kubectl", kubectl);
        var line = $"exec -a {Marker} kubectl " + string.Join(' ', kubectl.Select(CommandRunner.ShellQuote));
        return ("wsl.exe", ["-d", distro, "--exec", "bash", "-lc", line]);
    }
}

public enum PortForwardState
{
    Starting,
    Running,
    /// <summary>It stopped; Severino starts it again after a pause that grows up to 30 s.</summary>
    Restarting,
}

public sealed record PortForwardStatus(PortForwardState State, string? Detail = null);

public interface IPortForwardRunner
{
    /// <summary>Runs until the process ends or <paramref name="cancellationToken"/> fires; returns its last error line.</summary>
    Task<string?> RunAsync(PortForwardSpec spec, Action<string> onOutput, CancellationToken cancellationToken);

    /// <summary>Makes sure nothing is left running after a stop: inside WSL, killing wsl.exe may not reach kubectl.</summary>
    Task CleanupAsync(PortForwardSpec spec);
}

/// <summary>
/// Keeps a kubectl port-forward running for every enabled port-forwarded service route, while
/// the app is open and not paused, and starts it again when it stops (the VPN dropped, a pod
/// was replaced).
/// </summary>
public sealed class PortForwards(ConfigService config, IPortForwardRunner runner, ILogger<PortForwards> logger) : IAsyncDisposable
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, (PortForwardSpec Spec, CancellationTokenSource Stop, Task Loop)> _running = [];
    private readonly ConcurrentDictionary<Guid, PortForwardStatus> _status = new();
    private bool _paused;
    private bool _started;

    /// <summary>Raised on a thread-pool thread when a port-forward's state changes.</summary>
    public event EventHandler? Changed;

    public PortForwardStatus? StatusOf(Guid routeId) => _status.GetValueOrDefault(routeId);

    public void Start()
    {
        if (_started)
            return;
        _started = true;
        config.Changed += OnConfigChanged;
        Sync();
    }

    public Task PauseAsync()
    {
        _paused = true;
        return StopAllAsync();
    }

    public void Resume()
    {
        _paused = false;
        Sync();
    }

    public async Task StopAsync()
    {
        config.Changed -= OnConfigChanged;
        _paused = true;
        await StopAllAsync();
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private void OnConfigChanged(object? sender, SeverinoConfig e) => Sync();

    private void Sync()
    {
        var desired = _paused ? [] : config.Current.Services
            .Where(s => s.Enabled)
            .Select(PortForwardSpec.For)
            .OfType<PortForwardSpec>()
            .ToDictionary(s => s.RouteId);

        List<(PortForwardSpec Spec, CancellationTokenSource Stop, Task Loop)> stale;
        lock (_gate)
        {
            stale = [.. _running.Where(r => !desired.TryGetValue(r.Key, out var spec) || !SameSpec(spec, r.Value.Spec)).Select(r => r.Value)];
            foreach (var old in stale)
                _running.Remove(old.Spec.RouteId);
            foreach (var spec in desired.Values.Where(s => !_running.ContainsKey(s.RouteId)))
            {
                var stop = new CancellationTokenSource();
                _running[spec.RouteId] = (spec, stop, Task.Run(() => LoopAsync(spec, stop.Token)));
            }
        }
        foreach (var old in stale)
            _ = StopOneAsync(old, forget: !desired.ContainsKey(old.Spec.RouteId));
    }

    private static bool SameSpec(PortForwardSpec a, PortForwardSpec b) =>
        a.Source == b.Source && a.Context == b.Context && a.Namespace == b.Namespace && a.Service == b.Service && a.Ports.SequenceEqual(b.Ports);

    private async Task LoopAsync(PortForwardSpec spec, CancellationToken stop)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!stop.IsCancellationRequested)
        {
            SetStatus(spec.RouteId, new(PortForwardState.Starting));
            var started = Stopwatch.StartNew();
            string? error;
            try
            {
                error = await runner.RunAsync(spec, line =>
                {
                    if (line.StartsWith("Forwarding from", StringComparison.Ordinal))
                        SetStatus(spec.RouteId, new(PortForwardState.Running));
                }, stop);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            if (stop.IsCancellationRequested)
                break;

            // A run that lasted a while was a good one: start over with a short pause.
            backoff = started.Elapsed > MaxBackoff ? TimeSpan.FromSeconds(1) : backoff;
            SetStatus(spec.RouteId, new(PortForwardState.Restarting, string.IsNullOrWhiteSpace(error) ? "O port-forward parou." : error));
            logger.LogInformation("Port-forward for {Service} stopped: {Error}", spec.Service, error);
            try
            {
                await Task.Delay(backoff, stop);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    private async Task StopOneAsync((PortForwardSpec Spec, CancellationTokenSource Stop, Task Loop) running, bool forget)
    {
        await running.Stop.CancelAsync();
        await running.Loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        running.Stop.Dispose();
        try
        {
            await runner.CleanupAsync(running.Spec);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Port-forward cleanup");
        }
        if (forget && _status.TryRemove(running.Spec.RouteId, out _))
            Changed?.Invoke(this, EventArgs.Empty);
    }

    private Task StopAllAsync()
    {
        List<(PortForwardSpec Spec, CancellationTokenSource Stop, Task Loop)> all;
        lock (_gate)
        {
            all = [.. _running.Values];
            _running.Clear();
        }
        return Task.WhenAll(all.Select(r => StopOneAsync(r, forget: true)));
    }

    private void SetStatus(Guid id, PortForwardStatus status)
    {
        if (_status.TryGetValue(id, out var current) && current == status)
            return;
        _status[id] = status;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Runs kubectl port-forward as a process; output lines go to the caller, the last error line is returned.</summary>
public sealed class ProcessPortForwardRunner : IPortForwardRunner
{
    public async Task<string?> RunAsync(PortForwardSpec spec, Action<string> onOutput, CancellationToken cancellationToken)
    {
        var (program, arguments) = spec.Command();
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.Environment["WSL_UTF8"] = "1";
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        string? lastError = null;
        process.OutputDataReceived += (_, e) => { if (e.Data is { } line) onOutput(line); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) lastError = e.Data.Trim(); };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return $"O kubectl não está instalado em {spec.Source}.";
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        return lastError;
    }

    public async Task CleanupAsync(PortForwardSpec spec)
    {
        if (spec.Source.Distro is not { } distro)
            return;
        using var process = Process.Start(new ProcessStartInfo("wsl.exe")
        {
            ArgumentList = { "-d", distro, "--exec", "pkill", "-f", spec.Marker },
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (process is not null)
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }
}
