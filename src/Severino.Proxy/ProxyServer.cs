using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Severino.Core.Configuration;
using Severino.Core.Network;
using Severino.Core.Routes;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

namespace Severino.Proxy;

public enum ProxyState
{
    Stopped,
    Running,
    /// <summary>Another process listens on the port; see <see cref="ProxyStatus.PortOwner"/>.</summary>
    PortInUse,
    Failed,
}

public sealed record ProxyStatus(ProxyState State, int Port, string? Detail = null, PortOwnerInfo? PortOwner = null);

/// <summary>
/// Kestrel + YARP on loopback only, as two independent listeners: HTTP, and HTTPS when a port and
/// a certificate source are given. A busy HTTPS port never takes HTTP down. Routes change in
/// place; a port change restarts only that listener.
/// </summary>
/// <param name="certificates">Server certificate for a normalized domain, or null when it has none.</param>
public sealed class ProxyServer(ILoggerFactory loggerFactory, Func<string, X509Certificate2?>? certificates = null, RequestLog? requests = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _logger = loggerFactory.CreateLogger<ProxyServer>();
    private Listener? _http;
    private Listener? _https;
    private IReadOnlyList<RouteEntry> _routes = [];
    private int? _httpsPort;
    private bool _disposed;

    /// <summary>The HTTP listener.</summary>
    public ProxyStatus Status { get; private set; } = new(ProxyState.Stopped, 0);

    /// <summary>The HTTPS listener; Stopped when HTTPS is off.</summary>
    public ProxyStatus HttpsStatus { get; private set; } = new(ProxyState.Stopped, 0);

    /// <summary>Raised on the caller's thread of Start/Stop, for either listener.</summary>
    public event EventHandler<ProxyStatus>? StatusChanged;

    public Task StartAsync(int httpPort, IReadOnlyList<RouteEntry> routes, CancellationToken cancellationToken = default) =>
        StartAsync(httpPort, null, routes, cancellationToken);

    /// <summary>
    /// Listens on <paramref name="httpPort"/>, and on <paramref name="httpsPort"/> when given.
    /// A listener already running on the same port is kept; one not running is retried. Busy
    /// ports are reported in the statuses, not thrown.
    /// </summary>
    public async Task StartAsync(int httpPort, int? httpsPort, IReadOnlyList<RouteEntry> routes, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _routes = routes;
            _httpsPort = httpsPort;

            if (_http?.Port != httpPort)
            {
                await StopAsync(_http);
                _http = await StartListenerAsync(httpPort, tls: false, cancellationToken);
            }

            if (_https?.Port != httpsPort || (httpsPort is not null && certificates is null))
            {
                await StopAsync(_https);
                _https = null;
                if (httpsPort is { } port && certificates is not null)
                    _https = await StartListenerAsync(port, tls: true, cancellationToken);
                else
                    SetStatus(tls: true, new ProxyStatus(ProxyState.Stopped, httpsPort ?? 0));
            }

            UpdateRoutes(routes);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void UpdateRoutes(IReadOnlyList<RouteEntry> routes)
    {
        _routes = routes;
        var (yarpRoutes, clusters) = ProxyConfigMapper.Map(routes, Status.Port, _httpsPort);
        _http?.Config.Update(yarpRoutes, clusters);
        _https?.Config.Update(yarpRoutes, clusters);
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopAsync(_http);
            await StopAsync(_https);
            _http = _https = null;
            SetStatus(tls: false, new ProxyStatus(ProxyState.Stopped, Status.Port));
            SetStatus(tls: true, new ProxyStatus(ProxyState.Stopped, HttpsStatus.Port));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Safe to call more than once.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        await StopAsync();
        _disposed = true;
        _gate.Dispose();
    }

    /// <summary>The certificate for an SNI name: only routes with HTTPS on, and only names the CA covers.</summary>
    internal X509Certificate2? CertificateFor(string? serverName)
    {
        if (certificates is null || serverName is null)
            return null;
        var route = FindRoute(serverName);
        return route is { Https: true } ? certificates(RouteRules.Normalize(route.Domain)!) : null;
    }

    private RouteEntry? FindRoute(string host)
    {
        var name = RouteRules.Normalize(host);
        return name is null ? null : _routes.FirstOrDefault(r => r.Enabled && RouteRules.Normalize(r.Domain) == name);
    }

    private async Task<Listener?> StartListenerAsync(int port, bool tls, CancellationToken cancellationToken)
    {
        var (yarpRoutes, clusters) = ProxyConfigMapper.Map(_routes, tls ? Status.Port : port, _httpsPort);

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ProxyServer).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(loggerFactory);
        if (tls)
            builder.WebHost.UseKestrelHttpsConfiguration();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
            {
                kestrel.Listen(address, port, listen =>
                {
                    if (!tls)
                        return;
                    listen.Protocols = HttpProtocols.Http1AndHttp2;
                    listen.UseHttps(new HttpsConnectionAdapterOptions
                    {
                        // No certificate means no handshake: unknown names and routes without HTTPS fail at TLS.
                        ServerCertificateSelector = (_, serverName) => CertificateFor(serverName),
                    });
                });
            }
            kestrel.Limits.MaxRequestBodySize = null;
            kestrel.AddServerHeader = false;
        });
        builder.Services.AddReverseProxy().LoadFromMemory(yarpRoutes, clusters);
        builder.Services.AddSingleton<IForwarderHttpClientFactory, ProxyHttpClientFactory>();

        var app = builder.Build();
        if (requests is not null)
            app.Use(requests.RecordAsync);
        if (!tls)
            app.Use(RedirectToHttpsAsync);
        app.MapReverseProxy(pipeline => pipeline.Use(WriteErrorPageAsync));
        app.MapFallback(context => WriteNotFoundAsync(context, tls, port));

        try
        {
            await app.StartAsync(cancellationToken);
        }
        catch (IOException ex)
        {
            await app.DisposeAsync();
            SetStatus(tls, DescribeBindFailure(port, ex));
            return null;
        }

        _logger.LogInformation("Proxy listening on loopback port {Port} ({Scheme})", port, tls ? "https" : "http");
        SetStatus(tls, new ProxyStatus(ProxyState.Running, port));
        return new Listener(app, app.Services.GetRequiredService<InMemoryConfigProvider>(), port);
    }

    /// <summary>
    /// 307, never 301, and no HSTS: browsers remember both, and they get in the way the day HTTPS
    /// is turned off for the route.
    /// </summary>
    private Task RedirectToHttpsAsync(HttpContext context, Func<Task> next)
    {
        var host = context.Request.Host.Host;
        if (_https is not { } https || FindRoute(host) is not { Https: true, RedirectToHttps: true } || CertificateFor(host) is null)
            return next();

        var port = https.Port == 443 ? "" : $":{https.Port}";
        var request = context.Request;
        RequestLog.MarkFromProxy(context);
        context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
        context.Response.Headers.Location = $"https://{host}{port}{request.PathBase}{request.Path}{request.QueryString}";
        return Task.CompletedTask;
    }

    private static async Task StopAsync(Listener? listener)
    {
        if (listener is null)
            return;
        await listener.App.StopAsync();
        await listener.App.DisposeAsync();
    }

    private ProxyStatus DescribeBindFailure(int port, IOException ex)
    {
        _logger.LogWarning(ex, "Could not listen on port {Port}", port);

        var owner = PortOwner.Find(port);
        if (owner is not null || ex.InnerException is AddressInUseException)
            return new ProxyStatus(ProxyState.PortInUse, port,
                owner is null ? $"A porta {port} está em uso." : $"A porta {port} está em uso por {owner.Describe()}.", owner);

        if (ex.InnerException is SocketException { SocketErrorCode: SocketError.AccessDenied })
            return new ProxyStatus(ProxyState.Failed, port,
                $"O Windows reservou a porta {port} (costuma ser o Hyper-V ou o WinNAT). Escolha outra porta.");

        return new ProxyStatus(ProxyState.Failed, port, ex.Message);
    }

    private static async Task WriteErrorPageAsync(HttpContext context, Func<Task> next)
    {
        await next();

        var error = context.GetForwarderErrorFeature();
        if (error is null || context.Response.HasStarted)
            return;

        var metadata = context.GetReverseProxyFeature().Route.Config.Metadata!;
        var (domain, target) = (metadata[ProxyConfigMapper.DomainKey], metadata[ProxyConfigMapper.TargetKey]);
        var timedOut = error.Error == ForwarderError.RequestTimedOut;

        RequestLog.MarkFromProxy(context);
        context.Response.StatusCode = timedOut ? StatusCodes.Status504GatewayTimeout : StatusCodes.Status502BadGateway;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(timedOut
            ? ErrorPages.GatewayTimeout(domain, target)
            : ErrorPages.BadGateway(domain, target));
    }

    private Task WriteNotFoundAsync(HttpContext context, bool tls, int port)
    {
        RequestLog.MarkFromProxy(context);
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "text/html; charset=utf-8";
        var domains = RouteRules.ActiveDomains(_routes);
        var host = context.Request.Host.Host;
        var disabled = RouteRules.Normalize(host) is { } normalized
            && _routes.Any(r => !r.Enabled && RouteRules.Normalize(r.Domain) == normalized);
        return context.Response.WriteAsync(ErrorPages.NotFound(host, domains, tls ? "https" : "http", port, disabled));
    }

    private void SetStatus(bool tls, ProxyStatus status)
    {
        if (tls)
            HttpsStatus = status;
        else
            Status = status;
        StatusChanged?.Invoke(this, status);
    }

    private sealed record Listener(WebApplication App, InMemoryConfigProvider Config, int Port);
}
