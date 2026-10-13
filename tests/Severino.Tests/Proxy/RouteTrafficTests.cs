using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging.Abstractions;
using Severino.Core.Configuration;
using Severino.Core.Routes;
using Severino.Proxy;

namespace Severino.Tests.Proxy;

/// <summary>A route's traffic rules, through real Kestrel and YARP.</summary>
public sealed class RouteTrafficTests : IAsyncLifetime
{
    private readonly RequestLog _log = new();
    private WebApplication _backend = null!;
    private ProxyServer _proxy = null!;
    private int _port;
    private string _target = "";

    public async Task InitializeAsync()
    {
        _backend = await TestBackend.StartAsync();
        _target = $"http://127.0.0.1:{TestBackend.PortOf(_backend)}";
        _proxy = new ProxyServer(NullLoggerFactory.Instance, requests: _log);
        _port = TestBackend.FreePort();
        await _proxy.StartAsync(_port, []);
    }

    public async Task DisposeAsync()
    {
        await _proxy.DisposeAsync();
        await _backend.DisposeAsync();
    }

    private void Route(TrafficRules rules) =>
        _proxy.UpdateRoutes([new RouteEntry { Domain = "meuapp.sev", Target = _target, Rules = rules }]);

    private HttpClient Client() => new(new SocketsHttpHandler
    {
        ConnectCallback = async (_, ct) =>
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(IPAddress.Loopback, _port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        },
    });

    private string Url(string path) => $"http://meuapp.sev:{_port}{path}";

    [Fact]
    public async Task Sets_and_removes_headers_both_ways()
    {
        Route(new TrafficRules
        {
            RequestHeaders = [new() { Name = "X-Tenant", Value = "loja" }, new() { Name = "X-Remover", Value = "" }],
            ResponseHeaders = [new() { Name = "X-Backend", Value = "" }, new() { Name = "Cache-Control", Value = "no-store" }],
        });
        using var client = Client();
        using var request = new HttpRequestMessage(HttpMethod.Get, Url("/headers"));
        request.Headers.Add("X-Remover", "sai");

        using var response = await client.SendAsync(request);
        var seen = await response.Content.ReadAsStringAsync();

        Assert.Contains("x-tenant: loja", seen);
        Assert.DoesNotContain("x-remover", seen);
        Assert.False(response.Headers.Contains("X-Backend"));
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Cors_answers_the_preflight_and_opens_the_answer_to_the_origin()
    {
        Route(new TrafficRules { Cors = true });
        using var client = Client();

        using var preflight = new HttpRequestMessage(HttpMethod.Options, Url("/headers"));
        preflight.Headers.Add("Origin", "http://localhost:5173");
        preflight.Headers.Add("Access-Control-Request-Method", "PUT");
        preflight.Headers.Add("Access-Control-Request-Headers", "content-type, x-tenant");
        using var answer = await client.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, answer.StatusCode);
        Assert.Equal("http://localhost:5173", answer.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("PUT", answer.Headers.GetValues("Access-Control-Allow-Methods").Single());
        Assert.Equal("content-type, x-tenant", answer.Headers.GetValues("Access-Control-Allow-Headers").Single());
        Assert.True(_log.Since(-1)[0].FromProxy);

        using var request = new HttpRequestMessage(HttpMethod.Get, Url("/headers"));
        request.Headers.Add("Origin", "http://localhost:5173");
        using var response = await client.SendAsync(request);
        Assert.Equal("http://localhost:5173", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("true", response.Headers.GetValues("Access-Control-Allow-Credentials").Single());
        Assert.Contains("X-Backend", response.Headers.GetValues("Access-Control-Expose-Headers").Single());
    }

    [Fact]
    public async Task Waits_the_delay_before_forwarding()
    {
        Route(new TrafficRules { DelayMs = 300 });
        using var client = Client();

        var watch = Stopwatch.StartNew();
        using var response = await client.GetAsync(Url("/echo"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(watch.ElapsedMilliseconds >= 280, $"{watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Fails_on_purpose_and_says_so_with_cors_still_on()
    {
        Route(new TrafficRules { FailPercent = 100, FailStatus = 503, Cors = true });
        using var client = Client();
        using var request = new HttpRequestMessage(HttpMethod.Get, Url("/echo"));
        request.Headers.Add("Origin", "http://localhost:5173");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Falha simulada", await response.Content.ReadAsStringAsync());
        Assert.Equal("http://localhost:5173", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.True(_log.Since(-1).Single().FromProxy);
    }

    [Theory]
    [InlineData("X Tenant", "não é um nome")]
    [InlineData("Host", "não pode ser mudado")]
    [InlineData("Content-Length", "não pode ser mudado")]
    public void Refuses_header_names_it_cannot_set(string name, string error)
    {
        var rules = new TrafficRules { RequestHeaders = [new() { Name = name, Value = "x" }] };
        Assert.Contains(error, RouteRules.ValidateRules(rules));
    }

    [Fact]
    public void Refuses_a_delay_percent_or_status_out_of_range()
    {
        Assert.NotNull(RouteRules.ValidateRules(new TrafficRules { DelayMs = 30_001 }));
        Assert.NotNull(RouteRules.ValidateRules(new TrafficRules { FailPercent = 101 }));
        Assert.NotNull(RouteRules.ValidateRules(new TrafficRules { FailPercent = 10, FailStatus = 418 }));
        Assert.Null(RouteRules.ValidateRules(new TrafficRules { FailPercent = 10, FailStatus = 429, DelayMs = 800 }));
    }

    [Fact]
    public void A_backup_keeps_the_rules()
    {
        var rules = new TrafficRules { Cors = true, DelayMs = 800, ResponseHeaders = [new() { Name = "Cache-Control", Value = "no-store" }] };
        var json = RouteTransfer.Export([new RouteEntry { Domain = "meuapp.sev", Target = "http://localhost:3000", Rules = rules }]);

        var back = Assert.Single(RouteTransfer.Import(json, [], 80, 443).Added).Rules;

        Assert.True(back.Cors);
        Assert.Equal(800, back.DelayMs);
        Assert.Equal("no-store", Assert.Single(back.ResponseHeaders).Value);
    }
}
