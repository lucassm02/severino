using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging.Abstractions;
using Severino.Core.Configuration;
using Severino.Proxy;

namespace Severino.Tests.Proxy;

/// <summary>Service routes forwarding real sockets, each test on loopback addresses of its own.</summary>
public sealed class ServiceForwarderTests : IAsyncLifetime
{
    private readonly RequestLog _log = new();
    private readonly ServiceForwarder _forwarder;
    private readonly string _prefix = $"127.77.{Random.Shared.Next(100, 250)}.";
    private TcpListener _echo = null!;
    private WebApplication _backend = null!;

    public ServiceForwarderTests() => _forwarder = new ServiceForwarder(NullLoggerFactory.Instance, _log);

    public async Task InitializeAsync()
    {
        _echo = new TcpListener(IPAddress.Loopback, 0);
        _echo.Start();
        _ = EchoAsync(_echo);
        _backend = await TestBackend.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _forwarder.DisposeAsync();
        _echo.Stop();
        await _backend.DisposeAsync();
    }

    private int EchoPort => ((IPEndPoint)_echo.LocalEndpoint).Port;

    private static async Task EchoAsync(TcpListener listener)
    {
        while (true)
        {
            Socket client;
            try
            {
                client = await listener.AcceptSocketAsync();
            }
            catch (Exception)
            {
                return;
            }
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    var buffer = new byte[4096];
                    int read;
                    while ((read = await client.ReceiveAsync(buffer)) > 0)
                        await client.SendAsync(buffer.AsMemory(0, read));
                    client.Shutdown(SocketShutdown.Send);
                }
            });
        }
    }

    private ServiceRoute Service(int last, int port, string targetHost, int targetPort, params string[] names) => new()
    {
        Names = names.Length > 0 ? names : [$"svc{last}"],
        Address = _prefix + last,
        Ports = [new ServicePort { Port = port, TargetHost = targetHost, TargetPort = targetPort }],
    };

    private static async Task<string> RoundTripAsync(string address, int port, string message)
    {
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(IPAddress.Parse(address), port);
        await client.SendAsync(Encoding.UTF8.GetBytes(message));
        client.Shutdown(SocketShutdown.Send); // half-close: the echo must still answer
        var received = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await client.ReceiveAsync(buffer)) > 0)
            received.Write(buffer, 0, read);
        return Encoding.UTF8.GetString(received.ToArray());
    }

    [Fact]
    public async Task Bytes_flow_both_ways_and_half_close_passes_through()
    {
        var port = TestBackend.FreePort();
        await _forwarder.UpdateAsync([Service(2, port, "127.0.0.1", EchoPort)]);

        Assert.Equal(ProxyState.Running, Assert.Single(_forwarder.Statuses).State);
        Assert.Equal("olá, cluster", await RoundTripAsync(_prefix + 2, port, "olá, cluster"));
    }

    [Fact]
    public async Task Many_connections_at_once()
    {
        var port = TestBackend.FreePort();
        await _forwarder.UpdateAsync([Service(3, port, "127.0.0.1", EchoPort)]);

        var replies = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => RoundTripAsync(_prefix + 3, port, $"mensagem {i}")));

        Assert.Equal(Enumerable.Range(0, 20).Select(i => $"mensagem {i}"), replies);
    }

    [Fact]
    public async Task Http_host_arrives_as_the_app_sent_it()
    {
        var port = TestBackend.FreePort();
        await _forwarder.UpdateAsync([Service(4, port, "127.0.0.1", TestBackend.PortOf(_backend), "algarbffapi")]);
        using var client = new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(IPAddress.Parse(_prefix + 4), port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            },
        });

        var body = await client.GetStringAsync($"http://algarbffapi:{port}/echo");

        Assert.Contains($"host=algarbffapi:{port}", body);
    }

    [Fact]
    public async Task Two_services_on_the_same_port_do_not_mix()
    {
        var port = TestBackend.FreePort();
        var other = new TcpListener(IPAddress.Loopback, 0);
        other.Start();
        _ = Task.Run(async () =>
        {
            using var client = await other.AcceptSocketAsync();
            await client.SendAsync("sou o outro"u8.ToArray());
            client.Shutdown(SocketShutdown.Send);
        });
        try
        {
            await _forwarder.UpdateAsync(
            [
                Service(5, port, "127.0.0.1", EchoPort),
                Service(6, port, "127.0.0.1", ((IPEndPoint)other.LocalEndpoint).Port),
            ]);

            Assert.Equal("eco", await RoundTripAsync(_prefix + 5, port, "eco"));
            Assert.Equal("sou o outro", await RoundTripAsync(_prefix + 6, port, ""));
        }
        finally
        {
            other.Stop();
        }
    }

    [Fact]
    public async Task Busy_port_is_reported_with_its_owner_and_retried_later()
    {
        var port = TestBackend.FreePort();
        var squatter = new TcpListener(IPAddress.Parse(_prefix + 7), port);
        squatter.Start();
        try
        {
            await _forwarder.UpdateAsync([Service(7, port, "127.0.0.1", EchoPort)]);

            var status = Assert.Single(_forwarder.Statuses);
            Assert.Equal(ProxyState.PortInUse, status.State);
            Assert.Equal(Environment.ProcessId, status.Owner?.ProcessId);
            Assert.Equal("svc7", status.Name);
        }
        finally
        {
            squatter.Stop();
        }

        await _forwarder.UpdateAsync([Service(7, port, "127.0.0.1", EchoPort)]);
        Assert.Equal(ProxyState.Running, Assert.Single(_forwarder.Statuses).State);
    }

    [Fact]
    public async Task Unreachable_target_shows_up_in_the_log()
    {
        var port = TestBackend.FreePort();
        await _forwarder.UpdateAsync([Service(8, port, "127.0.0.1", TestBackend.FreePort(), "postgres.database")]);

        using (var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            await client.ConnectAsync(IPAddress.Parse(_prefix + 8), port);
            Assert.Equal(0, await client.ReceiveAsync(new byte[16])); // closed: nothing to talk to
        }

        var entry = Assert.Single(_log.Since(-1));
        Assert.Equal(("TCP", 502, true), (entry.Method, entry.Status, entry.FromProxy));
        Assert.Equal($"postgres.database:{port}", entry.Authority);
    }

    [Fact]
    public async Task Connections_are_logged_while_open_and_closed_after()
    {
        var port = TestBackend.FreePort();
        await _forwarder.UpdateAsync([Service(9, port, "127.0.0.1", EchoPort)]);

        await RoundTripAsync(_prefix + 9, port, "x");

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_log.Since(-1) is not [{ Duration: not null }] && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        var entry = Assert.Single(_log.Since(-1));
        Assert.Equal(("tcp", "TCP", 200), (entry.Scheme, entry.Method, entry.Status));
        Assert.Equal($"→ 127.0.0.1:{EchoPort}", entry.PathAndQuery);
        Assert.NotNull(entry.Duration);
    }

    [Fact]
    public async Task Removed_or_disabled_services_stop_listening()
    {
        var port = TestBackend.FreePort();
        var service = Service(10, port, "127.0.0.1", EchoPort);
        await _forwarder.UpdateAsync([service]);

        await _forwarder.UpdateAsync([service with { Enabled = false }]);

        Assert.Empty(_forwarder.Statuses);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await Assert.ThrowsAsync<SocketException>(() => client.ConnectAsync(IPAddress.Parse(_prefix + 10), port));
    }
}
