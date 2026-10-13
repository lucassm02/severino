using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging.Abstractions;
using Severino.Core.Configuration;
using Severino.Proxy;
using Severino.Proxy.Certificates;

namespace Severino.Tests.Proxy;

/// <summary>
/// Service ports marked HTTP: the forwarder hands them to the proxy, which forwards with the
/// service's HTTPS and rules. A test CA trusted only by these clients.
/// </summary>
public sealed class ServiceHttpTests : IAsyncLifetime
{
    private readonly CertificateAuthority _ca = CertificateAuthority.Create(["catalogoapi", "loja"], TimeProvider.System);
    private readonly Dictionary<string, X509Certificate2> _issued = [];
    private readonly RequestLog _log = new();
    private readonly string _address = $"127.77.{Random.Shared.Next(100, 250)}.251";
    private WebApplication _backend = null!;
    private int _backendPort;
    private ProxyServer _proxy = null!;
    private ServiceForwarder _forwarder = null!;
    private int _httpPort;
    private int _httpsPort;

    public async Task InitializeAsync()
    {
        _backend = await TestBackend.StartAsync();
        _backendPort = TestBackend.PortOf(_backend);
        _proxy = new ProxyServer(NullLoggerFactory.Instance, Issue, _log);
        _forwarder = new ServiceForwarder(NullLoggerFactory.Instance, _log);
        _httpPort = TestBackend.FreePort();
        _httpsPort = TestBackend.FreePort();
    }

    public async Task DisposeAsync()
    {
        await _forwarder.DisposeAsync();
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
            if (!_issued.TryGetValue(domain, out var leaf))
                _issued[domain] = leaf = _ca.IssueLeaf(domain, TimeProvider.System);
            return leaf;
        }
    }

    private ServiceRoute Service(int port, bool https = false, TrafficRules? rules = null, params ServicePort[] more) => new()
    {
        Names = ["catalogoapi", "catalogo.loja"],
        Address = _address,
        Ports = [new ServicePort { Port = port, TargetHost = "127.0.0.1", TargetPort = _backendPort, Http = true }, .. more],
        Https = https,
        Rules = rules ?? new(),
    };

    private async Task StartAsync(ServiceRoute service)
    {
        await _proxy.StartAsync(_httpPort, _httpsPort, [], [service]);
        await _forwarder.UpdateAsync([service], _httpPort, _httpsPort);
    }

    /// <summary>Connects every request to <paramref name="address"/>:<paramref name="port"/>, like the hosts file would, and trusts only the test CA.</summary>
    private HttpClient Client(string address, int? port = null) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectCallback = async (context, ct) =>
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(IPAddress.Parse(address), port ?? context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        },
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
                    return false;
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.CustomTrustStore.Add(_ca.PublicCertificate());
                return chain.Build(X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()));
            },
        },
    });

    [Fact]
    public async Task Http_port_goes_through_the_proxy_with_the_apps_host_and_the_rules()
    {
        var port = TestBackend.FreePort();
        var service = Service(port, rules: new TrafficRules { ResponseHeaders = [new HeaderRule { Name = "X-Servico", Value = "sim" }] });
        await StartAsync(service);
        using var client = Client(_address);

        using var response = await client.GetAsync($"http://catalogoapi:{port}/echo");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"host=catalogoapi:{port}", await response.Content.ReadAsStringAsync());
        Assert.Equal("sim", response.Headers.GetValues("X-Servico").Single());
        var entries = _log.Since(-1);
        Assert.Contains(entries, e => e.Scheme == "http" && e.Details?.RouteId == service.Id);
        Assert.DoesNotContain(entries, e => e.Scheme == "tcp");
    }

    [Fact]
    public async Task Each_name_and_port_reaches_its_own_port()
    {
        var port = TestBackend.FreePort();
        var other = TestBackend.FreePort();
        var service = Service(port, more: new ServicePort { Port = other, TargetHost = "127.0.0.1", TargetPort = TestBackend.FreePort(), Http = true });
        await StartAsync(service);
        using var client = Client(_address);

        using var first = await client.GetAsync($"http://catalogo.loja:{port}/echo");
        using var second = await client.GetAsync($"http://catalogo.loja:{other}/echo");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        // The second port's destination is not up: the proxy's 502, not the first port's answer.
        Assert.Equal(HttpStatusCode.BadGateway, second.StatusCode);
    }

    [Fact]
    public async Task Https_on_the_same_port_is_told_by_the_first_byte()
    {
        var port = TestBackend.FreePort();
        await StartAsync(Service(port, https: true));
        using var client = Client(_address);

        using var secure = await client.GetAsync($"https://catalogoapi:{port}/echo");
        using var plain = await client.GetAsync($"http://catalogoapi:{port}/echo");

        Assert.Equal(HttpStatusCode.OK, secure.StatusCode);
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
    }

    [Fact]
    public async Task Without_https_a_tls_handshake_gets_no_certificate()
    {
        var port = TestBackend.FreePort();
        await StartAsync(Service(port));
        using var client = Client(_address);

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync($"https://catalogoapi:{port}/echo"));
    }

    [Fact]
    public async Task Https_without_a_port_goes_to_the_main_http_port()
    {
        // What the forwarder's 443 hands over: the name alone, as https://catalogoapi sends it.
        await _proxy.StartAsync(_httpPort, _httpsPort, [], [Service(8080, https: true)]);
        using var client = Client("127.0.0.1", _httpsPort);

        using var response = await client.GetAsync("https://catalogoapi/echo");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("host=catalogoapi", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Port_80_answers_the_name_alone()
    {
        await _proxy.StartAsync(_httpPort, _httpsPort, [], [Service(80)]);
        using var client = Client("127.0.0.1", _httpPort);

        using var response = await client.GetAsync("http://catalogoapi/echo");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_port_not_marked_http_stays_a_tunnel()
    {
        var port = TestBackend.FreePort();
        var service = Service(port) with { Ports = [new ServicePort { Port = port, TargetHost = "127.0.0.1", TargetPort = _backendPort }] };
        await StartAsync(service);
        using var client = Client(_address);

        using var response = await client.GetAsync($"http://catalogoapi:{port}/echo");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(_log.Since(-1), e => e.Scheme == "tcp");
        Assert.DoesNotContain(_log.Since(-1), e => e.Scheme == "http");
    }
}
