using System.Collections.Concurrent;
using System.Net;
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
    private IReadOnlyList<RouteEntry> _routes = [];
    private Task? _loop;

    public HealthMonitor(TimeSpan? interval = null) => _interval = interval ?? TimeSpan.FromSeconds(5);

    /// <summary>Raised on a thread-pool thread when a route's target goes up or down.</summary>
    public event EventHandler<RouteHealth>? Changed;

    /// <summary>Last result per route; routes not checked yet are missing.</summary>
    public bool? IsUp(Guid routeId) => _status.TryGetValue(routeId, out var up) ? up : null;

    public void Start() => _loop ??= Task.Run(() => LoopAsync(_stop.Token));

    /// <summary>Replaces the routes to watch and checks them right away.</summary>
    public void Update(IReadOnlyList<RouteEntry> routes)
    {
        _routes = routes;
        foreach (var id in _status.Keys.Except(routes.Where(r => r.Enabled).Select(r => r.Id)))
            _status.TryRemove(id, out _);
        _ = CheckAllAsync(_stop.Token);
    }

    public async ValueTask DisposeAsync()
    {
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
        Task.WhenAll(_routes.Where(r => r.Enabled).Select(r => CheckAsync(r, cancellationToken)));

    private async Task CheckAsync(RouteEntry route, CancellationToken cancellationToken)
    {
        if (!RouteRules.TryParseTarget(route.Target, out var target, out _))
            return;

        var up = await CanConnectAsync(target.IdnHost, target.Port, cancellationToken);
        if (cancellationToken.IsCancellationRequested || !_routes.Any(r => r.Id == route.Id && r.Enabled))
            return;

        // Checks for the same route can overlap (Update plus the timer); only the first to see
        // a new value reports it.
        bool changed;
        lock (_gate)
        {
            changed = IsUp(route.Id) != up;
            _status[route.Id] = up;
        }
        if (changed)
            Changed?.Invoke(this, new RouteHealth(route.Id, up));
    }

    private static async Task<bool> CanConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            // Dual-stack and every resolved address: dev servers on "localhost" often listen on ::1 only.
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(new DnsEndPoint(host, port), timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }
}

public sealed record RouteHealth(Guid RouteId, bool IsUp);
