using Microsoft.Extensions.Logging.Abstractions;
using Severino.App.Services;
using Severino.Core.Configuration;
using Severino.Core.Discovery;
using Severino.Core.Wsl;
using Severino.Proxy.Certificates;
using Severino.Tests.Certificates;

namespace Severino.Tests.App;

public sealed class WslTrustTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "severino-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeWslShell _shell = new();
    private readonly FakeWslRunner _runner = new();
    private ConfigService _config = null!;
    private LocalCa _ca = null!;
    private WslTrust _trust = null!;

    public Task InitializeAsync()
    {
        _config = new ConfigService(new ConfigStore(Path.Combine(_dir, "config")));
        _config.Load();
        _config.Update(c => c with { Settings = c.Settings with { WslDistros = ["Ubuntu", "Debian"], WslTrustCa = true } });
        _ca = new LocalCa(new CaStore(Path.Combine(_dir, "ca")), new FakeTrustStore(), TimeProvider.System, NullLogger<LocalCa>.Instance);
        _ca.Activate(["sev"]);
        _runner.Running.Add("Ubuntu");
        _trust = new WslTrust(_config, _ca, _shell, new ServiceDiscovery(_runner), NullLogger<WslTrust>.Instance);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _trust.DisposeAsync();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void The_install_script_carries_only_the_public_certificate()
    {
        var pem = _ca.ExportPem()!;
        var script = WslCaScript.Install(pem);

        Assert.Contains(pem.Trim().Split('\n')[1], script);
        Assert.Contains("update-ca-certificates", script);
        Assert.Contains("NODE_EXTRA_CA_CERTS", script);
        Assert.DoesNotContain("\r", script);
        Assert.Throws<ArgumentException>(() => WslCaScript.Install("-----BEGIN PRIVATE KEY-----\nabc\n-----END PRIVATE KEY-----"));
    }

    [Fact]
    public async Task Running_chosen_distros_get_the_ca_once_and_are_remembered()
    {
        await _trust.SyncAsync(CancellationToken.None);
        await _trust.SyncAsync(CancellationToken.None);

        var run = Assert.Single(_shell.Runs);
        Assert.Equal("Ubuntu", run.Distro);
        Assert.Contains("severino.crt", run.Script);
        Assert.Equal(["Ubuntu"], _config.Current.State.WslTrustDistros);
        Assert.Equal(
            [new WslTrustStatus("Ubuntu", WslTrustState.Installed), new WslTrustStatus("Debian", WslTrustState.Stopped)],
            _trust.Statuses);
    }

    [Fact]
    public async Task Turning_it_off_takes_the_ca_out_of_the_distros_that_had_it()
    {
        await _trust.SyncAsync(CancellationToken.None);
        _config.Update(c => c with { Settings = c.Settings with { WslTrustCa = false } });

        await _trust.SyncAsync(CancellationToken.None);

        Assert.Equal(WslCaScript.Remove(), _shell.Runs[^1].Script);
        Assert.Empty(_config.Current.State.WslTrustDistros);
        Assert.Empty(_trust.Statuses);
    }

    [Fact]
    public async Task Removing_the_ca_takes_it_out_and_a_failure_is_shown()
    {
        await _trust.SyncAsync(CancellationToken.None);
        _ca.Remove();
        await _trust.SyncAsync(CancellationToken.None);
        Assert.Equal(WslCaScript.Remove(), _shell.Runs[^1].Script);

        _ca.Activate(["sev"]);
        _shell.Answer = new CommandResult(3, "", "The distro has neither update-ca-certificates nor update-ca-trust.", false);
        await _trust.SyncAsync(CancellationToken.None);

        var ubuntu = _trust.Statuses.First();
        Assert.Equal(WslTrustState.Failed, ubuntu.State);
        Assert.Contains("update-ca-trust", ubuntu.Detail);
        Assert.Empty(_config.Current.State.WslTrustDistros);
    }

    [Fact]
    public async Task Nothing_runs_for_a_distro_that_never_had_it()
    {
        _config.Update(c => c with { Settings = c.Settings with { WslTrustCa = false } });

        await _trust.SyncAsync(CancellationToken.None);

        Assert.Empty(_shell.Runs);
    }
}
