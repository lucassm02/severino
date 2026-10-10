using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Severino.Core.Configuration;
using Severino.Core.Routes;

namespace Severino.Core.Discovery;

public interface IServiceEvents
{
    /// <summary>Runs <c>docker events</c> for containers until it ends or is cancelled, calling <paramref name="onChange"/> per event.</summary>
    Task<string?> WatchDockerAsync(CommandSource source, string marker, Action onChange, CancellationToken cancellationToken);

    /// <summary>Stops what is left inside WSL, where killing wsl.exe may not reach docker.</summary>
    Task CleanupAsync(CommandSource source, string marker);
}

/// <summary>
/// "Atualizar" on its own, while the app is open and not paused: every source with imported
/// routes is followed, Docker through its events (a container started or stopped), Kubernetes by
/// asking again every 30 s. Only a change worth telling raises <see cref="Updated"/>.
/// </summary>
public sealed class ServiceWatcher(ConfigService config, ServiceRefresher refresher, IServiceEvents events, ILogger<ServiceWatcher> logger,
    TimeSpan? kubernetesInterval = null, TimeSpan? debounce = null) : IAsyncDisposable
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _kubernetesInterval = kubernetesInterval ?? TimeSpan.FromSeconds(30);
    private readonly TimeSpan _debounce = debounce ?? TimeSpan.FromSeconds(2);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (ServiceOrigin Origin, CancellationTokenSource Stop, Task Loop)> _watching = [];
    private bool _paused;
    private bool _started;

    /// <summary>"orchestrator com portas novas", on a thread-pool thread, when a source changed a route.</summary>
    public event EventHandler<string>? Updated;

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

    private static string Key(ServiceOrigin o) => $"{o.Kind}|{o.Source}|{o.Context}";

    private void OnConfigChanged(object? sender, SeverinoConfig e) => Sync();

    private void Sync()
    {
        var current = config.Current;
        var desired = _paused || !current.Settings.WatchServices ? [] : current.Services
            .Where(s => s.Enabled && s.Origin is { Source.Length: > 0 } o && o.Source != "paste")
            .Select(s => s.Origin! with { Namespace = "", Name = "" })
            .DistinctBy(Key)
            .ToDictionary(Key);

        List<(ServiceOrigin Origin, CancellationTokenSource Stop, Task Loop)> stale;
        lock (_gate)
        {
            stale = [.. _watching.Where(w => !desired.ContainsKey(w.Key)).Select(w => w.Value)];
            foreach (var old in stale)
                _watching.Remove(Key(old.Origin));
            foreach (var (key, origin) in desired.Where(d => !_watching.ContainsKey(d.Key)))
            {
                var stop = new CancellationTokenSource();
                var loop = origin.Kind == ServiceKind.Docker ? DockerLoopAsync(origin, stop.Token) : KubernetesLoopAsync(origin, stop.Token);
                _watching[key] = (origin, stop, loop);
            }
        }
        foreach (var old in stale)
            _ = StopOneAsync(old);
    }

    private async Task KubernetesLoopAsync(ServiceOrigin origin, CancellationToken stop)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_kubernetesInterval, stop);
                await RefreshAsync(origin, stop);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task DockerLoopAsync(ServiceOrigin origin, CancellationToken stop)
    {
        var source = CommandSource.FromId(origin.Source);
        var changed = new SemaphoreSlim(0);
        var refreshes = RefreshOnSignalAsync(origin, changed, stop);
        var backoff = TimeSpan.FromSeconds(1);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var started = Stopwatch.StartNew();
                var error = await events.WatchDockerAsync(source, Marker(origin), () => changed.Release(), stop);
                logger.LogDebug("docker events in {Source} ended: {Error}", source, error);
                backoff = started.Elapsed > MaxBackoff ? TimeSpan.FromSeconds(1) : backoff;
                await Task.Delay(backoff, stop);
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            }
        }
        catch (OperationCanceledException)
        {
        }
        await refreshes.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <summary>Several events in a row (a compose up) become one refresh.</summary>
    private async Task RefreshOnSignalAsync(ServiceOrigin origin, SemaphoreSlim changed, CancellationToken stop)
    {
        try
        {
            while (true)
            {
                await changed.WaitAsync(stop);
                await Task.Delay(_debounce, stop);
                while (changed.CurrentCount > 0)
                    await changed.WaitAsync(stop);
                await RefreshAsync(origin, stop);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RefreshAsync(ServiceOrigin origin, CancellationToken stop)
    {
        try
        {
            var outcome = await refresher.RefreshAsync(origin, stop);
            if (outcome.Result is { Updated.Count: > 0 } result)
            {
                var names = string.Join(", ", result.Updated.Select(r => r.Names[0]));
                Updated?.Invoke(this, $"{names} com portas novas, atualizado sozinho.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Watching {Source}", origin.Source);
        }
    }

    private static string Marker(ServiceOrigin origin) => "severino-watch-" + Math.Abs(Key(origin).GetHashCode()).ToString("x");

    private async Task StopOneAsync((ServiceOrigin Origin, CancellationTokenSource Stop, Task Loop) watching)
    {
        await watching.Stop.CancelAsync();
        await watching.Loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        watching.Stop.Dispose();
        if (watching.Origin.Kind == ServiceKind.Docker)
        {
            try
            {
                await events.CleanupAsync(CommandSource.FromId(watching.Origin.Source), Marker(watching.Origin));
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Watch cleanup");
            }
        }
    }

    private Task StopAllAsync()
    {
        List<(ServiceOrigin Origin, CancellationTokenSource Stop, Task Loop)> all;
        lock (_gate)
        {
            all = [.. _watching.Values];
            _watching.Clear();
        }
        return Task.WhenAll(all.Select(StopOneAsync));
    }
}

/// <summary><c>docker events</c> as a process, on Windows or inside a WSL distro under a name of its own.</summary>
public sealed class ProcessServiceEvents : IServiceEvents
{
    public async Task<string?> WatchDockerAsync(CommandSource source, string marker, Action onChange, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> docker = ["events", "--filter", "type=container", "--filter", "event=start", "--filter", "event=die", "--format", "{{.ID}}"];
        var start = new ProcessStartInfo(source.IsWsl ? "wsl.exe" : "docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.Environment["WSL_UTF8"] = "1";
        IReadOnlyList<string> arguments = source.Distro is { } distro
            ? ["-d", distro, "--exec", "bash", "-lc", $"exec -a {marker} docker " + string.Join(' ', docker.Select(CommandRunner.ShellQuote))]
            : docker;
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        string? lastError = null;
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) onChange(); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) lastError = e.Data.Trim(); };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return $"O docker não está instalado em {source}.";
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

    public async Task CleanupAsync(CommandSource source, string marker)
    {
        if (source.Distro is not { } distro)
            return;
        using var process = Process.Start(new ProcessStartInfo("wsl.exe")
        {
            ArgumentList = { "-d", distro, "--exec", "pkill", "-f", marker },
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (process is not null)
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }
}
