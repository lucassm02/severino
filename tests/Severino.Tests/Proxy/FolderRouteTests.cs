using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Severino.Core.Configuration;
using Severino.Core.Routes;
using Severino.Proxy;

namespace Severino.Tests.Proxy;

/// <summary>Routes whose target is a folder, served by the proxy itself.</summary>
public sealed class FolderRouteTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "severino-tests", Guid.NewGuid().ToString("N"));
    private readonly RequestLog _log = new();
    private ProxyServer _proxy = null!;
    private int _port;

    private string Site => Path.Combine(_root, "dist");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(Site, "assets"));
        Directory.CreateDirectory(Path.Combine(Site, "guia"));
        File.WriteAllText(Path.Combine(Site, "index.html"), "<h1>inicio</h1>");
        File.WriteAllText(Path.Combine(Site, "assets", "app.js"), "console.log(1)");
        File.WriteAllText(Path.Combine(Site, "guia", "index.html"), "<h1>guia</h1>");
        File.WriteAllText(Path.Combine(_root, "segredo.txt"), "fora");

        _proxy = new ProxyServer(NullLoggerFactory.Instance, requests: _log);
        _port = TestBackend.FreePort();
        await _proxy.StartAsync(_port, []);
    }

    public async Task DisposeAsync()
    {
        await _proxy.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    private void Route(string path = "", bool spa = false, string? folder = null) =>
        _proxy.UpdateRoutes([new RouteEntry { Domain = "docs.sev", Path = path, Target = RouteRules.FolderTarget(folder ?? Site), SpaFallback = spa }]);

    private HttpClient Client() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectCallback = async (_, ct) =>
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(IPAddress.Loopback, _port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        },
    });

    private string Url(string path) => $"http://docs.sev:{_port}{path}";

    private static async Task<HttpResponseMessage> Page(HttpClient client, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        return await client.SendAsync(request);
    }

    [Fact]
    public void A_folder_is_a_target_the_route_rules_accept()
    {
        var target = RouteRules.FolderTarget(@"C:\meus projetos\site\dist");
        Assert.Equal("file:///C:/meus%20projetos/site/dist", target);
        Assert.Equal(@"C:\meus projetos\site\dist", RouteRules.FolderOf(target));
        Assert.True(RouteRules.Validate(new RouteEntry { Domain = "docs.sev", Target = target }, [], 80).IsValid);
        Assert.Null(RouteRules.FolderOf("http://localhost:3000"));
        Assert.Null(RouteRules.FolderOf("file://servidor/compartilhado/site"));
    }

    [Fact]
    public async Task Serves_index_files_and_types_without_cache()
    {
        Route();
        using var client = Client();

        using var home = await client.GetAsync(Url("/"));
        Assert.Equal("<h1>inicio</h1>", await home.Content.ReadAsStringAsync());
        Assert.Equal("text/html", home.Content.Headers.ContentType!.MediaType);
        Assert.True(home.Headers.CacheControl!.NoCache);

        using var script = await client.GetAsync(Url("/assets/app.js"));
        Assert.Equal("console.log(1)", await script.Content.ReadAsStringAsync());
        Assert.Equal("text/javascript", script.Content.Headers.ContentType!.MediaType);

        var entry = _log.Since(-1)[0];
        Assert.Equal(Site, entry.Details!.Target);
        Assert.False(entry.FromProxy);
    }

    [Fact]
    public async Task A_folder_without_its_slash_gets_a_307_to_it()
    {
        Route();
        using var client = Client();

        using var response = await client.GetAsync(Url("/guia?x=1"));

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal("/guia/?x=1", response.Headers.Location!.OriginalString);
        Assert.Equal("<h1>guia</h1>", await (await client.GetAsync(Url("/guia/"))).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unknown_pages_go_to_index_only_for_single_page_apps_and_never_assets()
    {
        Route();
        using var client = Client();
        using var missing = await Page(client, Url("/painel/relatorios"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("Arquivo não encontrado", await missing.Content.ReadAsStringAsync());

        Route(spa: true);
        using var page = await Page(client, Url("/painel/relatorios"));
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("<h1>inicio</h1>", await page.Content.ReadAsStringAsync());

        using var asset = await client.GetAsync(Url("/assets/falta.js"));
        Assert.Equal(HttpStatusCode.NotFound, asset.StatusCode);
    }

    [Fact]
    public async Task A_path_route_serves_the_folder_below_its_path()
    {
        Route(path: "/docs");
        using var client = Client();

        using var script = await client.GetAsync(Url("/docs/assets/app.js"));
        Assert.Equal("console.log(1)", await script.Content.ReadAsStringAsync());

        using var bare = await client.GetAsync(Url("/docs"));
        Assert.Equal("/docs/", bare.Headers.Location!.OriginalString);
        Assert.Equal("<h1>inicio</h1>", await (await client.GetAsync(Url("/docs/"))).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Nothing_outside_the_folder_is_served()
    {
        Route();
        using var client = Client();

        using var response = await client.GetAsync(Url("/%2E%2E/segredo.txt"));

        Assert.NotEqual("fora", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_missing_folder_says_which_one()
    {
        var gone = Path.Combine(_root, "ainda-nao");
        Route(folder: gone);
        using var client = Client();

        using var response = await client.GetAsync(Url("/"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("ainda-nao", await response.Content.ReadAsStringAsync());
        Assert.True(_log.Since(-1).Single().FromProxy);
    }

    [Fact]
    public async Task The_route_rules_apply_to_a_folder()
    {
        _proxy.UpdateRoutes([new RouteEntry
        {
            Domain = "docs.sev", Target = RouteRules.FolderTarget(Site),
            Rules = new TrafficRules { Cors = true, ResponseHeaders = [new() { Name = "X-Build", Value = "42" }] },
        }]);
        using var client = Client();
        using var request = new HttpRequestMessage(HttpMethod.Get, Url("/"));
        request.Headers.Add("Origin", "http://localhost:5173");

        using var response = await client.SendAsync(request);

        Assert.Equal("42", response.Headers.GetValues("X-Build").Single());
        Assert.Equal("http://localhost:5173", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task Health_follows_the_folder()
    {
        var route = new RouteEntry { Domain = "docs.sev", Target = RouteRules.FolderTarget(Site) };
        var gone = new RouteEntry { Domain = "falta.sev", Target = RouteRules.FolderTarget(Path.Combine(_root, "nada")) };
        await using var health = new HealthMonitor(TimeSpan.FromHours(1));
        health.Update([route, gone]);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((health.IsUp(route.Id) is null || health.IsUp(gone.Id) is null) && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.True(health.IsUp(route.Id));
        Assert.False(health.IsUp(gone.Id));
    }
}
