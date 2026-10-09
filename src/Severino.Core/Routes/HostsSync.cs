using Microsoft.Extensions.Logging;
using Severino.Contracts;
using Severino.Core.Configuration;
using Severino.Core.Helper;

namespace Severino.Core.Routes;

public enum HostsSyncState
{
    Pending,
    Synced,
    /// <summary>The Helper is not installed, not running, or refused this user.</summary>
    HelperUnavailable,
    /// <summary>The Helper answered but could not apply the change.</summary>
    Failed,
}

public sealed record HostsSyncStatus(HostsSyncState State, string? Detail = null);

/// <summary>
/// Keeps the hosts block equal to the enabled routes. Changes are debounced and retried while
/// the Helper is unavailable; on exit the block is cleared.
/// </summary>
public sealed class HostsSync : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);

    private readonly ConfigService _config;
    private readonly IHelperClient _helper;
    private readonly ILogger<HostsSync> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _timer;
    private IReadOnlyList<string>? _synced;
    private bool _stopped;

    public HostsSync(ConfigService config, IHelperClient helper, ILogger<HostsSync> logger)
    {
        _config = config;
        _helper = helper;
        _logger = logger;
        _timer = new Timer(_ => _ = SyncAsync());
    }

    public HostsSyncStatus Status { get; private set; } = new(HostsSyncState.Pending);

    /// <summary>Raised on a thread-pool thread.</summary>
    public event EventHandler<HostsSyncStatus>? StatusChanged;

    public void Start()
    {
        _config.Changed += OnConfigChanged;
        _timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Syncs now instead of waiting for the debounce; used by tests and on demand.</summary>
    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_stopped)
                return;

            var domains = RouteRules.ActiveDomains(_config.Current.Routes);
            if (Status.State == HostsSyncState.Synced && _synced is not null && _synced.SequenceEqual(domains))
                return;

            var status = await SendAsync(domains, cancellationToken);
            if (status.State == HostsSyncState.Synced)
                _synced = domains;
            else
                _timer.Change(RetryInterval, Timeout.InfiniteTimeSpan);
            SetStatus(status);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops syncing and removes the block. Best effort: the Helper may be gone.</summary>
    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        _config.Changed -= OnConfigChanged;
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _stopped = true;
            await SendAsync([], cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _config.Changed -= OnConfigChanged;
        _timer.Dispose();
    }

    private void OnConfigChanged(object? sender, SeverinoConfig e)
    {
        if (_synced is not null && _synced.SequenceEqual(RouteRules.ActiveDomains(e.Routes)))
            return;
        _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
    }

    private async Task<HostsSyncStatus> SendAsync(IReadOnlyList<string> domains, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _helper.SendAsync(HelperRequest.Sync(domains), cancellationToken);
            if (response.ProtocolVersion != HelperProtocol.Version)
                return new(HostsSyncState.Failed, $"O serviço auxiliar é de outra versão ({response.HelperVersion}). Reinstale o Severino.");
            if (!response.Ok)
                return new(HostsSyncState.Failed, response.Error);

            _logger.LogInformation("Hosts synced with {Count} domains", domains.Count);
            return new(HostsSyncState.Synced);
        }
        catch (HelperUnavailableException ex)
        {
            _logger.LogWarning(ex, "Helper unavailable");
            return new(HostsSyncState.HelperUnavailable, ex.Message);
        }
    }

    private void SetStatus(HostsSyncStatus status)
    {
        if (status == Status)
            return;
        Status = status;
        StatusChanged?.Invoke(this, status);
    }
}
