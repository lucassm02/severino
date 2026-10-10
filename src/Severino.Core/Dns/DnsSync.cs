using Microsoft.Extensions.Logging;
using Severino.Contracts;
using Severino.Core.Configuration;
using Severino.Core.Helper;
using Severino.Core.Routes;

namespace Severino.Core.Dns;

/// <param name="Pending">Entries with a public address the Helper is holding back until an administrator approves them.</param>
public sealed record DnsSyncStatus(HostsSyncState State, string? Detail = null, IReadOnlyList<HostEntry>? Pending = null)
{
    public IReadOnlyList<HostEntry> PendingEntries => Pending ?? [];
}

/// <summary>
/// Keeps the hosts DNS block equal to the enabled DNS entries. Unlike the routes block it is
/// never emptied on exit or pause: the entries stand in for lines written by hand.
/// </summary>
public sealed class DnsSync : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);

    private readonly ConfigService _config;
    private readonly IHelperClient _helper;
    private readonly ILogger<DnsSync> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _timer;
    private IReadOnlyList<HostEntry>? _synced;

    public DnsSync(ConfigService config, IHelperClient helper, ILogger<DnsSync> logger)
    {
        _config = config;
        _helper = helper;
        _logger = logger;
        _timer = new Timer(_ => _ = SyncAsync());
    }

    public DnsSyncStatus Status { get; private set; } = new(HostsSyncState.Pending);

    /// <summary>Raised on a thread-pool thread.</summary>
    public event EventHandler<DnsSyncStatus>? StatusChanged;

    public void Start()
    {
        _config.Changed += OnConfigChanged;
        _timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Sends the block again, e.g. right after an approval.</summary>
    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var entries = DnsRules.BlockEntries(_config.Current.DnsEntries);
            var status = await SendAsync(entries, cancellationToken);
            if (status.State == HostsSyncState.Synced)
            {
                _synced = entries;
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
            else
            {
                _synced = null;
                _timer.Change(RetryInterval, Timeout.InfiniteTimeSpan);
            }
            SetStatus(status);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// "Limpar tudo": takes the DNS block out and stops following the entries, which stay in the
    /// config for the next start. Best effort, the Helper may be gone.
    /// </summary>
    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        _config.Changed -= OnConfigChanged;
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            SetStatus(await SendAsync([], cancellationToken));
            _synced = null;
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
        if (_synced is not null && _synced.SequenceEqual(DnsRules.BlockEntries(e.DnsEntries)))
            return;
        _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
    }

    private async Task<DnsSyncStatus> SendAsync(IReadOnlyList<HostEntry> entries, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _helper.SendAsync(HelperRequest.SyncDns(entries), cancellationToken);
            if (response.ProtocolVersion != HelperProtocol.Version)
                return new(HostsSyncState.Failed, $"O serviço auxiliar é de outra versão ({response.HelperVersion}). Reinstale o Severino.");
            if (!response.Ok)
                return new(HostsSyncState.Failed, response.Error);
            _logger.LogInformation("DNS block synced with {Count} entries, {Pending} awaiting approval", entries.Count, response.Pending?.Count ?? 0);
            return new(HostsSyncState.Synced, Pending: response.Pending);
        }
        catch (HelperUnavailableException ex)
        {
            if (Status.State != HostsSyncState.HelperUnavailable)
                _logger.LogWarning(ex, "Helper unavailable for the DNS block");
            return new(HostsSyncState.HelperUnavailable, ex.Message);
        }
    }

    private void SetStatus(DnsSyncStatus status)
    {
        if (status.State == Status.State && status.Detail == Status.Detail && status.PendingEntries.SequenceEqual(Status.PendingEntries))
            return;
        Status = status;
        StatusChanged?.Invoke(this, status);
    }
}
