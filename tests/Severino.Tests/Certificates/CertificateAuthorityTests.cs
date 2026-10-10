using System.Formats.Asn1;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Severino.Proxy.Certificates;

namespace Severino.Tests.Certificates;

public sealed class CertificateAuthorityTests : IDisposable
{
    private readonly CertificateAuthority _ca = CertificateAuthority.Create(["sev", "empresa.com"], TimeProvider.System);

    public void Dispose() => _ca.Dispose();

    [Fact]
    public void Name_constraints_round_trip_and_exclude_every_ip()
    {
        var extension = NameConstraints.Create(["sev", "api.empresa.com"]);

        Assert.True(extension.Critical);
        var outer = new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadSequence();
        outer.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true));
        var excluded = outer.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 1, true));
        var ipLengths = new List<int>();
        while (excluded.HasData)
            ipLengths.Add(excluded.ReadSequence().ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 7)).Length);
        Assert.Equal([8, 32], ipLengths);
    }

    [Fact]
    public void Name_constraints_require_at_least_one_name()
    {
        Assert.Throws<ArgumentException>(() => NameConstraints.Create([]));
    }

    [Fact]
    public void Root_is_a_constrained_ca_valid_for_ten_years()
    {
        var root = _ca.Certificate;

        Assert.Equal(["sev", "empresa.com"], _ca.Names);
        var basic = root.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        Assert.True(basic.CertificateAuthority);
        Assert.True(basic.HasPathLengthConstraint);
        Assert.Equal(0, basic.PathLengthConstraint);
        Assert.True(basic.Critical);
        Assert.Equal(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, root.Extensions.OfType<X509KeyUsageExtension>().Single().KeyUsages);
        Assert.InRange((root.NotAfter - DateTime.Now).TotalDays, 3640, 3650);
        Assert.Equal("ECC", root.PublicKey.Oid.FriendlyName);
    }

    [Fact]
    public void Leaf_has_san_server_auth_and_chains_to_the_root()
    {
        using var leaf = _ca.IssueLeaf("meuapp.sev", TimeProvider.System);

        Assert.True(leaf.HasPrivateKey);
        Assert.Equal("meuapp.sev", leaf.GetNameInfo(X509NameType.DnsName, forIssuer: false));
        Assert.Contains(leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<Oid>(), o => o.Value == "1.3.6.1.5.5.7.3.1");
        Assert.Equal(X509KeyUsageFlags.DigitalSignature, leaf.Extensions.OfType<X509KeyUsageExtension>().Single().KeyUsages);
        Assert.InRange((leaf.NotAfter - DateTime.Now).TotalDays, 395, 397);
        Assert.Equal("VALID", Chain(leaf));
    }

    [Fact]
    public void Leaf_for_a_subdomain_of_a_covered_name_is_valid()
    {
        using var leaf = _ca.IssueLeaf("api.empresa.com", TimeProvider.System);

        Assert.Equal("VALID", Chain(leaf));
    }

    [Fact]
    public void Ca_refuses_to_issue_outside_its_names()
    {
        Assert.Throws<ArgumentException>(() => _ca.IssueLeaf("banco.com.br", TimeProvider.System));
    }

    [Fact]
    public void Windows_rejects_a_leaf_outside_the_constraints_even_when_signed_by_the_key()
    {
        // What someone holding the CA key could forge: the root's Name Constraints must stop it.
        using var forged = Forge("banco.com.br");

        Assert.Equal($"REJECTED ({X509ChainStatusFlags.HasNotPermittedNameConstraint})", Chain(forged));
    }

    [Fact]
    public void Leaf_never_outlives_the_root()
    {
        var nearExpiry = new FixedTime(_ca.NotAfter.AddDays(-10));

        using var leaf = _ca.IssueLeaf("meuapp.sev", nearExpiry);

        Assert.True(leaf.NotAfter <= _ca.NotAfter);
    }

    [Fact]
    public async Task Leaf_works_as_a_server_certificate_in_schannel()
    {
        using var leaf = _ca.IssueLeaf("meuapp.sev", TimeProvider.System);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var server = Task.Run(async () =>
            {
                using var socket = await listener.AcceptTcpClientAsync();
                await using var ssl = new SslStream(socket.GetStream());
                await ssl.AuthenticateAsServerAsync(leaf);
            });
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            await using var clientSsl = new SslStream(client.GetStream(), false, (_, cert, _, _) => cert is not null && Chain(X509CertificateLoader.LoadCertificate(cert.GetRawCertData())) == "VALID");
            await clientSsl.AuthenticateAsClientAsync("meuapp.sev");
            await server;

            Assert.True(clientSsl.IsAuthenticated);
        }
        finally
        {
            listener.Stop();
        }
    }

    private string Chain(X509Certificate2 leaf)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.CustomTrustStore.Add(_ca.PublicCertificate());
        return chain.Build(leaf) ? "VALID" : $"REJECTED ({string.Join(",", chain.ChainStatus.Select(s => s.Status))})";
    }

    private X509Certificate2 Forge(string domain)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={domain}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(domain);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(_ca.Certificate, true, false));
        return request.Create(_ca.Certificate, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30), RandomNumberGenerator.GetBytes(16));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
