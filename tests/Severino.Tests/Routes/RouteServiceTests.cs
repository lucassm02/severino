using Severino.Core.Configuration;
using Severino.Core.Routes;

namespace Severino.Tests.Routes;

public sealed class RouteServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "severino-tests", Guid.NewGuid().ToString("N"));
    private readonly ConfigService _config;
    private readonly RouteService _routes;

    public RouteServiceTests()
    {
        _config = new ConfigService(new ConfigStore(_dir));
        _config.Load();
        _routes = new RouteService(_config);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static RouteEntry Route(string domain) => new() { Domain = domain, Target = "http://127.0.0.1:3000" };

    [Fact]
    public void Save_adds_with_normalized_domain_and_persists()
    {
        var saved = _routes.Save(Route(" CallFred.SEV. ".Trim()));

        Assert.Equal("callfred.sev", saved.Domain);
        Assert.Equal("callfred.sev", new ConfigStore(_dir).Load().Config.Routes.Single().Domain);
    }

    [Fact]
    public void Save_replaces_route_with_same_id()
    {
        var saved = _routes.Save(Route("a.sev"));

        _routes.Save(saved with { Target = "http://127.0.0.1:4000" });

        Assert.Equal("http://127.0.0.1:4000", Assert.Single(_routes.Routes).Target);
    }

    [Fact]
    public void Save_rejects_invalid_route()
    {
        _routes.Save(Route("a.sev"));

        Assert.Throws<ArgumentException>(() => _routes.Save(Route("a.sev")));
        Assert.Single(_routes.Routes);
    }

    [Fact]
    public void Remove_and_restore_put_route_back_in_place()
    {
        _routes.Save(Route("a.sev"));
        var b = _routes.Save(Route("b.sev"));
        _routes.Save(Route("c.sev"));

        var removed = _routes.Remove(b.Id);
        Assert.Equal(["a.sev", "c.sev"], _routes.Routes.Select(r => r.Domain));

        Assert.True(_routes.Restore(removed!));
        Assert.Equal(["a.sev", "b.sev", "c.sev"], _routes.Routes.Select(r => r.Domain));
    }

    [Fact]
    public void Restore_fails_when_domain_was_taken_meanwhile()
    {
        var removed = _routes.Remove(_routes.Save(Route("a.sev")).Id)!;
        _routes.Save(Route("a.sev"));

        Assert.False(_routes.Restore(removed));
        Assert.Single(_routes.Routes);
    }

    [Fact]
    public void Remove_unknown_id_returns_null()
    {
        Assert.Null(_routes.Remove(Guid.NewGuid()));
    }

    [Fact]
    public void SetEnabled_toggles_route()
    {
        var saved = _routes.Save(Route("a.sev"));

        _routes.SetEnabled(saved.Id, false);

        Assert.False(Assert.Single(_routes.Routes).Enabled);
    }
}
