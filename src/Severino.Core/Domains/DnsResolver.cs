using System.Net;
using System.Runtime.InteropServices;

namespace Severino.Core.Domains;

public enum DnsLookupOutcome
{
    /// <summary>The name has an A or AAAA record on the public DNS.</summary>
    Exists,
    /// <summary>The DNS answered that the name has no address.</summary>
    NotFound,
    /// <summary>No answer: offline, timeout, or a server error.</summary>
    Failed,
}

public sealed record DnsLookupResult(DnsLookupOutcome Outcome, IPAddress? Address = null);

public interface IDnsResolver
{
    /// <summary>Looks the name up on the DNS servers, ignoring the hosts file and the cache.</summary>
    Task<DnsLookupResult> LookupAsync(string name, CancellationToken cancellationToken);
}

/// <summary>
/// <c>DnsQuery_W</c> with <c>DNS_QUERY_NO_HOSTS_FILE</c>: a regular lookup would answer
/// 127.0.0.1 for any name Severino has already written to the hosts file.
/// </summary>
public sealed partial class WindowsDnsResolver : IDnsResolver
{
    // NXDOMAIN for a never-seen name can take a few seconds on slow or corporate DNS.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private const ushort TypeA = 1;
    private const ushort TypeAaaa = 28;
    private const uint QueryBypassCache = 0x8;
    private const uint QueryNoHostsFile = 0x40;
    private const int Success = 0;
    private const int NameError = 9003;   // DNS_ERROR_RCODE_NAME_ERROR (NXDOMAIN)
    private const int NoRecords = 9501;   // DNS_INFO_NO_RECORDS
    private const int FreeRecordList = 1; // DnsFreeRecordList

    public async Task<DnsLookupResult> LookupAsync(string name, CancellationToken cancellationToken)
    {
        // DnsQuery_W blocks and has no timeout of its own.
        var lookup = Task.Run(() => Lookup(name), CancellationToken.None);
        try
        {
            return await lookup.WaitAsync(Timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return new(DnsLookupOutcome.Failed);
        }
    }

    private static DnsLookupResult Lookup(string name)
    {
        var a = Query(name, TypeA);
        if (a.Outcome != DnsLookupOutcome.NotFound)
            return a;
        return Query(name, TypeAaaa);
    }

    private static DnsLookupResult Query(string name, ushort type)
    {
        var status = DnsQuery(name, type, QueryBypassCache | QueryNoHostsFile, 0, out var records, 0);
        try
        {
            if (status is NameError or NoRecords)
                return new(DnsLookupOutcome.NotFound);
            if (status != Success)
                return new(DnsLookupOutcome.Failed);

            // The answer may start with CNAME records; walk to the first address of the requested type.
            for (var record = records; record != 0; record = Marshal.ReadIntPtr(record))
            {
                var recordType = (ushort)Marshal.ReadInt16(record, 2 * IntPtr.Size);
                var data = record + 2 * IntPtr.Size + 16; // after pNext, pName, wType, wDataLength, Flags, dwTtl, dwReserved
                if (recordType == TypeA && type == TypeA)
                    return new(DnsLookupOutcome.Exists, new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(data))));
                if (recordType == TypeAaaa && type == TypeAaaa)
                {
                    var bytes = new byte[16];
                    Marshal.Copy(data, bytes, 0, 16);
                    return new(DnsLookupOutcome.Exists, new IPAddress(bytes));
                }
            }
            return new(DnsLookupOutcome.NotFound);
        }
        finally
        {
            if (records != 0)
                DnsRecordListFree(records, FreeRecordList);
        }
    }

    [LibraryImport("dnsapi.dll", EntryPoint = "DnsQuery_W", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DnsQuery(string name, ushort type, uint options, nint extra, out nint results, nint reserved);

    [LibraryImport("dnsapi.dll")]
    private static partial void DnsRecordListFree(nint records, int freeType);
}
