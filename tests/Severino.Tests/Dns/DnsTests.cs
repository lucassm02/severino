using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Severino.Contracts;
using Severino.Core.Configuration;
using Severino.Core.Dns;
using Severino.Core.Helper;
using Severino.Core.Routes;

namespace Severino.Tests.Dns;

public sealed class DnsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "severino-tests", Guid.NewGuid().ToString("N"));
    private readonly string _hostsPath;
    private readonly ConfigService _config;
    private readonly ExternalHosts _external;
    private readonly ScriptedHelper _helper = new();
    private readonly DnsService _dns;

    public DnsTests()
    {
        Directory.CreateDirectory(_dir);
        _hostsPath = Path.Combine(_dir, "hosts");
        File.WriteAllText(_hostsPath, "# feito à mão\r\n10.0.0.8 sql.interno sql\r\n", Encoding.Latin1);
        _config = new ConfigService(new ConfigStore(Path.Combine(_dir, "config")));
        _config.Load();
        _external = new ExternalHosts(_hostsPath);
        _dns = new DnsService(_config, _external, _helper);
    }

    public void Dispose()
    {
        _external.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private static DnsEntry Entry(string address, params string[] names) => new() { Names = names, Address = address };

    [Fact]
    public void An_entry_is_saved_normalized()
    {
        var saved = _dns.Save(Entry(" 10.0.0.20 ", "API.Interno", "api"));

        Assert.Equal(["api.interno", "api"], saved.Names);
        Assert.Equal("10.0.0.20", saved.Address);
    }

    [Fact]
    public void Names_are_shared_by_entries_routes_services_and_outside_lines()
    {
        _config.Update(c => c with
        {
            Routes = [new RouteEntry { Domain = "callfred.sev", Target = "http://localhost:3000" }],
            Services = [new ServiceRoute { Names = ["redis"], Address = "127.77.0.2" }],
        });
        _dns.Save(Entry("10.0.0.20", "api.interno"));

        Assert.Equal("api.interno já está em outra entrada DNS.", _dns.Validate(Entry("10.0.0.21", "api.interno")).Error);
        Assert.Equal("callfred.sev já é uma rota.", _dns.Validate(Entry("10.0.0.21", "callfred.sev")).Error);
        Assert.Equal("redis já é um serviço.", _dns.Validate(Entry("10.0.0.21", "redis")).Error);
        Assert.StartsWith("sql.interno já está no hosts, fora do Severino.", _dns.Validate(Entry("10.0.0.21", "sql.interno")).Error);

        // And the other way round.
        var routes = new RouteService(_config, _external);
        Assert.Equal("Já existe uma entrada DNS com este nome.", routes.Validate(new RouteEntry { Domain = "api.interno", Target = "http://localhost:1" }).Domain);
        Assert.StartsWith("Este nome já está no hosts", routes.Validate(new RouteEntry { Domain = "sql.interno", Target = "http://localhost:1" }).Domain);
        var services = new ServiceRouteService(_config, _external);
        Assert.Equal("api.interno já é uma entrada DNS.", services.Validate(new ServiceRoute
        {
            Names = ["api.interno"], Address = "127.77.0.3", Ports = [new ServicePort { Port = 80, TargetHost = "10.0.0.1", TargetPort = 80 }],
        }).Error);
    }

    [Fact]
    public void Addresses_that_are_not_hosts_are_refused()
    {
        Assert.Equal("Informe um IPv4 ou IPv6 de um computador, como 10.0.0.8.", _dns.Validate(Entry("0.0.0.0", "x.interno")).Error);
        Assert.Equal("Informe um IPv4 ou IPv6 de um computador, como 10.0.0.8.", _dns.Validate(Entry("sql.interno", "x.interno")).Error);
    }

    [Fact]
    public void Used_by_finds_routes_and_services_that_point_at_the_names()
    {
        _config.Update(c => c with
        {
            Routes = [new RouteEntry { Domain = "api.callfred.sev", Target = "http://gateway.k8s:8080" }],
            Services = [new ServiceRoute { Names = ["postgres"], Address = "127.77.0.2", Ports = [new ServicePort { Port = 5432, TargetHost = "gateway.k8s", TargetPort = 30711 }] }],
        });

        var (routes, services) = DnsRules.UsedBy(["Gateway.K8s"], _config.Current);

        Assert.Equal("api.callfred.sev", Assert.Single(routes).Domain);
        Assert.Equal("postgres", Assert.Single(services).Names[0]);
    }

    [Fact]
    public async Task The_block_holds_enabled_entries_and_reports_pending_ones()
    {
        _dns.Save(Entry("10.0.0.20", "api.interno"));
        _dns.Save(Entry("203.0.113.10", "site.novo"));
        _dns.Save(Entry("10.0.0.30", "velho.interno") with { Enabled = false });
        _helper.Pending = [new HostEntry("site.novo", "203.0.113.10")];
        using var sync = new DnsSync(_config, _helper, NullLogger<DnsSync>.Instance);

        await sync.SyncAsync();

        var sent = _helper.Requests.Last();
        Assert.Equal(HelperProtocol.SyncDnsCommand, sent.Command);
        Assert.Equal(["api.interno", "site.novo"], sent.Entries!.Select(e => e.Name));
        Assert.Equal(HostsSyncState.Synced, sync.Status.State);
        Assert.Equal([new HostEntry("site.novo", "203.0.113.10")], sync.Status.PendingEntries);
    }

    [Fact]
    public void Outside_lines_are_read_from_the_file()
    {
        var line = Assert.Single(_external.Lines);

        Assert.Equal(["sql.interno", "sql"], line.Names);
        Assert.Equal("# feito à mão", line.Origin);
        Assert.Contains("sql", _external.Names);
    }

    [Fact]
    public async Task Editing_an_outside_line_sends_the_line_as_read()
    {
        var line = _external.Lines[0];

        var result = await _dns.EditOutsideAsync(line, ["sql.interno", "sql"], "10.0.0.9", CancellationToken.None);

        Assert.True(result.Ok);
        var sent = _helper.Requests.Last();
        Assert.Equal(HelperProtocol.EditLineCommand, sent.Command);
        Assert.Equal("10.0.0.8 sql.interno sql", sent.Line);
        Assert.All(sent.Entries!, e => Assert.Equal("10.0.0.9", e.Address));
    }

    [Fact]
    public async Task An_outside_line_cannot_take_a_name_of_severino()
    {
        _dns.Save(Entry("10.0.0.20", "api.interno"));

        var result = await _dns.EditOutsideAsync(_external.Lines[0], ["sql.interno", "api.interno"], "10.0.0.8", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.StartsWith("api.interno já é do Severino", result.Error);
        Assert.Empty(_helper.Requests);
    }

    private sealed class ScriptedHelper : IHelperClient
    {
        public List<HelperRequest> Requests { get; } = [];
        public IReadOnlyList<HostEntry>? Pending { get; set; }

        public Task<HelperResponse> SendAsync(HelperRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(HelperResponse.Success("test", Pending));
        }
    }
}
