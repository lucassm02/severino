namespace Severino.Contracts;

/// <summary>Where the Helper's DNS server for wildcard names listens, and what the NRPT rules point to.</summary>
public static class WildcardDnsAddress
{
    /// <summary>
    /// A loopback address of its own: Windows' Internet Connection Sharing already takes
    /// 0.0.0.0:53 on some machines, and 127.0.0.1:53 may be another local DNS.
    /// </summary>
    public const string Server = "127.53.0.1";

    /// <summary>The comment on Severino's NRPT rules, which is how the Helper finds its own.</summary>
    public const string NrptComment = "Severino";
}
