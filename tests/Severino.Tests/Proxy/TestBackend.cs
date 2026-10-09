using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Severino.Tests.Proxy;

/// <summary>A fake dev server on a random loopback port: echoes headers, counts bodies, echoes WebSocket messages.</summary>
internal static class TestBackend
{
    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static int PortOf(WebApplication app) =>
        new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;

    public static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Loopback, 0);
            k.Limits.MaxRequestBodySize = null;
        });
        var app = builder.Build();
        app.UseWebSockets();

        app.MapGet("/echo", (HttpRequest r) => string.Join('\n',
            $"host={r.Host}",
            $"x-forwarded-host={r.Headers["X-Forwarded-Host"]}",
            $"x-forwarded-proto={r.Headers["X-Forwarded-Proto"]}",
            $"x-forwarded-for={r.Headers["X-Forwarded-For"]}"));

        app.MapPost("/size", async (HttpRequest r) =>
        {
            long total = 0;
            var buffer = new byte[81920];
            int read;
            while ((read = await r.Body.ReadAsync(buffer)) > 0)
                total += read;
            return total.ToString();
        });

        app.Map("/ws", async (HttpContext context) =>
        {
            using var ws = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[64];
            var received = await ws.ReceiveAsync(buffer, CancellationToken.None);
            var reply = Encoding.UTF8.GetBytes("eco: " + Encoding.UTF8.GetString(buffer, 0, received.Count));
            await ws.SendAsync(reply, WebSocketMessageType.Text, true, CancellationToken.None);
            await ws.ReceiveAsync(buffer, CancellationToken.None);
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        });

        await app.StartAsync();
        return app;
    }
}
