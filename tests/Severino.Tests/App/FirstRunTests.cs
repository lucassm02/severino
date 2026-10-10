using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Severino.App.Services;
using Severino.App.ViewModels;
using Severino.Core.Certificates;
using Severino.Core.Configuration;
using Severino.Core.Domains;
using Severino.Core.Routes;
using Severino.Proxy;
using Severino.Proxy.Certificates;
using Severino.Tests.Certificates;
using Severino.Tests.Proxy;
using Severino.Tests.Routes;

namespace Severino.Tests.App;

/// <summary>The first-run checks against a real proxy, a fake Helper and a throwaway registry key.</summary>
public sealed class FirstRunTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "severino-tests", Guid.NewGuid().ToString("N"));
    private readonly string _registryRoot = TestRegistry.NewKey();
    private readonly FakeHelper _helper = new();
    private ConfigService _config = null!;
    private ProxyServer _proxy = null!;
    private HostsSync _hosts = null!;
    private SystemProxy _systemProxy = null!;
    private ProxyCoordinator _coordinator = null!;
    private HttpsService _https = null!;
    private FirstRunViewModel _wizard = null!;

    public async Task InitializeAsync()
    {
        _config = new ConfigService(new ConfigStore(Path.Combine(_dir, "config")));
        _config.Load();
        _config.Update(c => c with { Settings = c.Settings with { HttpPort = TestBackend.FreePort() } });
        var dns = new FakeDns();
        var tlds = new TldDirectory(_config, dns);
        var ca = new LocalCa(new CaStore(Path.Combine(_dir, "ca")), new FakeTrustStore(), TimeProvider.System, NullLogger<LocalCa>.Instance);
        _proxy = new ProxyServer(NullLoggerFactory.Instance);
        _hosts = new HostsSync(_config, _helper, NullLogger<HostsSync>.Instance);
        _systemProxy = new SystemProxy(_config, tlds, $@"{_registryRoot}\Internet Settings");
        _coordinator = new ProxyCoordinator(_config, _proxy, new HealthMonitor(TimeSpan.FromHours(1)), _hosts, ca, new ServiceForwarder(NullLoggerFactory.Instance));
        _https = new HttpsService(_config, ca, tlds);
        await _coordinator.StartAsync();
        await _hosts.SyncAsync();
        _wizard = NewWizard();
    }

    public async Task DisposeAsync()
    {
        await _coordinator.StopAsync();
        await _proxy.DisposeAsync();
        _hosts.Dispose();
        _systemProxy.Dispose();
        TestRegistry.Delete(_registryRoot);
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private FirstRunViewModel NewWizard() => new(_config, _proxy, _hosts, _systemProxy, _coordinator, _https,
        new RouteService(_config), new DomainInspector(new FakeDns()), new Navigation());

    [Fact]
    public void All_green_on_a_healthy_machine()
    {
        Assert.Equal(CheckState.Ok, _wizard.Helper.State);
        Assert.Equal(CheckState.Ok, _wizard.Port.State);
        Assert.Contains($":{_config.Current.Settings.HttpPort}", _wizard.Port.Detail); // not on 80: the URLs carry the port
        Assert.Equal(CheckState.Ok, _wizard.SystemProxyCheck.State);
        Assert.All(_wizard.Checks, c => Assert.Null(c.FixLabel));
    }

    [Fact]
    public async Task Helper_down_is_a_problem_with_a_retry_that_clears_it()
    {
        _helper.Unavailable = true;
        _config.Update(c => c with { Routes = [new RouteEntry { Domain = "a.sev", Target = "http://localhost:3000" }] });
        await _hosts.SyncAsync();
        _wizard.Evaluate();

        Assert.Equal(CheckState.Problem, _wizard.Helper.State);
        Assert.Equal("Tentar de novo", _wizard.Helper.FixLabel);

        _helper.Unavailable = false;
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)_wizard.Helper.FixCommand!).ExecuteAsync(null);
        _wizard.Evaluate();
        Assert.Equal(CheckState.Ok, _wizard.Helper.State);
    }

    [Fact]
    public async Task Busy_port_is_a_problem_naming_its_owner()
    {
        var busy = new TcpListener(IPAddress.Loopback, 0);
        busy.Start();
        try
        {
            var port = ((IPEndPoint)busy.LocalEndpoint).Port;
            await _proxy.StartAsync(port, []);
            _wizard.Evaluate();

            Assert.Equal(CheckState.Problem, _wizard.Port.State);
            Assert.Contains($"porta {port}", _wizard.Port.Detail);
            Assert.Equal("Tentar de novo", _wizard.Port.FixLabel);
        }
        finally
        {
            busy.Stop();
        }
    }

    [Fact]
    public async Task System_proxy_in_the_way_is_fixed_with_an_exception_for_sev()
    {
        using (var key = Registry.CurrentUser.CreateSubKey($@"{_registryRoot}\Internet Settings"))
        {
            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            key.SetValue("ProxyServer", "proxy.empresa:8080");
            key.SetValue("ProxyOverride", "<local>");
        }
        _wizard.Evaluate();

        Assert.Equal(CheckState.Problem, _wizard.SystemProxyCheck.State);
        Assert.Equal("Adicionar exceção para .sev", _wizard.SystemProxyCheck.FixLabel);

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)_wizard.SystemProxyCheck.FixCommand!).ExecuteAsync(null);

        Assert.Equal(CheckState.Ok, _wizard.SystemProxyCheck.State);
        Assert.Equal("<local>;*.sev", _systemProxy.Read().Override);
        Assert.Equal(["*.sev"], _config.Current.State.ProxyBypassAdded);
    }

    [Fact]
    public void Skipping_marks_it_done_and_suggests_a_sev_route()
    {
        var finished = false;
        _wizard.Finished += (_, _) => finished = true;

        Assert.Equal("meuapp.sev", _wizard.Route.Domain);
        Assert.False(_wizard.Route.ShowHttpsOptions);
        Assert.False(_wizard.UseHttps);

        _wizard.SkipCommand.Execute(null);

        Assert.True(finished);
        Assert.True(_config.Current.State.FirstRunCompleted);
    }

    /// <summary>"sev" does not exist on the internet, "com" does; names never resolve.</summary>
    private sealed class FakeDns : IDnsResolver
    {
        public Task<DnsLookupResult> LookupAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult(new DnsLookupResult(DnsLookupOutcome.NotFound));

        public Task<bool?> TldExistsAsync(string tld, CancellationToken cancellationToken) =>
            Task.FromResult<bool?>(tld != "sev");
    }
}
