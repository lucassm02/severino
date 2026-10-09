using Severino.Core.Certificates;
using Severino.Core.Configuration;
using Severino.Core.Domains;

namespace Severino.Tests.Certificates;

public sealed class TldDirectoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "severino-tests", Guid.NewGuid().ToString("N"));
    private readonly ConfigService _config;
    private readonly FakeDns _dns = new();

    public TldDirectoryTests()
    {
        _config = new ConfigService(new ConfigStore(_dir));
        _config.Load();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Coverage_looks_up_tlds_once_and_remembers_them()
    {
        _dns.Answers["sev"] = false;
        _dns.Answers["com"] = true;
        var directory = new TldDirectory(_config, _dns);

        var names = await directory.CoverageAsync(["a.sev", "api.empresa.com"], CancellationToken.None);
        await directory.CoverageAsync(["b.sev"], CancellationToken.None);

        Assert.Equal(["api.empresa.com", "sev"], names);
        Assert.Equal(["sev", "com"], _dns.Asked);
        Assert.False(new ConfigStore(_dir).Load().Config.State.TldExists["sev"]);
    }

    [Fact]
    public async Task Unanswered_tld_is_not_cached_and_is_asked_again()
    {
        var directory = new TldDirectory(_config, _dns);

        Assert.Equal(["a.zzz"], await directory.CoverageAsync(["a.zzz"], CancellationToken.None));
        _dns.Answers["zzz"] = false;
        Assert.Equal(["zzz"], await directory.CoverageAsync(["a.zzz"], CancellationToken.None));
    }

    [Fact]
    public async Task Reserved_tlds_are_never_asked()
    {
        await new TldDirectory(_config, _dns).CoverageAsync(["a.test", "b.localhost"], CancellationToken.None);

        Assert.Empty(_dns.Asked);
    }

    private sealed class FakeDns : IDnsResolver
    {
        public Dictionary<string, bool> Answers { get; } = [];
        public List<string> Asked { get; } = [];

        public Task<DnsLookupResult> LookupAsync(string name, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool?> TldExistsAsync(string tld, CancellationToken cancellationToken)
        {
            Asked.Add(tld);
            return Task.FromResult<bool?>(Answers.TryGetValue(tld, out var exists) ? exists : null);
        }
    }
}
