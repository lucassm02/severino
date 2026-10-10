using System.Net;
using Severino.Core.Domains;

namespace Severino.Tests.Domains;

public sealed class DomainInspectorTests
{
    [Theory]
    [InlineData("app.dev")]
    [InlineData("meuapp.app")]
    public void Hsts_preloaded_tld_warns(string domain)
    {
        var warning = Assert.Single(new DomainInspector(new FakeDns()).CheckLocal(domain));
        Assert.Equal(DomainWarningKind.HstsPreload, warning.Kind);
    }

    [Fact]
    public void Local_tld_warns_about_mdns()
    {
        var warning = Assert.Single(new DomainInspector(new FakeDns()).CheckLocal("algo.local"));
        Assert.Equal(DomainWarningKind.Mdns, warning.Kind);
    }

    [Theory]
    [InlineData("meuapp.sev")]
    [InlineData("app.devel.sev")]
    public void Ordinary_tld_has_no_local_warnings(string domain)
    {
        Assert.Empty(new DomainInspector(new FakeDns()).CheckLocal(domain));
    }

    [Fact]
    public async Task Existing_name_warns_with_address()
    {
        var inspector = new DomainInspector(new FakeDns(new(DnsLookupOutcome.Exists, IPAddress.Parse("203.0.113.10"))));

        var warning = await inspector.CheckInternetAsync("api.empresa.com", CancellationToken.None);

        Assert.Equal(DomainWarningKind.ExistsOnInternet, warning!.Kind);
        Assert.Contains("203.0.113.10", warning.Message);
    }

    [Fact]
    public async Task Missing_name_has_no_warning()
    {
        var inspector = new DomainInspector(new FakeDns(new(DnsLookupOutcome.NotFound)));

        Assert.Null(await inspector.CheckInternetAsync("meuapp.sev", CancellationToken.None));
    }

    [Fact]
    public async Task Failed_lookup_says_so()
    {
        var inspector = new DomainInspector(new FakeDns(new(DnsLookupOutcome.Failed)));

        Assert.Equal(DomainWarningKind.LookupFailed, (await inspector.CheckInternetAsync("x.sev", CancellationToken.None))!.Kind);
    }

    private sealed class FakeDns(DnsLookupResult? result = null) : IDnsResolver
    {
        public Task<DnsLookupResult> LookupAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult(result ?? new DnsLookupResult(DnsLookupOutcome.NotFound));

        public Task<bool?> TldExistsAsync(string tld, CancellationToken cancellationToken) =>
            Task.FromResult<bool?>(null);
    }
}
