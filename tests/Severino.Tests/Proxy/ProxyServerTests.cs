using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Severino.Core.Configuration;
using Severino.Proxy;

namespace Severino.Tests.Proxy;

/// <summary>Kestrel + YARP on a random loopback port, in front of a fake backend.</summary>
public sealed class ProxyServerTests : IAsyncLifetime
{
    private WebApplication _backend = null!;
    private int _backendPort;
    private readonly ProxyServer _proxy = new(NullLoggerFactory.Instance);
    private int _proxyPort;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _backend = await StartBackendAsync();
        _backendPort = TestBackend.PortOf(_backend);
        _proxyPort = FreePort();
        _client = new HttpClient(Handler());
        await _proxy.StartAsync(_proxyPort, [Route("callfred.sev", $"http://127.0.0.1:{_backendPort}")]);
        Assert.Equal(ProxyState.Running, _proxy.Status.State);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _proxy.DisposeAsync();
        await _backend.DisposeAsync();
    }

    private static RouteEntry Route(string domain, string target, bool preserveHost = false) =>
        new() { Domain = domain, Target = target, PreserveHost = preserveHost };

    private Uri Url(string domain, string path = "/") => new($"http://{domain}:{_proxyPort}{path}");

    /// <summary>Sends every request to the proxy whatever the host name, like the hosts file would.</summary>
    private SocketsHttpHandler Handler() => new()
    {
        ConnectCallback = async (_, ct) =>
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(IPAddress.Loopback, _proxyPort, ct);
            return new NetworkStream(socket, ownsSocket: true);
        },
    };

    [Fact]
    public async Task Routes_by_host_and_rewrites_host_header_by_default()
    {
        var body = await _client.GetStringAsync(Url("callfred.sev", "/echo"));

        Assert.Contains($"host=127.0.0.1:{_backendPort}", body);
        Assert.Contains($"x-forwarded-host=callfred.sev:{_proxyPort}", body);
        Assert.Contains("x-forwarded-proto=http", body);
        Assert.Contains("x-forwarded-for=127.0.0.1", body);
    }

    [Fact]
    public async Task Preserve_host_keeps_original_host()
    {
        _proxy.UpdateRoutes([Route("callfred.sev", $"http://127.0.0.1:{_backendPort}", preserveHost: true)]);

        var body = await _client.GetStringAsync(Url("callfred.sev", "/echo"));

        Assert.Contains($"host=callfred.sev:{_proxyPort}", body);
    }

    [Fact]
    public async Task Unknown_host_gets_404_listing_active_routes()
    {
        var response = await _client.GetAsync(Url("nada.sev"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("nada.sev", html);
        Assert.Contains($"http://callfred.sev:{_proxyPort}/", html);
    }

    [Fact]
    public async Task Target_down_gets_502_page()
    {
        var closedPort = FreePort();
        _proxy.UpdateRoutes([Route("fora.sev", $"http://127.0.0.1:{closedPort}")]);

        var response = await _client.GetAsync(Url("fora.sev"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains($"fora.sev</strong> → <code>127.0.0.1:{closedPort}</code> não respondeu.", html);
        Assert.Contains("Seu servidor está rodando?", html);
    }

    [Fact]
    public async Task Routes_update_without_restarting_the_listener()
    {
        var before = _proxy.Status;

        _proxy.UpdateRoutes([Route("callfred.sev", $"http://127.0.0.1:{_backendPort}"), Route("novo.sev", $"http://127.0.0.1:{_backendPort}")]);

        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync(Url("novo.sev", "/echo"))).StatusCode);
        Assert.Same(before, _proxy.Status);
    }

    [Fact]
    public async Task A_wildcard_route_takes_any_name_below_and_an_exact_route_wins()
    {
        var deadPort = TestBackend.FreePort();
        _proxy.UpdateRoutes(
        [
            Route("*.callfred.sev", $"http://127.0.0.1:{deadPort}"),
            Route("api.callfred.sev", $"http://127.0.0.1:{_backendPort}"),
        ]);

        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync(Url("api.callfred.sev", "/echo"))).StatusCode);
        // The wildcard's target is down: a 502 shows the wildcard route was the one chosen.
        Assert.Equal(HttpStatusCode.BadGateway, (await _client.GetAsync(Url("cliente42.callfred.sev", "/echo"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, (await _client.GetAsync(Url("a.b.callfred.sev", "/echo"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(Url("callfred.sev", "/echo"))).StatusCode);
    }

    [Fact]
    public async Task Disabled_and_looping_routes_are_not_served()
    {
        _proxy.UpdateRoutes(
        [
            Route("callfred.sev", $"http://127.0.0.1:{_backendPort}") with { Enabled = false },
            Route("loop.sev", $"http://127.0.0.1:{_proxyPort}"),
        ]);

        var disabled = await _client.GetAsync(Url("callfred.sev"));
        Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);
        Assert.Contains("Rota desligada", await disabled.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(Url("loop.sev"))).StatusCode);
    }

    [Fact]
    public async Task Error_pages_are_self_contained_with_the_mascot_inline()
    {
        var html = await (await _client.GetAsync(Url("nada.sev"))).Content.ReadAsStringAsync();

        Assert.Contains("Rota não encontrada", html);
        Assert.Contains("src=\"data:image/png;base64,iVBORw0KGgo", html);
        // Nothing loaded from elsewhere; the route links are plain anchors.
        Assert.DoesNotMatch("src=\"(https?:)?//|<link[^>]+href=\"(https?:)?//", html);
    }

    [Fact]
    public async Task Localhost_target_on_ipv4_only_answers_without_the_refused_ipv6_delay()
    {
        // The backend listens on 127.0.0.1 only; "localhost" resolves to ::1 first.
        _proxy.UpdateRoutes([Route("callfred.sev", $"http://localhost:{_backendPort}")]);
        using var client = new HttpClient(Handler());

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var response = await client.GetAsync(Url("callfred.sev", "/echo"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task Websocket_passes_through()
    {
        using var socket = new ClientWebSocket();
        using var invoker = new HttpMessageInvoker(Handler());
        await socket.ConnectAsync(new Uri($"ws://callfred.sev:{_proxyPort}/ws"), invoker, CancellationToken.None);

        await socket.SendAsync("olá hmr"u8.ToArray(), WebSocketMessageType.Text, true, CancellationToken.None);
        var buffer = new byte[64];
        var received = await socket.ReceiveAsync(buffer, CancellationToken.None);

        Assert.Equal("eco: olá hmr", Encoding.UTF8.GetString(buffer, 0, received.Count));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    [Fact]
    public async Task Large_bodies_are_not_limited()
    {
        var payload = new byte[40 * 1024 * 1024]; // above Kestrel's default 30 MB limit

        var response = await _client.PostAsync(Url("callfred.sev", "/size"), new ByteArrayContent(payload));

        Assert.Equal(payload.Length.ToString(), await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Busy_port_is_reported_with_its_owner()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var busyPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var other = new ProxyServer(NullLoggerFactory.Instance);
        try
        {
            await other.StartAsync(busyPort, []);

            Assert.Equal(ProxyState.PortInUse, other.Status.State);
            Assert.Equal(Environment.ProcessId, other.Status.PortOwner?.ProcessId);
            Assert.Contains($"porta {busyPort}", other.Status.Detail);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Restart_moves_to_a_new_port()
    {
        var newPort = FreePort();

        await _proxy.StartAsync(newPort, [Route("callfred.sev", $"http://127.0.0.1:{_backendPort}")]);
        _proxyPort = newPort;

        Assert.Equal(new ProxyStatus(ProxyState.Running, newPort), _proxy.Status);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync(Url("callfred.sev", "/echo"))).StatusCode);
    }

    [Fact]
    public async Task Disposing_twice_is_harmless()
    {
        // The app stops the proxy on exit, then the DI container disposes it again.
        var proxy = new ProxyServer(NullLoggerFactory.Instance);
        await proxy.StartAsync(FreePort(), []);

        await proxy.StopAsync();
        await proxy.DisposeAsync();
        await proxy.DisposeAsync();
    }

    private static int FreePort() => TestBackend.FreePort();

    private static Task<WebApplication> StartBackendAsync() => TestBackend.StartAsync();
}
