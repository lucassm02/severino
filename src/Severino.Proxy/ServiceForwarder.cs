using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Severino.Core.Configuration;
using Severino.Core.Network;

namespace Severino.Proxy;

/// <summary>One listening port of a service route, and how it is doing.</summary>
public sealed record ServicePortStatus(Guid RouteId, string Name, string Address, int Port, ProxyState State, string? Detail = null, PortOwnerInfo? Owner = null);

/// <summary>
/// Listens on each service route's own loopback address, on every port, and pipes every
/// connection to its target byte for byte: whatever the app sent, including the HTTP Host or the
/// TLS SNI, arrives as sent, and any protocol works (HTTP, gRPC, Postgres, Redis…).
/// </summary>
public sealed class ServiceForwarder(ILoggerFactory loggerFactory, RequestLog? requests = null) : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private const int BufferSize = 64 * 1024;

    private readonly ILogger _logger = loggerFactory.CreateLogger<ServiceForwarder>();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Endpoint, Listener> _listeners = [];
    private readonly CancellationTokenSource _shutdown = new();
    private IReadOnlyDictionary<Endpoint, Target> _targets = new Dictionary<Endpoint, Target>();
    private bool _disposed;

    /// <summary>Every port of every enabled service route.</summary>
    public IReadOnlyList<ServicePortStatus> Statuses { get; private set; } = [];

    /// <summary>Raised after <see cref="UpdateAsync"/> when anything changed.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>
    /// Listens for the enabled routes, and only those. Listeners already running are kept, so
    /// open connections survive edits; a changed target applies to new connections. Ports that
    /// failed before are tried again.
    /// </summary>
    public async Task UpdateAsync(IReadOnlyList<ServiceRoute> services, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var wanted = new Dictionary<Endpoint, Target>();
            foreach (var service in services.Where(s => s.Enabled))
            {
                foreach (var port in service.Ports)
                    wanted[new(service.Address, port.Port)] = new(service.Id, service.Names.FirstOrDefault() ?? service.Address, port.TargetHost, port.TargetPort);
            }
            _targets = wanted;

            foreach (var endpoint in _listeners.Keys.Except(wanted.Keys).ToList())
            {
                _listeners[endpoint].Dispose();
                _listeners.Remove(endpoint);
            }

            var statuses = new List<ServicePortStatus>();
            foreach (var (endpoint, target) in wanted)
            {
                if (!_listeners.ContainsKey(endpoint))
                {
                    if (TryListen(endpoint, out var listener, out var failure))
                        _listeners[endpoint] = listener;
                    else
                    {
                        statuses.Add(failure with { RouteId = target.RouteId, Name = target.Name });
                        continue;
                    }
                }
                statuses.Add(new(target.RouteId, target.Name, endpoint.Address, endpoint.Port, ProxyState.Running));
            }

            var changed = !statuses.SequenceEqual(Statuses);
            Statuses = statuses;
            if (changed)
                StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops listening on every port. Open connections run until either side closes.</summary>
    public Task StopAsync() => UpdateAsync([]);

    /// <summary>Safe to call more than once. Cuts open connections too.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        await StopAsync();
        _disposed = true;
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    private bool TryListen(Endpoint endpoint, out Listener listener, out ServicePortStatus failure)
    {
        listener = null!;
        failure = null!;
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Parse(endpoint.Address), endpoint.Port));
            socket.Listen(128);
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            var owner = PortOwner.Find(endpoint.Port);
            var detail = owner is null
                ? $"A porta {endpoint.Port} não abriu em {endpoint.Address}: {ex.Message}"
                : $"A porta {endpoint.Port} está em uso por {owner.Describe()}.";
            _logger.LogWarning("Service port {Address}:{Port} unavailable: {Error}", endpoint.Address, endpoint.Port, ex.SocketErrorCode);
            failure = new(Guid.Empty, "", endpoint.Address, endpoint.Port, ProxyState.PortInUse, detail, owner);
            return false;
        }

        listener = new Listener(socket);
        listener.Loop = AcceptLoopAsync(listener, endpoint);
        _logger.LogInformation("Service listening on {Address}:{Port}", endpoint.Address, endpoint.Port);
        return true;
    }

    private async Task AcceptLoopAsync(Listener listener, Endpoint endpoint)
    {
        while (true)
        {
            Socket client;
            try
            {
                client = await listener.Socket.AcceptAsync(listener.Stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return; // the listener was stopped
            }
            _ = ForwardAsync(client, endpoint);
        }
    }

    private async Task ForwardAsync(Socket client, Endpoint endpoint)
    {
        using (client)
        {
            // The target of the moment: an edit since the listener started applies here.
            if (!_targets.TryGetValue(endpoint, out var target))
                return;

            var authority = $"{target.Name}:{endpoint.Port}";
            var path = $"→ {target.Host}:{target.Port}";
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            Socket upstream;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                timeout.CancelAfter(ConnectTimeout);
                upstream = await ParallelConnect.ConnectAsync(target.Host, target.Port, timeout.Token);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                _logger.LogInformation("Service {Name} could not reach {Host}:{Port}: {Error}", target.Name, target.Host, target.Port, ex.Message);
                requests?.Add("tcp", authority, "TCP", path, 502, fromProxy: true, duration: System.Diagnostics.Stopwatch.GetElapsedTime(started));
                return;
            }

            using (upstream)
            {
                var entry = requests?.Add("tcp", authority, "TCP", path, 200);
                await Task.WhenAll(PumpAsync(client, upstream), PumpAsync(upstream, client));
                entry?.Close(System.Diagnostics.Stopwatch.GetElapsedTime(started));
            }
        }
    }

    /// <summary>
    /// One direction. A clean end passes the half-close on, so the other direction can finish; a
    /// reset closes both sockets, which ends the other direction too.
    /// </summary>
    private async Task PumpAsync(Socket from, Socket to)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = await from.ReceiveAsync(buffer, SocketFlags.None, _shutdown.Token)) > 0)
                await to.SendAsync(buffer.AsMemory(0, read), SocketFlags.None, _shutdown.Token);
            to.Shutdown(SocketShutdown.Send);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
        {
            from.Close();
            to.Close();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private readonly record struct Endpoint(string Address, int Port);

    private sealed record Target(Guid RouteId, string Name, string Host, int Port);

    private sealed class Listener(Socket socket) : IDisposable
    {
        public Socket Socket { get; } = socket;
        public CancellationTokenSource Stop { get; } = new();
        public Task Loop { get; set; } = Task.CompletedTask;

        public void Dispose()
        {
            Stop.Cancel();
            Socket.Dispose();
            Stop.Dispose();
        }
    }
}
