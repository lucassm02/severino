using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using Severino.Proxy.Certificates;

namespace Severino.Tests.Certificates;

public sealed class LocalCaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "severino-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTrustStore _trust = new();
    private readonly MovableTime _time = new(DateTimeOffset.UtcNow);
    private readonly CaStore _store;
    private readonly LocalCa _ca;

    public LocalCaTests()
    {
        _store = new CaStore(_dir);
        _ca = new LocalCa(_store, _trust, _time, NullLogger<LocalCa>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Activation_trusts_and_saves_the_root()
    {
        Assert.Equal(ActivationResult.Activated, _ca.Activate(["sev"]));

        Assert.Equal(LocalCaState.Active, _ca.Status.State);
        Assert.Equal(["sev"], _ca.Status.Names);
        Assert.Single(_trust.Roots);
        Assert.True(_store.Exists);
    }

    [Fact]
    public void Declined_warning_leaves_nothing_behind()
    {
        _trust.Decline = true;

        Assert.Equal(ActivationResult.Declined, _ca.Activate(["sev"]));

        Assert.Equal(LocalCaState.Disabled, _ca.Status.State);
        Assert.False(_store.Exists);
        Assert.Empty(_trust.Roots);
    }

    [Fact]
    public void Reissue_replaces_the_root_and_issues_new_leaves()
    {
        _ca.Activate(["sev"]);
        var oldRoot = _trust.Roots.Single();
        var oldLeaf = _ca.CertificateFor("a.sev")!;

        _ca.Activate(["sev", "empresa.com"]);

        Assert.DoesNotContain(oldRoot, _trust.Roots);
        Assert.Single(_trust.Roots);
        var newLeaf = _ca.CertificateFor("a.sev")!;
        Assert.NotEqual(oldLeaf.Thumbprint, newLeaf.Thumbprint);
        Assert.NotNull(_ca.CertificateFor("api.empresa.com"));
    }

    [Fact]
    public void Reissue_reports_an_old_root_the_user_kept()
    {
        _ca.Activate(["sev"]);
        _trust.DeclineRemoval = true;

        Assert.Equal(ActivationResult.ActivatedOldRootKept, _ca.Activate(["sev", "test"]));
        Assert.Equal(LocalCaState.Active, _ca.Status.State);
    }

    [Fact]
    public void Only_covered_domains_get_a_certificate()
    {
        _ca.Activate(["sev"]);

        Assert.NotNull(_ca.CertificateFor("meuapp.sev"));
        Assert.Null(_ca.CertificateFor("banco.com.br"));
    }

    [Fact]
    public void Leaves_are_reused_from_memory_and_disk_and_renewed_near_expiry()
    {
        _ca.Activate(["sev"]);
        var first = _ca.CertificateFor("a.sev")!;
        Assert.Same(first, _ca.CertificateFor("a.sev"));

        // A fresh LocalCa reads the same leaf back from disk.
        var reloaded = new LocalCa(_store, _trust, _time, NullLogger<LocalCa>.Instance);
        reloaded.Load();
        Assert.Equal(first.Thumbprint, reloaded.CertificateFor("a.sev")!.Thumbprint);

        _time.Now = new DateTimeOffset(first.NotAfter.ToUniversalTime()) - LeafCache.RenewBefore + TimeSpan.FromHours(1);
        Assert.NotEqual(first.Thumbprint, _ca.CertificateFor("a.sev")!.Thumbprint);
    }

    [Fact]
    public async Task Concurrent_first_requests_issue_a_single_leaf()
    {
        _ca.Activate(["sev"]);

        var thumbprints = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => _ca.CertificateFor("new.sev")!.Thumbprint)));

        Assert.Single(thumbprints.Distinct());
    }

    [Fact]
    public void Remove_untrusts_and_deletes_everything()
    {
        _ca.Activate(["sev"]);
        _ca.CertificateFor("a.sev");

        Assert.True(_ca.Remove());

        Assert.Empty(_trust.Roots);
        Assert.False(Directory.Exists(_dir));
        Assert.Equal(LocalCaState.Disabled, _ca.Status.State);
        Assert.Null(_ca.CertificateFor("a.sev"));
    }

    [Fact]
    public void Root_removed_by_hand_shows_as_not_trusted()
    {
        _ca.Activate(["sev"]);
        _trust.Roots.Clear();

        var reloaded = new LocalCa(_store, _trust, _time, NullLogger<LocalCa>.Instance);
        reloaded.Load();

        Assert.Equal(LocalCaState.NotTrusted, reloaded.Status.State);
        Assert.Null(reloaded.CertificateFor("a.sev"));
    }

    [Fact]
    public void Keys_on_disk_are_dpapi_blobs()
    {
        _ca.Activate(["sev"]);
        _ca.CertificateFor("a.sev");

        // Every DPAPI blob starts with version 1 and the DPAPI provider GUID df9d8cd0-....
        byte[] dpapiHeader = [0x01, 0x00, 0x00, 0x00, 0xD0, 0x8C, 0x9D, 0xDF];
        Assert.Equal(dpapiHeader, File.ReadAllBytes(Path.Combine(_dir, "ca.key"))[..8]);
        Assert.Equal(dpapiHeader, File.ReadAllBytes(Path.Combine(_dir, "certs", "a.sev.pfx"))[..8]);
    }

    [Fact]
    public void Corrupted_key_file_shows_as_unreadable()
    {
        _ca.Activate(["sev"]);
        File.WriteAllBytes(Path.Combine(_dir, "ca.key"), [1, 2, 3]);

        var reloaded = new LocalCa(_store, _trust, _time, NullLogger<LocalCa>.Instance);
        reloaded.Load();

        Assert.Equal(LocalCaState.Unreadable, reloaded.Status.State);
    }

    [Fact]
    public void Export_is_the_root_alone_in_pem()
    {
        _ca.Activate(["sev"]);

        var pem = _ca.ExportPem()!;

        Assert.StartsWith("-----BEGIN CERTIFICATE-----", pem);
        Assert.DoesNotContain("PRIVATE KEY", pem);
        Assert.Equal(_trust.Roots.Single(), X509Certificate2.CreateFromPem(pem).Thumbprint);
    }

    private sealed class MovableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
