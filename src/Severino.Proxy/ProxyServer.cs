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
using Severino.Proxy.Resources;
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
/// <param name="sessionKey">Signs the login cookies of routes with a password; one of this run when null, which ends the sessions at the next start.</param>
public sealed class ProxyServer(ILoggerFactory loggerFactory, Func<string, X509Certificate2?>? certificates = null, RequestLog? requests = null, byte[]? sessionKey = null)
    : IAsyncDisposable
{
    private readonly RouteAccessGate _gate = new(sessionKey ?? System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), TimeProvider.System);

    /// <summary>The routes that ask for a password or a header, by YARP route id.</summary>
    private IReadOnlyDictionary<string, AccessRule> _access = new Dictionary<string, AccessRule>();

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger _logger = loggerFactory.CreateLogger<ProxyServer>();
    private Listener? _http;
    private Listener? _https;
    private IReadOnlyList<RouteEntry> _routes = [];

    /// <summary>Services whose HTTP ports the forwarder hands to this proxy (see ServiceForwarder).</summary>
    private IReadOnlyList<ServiceRoute> _services = [];

    /// <summary>CORS, delay and failure of the routes that have them, by YARP route id.</summary>
    private IReadOnlyDictionary<string, TrafficRules> _rules = new Dictionary<string, TrafficRules>();
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

    /// <summary>As the other, with the services whose HTTP ports come through the proxy.</summary>
    public Task StartAsync(int httpPort, int? httpsPort, IReadOnlyList<RouteEntry> routes, IReadOnlyList<ServiceRoute> services, CancellationToken cancellationToken = default)
    {
        _services = services;
        return StartAsync(httpPort, httpsPort, routes, cancellationToken);
    }

    /// <summary>
    /// Listens on <paramref name="httpPort"/>, and on <paramref name="httpsPort"/> when given.
    /// A listener already running on the same port is kept; one not running is retried. Busy
    /// ports are reported in the statuses, not thrown.
    /// </summary>
    public async Task StartAsync(int httpPort, int? httpsPort, IReadOnlyList<RouteEntry> routes, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
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
            _lock.Release();
        }
    }

    /// <param name="services">The services whose HTTP ports go through the proxy; null keeps the last ones.</param>
    public void UpdateRoutes(IReadOnlyList<RouteEntry> routes, IReadOnlyList<ServiceRoute>? services = null)
    {
        _routes = routes;
        _services = services ?? _services;
        _rules = RulesOf(routes, _services);
        _access = AccessOf(routes);
        _http?.Folders.Clear();
        _https?.Folders.Clear();
        var (yarpRoutes, clusters) = ProxyConfigMapper.Map(routes, Status.Port, _httpsPort, _services);
        _http?.Config.Update(yarpRoutes, clusters);
        _https?.Config.Update(yarpRoutes, clusters);
    }

    public async Task StopAsync()
    {
        await _lock.WaitAsync();
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
            _lock.Release();
        }
    }

    /// <summary>Safe to call more than once.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        await StopAsync();
        _disposed = true;
        _lock.Dispose();
    }

    /// <summary>The certificate for an SNI name: only routes and services with HTTPS on, and only names the CA covers.</summary>
    internal X509Certificate2? CertificateFor(string? serverName)
    {
        // One-label names are services' (catalogoapi), which routes never have.
        if (certificates is null || serverName is null || !Severino.Contracts.DomainName.TryNormalize(serverName, out var name, out _, allowSingleLabel: true))
            return null;
        // HTTPS is the domain's: any of its routes (a path, or the whole of it) asking for it.
        // A wildcard route gets a leaf for the name actually asked, which the CA covers too.
        var https = RouteRules.ForHost(_routes, serverName).Any(r => r.Https)
            || _services.Any(s => s.Enabled && s.Https && ServiceHttp.HasHttp(s) && s.Names.Contains(name));
        return https ? certificates(name) : null;
    }


    private async Task<Listener?> StartListenerAsync(int port, bool tls, CancellationToken cancellationToken)
    {
        var (yarpRoutes, clusters) = ProxyConfigMapper.Map(_routes, tls ? Status.Port : port, _httpsPort, _services);

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
        var folders = new FolderSites(app);
        app.Use((context, next) => ServeFolderAsync(context, next, folders));
        app.MapReverseProxy(pipeline =>
        {
            pipeline.Use(GateAsync);
            pipeline.Use((context, next) => RouteTraffic.ApplyAsync(context, next, _rules));
            pipeline.Use(WriteErrorPageAsync);
        });
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
        return new Listener(app, app.Services.GetRequiredService<InMemoryConfigProvider>(), port, folders);
    }

    /// <summary>
    /// 307, never 301, and no HSTS: browsers remember both, and they get in the way the day HTTPS
    /// is turned off for the route.
    /// </summary>
    private Task RedirectToHttpsAsync(HttpContext context, Func<Task> next)
    {
        var host = context.Request.Host.Host;
        if (_https is not { } https || RouteRules.Find(_routes, host, context.Request.Path.Value) is not { Https: true, RedirectToHttps: true } || CertificateFor(host) is null)
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
        listener.Folders.Dispose();
    }

    private ProxyStatus DescribeBindFailure(int port, IOException ex)
    {
        _logger.LogWarning(ex, "Could not listen on port {Port}", port);

        var owner = PortOwner.Find(port);
        if (owner is not null || ex.InnerException is AddressInUseException)
            return new ProxyStatus(ProxyState.PortInUse, port,
                owner is null ? string.Format(Strings.Proxy_PortInUse, port) : string.Format(Strings.Proxy_PortInUseBy, port, owner.Describe()), owner);

        if (ex.InnerException is SocketException { SocketErrorCode: SocketError.AccessDenied })
            return new ProxyStatus(ProxyState.Failed, port,
                string.Format(Strings.Proxy_PortReserved, port));

        return new ProxyStatus(ProxyState.Failed, port, ex.Message);
    }

    /// <summary>
    /// A route whose target is a folder: answered here, before YARP, with the route's rules
    /// (all but the request headers, which have no server to go to).
    /// </summary>
    private async Task ServeFolderAsync(HttpContext context, Func<Task> next, FolderSites folders)
    {
        var route = RouteRules.Find(_routes, context.Request.Host.Host, context.Request.Path.Value);
        if (route is null || RouteRules.FolderOf(route.Target) is not { } folder)
        {
            await next();
            return;
        }

        var domain = RouteRules.Normalize(route.Domain) ?? context.Request.Host.Host;
        var prefix = RouteRules.NormalizePath(route.Path) ?? "";
        RequestLog.MarkFolder(context, route.Id, folder);
        if (_access.TryGetValue(route.Id.ToString("N"), out var access) && !await _gate.AllowAsync(context, access, context.Request.Host.Host))
            return;
        RouteTraffic.SetResponseHeaders(context, route.Rules.ResponseHeaders);
        await RouteTraffic.ApplyAsync(context, () =>
        {
            // The static file middlewares only serve requests no endpoint took, and the 404
            // fallback takes every path without a dot: this request is the folder's.
            context.SetEndpoint(null);
            if (Directory.Exists(folder))
                return folders.For(route.Id, folder, prefix, route.SpaFallback, domain)(context);
            RequestLog.MarkFromProxy(context);
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            context.Response.ContentType = "text/html; charset=utf-8";
            return context.Response.WriteAsync(ErrorPages.FolderMissing(domain, folder));
        }, route.Rules, domain);
    }

    /// <summary>Before the rules and the forwarding: the route's password or header, when it asks for one.</summary>
    private async Task GateAsync(HttpContext context, Func<Task> next)
    {
        var route = context.GetReverseProxyFeature().Route.Config;
        if (_access.TryGetValue(route.RouteId, out var access) && !await _gate.AllowAsync(context, access, context.Request.Host.Host))
            return;
        await next();
    }

    private static Dictionary<string, AccessRule> AccessOf(IReadOnlyList<RouteEntry> routes) =>
        routes.Where(r => r.Enabled && r.Access.Mode != AccessMode.Open)
            .ToDictionary(r => r.Id.ToString("N"), r => new AccessRule(r.Id, r.Access, RouteRules.NormalizePath(r.Path) ?? "", r.Rules.Cors));

    private static Dictionary<string, TrafficRules> RulesOf(IReadOnlyList<RouteEntry> routes, IReadOnlyList<ServiceRoute> services)
    {
        var rules = routes.Where(r => r.Enabled && Applies(r.Rules)).ToDictionary(r => r.Id.ToString("N"), r => r.Rules);
        foreach (var service in services.Where(s => s.Enabled && Applies(s.Rules)))
        {
            foreach (var port in service.Ports.Where(p => p.Http))
                rules[ProxyConfigMapper.ServiceRouteId(service, port.Port)] = service.Rules;
        }
        return rules;

        static bool Applies(TrafficRules rules) => rules.Cors || rules.DelayMs > 0 || rules.FailPercent > 0;
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

    private sealed record Listener(WebApplication App, InMemoryConfigProvider Config, int Port, FolderSites Folders);
}
