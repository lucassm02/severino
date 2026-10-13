using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging.Abstractions;
using Severino.Core.Configuration;
using Severino.Core.Routes;
using Severino.Proxy;

namespace Severino.Tests.Proxy;

/// <summary>Routes that ask for a password or a header before forwarding.</summary>
public sealed class RouteAccessTests : IAsyncLifetime
{
    private static readonly string Hash = AccessSecrets.HashPassword("segredo");
    private readonly RequestLog _log = new();
    private WebApplication _backend = null!;
    private ProxyServer _proxy = null!;
    private int _port;
    private int _backendPort;

    public async Task InitializeAsync()
    {
        _backend = await TestBackend.StartAsync();
        _backendPort = TestBackend.PortOf(_backend);
        _proxy = new ProxyServer(NullLoggerFactory.Instance, requests: _log, sessionKey: new byte[32]);
        _port = TestBackend.FreePort();
    }

    public async Task DisposeAsync()
    {
        await _proxy.DisposeAsync();
        await _backend.DisposeAsync();
    }

    private Task StartAsync(RouteAccess access, string path = "", bool cors = false) => _proxy.StartAsync(_port,
    [
        new RouteEntry
        {
            Domain = "meuapp.sev",
            Path = path,
            Target = $"http://127.0.0.1:{_backendPort}",
            Access = access,
            Rules = new TrafficRules { Cors = cors },
        },
    ]);

