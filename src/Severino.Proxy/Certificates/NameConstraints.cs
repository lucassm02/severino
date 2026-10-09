using System.Formats.Asn1;
using System.Security.Cryptography.X509Certificates;

namespace Severino.Proxy.Certificates;

/// <summary>
/// The X.509 Name Constraints extension (RFC 5280 4.2.1.10), which .NET has no builder for.
/// Severino's CA permits only its DNS names and excludes every IP address.
/// </summary>
public static class NameConstraints
{
    public const string Oid = "2.5.29.30";

    private static readonly Asn1Tag Permitted = new(TagClass.ContextSpecific, 0, isConstructed: true);
    private static readonly Asn1Tag Excluded = new(TagClass.ContextSpecific, 1, isConstructed: true);
    private static readonly Asn1Tag DnsName = new(TagClass.ContextSpecific, 2);
    private static readonly Asn1Tag IpAddress = new(TagClass.ContextSpecific, 7);

    /// <summary>Critical extension permitting <paramref name="dnsNames"/> and subdomains, and no IP.</summary>
    /// <exception cref="ArgumentException">No names: an empty permitted list would mean no constraint at all.</exception>
    public static X509Extension Create(IReadOnlyCollection<string> dnsNames)
    {
        if (dnsNames.Count == 0)
            throw new ArgumentException("At least one name is required.", nameof(dnsNames));

        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence(Permitted))
            {
                foreach (var name in dnsNames)
                {
                    using (writer.PushSequence())
                        writer.WriteCharacterString(UniversalTagNumber.IA5String, name, DnsName);
                }
            }

            using (writer.PushSequence(Excluded))
            {
                // 0.0.0.0/0 and ::/0: address followed by an all-zero mask.
                foreach (var length in new[] { 8, 32 })
                {
                    using (writer.PushSequence())
                        writer.WriteOctetString(new byte[length], IpAddress);
                }
            }
        }

        return new X509Extension(Oid, writer.Encode(), critical: true);
    }

    /// <summary>The permitted DNS names of <paramref name="certificate"/>, or empty when it has none.</summary>
    public static IReadOnlyList<string> PermittedDnsNames(X509Certificate2 certificate)
    {
        var extension = certificate.Extensions[Oid];
        if (extension is null)
            return [];

        var names = new List<string>();
        var outer = new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadSequence();
        if (!outer.HasData || !outer.PeekTag().HasSameClassAndValue(Permitted))
            return names;

        var subtrees = outer.ReadSequence(Permitted);
        while (subtrees.HasData)
        {
            var subtree = subtrees.ReadSequence();
            if (subtree.PeekTag().HasSameClassAndValue(DnsName))
                names.Add(subtree.ReadCharacterString(UniversalTagNumber.IA5String, DnsName));
            else
                subtree.ReadEncodedValue();
        }
        return names;
    }
}
