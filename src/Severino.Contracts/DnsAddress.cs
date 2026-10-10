using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace Severino.Contracts;

public enum AddressScope
{
    Loopback,
    /// <summary>Private, carrier-grade NAT or link-local: reachable only inside a network.</summary>
    Private,
    /// <summary>Routable on the internet: needs an approval before the Helper writes it.</summary>
    Public,
}

/// <summary>The addresses a DNS entry may point to, and how far they reach.</summary>
public static class DnsAddress
{
    private static readonly (IPAddress Network, int Prefix)[] PrivateRanges =
    [
        (IPAddress.Parse("10.0.0.0"), 8),
        (IPAddress.Parse("172.16.0.0"), 12),
        (IPAddress.Parse("192.168.0.0"), 16),
        (IPAddress.Parse("100.64.0.0"), 10), // carrier-grade NAT, also Tailscale
        (IPAddress.Parse("169.254.0.0"), 16),
        (IPAddress.Parse("fc00::"), 7),
        (IPAddress.Parse("fe80::"), 10),
    ];

    /// <summary>
    /// The address in canonical form and its scope. Refuses what cannot be a host: unspecified,
    /// multicast and broadcast addresses, and anything with a zone or port.
    /// </summary>
    public static bool TryClassify(string? address, [NotNullWhen(true)] out string? normalized, out AddressScope scope)
    {
        normalized = null;
        scope = AddressScope.Public;
        if (string.IsNullOrWhiteSpace(address) || address.Any(char.IsWhiteSpace) || address.Contains('%')
            || !IPAddress.TryParse(address, out var ip))
            return false;
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast)
            || (ip.AddressFamily == AddressFamily.InterNetwork && ip.GetAddressBytes()[0] >= 224)
            || ip.IsIPv6Multicast)
            return false;

        normalized = ip.ToString();
        scope = IPAddress.IsLoopback(ip) ? AddressScope.Loopback
            : PrivateRanges.Any(r => InRange(ip, r.Network, r.Prefix)) ? AddressScope.Private
            : AddressScope.Public;
        return true;
    }

    private static bool InRange(IPAddress ip, IPAddress network, int prefix)
    {
        if (ip.AddressFamily != network.AddressFamily)
            return false;
        var a = ip.GetAddressBytes();
        var b = network.GetAddressBytes();
        for (var bit = 0; bit < prefix; bit++)
        {
            var mask = 0x80 >> (bit % 8);
            if ((a[bit / 8] & mask) != (b[bit / 8] & mask))
                return false;
        }
        return true;
    }
}
