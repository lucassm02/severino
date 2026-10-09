using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
/// Kestrel + YARP on loopback only. Routes change in place; a port change restarts the listener.
/// </summary>
public sealed class ProxyServer(ILoggerFactory loggerFactory) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _logger = loggerFactory.CreateLogger<ProxyServer>();
    private WebApplication? _app;
    private InMemoryConfigProvider? _config;
    private IReadOnlyList<RouteEntry> _routes = [];

    public ProxyStatus Status { get; private set; } = new(ProxyState.Stopped, 0);

    /// <summary>Raised on the caller's thread of Start/Restart/Stop.</summary>
    public event EventHandler<ProxyStatus>? StatusChanged;

    /// <summary>Starts listening on <paramref name="port"/>. A busy port is reported in <see cref="Status"/>, not thrown.</summary>
    public async Task StartAsync(int port, IReadOnlyList<RouteEntry> routes, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _routes = routes;
            await StopCoreAsync();
            await StartCoreAsync(port, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void UpdateRoutes(IReadOnlyList<RouteEntry> routes)
    {
        _routes = routes;
        if (_config is null)
            return;
        var (yarpRoutes, clusters) = ProxyConfigMapper.Map(routes, Status.Port);
        _config.Update(yarpRoutes, clusters);
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopCoreAsync();
            SetStatus(new ProxyStatus(ProxyState.Stopped, Status.Port));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _gate.Dispose();
    }

    private async Task StartCoreAsync(int port, CancellationToken cancellationToken)
    {
        var (yarpRoutes, clusters) = ProxyConfigMapper.Map(_routes, port);

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ProxyServer).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(loggerFactory);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, port);
            kestrel.Listen(IPAddress.IPv6Loopback, port);
            kestrel.Limits.MaxRequestBodySize = null;
            kestrel.AddServerHeader = false;
        });
        builder.Services.AddReverseProxy().LoadFromMemory(yarpRoutes, clusters);
        builder.Services.AddSingleton<IForwarderHttpClientFactory, ProxyHttpClientFactory>();

        var app = builder.Build();
        app.MapReverseProxy(pipeline => pipeline.Use(WriteErrorPageAsync));
        app.MapFallback(context => WriteNotFoundAsync(context, port));

        try
        {
            await app.StartAsync(cancellationToken);
        }
        catch (IOException ex)
        {
            await app.DisposeAsync();
            SetStatus(DescribeBindFailure(port, ex));
            return;
        }

        _app = app;
        _config = app.Services.GetRequiredService<InMemoryConfigProvider>();
        _logger.LogInformation("Proxy listening on loopback port {Port} with {Count} routes", port, yarpRoutes.Count);
        SetStatus(new ProxyStatus(ProxyState.Running, port));
    }

    private async Task StopCoreAsync()
    {
        if (_app is null)
            return;
        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
        _config = null;
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

        context.Response.StatusCode = timedOut ? StatusCodes.Status504GatewayTimeout : StatusCodes.Status502BadGateway;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(timedOut
            ? ErrorPages.GatewayTimeout(domain, target)
            : ErrorPages.BadGateway(domain, target));
    }

    private Task WriteNotFoundAsync(HttpContext context, int port)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "text/html; charset=utf-8";
        var domains = RouteRules.ActiveDomains(_routes);
        return context.Response.WriteAsync(ErrorPages.NotFound(context.Request.Host.Host, domains, port));
    }

    private void SetStatus(ProxyStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }
}
