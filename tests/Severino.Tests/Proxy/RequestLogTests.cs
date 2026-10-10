using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging.Abstractions;
using Severino.App.ViewModels;
using Severino.Core.Configuration;
using Severino.Proxy;

namespace Severino.Tests.Proxy;

public sealed class RequestLogTests
{
    private static RequestEntry Add(RequestLog log, string path = "/", string host = "a.sev", int status = 200) =>
        log.Add("http", host, "GET", path, status, duration: TimeSpan.FromMilliseconds(5));

    [Fact]
    public void Keeps_only_the_last_thousand()
    {
        var log = new RequestLog();
        for (var i = 0; i < RequestLog.Capacity + 250; i++)
            Add(log, $"/{i}");

        var all = log.Since(-1);

        Assert.Equal(RequestLog.Capacity, all.Count);
        Assert.Equal("/250", all[0].PathAndQuery);
        Assert.Equal($"/{RequestLog.Capacity + 249}", all[^1].PathAndQuery);
    }

    [Fact]
    public void Since_returns_only_newer_entries_and_clear_hides_old_ones()
    {
        var log = new RequestLog();
        var first = Add(log, "/1");
        Add(log, "/2");

        Assert.Equal(["/2"], log.Since(first.Sequence).Select(e => e.PathAndQuery));

        log.Clear();
        Assert.Empty(log.Since(-1));
        Add(log, "/3");
        Assert.Equal(["/3"], log.Since(-1).Select(e => e.PathAndQuery));
    }

    [Fact]
    public async Task Concurrent_writers_lose_nothing_within_capacity()
    {
        var log = new RequestLog();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
                Add(log, $"/{w}/{i}");
        })));

        var all = log.Since(-1);
        Assert.Equal(800, all.Count);
        Assert.Equal(800, all.Select(e => e.Sequence).Distinct().Count());
    }

    [Fact]
    public void View_model_shows_newest_first_holds_while_paused_and_filters()
    {
        var log = new RequestLog();
        var config = new ConfigService(new ConfigStore(Path.Combine(Path.GetTempPath(), "severino-tests", Guid.NewGuid().ToString("N"))));
        config.Load();
        var requests = new RequestsViewModel(log, config, new ProxyServer(NullLoggerFactory.Instance));

        Add(log, "/1");
        Add(log, "/2", host: "b.sev");
        requests.Poll();
        Assert.Equal(["/2", "/1"], requests.Items.Select(i => i.Path));

        requests.TogglePauseCommand.Execute(null);
        Add(log, "/3");
        requests.Poll();
        Assert.Equal(2, requests.Items.Count);
        Assert.Equal("1 nova", requests.HeldText);

        requests.TogglePauseCommand.Execute(null);
        Assert.Equal("/3", requests.Items[0].Path);
        Assert.Null(requests.HeldText);

        requests.SelectedDomain = new DomainFilter("b.sev", "b.sev");
        Assert.Equal(["/2"], requests.View.Cast<RequestItemViewModel>().Select(i => i.Path));
    }
}

/// <summary>The log as the proxy fills it, through real Kestrel and YARP.</summary>
public sealed class RequestLogProxyTests : IAsyncLifetime
{
    private readonly RequestLog _log = new();
    private WebApplication _backend = null!;
    private ProxyServer _proxy = null!;
    private int _port;
    private int _backendPort;

    public async Task InitializeAsync()
    {
        _backend = await TestBackend.StartAsync();
        _backendPort = TestBackend.PortOf(_backend);
        _proxy = new ProxyServer(NullLoggerFactory.Instance, requests: _log);
        _port = TestBackend.FreePort();
        await _proxy.StartAsync(_port,
        [
            new RouteEntry { Domain = "callfred.sev", Target = $"http://127.0.0.1:{_backendPort}" },
            new RouteEntry { Domain = "fora.sev", Target = $"http://127.0.0.1:{TestBackend.FreePort()}" },
        ]);
    }

    public async Task DisposeAsync()
    {
        await _proxy.DisposeAsync();
        await _backend.DisposeAsync();
    }

    private HttpClient Client() => new(new SocketsHttpHandler
    {
        ConnectCallback = async (_, ct) =>
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(IPAddress.Loopback, _port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        },
    });

    [Fact]
    public async Task Records_forwarded_and_proxy_answers()
    {
        using var client = Client();
        await client.GetAsync($"http://callfred.sev:{_port}/echo?x=1");
        await client.GetAsync($"http://nada.sev:{_port}/");
        await client.GetAsync($"http://fora.sev:{_port}/");

        var entries = _log.Since(-1);

        Assert.Collection(entries,
            e =>
            {
                Assert.Equal(("callfred.sev", "/echo?x=1", 200, false), (e.Host, e.PathAndQuery, e.Status, e.FromProxy));
                Assert.Equal($"callfred.sev:{_port}", e.Authority);
                Assert.NotNull(e.Duration);
            },
            e => Assert.Equal(("nada.sev", 404, true), (e.Host, e.Status, e.FromProxy)),
            e => Assert.Equal(("fora.sev", 502, true), (e.Host, e.Status, e.FromProxy)));
    }

    [Fact]
    public async Task Websocket_shows_up_on_upgrade_and_closes_later()
    {
        using var socket = new ClientWebSocket();
        using var invoker = new HttpMessageInvoker(new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var s = new Socket(SocketType.Stream, ProtocolType.Tcp);
                await s.ConnectAsync(IPAddress.Loopback, _port, ct);
                return new NetworkStream(s, ownsSocket: true);
            },
        });
        await socket.ConnectAsync(new Uri($"ws://callfred.sev:{_port}/ws"), invoker, CancellationToken.None);

        var open = Assert.Single(_log.Since(-1));
        Assert.True(open.IsWebSocket);
        Assert.Equal(101, open.Status);
        Assert.Null(open.Duration);

        await socket.SendAsync("oi"u8.ToArray(), WebSocketMessageType.Text, true, CancellationToken.None);
        await socket.ReceiveAsync(new byte[64], CancellationToken.None);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (open.Duration is null && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.NotNull(open.Duration);
        Assert.Single(_log.Since(-1));
    }
}
