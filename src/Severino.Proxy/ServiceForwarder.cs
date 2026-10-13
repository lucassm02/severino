using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Severino.Core.Configuration;
using Severino.Core.Network;
using Severino.Core.Routes;
using Severino.Proxy.Resources;

namespace Severino.Proxy;

/// <summary>One listening port of a service route, and how it is doing.</summary>
public sealed record ServicePortStatus(Guid RouteId, string Name, string Address, int Port, ProxyState State, string? Detail = null, PortOwnerInfo? Owner = null);

/// <summary>
/// Listens on each service route's own loopback address, on every port, and pipes every
/// connection to its target byte for byte: whatever the app sent, including the HTTP Host or the
/// TLS SNI, arrives as sent, and any protocol works (HTTP, gRPC, Postgres, Redis…).
/// A port marked HTTP is piped to the proxy instead, which forwards it with the service's HTTPS
/// and rules (see <see cref="ServiceHttp"/>): to its HTTPS port when the connection opens with a
/// TLS handshake, to its HTTP port otherwise.
/// </summary>
public sealed class ServiceForwarder(ILoggerFactory loggerFactory, RequestLog? requests = null) : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long a connection to an HTTP port may stay silent before it is taken as plain HTTP.</summary>
    private static readonly TimeSpan SniffTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The first byte of a TLS handshake record.</summary>
    private const byte TlsHandshake = 0x16;

    private static readonly string Loopback = IPAddress.Loopback.ToString();
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
    /// <param name="proxyHttpPort">Where the proxy listens for HTTP on 127.0.0.1; without it the HTTP ports are tunnels like the rest.</param>
    /// <param name="proxyHttpsPort">Where it listens for HTTPS; without it the services answer no HTTPS.</param>
    public async Task UpdateAsync(IReadOnlyList<ServiceRoute> services, int? proxyHttpPort = null, int? proxyHttpsPort = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var wanted = new Dictionary<Endpoint, Target>();
            foreach (var service in services.Where(s => s.Enabled))
            {
                var name = service.Names.FirstOrDefault() ?? service.Address;
                var https = proxyHttpPort is not null && proxyHttpsPort is not null && service.Https;
                foreach (var port in service.Ports)
                {
                    var via = proxyHttpPort is null || !port.Http ? Via.Tunnel : https ? Via.Sniff : Via.Http;
                    wanted[new(service.Address, port.Port)] = new(service.Id, name, port.TargetHost, port.TargetPort, via, proxyHttpPort ?? 0, proxyHttpsPort ?? 0);
                }
                if (https && ServiceHttp.ExtraTlsPort(service) is { } tls)
                    wanted[new(service.Address, tls)] = new(service.Id, name, Loopback, proxyHttpsPort!.Value, Via.Tls, proxyHttpPort!.Value, proxyHttpsPort.Value);
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
                ? string.Format(Strings.Forwarder_PortFailed, endpoint.Port, endpoint.Address, ex.Message)
                : string.Format(Strings.Proxy_PortInUseBy, endpoint.Port, owner.Describe());
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
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var (host, port) = target.Via switch
            {
                Via.Http => (Loopback, target.ProxyHttp),
                Via.Tls => (Loopback, target.ProxyHttps),
                Via.Sniff => (Loopback, await StartsWithTlsAsync(client) ? target.ProxyHttps : target.ProxyHttp),
                _ => (target.Host, target.Port),
            };
            var path = $"→ {host}:{port}";
            Socket upstream;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                timeout.CancelAfter(ConnectTimeout);
                upstream = await ParallelConnect.ConnectAsync(host, port, timeout.Token);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                _logger.LogInformation("Service {Name} could not reach {Host}:{Port}: {Error}", target.Name, host, port, ex.Message);
                requests?.Add("tcp", authority, "TCP", path, 502, fromProxy: true, duration: System.Diagnostics.Stopwatch.GetElapsedTime(started));
                return;
            }

            using (upstream)
            {
                // Through the proxy each request shows up in the log as HTTP: no TCP line as well.
                var entry = target.Via == Via.Tunnel ? requests?.Add("tcp", authority, "TCP", path, 200) : null;
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

    /// <summary>Peeks at the first byte without taking it: a TLS handshake starts with 0x16, an HTTP request with a letter.</summary>
    private async Task<bool> StartsWithTlsAsync(Socket client)
    {
        var first = new byte[1];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        timeout.CancelAfter(SniffTimeout);
        try
        {
            return await client.ReceiveAsync(first, SocketFlags.Peek, timeout.Token) == 1 && first[0] == TlsHandshake;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    private readonly record struct Endpoint(string Address, int Port);

    /// <summary>How a port's connections leave: straight to the target, or to one of the proxy's ports.</summary>
    private enum Via
    {
        Tunnel,
        Http,
        Tls,

        /// <summary>To the proxy's HTTPS port when the connection opens with TLS, to its HTTP port otherwise.</summary>
        Sniff,
    }

    private sealed record Target(Guid RouteId, string Name, string Host, int Port, Via Via = Via.Tunnel, int ProxyHttp = 0, int ProxyHttps = 0);

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
