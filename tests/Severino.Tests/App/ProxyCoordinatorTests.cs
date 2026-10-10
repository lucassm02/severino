using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Severino.App.Services;
using Severino.Core.Configuration;
using Severino.Core.Routes;
using Severino.Proxy;
using Severino.Proxy.Certificates;
using Severino.Tests.Certificates;
using Severino.Tests.Proxy;
using Severino.Tests.Routes;

namespace Severino.Tests.App;

public sealed class ProxyCoordinatorTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "severino-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeHelper _helper = new();
    private ConfigService _config = null!;
    private ProxyServer _proxy = null!;
    private HostsSync _hosts = null!;
    private ProxyCoordinator _coordinator = null!;
    private int _port;
    private ServiceForwarder _services = null!;
    private readonly string _serviceAddress = $"127.77.{Random.Shared.Next(100, 250)}.250";
    private int _servicePort;

    public async Task InitializeAsync()
    {
        _config = new ConfigService(new ConfigStore(Path.Combine(_dir, "config")));
        _config.Load();
        _port = TestBackend.FreePort();
        _config.Update(c => c with
        {
            Settings = c.Settings with { HttpPort = _port },
            Routes = [new RouteEntry { Domain = "a.sev", Target = "http://127.0.0.1:1" }],
            Services =
            [
                new ServiceRoute
                {
                    Names = ["redis"],
                    Address = _serviceAddress,
                    Ports = [new ServicePort { Port = _servicePort = TestBackend.FreePort(), TargetHost = "127.0.0.1", TargetPort = 1 }],
                },
            ],
        });
        _proxy = new ProxyServer(NullLoggerFactory.Instance);
        _hosts = new HostsSync(_config, _helper, NullLogger<HostsSync>.Instance);
        var ca = new LocalCa(new CaStore(Path.Combine(_dir, "ca")), new FakeTrustStore(), TimeProvider.System, NullLogger<LocalCa>.Instance);
        _services = new ServiceForwarder(NullLoggerFactory.Instance);
        _coordinator = new ProxyCoordinator(_config, _proxy, new HealthMonitor(TimeSpan.FromHours(1)), _hosts, ca, _services);
        await _coordinator.StartAsync();
        await WaitUntil(() => _helper.Requests.Count > 0);
    }

    public async Task DisposeAsync()
    {
        await _coordinator.StopAsync();
        await _proxy.DisposeAsync();
        _hosts.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Pause_frees_the_port_and_empties_hosts_until_resumed()
    {
        Assert.Equal(ProxyState.Running, _proxy.Status.State);

        await _coordinator.PauseAsync();

        Assert.True(_coordinator.IsPaused);
        Assert.NotEqual(ProxyState.Running, _proxy.Status.State);
        Assert.Empty(_helper.Requests[^1].Domains!);
        var other = new TcpListener(IPAddress.Loopback, _port);
        other.Start(); // the port is free for another program
        other.Stop();

        // Changes while paused wait for the resume.
        _config.Update(c => c with { Routes = [.. c.Routes, new RouteEntry { Domain = "b.sev", Target = "http://127.0.0.1:1" }] });
        await Task.Delay(500);
        Assert.NotEqual(ProxyState.Running, _proxy.Status.State);
        Assert.Empty(_helper.Requests[^1].Domains!);

        await _coordinator.ResumeAsync();

        Assert.False(_coordinator.IsPaused);
        Assert.Equal(new ProxyStatus(ProxyState.Running, _port), _proxy.Status);
        await WaitUntil(() => _helper.Requests[^1].Domains is ["a.sev", "b.sev", "redis"]);
    }

    [Fact]
    public async Task Service_routes_listen_and_pause_with_the_proxy()
    {
        Assert.Equal(ProxyState.Running, Assert.Single(_services.Statuses).State);
        Assert.Contains(new Severino.Contracts.HostEntry("redis", _serviceAddress), _helper.Requests[^1].Entries!);

        await _coordinator.PauseAsync();
        Assert.Empty(_services.Statuses);
        var other = new TcpListener(IPAddress.Parse(_serviceAddress), _servicePort);
        other.Start(); // free while paused
        other.Stop();

        await _coordinator.ResumeAsync();
        Assert.Equal(ProxyState.Running, Assert.Single(_services.Statuses).State);
    }

    [Fact]
    public async Task Retry_does_not_wake_a_paused_proxy()
    {
        await _coordinator.PauseAsync();

        await _coordinator.RetryAsync();

        Assert.NotEqual(ProxyState.Running, _proxy.Status.State);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 60 && !condition(); i++)
            await Task.Delay(50);
        Assert.True(condition());
    }
}