    private HttpClient Client(CookieContainer? cookies = null) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        CookieContainer = cookies ?? new CookieContainer(),
        UseCookies = true,
        ConnectCallback = async (context, ct) =>
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(IPAddress.Loopback, _port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        },
    });

    private static HttpRequestMessage Page(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("text/html");
        return request;
    }

    private static readonly RouteAccess Login = new() { Mode = AccessMode.Login, User = "ana", PasswordHash = Hash };

    [Fact]
    public async Task A_page_goes_to_the_login_and_back_after_the_password()
    {
        await StartAsync(Login);
        using var client = Client();

        using var first = await client.SendAsync(Page("http://meuapp.sev/echo?x=1"));
        Assert.Equal(HttpStatusCode.SeeOther, first.StatusCode);
        Assert.Equal("/.severino/login?next=%2Fecho%3Fx%3D1", first.Headers.Location!.OriginalString);

        using var form = await client.SendAsync(Page("http://meuapp.sev/.severino/login?next=%2Fecho%3Fx%3D1"));
        Assert.Equal(HttpStatusCode.OK, form.StatusCode);
        Assert.Contains("name=\"password\"", await form.Content.ReadAsStringAsync());

        using var signIn = await client.PostAsync("http://meuapp.sev/.severino/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["user"] = "ana", ["password"] = "segredo", ["next"] = "/echo?x=1",
        }));
        Assert.Equal(HttpStatusCode.SeeOther, signIn.StatusCode);
        Assert.Equal("/echo?x=1", signIn.Headers.Location!.OriginalString);
        Assert.Contains(signIn.Headers.GetValues("Set-Cookie"), c => c.Contains("httponly", StringComparison.OrdinalIgnoreCase));

        using var after = await client.SendAsync(Page("http://meuapp.sev/echo?x=1"));
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Contains("host=", await after.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_wrong_password_shows_the_page_again_with_the_reason()
    {
        await StartAsync(Login);
        using var client = Client();

        using var signIn = await client.PostAsync("http://meuapp.sev/.severino/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["user"] = "ana", ["password"] = "errada",
        }));

        Assert.Equal(HttpStatusCode.Unauthorized, signIn.StatusCode);
        Assert.Contains("role=\"alert\"", await signIn.Content.ReadAsStringAsync());
        using var still = await client.GetAsync("http://meuapp.sev/echo");
        Assert.Equal(HttpStatusCode.Unauthorized, still.StatusCode);
    }

    [Fact]
    public async Task A_client_that_is_not_a_browser_gets_401_without_a_redirect()
    {
        await StartAsync(Login);
        using var client = Client();

        using var response = await client.GetAsync("http://meuapp.sev/api/pedidos");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.StartsWith("text/plain", response.Content.Headers.ContentType!.ToString());
        Assert.True(_log.Since(-1).Single().FromProxy);
    }

    [Fact]
    public async Task Changing_the_password_ends_the_sessions()
    {
        await StartAsync(Login);
        var cookies = new CookieContainer();
        using var client = Client(cookies);
        using var _ = await client.PostAsync("http://meuapp.sev/.severino/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["user"] = "ana", ["password"] = "segredo" }));
        using var signedIn = await client.GetAsync("http://meuapp.sev/echo");
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);

        await StartAsync(Login with { PasswordHash = AccessSecrets.HashPassword("outra") });
        using var after = await client.GetAsync("http://meuapp.sev/echo");

        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Theory]
    [InlineData("https://outro.site/", "/")]
    [InlineData("//outro.site/", "/")]
    [InlineData("/\\outro.site", "/")]
    [InlineData("/pedidos?x=1", "/pedidos?x=1")]
    [InlineData("/.severino/logout", "/")]
    public void The_way_back_stays_on_the_route(string next, string expected) =>
        Assert.Equal(expected, RouteAccessGate.SafeNext(next, ""));

    [Fact]
    public void A_path_route_goes_back_only_below_its_path() =>
        Assert.Equal("/api", RouteAccessGate.SafeNext("/outra", "/api"));

    [Fact]
    public async Task Basic_auth_asks_the_browser_and_lets_the_right_pair_through()
    {
        await StartAsync(new RouteAccess { Mode = AccessMode.Basic, User = "ana", PasswordHash = Hash });
        using var client = Client();

        using var without = await client.GetAsync("http://meuapp.sev/echo");
        Assert.Equal(HttpStatusCode.Unauthorized, without.StatusCode);
        Assert.Equal("Basic", without.Headers.WwwAuthenticate.Single().Scheme);

        using var wrong = new HttpRequestMessage(HttpMethod.Get, "http://meuapp.sev/echo");
        wrong.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("ana:errada")));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(wrong)).StatusCode);

        using var right = new HttpRequestMessage(HttpMethod.Get, "http://meuapp.sev/echo");
        right.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("ana:segredo")));
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(right)).StatusCode);
    }

    [Fact]
    public async Task A_required_header_passes_with_its_value_and_reaches_the_destination()
    {
        await StartAsync(new RouteAccess { Mode = AccessMode.Header, HeaderName = "X-Api-Key", HeaderHash = AccessSecrets.HashKey("chave-123") });
        using var client = Client();

        using var without = await client.GetAsync("http://meuapp.sev/headers");
        Assert.Equal(HttpStatusCode.Unauthorized, without.StatusCode);

        using var with = new HttpRequestMessage(HttpMethod.Get, "http://meuapp.sev/headers");
        with.Headers.Add("X-Api-Key", "chave-123");
        using var response = await client.SendAsync(with);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("x-api-key: chave-123", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task With_cors_on_the_preflight_passes_without_credentials()
    {
        await StartAsync(new RouteAccess { Mode = AccessMode.Header, HeaderName = "X-Api-Key", HeaderHash = AccessSecrets.HashKey("k") }, cors: true);
        using var client = Client();
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "http://meuapp.sev/echo");
        preflight.Headers.Add("Origin", "http://localhost:5173");
        preflight.Headers.Add("Access-Control-Request-Method", "GET");

        using var response = await client.SendAsync(preflight);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task A_path_route_keeps_its_login_below_its_path()
    {
        await StartAsync(Login, path: "/api");
        using var client = Client();

        using var response = await client.SendAsync(Page("http://meuapp.sev/api/pedidos"));

        Assert.Equal("/api/.severino/login?next=%2Fapi%2Fpedidos", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task A_folder_route_is_kept_behind_the_header_too()
    {
        var folder = Directory.CreateTempSubdirectory("severino-access-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "index.html"), "<h1>docs</h1>");
            await _proxy.StartAsync(_port,
            [
                new RouteEntry
                {
                    Domain = "docs.sev",
                    Target = RouteRules.FolderTarget(folder),
                    Access = new RouteAccess { Mode = AccessMode.Header, HeaderName = "X-Api-Key", HeaderHash = AccessSecrets.HashKey("k") },
                },
            ]);
            using var client = Client();

            using var without = await client.GetAsync("http://docs.sev/");
            using var with = new HttpRequestMessage(HttpMethod.Get, "http://docs.sev/");
            with.Headers.Add("X-Api-Key", "k");
            using var allowed = await client.SendAsync(with);

            Assert.Equal(HttpStatusCode.Unauthorized, without.StatusCode);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Secrets_are_hashed_and_checked()
    {
        Assert.True(AccessSecrets.VerifyPassword("segredo", Hash));
        Assert.False(AccessSecrets.VerifyPassword("Segredo", Hash));
        Assert.NotEqual(Hash, AccessSecrets.HashPassword("segredo"));
        Assert.True(AccessSecrets.VerifyKey("k", AccessSecrets.HashKey("k")));
        Assert.False(AccessSecrets.VerifyKey("k2", AccessSecrets.HashKey("k")));
    }

    [Fact]
    public void Access_needs_its_parts()
    {
        Assert.Equal("Preencha a senha.", RouteRules.ValidateAccess(new RouteAccess { Mode = AccessMode.Login, User = "ana" }));
        Assert.Equal("Preencha o usuário.", RouteRules.ValidateAccess(new RouteAccess { Mode = AccessMode.Basic, PasswordHash = Hash }));
        Assert.StartsWith("Host não serve", RouteRules.ValidateAccess(new RouteAccess { Mode = AccessMode.Header, HeaderName = "Host", HeaderHash = AccessSecrets.HashKey("k") }));
        Assert.Null(RouteRules.ValidateAccess(new RouteAccess()));
    }
}
