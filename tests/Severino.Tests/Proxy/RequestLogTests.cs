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

    private static RequestEntry Post(RequestLog log, string? body, long bytes) => log.Add("https", "meuapp.sev", "POST", "/api/pedidos?x=1", 201,
        duration: TimeSpan.FromMilliseconds(12), details: new RequestDetails
        {
            RequestHeaders = [new("Host", "meuapp.sev"), new("Content-Type", "application/json"), new("Authorization", "Bearer it's"), new("Accept-Encoding", "gzip")],
            ResponseHeaders = [new("Content-Type", "application/json")],
            RequestBytes = bytes,
            RequestBody = body is null ? null : new CapturedBody(System.Text.Encoding.UTF8.GetBytes(body), Cut: false),
            ResponseBody = new CapturedBody("{\"id\":7}"u8.ToArray(), Cut: false),
            ResponseBytes = 8,
        });

    [Fact]
    public void Curl_carries_method_headers_and_body_quoted_for_each_shell()
    {
        var entry = Post(new RequestLog(), "{\"a\":\"o'k\"}", 11);

        var powershell = RequestExport.ToCurl(entry, CurlShell.PowerShell);
        Assert.StartsWith("curl.exe 'https://meuapp.sev/api/pedidos?x=1'", powershell);
        Assert.Contains("-X POST", powershell);
        Assert.Contains("-H 'Authorization: Bearer it''s'", powershell);
        Assert.Contains("--data-raw '{\"a\":\"o''k\"}'", powershell);
        Assert.DoesNotContain("Host:", powershell);
        Assert.DoesNotContain("Accept-Encoding", powershell);

        var bash = RequestExport.ToCurl(entry, CurlShell.Bash);
        Assert.StartsWith("curl 'https://meuapp.sev/api/pedidos?x=1'", bash);
        Assert.Contains("-H 'Authorization: Bearer it'\\''s'", bash);
    }

    [Fact]
    public void A_request_whose_body_was_not_kept_cannot_be_sent_again()
    {
        var log = new RequestLog();
        Assert.False(RequestExport.CanReplay(Post(log, null, 11)));
        Assert.True(RequestExport.CanReplay(Post(log, "{}", 2)));
        Assert.True(RequestExport.CanReplay(log.Add("http", "a.sev", "GET", "/", 200, details: new RequestDetails())));
        Assert.False(RequestExport.CanReplay(log.Add("tcp", "pedidos:80", "TCP", "→ x", 200)));
    }

    [Fact]
    public void Har_has_the_request_response_and_bodies()
    {
        var entry = Post(new RequestLog(), "{\"a\":1}", 7);

        using var har = System.Text.Json.JsonDocument.Parse(RequestExport.ToHar([entry], "0.5.1"));
        var item = har.RootElement.GetProperty("log").GetProperty("entries")[0];
        var request = item.GetProperty("request");
        Assert.Equal("https://meuapp.sev/api/pedidos?x=1", request.GetProperty("url").GetString());
        Assert.Equal("x", request.GetProperty("queryString")[0].GetProperty("name").GetString());
        Assert.Equal("{\"a\":1}", request.GetProperty("postData").GetProperty("text").GetString());
        var response = item.GetProperty("response");
        Assert.Equal(201, response.GetProperty("status").GetInt32());
        Assert.Equal("{\"id\":7}", response.GetProperty("content").GetProperty("text").GetString());
        Assert.Equal(12, item.GetProperty("time").GetDouble());
    }

    [Fact]
    public void Decodes_gzip_bodies_and_tells_text_types()
    {
        using var packed = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(packed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write("olá"u8);
        Assert.Equal("olá", System.Text.Encoding.UTF8.GetString(CapturedBodies.Decode(new CapturedBody(packed.ToArray(), false), "gzip")!));

        Assert.True(CapturedBodies.IsText("application/problem+json; charset=utf-8"));
        Assert.True(CapturedBodies.IsText("text/html"));
        Assert.False(CapturedBodies.IsText("image/png"));
    }

    [Fact]
    public void Details_mask_credentials_until_asked_and_explain_missing_bodies()
    {
        var log = new RequestLog();
        var details = new RequestDetailsViewModel(new RequestItemViewModel(Post(log, null, 11)), "meuapp.sev/api", bodiesOn: false);

        Assert.Equal("••••••••", details.RequestHeaders.Single(h => h.Name == "Authorization").Shown);
        details.ShowHidden = true;
        Assert.Equal("Bearer it's", details.RequestHeaders.Single(h => h.Name == "Authorization").Shown);

        Assert.Null(details.RequestBodyText);
        Assert.Contains("Guardar corpos", details.RequestBodyNote);
        Assert.Contains("\"id\": 7", details.ResponseBodyText);
        Assert.Contains(details.General, l => l.Value == "meuapp.sev/api");
        Assert.False(details.CanReplay);

        var tcp = new RequestDetailsViewModel(new RequestItemViewModel(log.Add("tcp", "pedidos:80", "TCP", "→ x", 200)), null, bodiesOn: true);
        Assert.False(tcp.IsHttp);
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
            new RouteEntry { Domain = "meuapp.sev", Target = $"http://127.0.0.1:{_backendPort}" },
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
        await client.GetAsync($"http://meuapp.sev:{_port}/echo?x=1");
        await client.GetAsync($"http://nada.sev:{_port}/");
        await client.GetAsync($"http://fora.sev:{_port}/");

        var entries = _log.Since(-1);

        Assert.Collection(entries,
            e =>
            {
                Assert.Equal(("meuapp.sev", "/echo?x=1", 200, false), (e.Host, e.PathAndQuery, e.Status, e.FromProxy));
                Assert.Equal($"meuapp.sev:{_port}", e.Authority);
                Assert.NotNull(e.Duration);
            },
            e => Assert.Equal(("nada.sev", 404, true), (e.Host, e.Status, e.FromProxy)),
            e => Assert.Equal(("fora.sev", 502, true), (e.Host, e.Status, e.FromProxy)));
    }

    [Fact]
    public async Task Keeps_headers_target_and_sizes_but_no_bodies_by_default()
    {
        using var client = Client();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://meuapp.sev:{_port}/size") { Content = new StringContent("{\"a\":1}", System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Teste", "sim");
        await client.SendAsync(request);

        var details = Assert.Single(_log.Since(-1)).Details!;
        Assert.Contains(details.RequestHeaders, h => h is { Key: "X-Teste", Value: "sim" });
        Assert.Contains(details.ResponseHeaders, h => h.Key == "Content-Type");
        Assert.Equal($"http://127.0.0.1:{_backendPort}", details.Target!.TrimEnd('/'));
        Assert.NotNull(details.RouteId);
        Assert.Equal(7, details.RequestBytes);
        Assert.Equal(1, details.ResponseBytes); // "7"
        Assert.Null(details.RequestBody);
        Assert.Null(details.ResponseBody);
    }

    [Fact]
    public async Task Keeps_text_bodies_when_asked_and_cuts_long_ones()
    {
        _log.CaptureBodies = true;
        using var client = Client();
        await client.PostAsync($"http://meuapp.sev:{_port}/size", new StringContent("{\"pedido\":42}", System.Text.Encoding.UTF8, "application/json"));
        var big = new string('x', RequestLog.BodyLimit + 100);
        var answer = await client.PostAsync($"http://meuapp.sev:{_port}/size", new StringContent(big));

        var entries = _log.Since(-1);
        var small = entries[0].Details!;
        Assert.Equal("{\"pedido\":42}", System.Text.Encoding.UTF8.GetString(small.RequestBody!.Bytes));
        Assert.False(small.RequestBody.Cut);
        Assert.Equal("13", System.Text.Encoding.UTF8.GetString(small.ResponseBody!.Bytes));

        var cut = entries[1].Details!;
        Assert.Equal(RequestLog.BodyLimit, cut.RequestBody!.Bytes.Length);
        Assert.True(cut.RequestBody.Cut);
        // The tap only copies: the server still got all of it.
        Assert.Equal((RequestLog.BodyLimit + 100).ToString(), await answer.Content.ReadAsStringAsync());
        Assert.Equal(RequestLog.BodyLimit + 100, cut.RequestBytes);
    }

    [Fact]
    public async Task Replay_sends_the_same_request_again_through_the_proxy()
    {
        _log.CaptureBodies = true;
        using var client = Client();
        await client.PostAsync($"http://meuapp.sev:{_port}/size", new StringContent("abc"));
        var first = Assert.Single(_log.Since(-1));
        Assert.True(RequestExport.CanReplay(first));

        Assert.Equal(200, await RequestExport.ReplayAsync(first, _port, null));

        var again = _log.Since(first.Sequence).Single();
        Assert.Equal(("POST", "/size", 3L), (again.Method, again.PathAndQuery, again.Details!.RequestBytes));
        Assert.Equal("3", System.Text.Encoding.UTF8.GetString(again.Details.ResponseBody!.Bytes));
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
        await socket.ConnectAsync(new Uri($"ws://meuapp.sev:{_port}/ws"), invoker, CancellationToken.None);

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
