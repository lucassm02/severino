using Severino.Core.Certificates;
using Severino.Core.Configuration;

namespace Severino.Tests.Certificates;

public sealed class CaCoverageTests
{
    private static bool? Known(string tld) => tld switch
    {
        "sev" => false,
        "com" or "br" or "dev" => true,
        _ => null,
    };

    [Fact]
    public void Missing_tld_is_covered_whole_and_real_tld_by_exact_name()
    {
        var names = CaCoverage.Compute(["meuapp.sev", "api.empresa.com"], Known);

        Assert.Equal(["api.empresa.com", "sev"], names);
    }

    [Theory]
    [InlineData("app.test", "test")]
    [InlineData("app.localhost", "localhost")]
    [InlineData("app.internal", "internal")]
    public void Reserved_tlds_are_covered_whole_without_asking(string domain, string expected)
    {
        Assert.Equal([expected], CaCoverage.Compute([domain], _ => throw new InvalidOperationException("should not ask")));
    }

    [Fact]
    public void Unknown_tld_keeps_the_exact_name()
    {
        Assert.Equal(["app.zzz"], CaCoverage.Compute(["app.zzz"], Known));
    }

    [Fact]
    public void Names_covered_by_another_are_dropped()
    {
        var names = CaCoverage.Compute(["empresa.com", "api.empresa.com", "a.sev", "b.sev"], Known);

        Assert.Equal(["empresa.com", "sev"], names);
    }

    [Theory]
    [InlineData("a.sev", true)]
    [InlineData("a.b.sev", true)]
    [InlineData("sev", true)]
    [InlineData("api.empresa.com", true)]
    [InlineData("empresa.com", true)]
    [InlineData("xempresa.com", false)]
    [InlineData("empresa.com.br", false)]
    [InlineData("banco.com.br", false)]
    public void IsCovered_matches_whole_labels(string domain, bool expected)
    {
        Assert.Equal(expected, CaCoverage.IsCovered(domain, ["sev", "empresa.com"]));
    }

    [Fact]
    public void Required_domains_are_https_routes_enabled_or_not()
    {
        RouteEntry Route(string domain, bool https, bool enabled = true) =>
            new() { Domain = domain, Target = "http://localhost:3000", Https = https, Enabled = enabled };

        var domains = CaCoverage.RequiredDomains([Route("b.sev", true), Route("A.sev", true, enabled: false), Route("plain.sev", false)]);

        Assert.Equal(["a.sev", "b.sev"], domains);
    }
}
