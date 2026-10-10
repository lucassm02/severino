using Microsoft.Extensions.Logging.Abstractions;
using Severino.App.Services;
using Severino.App.ViewModels;
using Severino.Core.Certificates;
using Severino.Core.Configuration;
using Severino.Core.Domains;
using Severino.Core.Routes;
using Severino.Proxy.Certificates;
using Severino.Tests.Certificates;
using Severino.Tests.Routes;

namespace Severino.Tests.App;

public sealed class RouteEditorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "severino-tests", Guid.NewGuid().ToString("N"));
    private readonly ConfigService _config;
    private readonly RouteService _routes;
    private readonly HttpsService _https;

    public RouteEditorTests()
    {
        _config = new ConfigService(new ConfigStore(Path.Combine(_dir, "config")));
        _config.Load();
        _routes = new RouteService(_config);
        var ca = new LocalCa(new CaStore(Path.Combine(_dir, "ca")), new FakeTrustStore(), TimeProvider.System, NullLogger<LocalCa>.Instance);
        _https = new HttpsService(_config, ca, new TldDirectory(_config, new FakeDns()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private RouteEditorViewModel Open(RouteEntry? existing = null) =>
        new(_routes, new DomainInspector(new FakeDns()), _https, new Navigation(), existing, isCopy: false);

    [Fact]
    public async Task The_path_is_typed_in_the_address()
    {
        var form = Open();
        form.Domain = "https://callfred.sev/api/";
        form.TargetPort = "8080";
        form.StripPath = true;

        Assert.True(form.HasPath);
        Assert.Equal("/api", form.Path);
        await form.SaveCommand.ExecuteAsync(null);

        var route = Assert.Single(_config.Current.Routes);
        Assert.Equal(("callfred.sev", "/api", true), (route.Domain, route.Path, route.StripPath));
        Assert.Equal("callfred.sev/api", Open(route).Domain);
    }

    [Fact]
    public async Task Without_a_path_nothing_is_stripped()
    {
        var form = Open();
        form.Domain = "callfred.sev";
        form.StripPath = true;

        Assert.False(form.HasPath);
        await form.SaveCommand.ExecuteAsync(null);

        Assert.False(Assert.Single(_config.Current.Routes).StripPath);
    }

    [Fact]
    public void A_database_port_suggests_a_service()
    {
        var form = Open();
        form.Domain = "banco.sev";
        form.TargetPort = "5432";

        Assert.Equal("Criar como serviço", form.KindSwitchLabel);
        Assert.Contains("PostgreSQL", form.KindHint);
        form.SwitchKindCommand.Execute(null);
        Assert.Equal(new NameKindSwitch(NameKind.Service, "banco.sev", "localhost", 5432), form.SwitchRequested);
    }

    [Fact]
    public void A_network_ip_on_port_80_suggests_a_dns_entry()
    {
        var form = Open();
        form.Domain = "sql.interno";
        form.TargetHost = "10.0.0.8";
        form.TargetPort = "80";

        Assert.Equal("Criar entrada DNS", form.KindSwitchLabel);
        form.SwitchKindCommand.Execute(null);
        Assert.Equal(new NameKindSwitch(NameKind.Dns, "sql.interno", "10.0.0.8", 80), form.SwitchRequested);

        form.TargetPort = "8080";
        Assert.Null(form.KindHint);
        form.TargetHost = "localhost";
        form.TargetPort = "80";
        Assert.Null(form.KindHint);
    }

    [Fact]
    public void Editing_a_route_suggests_nothing()
    {
        var form = Open(_routes.Save(new RouteEntry { Domain = "banco.sev", Target = "http://localhost:5432" }));

        Assert.Null(form.KindHint);
    }

    [Theory]
    [InlineData(5432, "PostgreSQL")]
    [InlineData(6379, "Redis")]
    [InlineData(3000, null)]
    [InlineData(8080, null)]
    public void Known_ports_that_do_not_speak_http(int port, string? program) =>
        Assert.Equal(program, KnownPorts.NonHttpProgram(port));

    /// <summary>Names never resolve, and only "sev" is not a TLD on the internet.</summary>
    private sealed class FakeDns : IDnsResolver
    {
        public Task<DnsLookupResult> LookupAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult(new DnsLookupResult(DnsLookupOutcome.NotFound));

        public Task<bool?> TldExistsAsync(string tld, CancellationToken cancellationToken) =>
            Task.FromResult<bool?>(tld != "sev");
    }
}
