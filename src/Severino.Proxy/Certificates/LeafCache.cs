using System.Security.Cryptography.X509Certificates;

namespace Severino.Proxy.Certificates;

/// <summary>
/// One server certificate per domain, issued on first use: memory first, then disk, then the CA.
/// Renewed when 30 days or less remain, and dropped whenever the CA changes.
/// </summary>
public sealed class LeafCache(CaStore store, TimeProvider time)
{
    public static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, X509Certificate2> _leaves = new(StringComparer.Ordinal);
    private string? _caThumbprint;

    /// <summary>The certificate for <paramref name="domain"/>, issuing one when needed.</summary>
    /// <param name="domain">Normalized and covered by <paramref name="ca"/>.</param>
    public X509Certificate2 Get(CertificateAuthority ca, string domain)
    {
        // One lock for all domains: issuing takes milliseconds, and it keeps two handshakes for
        // a new domain from issuing twice.
        lock (_gate)
        {
            if (_caThumbprint != ca.Thumbprint)
            {
                // Certificates handed out earlier may still be in use by a handshake, so they are
                // left to the GC rather than disposed here.
                _leaves.Clear();
                _caThumbprint = ca.Thumbprint;
            }

            if (_leaves.TryGetValue(domain, out var cached) && IsUsable(cached, ca))
                return cached;

            var leaf = store.LoadLeaf(domain);
            if (leaf is null || !IsUsable(leaf, ca))
            {
                leaf = ca.IssueLeaf(domain, time);
                store.SaveLeaf(domain, leaf);
            }

            _leaves[domain] = leaf;
            return leaf;
        }
    }

    private bool IsUsable(X509Certificate2 leaf, CertificateAuthority ca) =>
        leaf.Issuer == ca.Certificate.Subject
        && leaf.GetCertHash().Length > 0
        && IssuedBy(leaf, ca)
        && time.GetUtcNow() + RenewBefore < leaf.NotAfter.ToUniversalTime();

    private static bool IssuedBy(X509Certificate2 leaf, CertificateAuthority ca)
    {
        var authorityKey = leaf.Extensions.OfType<X509AuthorityKeyIdentifierExtension>().FirstOrDefault()?.KeyIdentifier;
        var subjectKey = ca.Certificate.Extensions.OfType<X509SubjectKeyIdentifierExtension>().FirstOrDefault()?.SubjectKeyIdentifierBytes;
        return authorityKey is { } a && subjectKey is { } s && a.Span.SequenceEqual(s.Span);
    }
}
