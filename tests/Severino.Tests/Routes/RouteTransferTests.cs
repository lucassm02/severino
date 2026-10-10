using Severino.App.ViewModels;
using Severino.Core.Configuration;
using Severino.Core.Routes;

namespace Severino.Tests.Routes;

public sealed class RouteTransferTests
{
    private static RouteEntry Route(string domain, string target = "http://localhost:3000", bool https = false) =>
        new() { Domain = domain, Target = target, Https = https, RedirectToHttps = https, Notes = "nota" };

    private static ImportResult Import(string json, params RouteEntry[] existing) =>
        RouteTransfer.Import(json, existing, httpPort: 80, httpsPort: 443);

    [Fact]
    public void Round_trip_keeps_everything_but_the_ids()
    {
        RouteEntry[] routes = [Route("a.sev", https: true), Route("b.sev", "https://localhost:5001") with { Enabled = false, IgnoreTargetCertErrors = true }];

        var result = Import(RouteTransfer.Export(routes));

        Assert.Empty(result.Skipped);
        Assert.Empty(result.Invalid);
        Assert.Equal(routes.Select(r => r with { Id = Guid.Empty }), result.Added.Select(r => r with { Id = Guid.Empty }));
        Assert.DoesNotContain(result.Added, r => routes.Any(o => o.Id == r.Id));
    }

    [Fact]
    public void Export_has_no_ids()
    {
        Assert.DoesNotContain("\"id\"", RouteTransfer.Export([Route("a.sev")]));
    }

    [Fact]
    public void Existing_domains_are_skipped_not_replaced()
    {
        var json = RouteTransfer.Export([Route("A.Sev", "http://localhost:9999"), Route("novo.sev"), Route("novo.sev")]);

        var result = Import(json, Route("a.sev"));

        Assert.Equal(["novo.sev"], result.Added.Select(r => r.Domain));
        Assert.Equal(["a.sev", "novo.sev"], result.Skipped);
    }

    [Fact]
    public void Invalid_routes_are_listed_with_the_reason()
    {
        var json = """{ "severino": 1, "routes": [ { "domain": "ruim domínio", "target": "http://localhost:3000" }, { "domain": "ok.sev", "target": "ftp://x" } ] }""";

        var result = Import(json);

        Assert.Empty(result.Added);
        Assert.Equal(["ruim domínio", "ok.sev"], result.Invalid.Select(i => i.Domain));
        Assert.All(result.Invalid, i => Assert.NotEmpty(i.Reason));
    }

    [Fact]
    public void Redirect_without_https_is_dropped()
    {
        var json = """{ "severino": 1, "routes": [ { "domain": "a.sev", "target": "http://localhost:3000", "redirectToHttps": true } ] }""";

        Assert.False(Import(json).Added.Single().RedirectToHttps);
    }

    [Theory]
    [InlineData("isto não é json")]
    [InlineData("""{ "version": 1, "routes": [] }""")]
    [InlineData("""{ "severino": 2, "routes": [] }""")]
    public void Foreign_or_newer_files_are_refused(string json)
    {
        Assert.Throws<InvalidRouteFileException>(() => Import(json));
    }

    [Fact]
    public void Summary_lists_added_skipped_and_invalid()
    {
        var text = SettingsViewModel.DescribeImport(new ImportResult([Route("a.sev")], ["b.sev"], [new("c", "Domínio inválido.")]));

        Assert.Equal("1 rota importada.\n\nJá existiam, e ficaram como estavam: b.sev.\n\nNão importadas:\n• c: Domínio inválido.", text);
    }
}
