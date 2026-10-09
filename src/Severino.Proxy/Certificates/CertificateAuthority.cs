using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Severino.Proxy.Certificates;

/// <summary>
/// The local root CA: ECDSA P-256, valid for 10 years, limited by Name Constraints to the names
/// it was created for. Signs one leaf per domain.
/// </summary>
public sealed class CertificateAuthority : IDisposable
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(3650);
    public static readonly TimeSpan LeafLifetime = TimeSpan.FromDays(397);

    // Backdated so a clock a little off does not make a fresh certificate "not yet valid".
    private static readonly TimeSpan Backdate = TimeSpan.FromDays(1);
    private static readonly Oid ServerAuth = new("1.3.6.1.5.5.7.3.1");

    public CertificateAuthority(X509Certificate2 certificate)
    {
        if (!certificate.HasPrivateKey)
            throw new ArgumentException("The CA certificate needs its private key.", nameof(certificate));
        Certificate = certificate;
        Names = NameConstraints.PermittedDnsNames(certificate);
    }

    /// <summary>The root, with its private key.</summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>The DNS names the root may sign for, read from its Name Constraints.</summary>
    public IReadOnlyList<string> Names { get; }

    public string Thumbprint => Certificate.Thumbprint;
    public DateTimeOffset NotAfter => Certificate.NotAfter;

    public static CertificateAuthority Create(IReadOnlyCollection<string> names, TimeProvider time)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = time.GetUtcNow();
        var subject = new X500DistinguishedName(
            $"CN=Severino Local CA ({Environment.UserName}@{Environment.MachineName}), OU=Emitida em {now:yyyy-MM-dd HH:mm}Z, O=Severino");

        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(NameConstraints.Create(names));

        using var created = request.CreateSelfSigned(now - Backdate, now + Lifetime);
        return new CertificateAuthority(Reload(created));
    }

    public bool Covers(string domain) => Severino.Core.Certificates.CaCoverage.IsCovered(domain, Names);

    /// <summary>
    /// A server certificate for <paramref name="domain"/>, ready for SslStream: Schannel rejects
    /// keys created in memory, so the result goes through PKCS#12 and is loaded back.
    /// </summary>
    public X509Certificate2 IssueLeaf(string domain, TimeProvider time)
    {
        if (!Covers(domain))
            throw new ArgumentException($"{domain} is outside this CA's names.", nameof(domain));

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={domain}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(domain);
        request.CertificateExtensions.Add(san.Build(critical: false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ServerAuth], false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Certificate, true, false));

        var now = time.GetUtcNow();
        // A leaf cannot outlive its issuer.
        var notAfter = now + LeafLifetime < Certificate.NotAfter ? now + LeafLifetime : new DateTimeOffset(Certificate.NotAfter);
        using var signed = request.Create(Certificate, now - Backdate, notAfter, RandomNumberGenerator.GetBytes(16));
        using var withKey = signed.CopyWithPrivateKey(key);
        return Reload(withKey);
    }

    /// <summary>The root alone, without the key, for the trust store and for export.</summary>
    public X509Certificate2 PublicCertificate() => X509CertificateLoader.LoadCertificate(Certificate.RawData);

    public string ExportPem() => PublicCertificate().ExportCertificatePem();

    public void Dispose() => Certificate.Dispose();

    // Exportable: CaStore exports it once more to encrypt it with DPAPI for disk.
    internal static X509Certificate2 Reload(X509Certificate2 certificate) =>
        X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
}
