using Microsoft.Extensions.Logging.Abstractions;
using Severino.Contracts;
using Severino.Core.Configuration;
using Severino.Core.Helper;
using Severino.Core.Routes;

namespace Severino.Tests.Routes;

public sealed class HostsSyncTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "severino-tests", Guid.NewGuid().ToString("N"));
    private readonly ConfigService _config;
    private readonly FakeHelper _helper = new();
    private readonly HostsSync _sync;

    public HostsSyncTests()
    {
        _config = new ConfigService(new ConfigStore(_dir));
        _config.Load();
        _sync = new HostsSync(_config, _helper, NullLogger<HostsSync>.Instance);
    }

    public void Dispose()
    {
        _sync.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private void SetRoutes(params RouteEntry[] routes) => _config.Update(c => c with { Routes = routes });

    private static RouteEntry Route(string domain, bool enabled = true) =>
        new() { Domain = domain, Target = "http://127.0.0.1:3000", Enabled = enabled };

    [Fact]
    public async Task Sends_enabled_domains_and_reports_synced()
    {
        SetRoutes(Route("b.sev"), Route("a.sev"), Route("off.sev", enabled: false));

        await _sync.SyncAsync();

        Assert.Equal(["a.sev", "b.sev"], _helper.Requests.Single().Domains);
        Assert.Equal(HostsSyncState.Synced, _sync.Status.State);
    }

    [Fact]
    public async Task Only_pings_when_nothing_changed()
    {
        SetRoutes(Route("a.sev"));
        await _sync.SyncAsync();

        await _sync.SyncAsync();

        Assert.Equal([HelperProtocol.SyncCommand, HelperProtocol.PingCommand], _helper.Requests.Select(r => r.Command));
    }

    [Fact]
    public async Task Notices_the_helper_stopping_and_resyncs_when_it_returns()
    {
        SetRoutes(Route("a.sev"));
        await _sync.SyncAsync();

        _helper.Unavailable = true;
        await _sync.SyncAsync();
        Assert.Equal(HostsSyncState.HelperUnavailable, _sync.Status.State);

        _helper.Unavailable = false;
        await _sync.SyncAsync();

        Assert.Equal(HostsSyncState.Synced, _sync.Status.State);
        Assert.Equal(HelperProtocol.SyncCommand, _helper.Requests[^1].Command);
        Assert.Equal(["a.sev"], _helper.Requests[^1].Domains);
    }

    [Fact]
    public async Task Reports_unavailable_helper_and_retries_on_next_sync()
    {
        SetRoutes(Route("a.sev"));
        _helper.Unavailable = true;
        await _sync.SyncAsync();
        Assert.Equal(HostsSyncState.HelperUnavailable, _sync.Status.State);

        _helper.Unavailable = false;
        await _sync.SyncAsync();

        Assert.Equal(HostsSyncState.Synced, _sync.Status.State);
    }

    [Fact]
    public async Task Reports_helper_errors()
    {
        _helper.Error = "sem permissão";

        await _sync.SyncAsync();

        Assert.Equal(new HostsSyncStatus(HostsSyncState.Failed, "sem permissão"), _sync.Status);
    }

    [Fact]
    public async Task Rejects_other_protocol_versions()
    {
        _helper.ProtocolVersion = HelperProtocol.Version + 1;

        await _sync.SyncAsync();

        Assert.Equal(HostsSyncState.Failed, _sync.Status.State);
    }

    [Fact]
    public async Task Clear_sends_empty_list_and_stops_syncing()
    {
        SetRoutes(Route("a.sev"));
        await _sync.SyncAsync();

        await _sync.ClearAsync(CancellationToken.None);
        SetRoutes(Route("b.sev"));
        await _sync.SyncAsync();

        Assert.Equal(2, _helper.Requests.Count);
        Assert.Empty(_helper.Requests[^1].Domains!);
    }

    [Fact]
    public async Task Config_change_triggers_debounced_sync()
    {
        _sync.Start();
        await WaitUntil(() => _helper.Requests.Count == 1);

        SetRoutes(Route("a.sev"));
        SetRoutes(Route("a.sev"), Route("b.sev"));

        await WaitUntil(() => _helper.Requests.Count == 2);
        Assert.Equal(["a.sev", "b.sev"], _helper.Requests[^1].Domains);
    }

    [Fact]
    public async Task Pause_empties_the_block_until_resumed()
    {
        SetRoutes(Route("a.sev"));
        _sync.Start();
        await WaitUntil(() => _helper.Requests.Count == 1);

        await _sync.PauseAsync(CancellationToken.None);
        SetRoutes(Route("a.sev"), Route("b.sev"));
        await _sync.SyncAsync();
        Assert.Equal(2, _helper.Requests.Count);
        Assert.Empty(_helper.Requests[^1].Domains!);

        _sync.Resume();
        await WaitUntil(() => _helper.Requests.Count == 3);
        Assert.Equal(["a.sev", "b.sev"], _helper.Requests[^1].Domains);
    }

    [Fact]
    public async Task Clear_after_pause_stays_cleared()
    {
        SetRoutes(Route("a.sev"));
        await _sync.PauseAsync(CancellationToken.None);
        await _sync.ClearAsync(CancellationToken.None);

        _sync.Resume();
        await _sync.SyncAsync();

        Assert.All(_helper.Requests, r => Assert.Empty(r.Domains!));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 50 && !condition(); i++)
            await Task.Delay(50);
        Assert.True(condition());
    }
}
