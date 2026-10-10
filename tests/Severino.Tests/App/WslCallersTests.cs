using Microsoft.Extensions.Logging.Abstractions;
using Severino.App.Services;
using Severino.Core.Configuration;
using Severino.Core.Discovery;
using Severino.Core.Wsl;

namespace Severino.Tests.App;

public sealed class WslCallersTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "severino-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeWslShell _shell = new();
    private readonly FakeWslRunner _runner = new();
    private ConfigService _config = null!;
    private WslCallers _wsl = null!;

    private static ServiceRoute Kube(string name, string address, string target = "192.168.203.100") => new()
    {
        Names = [name, $"{name}.loja"],
        Address = address,
        Ports = [new ServicePort { Port = 80, TargetHost = target, TargetPort = 30080 }],
        Origin = new ServiceOrigin { Kind = ServiceKind.Kubernetes, Source = "wsl:Ubuntu", Context = "k", Namespace = "loja", Name = name },
    };

    public Task InitializeAsync()
    {
        _config = new ConfigService(new ConfigStore(_dir));
        _config.Load();
        _config.Update(c => c with
        {
            Settings = c.Settings with { WslDistros = ["Ubuntu", "Debian"] },
            Services = [Kube("pedidos", "127.77.0.2")],
        });
        _runner.Running.Add("Ubuntu");
        _wsl = new WslCallers(_config, _shell, new ServiceDiscovery(_runner), NullLogger<WslCallers>.Instance);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _wsl.DisposeAsync();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Running_distros_get_the_script_once_and_stopped_ones_wait()
    {
        await _wsl.SyncAsync(CancellationToken.None);
        await _wsl.SyncAsync(CancellationToken.None);

        var run = Assert.Single(_shell.Runs);
        Assert.Equal("Ubuntu", run.Distro);
        Assert.Contains("127.77.0.2  pedidos.loja", run.Script);
        Assert.Contains("dnat '127.77.0.2' 80 '192.168.203.100' 30080", run.Script);
        Assert.Equal(
            [new WslCallerStatus("Ubuntu", WslCallerState.Applied, 1), new WslCallerStatus("Debian", WslCallerState.Stopped)],
            _wsl.Statuses);
    }

    [Fact]
    public async Task A_restarted_distro_gets_the_rules_again()
    {
        await _wsl.SyncAsync(CancellationToken.None);
        _runner.Running.Clear();
        await _wsl.SyncAsync(CancellationToken.None);
        _runner.Running.Add("Ubuntu");

        await _wsl.SyncAsync(CancellationToken.None);

        Assert.Equal(2, _shell.Runs.Count);
    }

    [Fact]
    public async Task Config_changes_are_written_and_an_unchosen_distro_is_cleaned()
    {
        await _wsl.SyncAsync(CancellationToken.None);

        _config.Update(c => c with { Services = [.. c.Services, Kube("api", "127.77.0.3")] });
        await _wsl.SyncAsync(CancellationToken.None);
        Assert.Contains("api.loja", _shell.Runs[^1].Script);

        _config.Update(c => c with { Settings = c.Settings with { WslDistros = ["Debian"] } });
        await _wsl.SyncAsync(CancellationToken.None);
        Assert.Equal(("Ubuntu", WslCallerScript.Remove()), _shell.Runs[^1]);
    }

    [Fact]
    public async Task A_failure_is_shown_and_not_retried_until_asked()
    {
        _shell.Answer = new CommandResult(3, "", "O iptables não está instalado na distro.\n", false);

        await _wsl.SyncAsync(CancellationToken.None);
        await _wsl.SyncAsync(CancellationToken.None);

        Assert.Single(_shell.Runs);
        Assert.Equal(new WslCallerStatus("Ubuntu", WslCallerState.Failed, 1, "O iptables não está instalado na distro."), _wsl.Statuses[0]);

        await _wsl.RetryAsync();
        Assert.Equal(2, _shell.Runs.Count);
    }
}

public sealed class WslCallerScriptTests
{
    private static ServiceRoute Route(string source, string target, string address = "127.77.0.2") => new()
    {
        Names = ["svc"],
        Address = address,
        Ports = [new ServicePort { Port = 80, TargetHost = target, TargetPort = 8080 }],
        Origin = source == "" ? null : new ServiceOrigin { Kind = ServiceKind.Docker, Source = source },
    };

    [Theory]
    [InlineData("wsl:Ubuntu", "127.0.0.1", true)] // Docker in the distro: its own loopback
    [InlineData("windows", "127.0.0.1", false)] // Docker on Windows: the distro cannot see it
    [InlineData("", "localhost", false)] // made by hand for a Windows app
    [InlineData("", "10.0.0.5", true)]
    [InlineData("windows", "192.168.0.10", true)]
    public void Which_routes_a_distro_can_reach(string source, string target, bool reachable)
    {
        Assert.Equal(reachable, WslCallerScript.Reachable(Route(source, target)));
    }

    [Fact]
    public void Nothing_to_bring_means_cleaning_up()
    {
        Assert.Equal(WslCallerScript.Remove(), WslCallerScript.Apply([Route("windows", "127.0.0.1")]));
        Assert.Equal(WslCallerScript.Remove(), WslCallerScript.Apply([Route("", "10.0.0.5") with { Enabled = false }]));
    }

    [Fact]
    public void The_script_holds_the_block_and_one_rule_per_port_with_lf_only()
    {
        var script = WslCallerScript.Apply([Route("", "10.0.0.5"), Route("", "db.local", "127.77.0.3")]);

        Assert.DoesNotContain('\r', script);
        Assert.Contains(WslCallerScript.StartMarker + "\n127.77.0.2  svc\n127.77.0.3  svc\n" + WslCallerScript.EndMarker + "\n", script);
        Assert.Contains("dnat '127.77.0.2' 80 '10.0.0.5' 8080\n", script);
        Assert.Contains("dnat '127.77.0.3' 80 'db.local' 8080\n", script);
        Assert.Contains("-j MASQUERADE", script);
        Assert.Contains("route_localnet=1", script);
    }
}
