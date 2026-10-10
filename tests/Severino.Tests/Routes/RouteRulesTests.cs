using Severino.Core.Configuration;
using Severino.Core.Routes;

namespace Severino.Tests.Routes;

public sealed class RouteRulesTests
{
    private static RouteEntry Route(string domain, string target = "http://127.0.0.1:3000", bool enabled = true) =>
        new() { Domain = domain, Target = target, Enabled = enabled };

    [Fact]
    public void Valid_route_has_no_errors()
    {
        Assert.True(RouteRules.Validate(Route("meuapp.sev"), [], 80).IsValid);
    }

    [Fact]
    public void Duplicate_domain_is_an_error_even_with_different_case()
    {
        var existing = Route("meuapp.sev");

        var errors = RouteRules.Validate(Route("MeuApp.sev"), [existing], 80);

        Assert.NotNull(errors.Domain);
    }

    [Fact]
    public void Editing_a_route_does_not_clash_with_itself()
    {
        var existing = Route("meuapp.sev");

        Assert.True(RouteRules.Validate(existing with { Target = "http://127.0.0.1:4000" }, [existing], 80).IsValid);
    }

    [Theory]
    [InlineData("127.0.0.1:3000")]
    [InlineData("ftp://127.0.0.1")]
    [InlineData("http://127.0.0.1:3000/?x=1")]
    [InlineData("http://user:pass@127.0.0.1:3000")]
    [InlineData("")]
    public void Invalid_targets_are_errors(string target)
    {
        Assert.NotNull(RouteRules.Validate(Route("a.sev", target), [], 80).Target);
    }

    [Theory]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://127.0.0.1:80")]
    [InlineData("http://127.5.5.5")]
    [InlineData("http://[::1]")]
    [InlineData("http://localhost")]
    [InlineData("http://app.localhost")]
    [InlineData("http://a.sev")]
    [InlineData("http://other.sev")]
    [InlineData("http://0.0.0.0")]
    public void Targets_that_come_back_into_the_proxy_are_loops(string target)
    {
        var other = Route("other.sev");

        Assert.NotNull(RouteRules.Validate(Route("a.sev", target), [other], 80).Target);
    }

    [Theory]
    [InlineData("http://127.0.0.1:3000")]
    [InlineData("https://127.0.0.1")]     // 443, not the proxy's port in phase 1
    [InlineData("http://192.168.0.10")]   // another machine
    [InlineData("http://other.sev:5000")]
    public void Other_targets_are_not_loops(string target)
    {
        var other = Route("other.sev");

        Assert.True(RouteRules.Validate(Route("a.sev", target), [other], 80).IsValid);
    }

    [Fact]
    public void Loop_check_follows_the_configured_port()
    {
        Assert.NotNull(RouteRules.Validate(Route("a.sev", "http://127.0.0.1:8080"), [], 8080).Target);
        Assert.True(RouteRules.Validate(Route("a.sev", "http://127.0.0.1"), [], 8080).IsValid);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("api", "/api")]
    [InlineData("/v1/pedidos/", "/v1/pedidos")]
    [InlineData("/a b", null)]
    [InlineData("/api?x=1", null)]
    [InlineData("//api", null)]
    public void Paths_are_normalized(string path, string? expected)
    {
        Assert.Equal(expected, RouteRules.NormalizePath(path));
    }

    [Fact]
    public void One_domain_can_have_a_route_per_path_and_the_longest_path_wins()
    {
        var root = Route("meuapp.sev");
        var api = Route("meuapp.sev") with { Path = "/api" };
        var v2 = Route("meuapp.sev") with { Path = "/api/v2" };

        Assert.True(RouteRules.Validate(api, [root], 80).IsValid);
        Assert.Equal("Já existe uma rota para meuapp.sev/api.", RouteRules.Validate(Route("meuapp.sev") with { Path = "api/" }, [root, api], 80).Domain);
        Assert.Same(v2, RouteRules.Find([root, api, v2], "meuapp.sev", "/api/v2/x"));
        Assert.Same(api, RouteRules.Find([root, api, v2], "meuapp.sev", "/api"));
        Assert.Same(root, RouteRules.Find([root, api, v2], "meuapp.sev", "/apix"));
        Assert.Same(root, RouteRules.Find([root, api, v2], "meuapp.sev"));
    }

    [Fact]
    public void Active_domains_are_enabled_distinct_normalized_and_sorted()
    {
        var domains = RouteRules.ActiveDomains([Route("b.sev"), Route("A.sev"), Route("off.sev", enabled: false), Route("a.sev.")]);

        Assert.Equal(["a.sev", "b.sev"], domains);
    }
}
