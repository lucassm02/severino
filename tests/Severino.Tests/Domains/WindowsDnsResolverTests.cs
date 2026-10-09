using Severino.Core.Domains;

namespace Severino.Tests.Domains;

/// <summary>
/// Hits the real DNS: needs network access. A cold lookup can pass the resolver's timeout
/// and come back as Failed, so each test allows one retry.
/// </summary>
[Trait("Category", "Network")]
public sealed class WindowsDnsResolverTests
{
    private readonly WindowsDnsResolver _dns = new();

    private async Task<DnsLookupResult> LookupWithRetry(string name)
    {
        var result = await _dns.LookupAsync(name, CancellationToken.None);
        return result.Outcome == DnsLookupOutcome.Failed
            ? await _dns.LookupAsync(name, CancellationToken.None)
            : result;
    }

    [Fact]
    public async Task Public_name_exists()
    {
        var result = await LookupWithRetry("example.com");

        Assert.Equal(DnsLookupOutcome.Exists, result.Outcome);
        Assert.NotNull(result.Address);
        Assert.False(System.Net.IPAddress.IsLoopback(result.Address));
    }

    [Theory]
    [InlineData("com", true)]
    [InlineData("br", true)]
    [InlineData("sev", false)]
    public async Task Tld_existence_comes_from_the_root_zone(string tld, bool expected)
    {
        var exists = await _dns.TldExistsAsync(tld, CancellationToken.None)
            ?? await _dns.TldExistsAsync(tld, CancellationToken.None);

        Assert.Equal(expected, exists);
    }

    [Fact]
    public async Task Name_under_unknown_tld_is_not_found()
    {
        var result = await LookupWithRetry($"x{Guid.NewGuid():N}.sev");

        Assert.Equal(DnsLookupOutcome.NotFound, result.Outcome);
    }
}
