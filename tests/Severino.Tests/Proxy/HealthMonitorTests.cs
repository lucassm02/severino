using System.Net;
using System.Net.Sockets;
using Severino.Core.Configuration;
using Severino.Proxy;

namespace Severino.Tests.Proxy;

public sealed class HealthMonitorTests
{
    [Fact]
    public async Task Reports_target_going_up_and_down()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var route = new RouteEntry { Domain = "a.sev", Target = $"http://127.0.0.1:{port}" };

        await using var monitor = new HealthMonitor(TimeSpan.FromMilliseconds(100));
        var changes = new List<RouteHealth>();
        monitor.Changed += (_, h) => { lock (changes) changes.Add(h); };
        monitor.Update([route]);
        monitor.Start();

        await WaitUntil(() => monitor.IsUp(route.Id) == true);
        listener.Stop();
        await WaitUntil(() => monitor.IsUp(route.Id) == false);

        lock (changes)
            Assert.Equal([new RouteHealth(route.Id, true), new RouteHealth(route.Id, false)], changes);
    }

    [Fact]
    public async Task Localhost_target_listening_on_ipv6_only_is_up()
    {
        var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
        listener.Start();
        try
        {
            var route = new RouteEntry { Domain = "a.sev", Target = $"http://localhost:{((IPEndPoint)listener.LocalEndpoint).Port}" };
            await using var monitor = new HealthMonitor(TimeSpan.FromMilliseconds(100));
            monitor.Update([route]);

            await WaitUntil(() => monitor.IsUp(route.Id) == true);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Localhost_target_listening_on_ipv4_only_is_up_within_the_timeout()
    {
        // "localhost" resolves to ::1 first; a refused ::1 must not eat the 1 s budget.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            Assert.True(await HealthMonitor.CanConnectAsync("localhost", port, CancellationToken.None));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Disabled_routes_are_not_checked()
    {
        var route = new RouteEntry { Domain = "a.sev", Target = "http://127.0.0.1:1", Enabled = false };
        await using var monitor = new HealthMonitor(TimeSpan.FromMilliseconds(50));
        monitor.Update([route]);
        monitor.Start();

        await Task.Delay(300);

        Assert.Null(monitor.IsUp(route.Id));
    }

    [Fact]
    public async Task Disposing_twice_is_harmless()
    {
        var monitor = new HealthMonitor();
        monitor.Start();

        await monitor.DisposeAsync();
        await monitor.DisposeAsync();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 60 && !condition(); i++)
            await Task.Delay(50);
        Assert.True(condition());
    }
}
