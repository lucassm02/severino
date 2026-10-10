using Microsoft.Extensions.Logging;
using Severino.Core.Configuration;
using Severino.Core.Discovery;
using Severino.Core.Wsl;

namespace Severino.App.Services;

public enum WslCallerState
{
    /// <summary>The distro is not running; it gets the rules when it starts.</summary>
    Stopped,
    Applied,
    /// <summary>Applied, but some destination did not resolve in the distro.</summary>
    Partial,
    Failed,
}

/// <param name="Count">Service routes in the distro.</param>
/// <param name="Detail">What went wrong, in the distro's words.</param>
public sealed record WslCallerStatus(string Distro, WslCallerState State, int Count = 0, string? Detail = null);

/// <summary>
/// Keeps the chosen WSL distros calling the service routes by name (see <see cref="WslCallerScript"/>).
/// WSL loses the rules when a distro restarts, so this watches which distros run and writes them
/// again when one comes back. A stopped distro is never started for this.
/// </summary>
public sealed class WslCallers(ConfigService config, IWslShell shell, ServiceDiscovery discovery, ILogger<WslCallers> logger) : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Distro → the script it holds now, as far as this run of the app knows.</summary>
    private readonly Dictionary<string, string> _applied = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WslCallerStatus> _status = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string> _chosen = [];
    private Task? _loop;
    private bool _paused;

    /// <summary>Raised on a thread-pool thread when a distro's status changes.</summary>
    public event EventHandler? Changed;

    /// <summary>One line per chosen distro.</summary>
    public IReadOnlyList<WslCallerStatus> Statuses
    {
        get
        {
            lock (_status)
                return [.. config.Current.Settings.WslDistros.Select(d => _status.GetValueOrDefault(d) ?? new WslCallerStatus(d, WslCallerState.Stopped))];
        }
    }

    public void Start()
    {
        _chosen = config.Current.Settings.WslDistros;
        config.Changed += OnConfigChanged;
        _loop ??= Task.Run(() => LoopAsync(_stop.Token));
    }

    /// <summary>Paused, the distros resolve the names as without Severino.</summary>
    public Task PauseAsync()
    {
        _paused = true;
        return SyncAsync(CancellationToken.None);
    }

    public Task ResumeAsync()
    {
        _paused = false;
        return SyncAsync(CancellationToken.None);
    }

    /// <summary>Writes every running chosen distro again, failed or not.</summary>
    public Task RetryAsync()
    {
        lock (_applied)
            _applied.Clear();
        return SyncAsync(CancellationToken.None);
    }

    /// <summary>On exit: takes the rules out of the distros that have them, waiting at most <paramref name="limit"/>.</summary>
    public async Task StopAsync(TimeSpan limit)
    {
        config.Changed -= OnConfigChanged;
        await _stop.CancelAsync();
        if (_loop is not null)
            await _loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        string[] distros;
        lock (_applied)
            distros = [.. _applied.Where(a => a.Value != WslCallerScript.Remove()).Select(a => a.Key)];
        await RemoveAsync(shell, distros, limit);
    }

    /// <summary>
    /// "Limpar tudo" and <c>--cleanup</c>: takes the rules out of <paramref name="distros"/>,
    /// stopped ones included, in parallel.
    /// </summary>
    public static async Task RemoveAsync(IWslShell shell, IReadOnlyCollection<string> distros, TimeSpan limit)
    {
        if (distros.Count == 0)
            return;
        using var timeout = new CancellationTokenSource(limit);
        try
        {
            await Task.WhenAll(distros.Select(d => shell.RunAsRootAsync(d, WslCallerScript.Remove(), timeout.Token)));
        }
        catch (OperationCanceledException)
        {
            // Leftovers go away when the distro restarts.
        }
    }

    public async ValueTask DisposeAsync()
    {
        config.Changed -= OnConfigChanged;
        if (!_stop.IsCancellationRequested)
            await _stop.CancelAsync();
        if (_loop is not null)
            await _loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private void OnConfigChanged(object? sender, SeverinoConfig updated) => _ = SyncAsync(_stop.Token);

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            do
            {
                await SyncAsync(cancellationToken);
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Brings every running chosen distro to what the config asks, and takes the rules out of distros no longer chosen.</summary>
    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            var current = config.Current;
            var chosen = current.Settings.WslDistros;
            var running = (await discovery.SourcesAsync(cancellationToken)).Where(s => s.IsWsl).Select(s => s.Distro!).ToHashSet(StringComparer.OrdinalIgnoreCase);

            lock (_applied)
            {
                // A distro that stopped lost its rules: it gets them again when it is back.
                foreach (var distro in _applied.Keys.Where(d => !running.Contains(d)).ToList())
                    _applied.Remove(distro);
            }

            // No longer chosen: out, if it is running (a stopped one lost the rules already).
            foreach (var distro in _chosen.Except(chosen, StringComparer.OrdinalIgnoreCase).Where(running.Contains))
            {
                await RunAsync(distro, WslCallerScript.Remove(), 0, cancellationToken);
                lock (_status)
                    _status.Remove(distro);
            }
            _chosen = chosen;

            var desired = _paused ? WslCallerScript.Remove() : WslCallerScript.Apply(current.Services);
            var count = _paused ? 0 : current.Services.Count(s => s.Enabled && WslCallerScript.Reachable(s));
            foreach (var distro in chosen)
            {
                if (!running.Contains(distro))
                {
                    SetStatus(new(distro, WslCallerState.Stopped));
                    continue;
                }
                bool same;
                lock (_applied)
                    same = _applied.GetValueOrDefault(distro) == desired;
                if (!same)
                    await RunAsync(distro, desired, count, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "WSL callers sync failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RunAsync(string distro, string script, int count, CancellationToken cancellationToken)
    {
        var result = await shell.RunAsRootAsync(distro, script, cancellationToken);
        var detail = result.TimedOut ? "A distro não respondeu a tempo."
            : string.Join(" ", result.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var state = result.ExitCode switch
        {
            0 when !result.TimedOut => WslCallerState.Applied,
            WslCallerScript.PartialExitCode => WslCallerState.Partial,
            _ => WslCallerState.Failed,
        };
        // A failure is recorded too: trying again every few seconds would only keep the distro
        // awake. It is tried again when the config changes, the distro restarts, or on RetryAsync.
        lock (_applied)
            _applied[distro] = script;
        if (state == WslCallerState.Failed)
            logger.LogWarning("WSL callers in {Distro} failed with {ExitCode}: {Error}", distro, result.ExitCode, detail);
        SetStatus(new(distro, state, count, state == WslCallerState.Applied ? null : detail.Length > 0 ? detail : $"O script terminou com o código {result.ExitCode}."));
    }

    private void SetStatus(WslCallerStatus status)
    {
        lock (_status)
        {
            if (_status.GetValueOrDefault(status.Distro) == status)
                return;
            _status[status.Distro] = status;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
