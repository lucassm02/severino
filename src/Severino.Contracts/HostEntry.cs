using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace Severino.Contracts;

/// <summary>One line of the hosts block: a name and the loopback address it points to.</summary>
public sealed record HostEntry(string Name, string Address)
{
    public const string IPv4Loopback = "127.0.0.1";
    public const string IPv6Loopback = "::1";

    /// <summary>A web route's domain: both loopbacks, since a browser may try either.</summary>
    public static IEnumerable<HostEntry> ForDomain(string domain) => [new(domain, IPv4Loopback), new(domain, IPv6Loopback)];

    public static IEnumerable<HostEntry> ForDomains(IEnumerable<string> domains) => domains.SelectMany(ForDomain);

    /// <summary>
    /// Loopback only, in canonical form: any address in 127.0.0.0/8, or ::1. Service routes get
    /// one of their own (127.77.x.y), so the worst a rogue caller can do is still point a name at
    /// this machine.
    /// </summary>
    public static bool TryNormalizeAddress(string? address, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(address) || address.Any(char.IsWhiteSpace) || !IPAddress.TryParse(address, out var ip))
            return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork && ip.GetAddressBytes()[0] == 127)
            normalized = ip.ToString();
        else if (ip.Equals(IPAddress.IPv6Loopback))
            normalized = IPv6Loopback;
        return normalized is not null;
    }
}
