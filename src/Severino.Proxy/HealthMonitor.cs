using System.Collections.Concurrent;
using System.Net.Sockets;
using Severino.Core.Configuration;
using Severino.Core.Routes;

namespace Severino.Proxy;

/// <summary>Opens a TCP connection to each enabled route's target on an interval.</summary>
public sealed class HealthMonitor : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(1);

    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<Guid, bool> _status = new();
    private readonly Lock _gate = new();
    private IReadOnlyList<Probe> _probes = [];
    private Task? _loop;
    private bool _disposed;

    public HealthMonitor(TimeSpan? interval = null) => _interval = interval ?? TimeSpan.FromSeconds(5);

    /// <summary>Raised on a thread-pool thread when a route's target goes up or down.</summary>
    public event EventHandler<RouteHealth>? Changed;

    /// <summary>Last result per route; routes not checked yet are missing.</summary>
    public bool? IsUp(Guid routeId) => _status.TryGetValue(routeId, out var up) ? up : null;

    public void Start() => _loop ??= Task.Run(() => LoopAsync(_stop.Token));

    /// <summary>
    /// Replaces the routes to watch and checks them right away. A service route is up when its
    /// first port's destination accepts a connection.
    /// </summary>
    public void Update(IReadOnlyList<RouteEntry> routes, IReadOnlyList<ServiceRoute>? services = null)
    {
        var probes = new List<Probe>();
        foreach (var route in routes.Where(r => r.Enabled))
        {
            if (RouteRules.TryParseTarget(route.Target, out var target, out _))
                probes.Add(new(route.Id, target.IdnHost, target.Port));
        }
        foreach (var service in (services ?? []).Where(s => s.Enabled && s.Ports.Count > 0))
            probes.Add(new(service.Id, service.Ports[0].TargetHost, service.Ports[0].TargetPort));

        _probes = probes;
        foreach (var id in _status.Keys.Except(probes.Select(p => p.Id)))
            _status.TryRemove(id, out _);
        _ = CheckAllAsync(_stop.Token);
    }

    /// <summary>Safe to call more than once.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await _stop.CancelAsync();
        if (_loop is not null)
            await _loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _stop.Dispose();
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            do
            {
                await CheckAllAsync(cancellationToken);
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private Task CheckAllAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(_probes.Select(p => CheckAsync(p, cancellationToken)));

    private async Task CheckAsync(Probe probe, CancellationToken cancellationToken)
    {
        var up = await CanConnectAsync(probe.Host, probe.Port, cancellationToken);
        if (cancellationToken.IsCancellationRequested || !_probes.Contains(probe))
            return;

        // Checks for the same route can overlap (Update plus the timer); only the first to see
        // a new value reports it.
        bool changed;
        lock (_gate)
        {
            changed = IsUp(probe.Id) != up;
            _status[probe.Id] = up;
        }
        if (changed)
            Changed?.Invoke(this, new RouteHealth(probe.Id, up));
    }

    private sealed record Probe(Guid Id, string Host, int Port);

    /// <summary>True when a TCP connection to <paramref name="host"/>:<paramref name="port"/> opens within a second.</summary>
    public static async Task<bool> CanConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            using var socket = await ParallelConnect.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }
}

public sealed record RouteHealth(Guid RouteId, bool IsUp);
