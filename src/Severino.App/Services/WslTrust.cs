using Microsoft.Extensions.Logging;
using Severino.App.Resources;
using Severino.Core.Configuration;
using Severino.Core.Discovery;
using Severino.Core.Wsl;
using Severino.Proxy.Certificates;

namespace Severino.App.Services;

public enum WslTrustState
{
    /// <summary>The distro is not running; it gets the CA when it starts.</summary>
    Stopped,
    Installed,
    Failed,
}

public sealed record WslTrustStatus(string Distro, WslTrustState State, string? Detail = null);

/// <summary>
/// Keeps the local CA trusted in the chosen WSL distros while <see cref="AppSettings.WslTrustCa"/>
/// is on and the CA is active, and takes it out when either goes, or the distro leaves the list.
/// The distros that got it are kept in the config, so it can be taken out of them later, even
/// across runs. Pausing does not touch it. A stopped distro is never started for this.
/// </summary>
public sealed class WslTrust(ConfigService config, LocalCa ca, IWslShell shell, ServiceDiscovery discovery, ILogger<WslTrust> logger) : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Distro → the script it ran last in this run of the app.</summary>
    private readonly Dictionary<string, string> _applied = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WslTrustStatus> _status = new(StringComparer.OrdinalIgnoreCase);
    private Task? _loop;

    /// <summary>Raised on a thread-pool thread when a distro's status changes.</summary>
    public event EventHandler? Changed;

    /// <summary>One per chosen distro while the option is on.</summary>
    public IReadOnlyList<WslTrustStatus> Statuses
    {
        get
        {
            var settings = config.Current.Settings;
            if (!settings.WslTrustCa)
                return [];
            lock (_status)
                return [.. settings.WslDistros.Select(d => _status.GetValueOrDefault(d) ?? new WslTrustStatus(d, WslTrustState.Stopped))];
        }
    }

    public void Start()
    {
        config.Changed += OnChanged;
        ca.Changed += OnCaChanged;
        _loop ??= Task.Run(() => LoopAsync(_stop.Token));
    }

    public async ValueTask DisposeAsync()
    {
        config.Changed -= OnChanged;
        ca.Changed -= OnCaChanged;
        if (!_stop.IsCancellationRequested)
            await _stop.CancelAsync();
        if (_loop is not null)
            await _loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <summary>"Limpar tudo": takes the CA out of every distro that got it, and forgets them.</summary>
    public async Task RemoveEverywhereAsync(TimeSpan limit)
    {
        await RemoveAsync(shell, [.. config.Current.State.WslTrustDistros], limit);
        config.Update(c => c with { State = c.State with { WslTrustDistros = [] } });
    }

    /// <summary>
    /// "Limpar tudo" and <c>--cleanup</c>: takes the CA out of every distro that got it, stopped
    /// ones included, in parallel, waiting at most <paramref name="limit"/>.
    /// </summary>
    public static async Task RemoveAsync(IWslShell shell, IReadOnlyCollection<string> distros, TimeSpan limit)
    {
        if (distros.Count == 0)
            return;
        using var timeout = new CancellationTokenSource(limit);
        try
        {
            await Task.WhenAll(distros.Select(d => shell.RunAsRootAsync(d, WslCaScript.Remove(), timeout.Token)));
        }
        catch (OperationCanceledException)
        {
            // The certificate stays in a distro that did not answer; Settings shows the option off.
        }
    }

    private void OnChanged(object? sender, SeverinoConfig updated) => _ = SyncAsync(_stop.Token);

    private void OnCaChanged(object? sender, LocalCaStatus status) => _ = SyncAsync(_stop.Token);

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

    /// <summary>Brings every running distro to what the config asks: the CA in the chosen ones, out of the rest that had it.</summary>
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
            var settings = current.Settings;
            var pem = settings.WslTrustCa && ca.Status.State == LocalCaState.Active ? ca.ExportPem() : null;
            var chosen = settings.WslDistros;
            var had = current.State.WslTrustDistros;
            var targets = chosen.Union(had, StringComparer.OrdinalIgnoreCase).ToList();
            if (targets.Count == 0)
                return;
            var running = (await discovery.SourcesAsync(cancellationToken)).Where(s => s.IsWsl).Select(s => s.Distro!).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var distro in targets)
            {
                var wanted = pem is not null && chosen.Contains(distro, StringComparer.OrdinalIgnoreCase);
                var installed = had.Contains(distro, StringComparer.OrdinalIgnoreCase);
                if (!wanted && !installed)
                    continue;
                if (!running.Contains(distro))
                {
                    lock (_applied)
                        _applied.Remove(distro);
                    if (wanted)
                        SetStatus(new(distro, WslTrustState.Stopped));
                    continue;
                }
                var script = wanted ? WslCaScript.Install(pem!) : WslCaScript.Remove();
                bool same;
                lock (_applied)
                    same = _applied.GetValueOrDefault(distro) == script;
                if (same)
                    continue;

                var result = await shell.RunAsRootAsync(distro, script, cancellationToken);
                lock (_applied)
                    _applied[distro] = script;
                var ok = result.ExitCode == 0 && !result.TimedOut;
                if (ok)
                    Track(distro, wanted);
                else
                    logger.LogWarning("WSL trust in {Distro} failed with {ExitCode}: {Error}", distro, result.ExitCode, result.Error);
                if (wanted)
                {
                    var detail = result.TimedOut ? Strings.Wsl_NoAnswer
                        : string.Join(" ", result.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    SetStatus(ok ? new(distro, WslTrustState.Installed) : new(distro, WslTrustState.Failed, detail.Length > 0 ? detail : string.Format(Strings.Wsl_ScriptExit, result.ExitCode)));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "WSL trust sync failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Remembers, in the config, whether <paramref name="distro"/> holds the CA now.</summary>
    private void Track(string distro, bool holds) => config.Update(c =>
    {
        var others = c.State.WslTrustDistros.Where(d => !d.Equals(distro, StringComparison.OrdinalIgnoreCase)).ToList();
        if (holds)
            others.Add(distro);
        return others.SequenceEqual(c.State.WslTrustDistros) ? c : c with { State = c.State with { WslTrustDistros = others } };
    });

    private void SetStatus(WslTrustStatus status)
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
