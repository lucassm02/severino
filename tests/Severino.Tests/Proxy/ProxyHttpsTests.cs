using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging.Abstractions;
using Severino.Core.Configuration;
using Severino.Proxy;
using Severino.Proxy.Certificates;

namespace Severino.Tests.Proxy;

/// <summary>
/// The HTTPS listener with a test CA trusted only by these clients, never by Windows.
/// </summary>
public sealed class ProxyHttpsTests : IAsyncLifetime
{
    private WebApplication _backend = null!;
    private int _backendPort;
    private CertificateAuthority _ca = CertificateAuthority.Create(["sev"], TimeProvider.System);
    private readonly Dictionary<string, X509Certificate2> _issued = [];
    private readonly RequestLog _log = new();
    private ProxyServer _proxy = null!;
    private int _httpPort;
    private int _httpsPort;

    public async Task InitializeAsync()
    {
        _backend = await TestBackend.StartAsync();
        _backendPort = TestBackend.PortOf(_backend);
        _proxy = new ProxyServer(NullLoggerFactory.Instance, Issue, _log);
        _httpPort = TestBackend.FreePort();
        _httpsPort = TestBackend.FreePort();
        await _proxy.StartAsync(_httpPort, _httpsPort,
        [
            Route("meuapp.sev", https: true, redirect: true),
            Route("plain.sev", https: false),
            Route("noredirect.sev", https: true, redirect: false),
        ]);
        Assert.Equal(ProxyState.Running, _proxy.Status.State);
        Assert.Equal(ProxyState.Running, _proxy.HttpsStatus.State);
    }

    public async Task DisposeAsync()
    {
        await _proxy.DisposeAsync();
        await _backend.DisposeAsync();
        _ca.Dispose();
    }

    private X509Certificate2? Issue(string domain)
    {
        lock (_issued)
        {
            if (!_ca.Covers(domain))
                return null;
            var key = _ca.Thumbprint + domain;
            if (!_issued.TryGetValue(key, out var leaf))
                _issued[key] = leaf = _ca.IssueLeaf(domain, TimeProvider.System);
            return leaf;
        }
    }

    private RouteEntry Route(string domain, bool https, bool redirect = false) => new()
    {
        Domain = domain,
        Target = $"http://127.0.0.1:{_backendPort}",
        Https = https,
        RedirectToHttps = redirect,
    };

    /// <summary>Connects every request to the proxy, like the hosts file would, and trusts only the test CA.</summary>
    private SocketsHttpHandler Handler(Action<X509Certificate2>? onCertificate = null) => new()
    {
        AllowAutoRedirect = false,
        ConnectCallback = async (context, ct) =>
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(IPAddress.Loopback, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        },
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
                    return false;
                var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                onCertificate?.Invoke(leaf);
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.CustomTrustStore.Add(_ca.PublicCertificate());
                return chain.Build(leaf);
            },
        },
    };

    private Uri Https(string domain, string path = "/") => new($"https://{domain}:{_httpsPort}{path}");
    private Uri Http(string domain, string path = "/") => new($"http://{domain}:{_httpPort}{path}");

    [Fact]
    public async Task Serves_https_over_http2_with_forwarded_proto()
    {
        using var client = new HttpClient(Handler());
        using var request = new HttpRequestMessage(HttpMethod.Get, Https("meuapp.sev", "/echo"))
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpVersion.Version20, response.Version);
        Assert.Contains("x-forwarded-proto=https", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_wildcard_route_serves_https_with_a_leaf_for_the_name_asked()
    {
        _proxy.UpdateRoutes([Route("*.lojas.sev", https: true)]);
        X509Certificate2? seen = null;
        using var client = new HttpClient(Handler(c => seen = c));

        using var response = await client.GetAsync(Https("cliente42.lojas.sev", "/echo"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("cliente42.lojas.sev", seen!.GetNameInfo(X509NameType.DnsName, forIssuer: false));
    }

    [Theory]
    [InlineData("plain.sev")]     // route without HTTPS
    [InlineData("nothere.sev")]   // no route at all
    public async Task Handshake_fails_without_a_certificate_for_the_name(string domain)
    {
        using var client = new HttpClient(Handler());

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(Https(domain)));
    }

    [Fact]
    public async Task Http_redirects_with_307_keeping_path_and_query()
    {
        using var client = new HttpClient(Handler());

        using var response = await client.GetAsync(Http("meuapp.sev", "/x?y=1"));

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal(Https("meuapp.sev", "/x?y=1"), response.Headers.Location);
        var logged = _log.Since(-1)[^1];
        Assert.Equal((307, true, "http"), (logged.Status, logged.FromProxy, logged.Scheme));
    }

    [Theory]
    [InlineData("noredirect.sev")]
    [InlineData("plain.sev")]
    public async Task Routes_without_redirect_stay_on_http(string domain)
    {
        using var client = new HttpClient(Handler());

        using var response = await client.GetAsync(Http(domain, "/echo"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Never_sends_hsts()
    {
        using var client = new HttpClient(Handler());

        using var secure = await client.GetAsync(Https("meuapp.sev", "/echo"));
        using var redirect = await client.GetAsync(Http("meuapp.sev"));
        using var notFound = await client.GetAsync(Http("nothere.sev"));

        Assert.All(new[] { secure, redirect, notFound }, r => Assert.False(r.Headers.Contains("Strict-Transport-Security")));
    }

    [Fact]
    public async Task Websocket_passes_through_wss()
    {
        using var socket = new ClientWebSocket();
        using var invoker = new HttpMessageInvoker(Handler());
        await socket.ConnectAsync(new Uri($"wss://meuapp.sev:{_httpsPort}/ws"), invoker, CancellationToken.None);

        await socket.SendAsync("hmr"u8.ToArray(), WebSocketMessageType.Text, true, CancellationToken.None);
        var buffer = new byte[64];
        var received = await socket.ReceiveAsync(buffer, CancellationToken.None);

        Assert.Equal("eco: hmr", Encoding.UTF8.GetString(buffer, 0, received.Count));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    [Fact]
    public async Task New_ca_is_used_by_new_connections_without_restarting()
    {
        X509Certificate2? presented = null;
        _ca = CertificateAuthority.Create(["sev"], TimeProvider.System);

        using var client = new HttpClient(Handler(cert => presented = cert));
        using var response = await client.GetAsync(Https("meuapp.sev", "/echo"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(_ca.Certificate.Subject, presented!.Issuer);
    }

    [Fact]
    public async Task Busy_https_port_keeps_http_running()
    {
        var busy = new TcpListener(IPAddress.Loopback, 0);
        busy.Start();
        var busyPort = ((IPEndPoint)busy.LocalEndpoint).Port;
        await using var other = new ProxyServer(NullLoggerFactory.Instance, Issue);
        try
        {
            var httpPort = TestBackend.FreePort();
            await other.StartAsync(httpPort, busyPort, [Route("meuapp.sev", https: true)]);

            Assert.Equal(ProxyState.Running, other.Status.State);
            Assert.Equal(ProxyState.PortInUse, other.HttpsStatus.State);
            Assert.Equal(Environment.ProcessId, other.HttpsStatus.PortOwner?.ProcessId);
        }
        finally
        {
            busy.Stop();
        }
    }
}
