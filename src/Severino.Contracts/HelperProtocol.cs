using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Severino.Contracts;

/// <summary>
/// Wire format between the app and Severino.Helper: one JSON request per connection, one JSON
/// response back, each terminated by '\n'.
/// </summary>
public static class HelperProtocol
{
    /// <summary>
    /// 2: sync carries name and address pairs instead of bare domains.
    /// 3: the DNS block (sync-dns) and changing a line outside Severino's blocks (edit-line).
    /// </summary>
    public const int Version = 3;
    public const string PipeName = "Severino.Helper";
    public const int MaxMessageBytes = 1024 * 1024;

    /// <summary>Lines in the block. A cluster import is about 120 services with 4 names each.</summary>
    public const int MaxEntries = 2000;

    public const string PingCommand = "ping";
    public const string SyncCommand = "sync";

    /// <summary>The DNS block: kept when the app closes or pauses, any non-public address, public ones once approved.</summary>
    public const string SyncDnsCommand = "sync-dns";

    /// <summary>Replaces or comments out one line outside Severino's blocks, leaving a note above it.</summary>
    public const string EditLineCommand = "edit-line";

    /// <summary>
    /// The argument of <c>Severino.Helper.exe --approve-dns</c> for public addresses: base64 of a
    /// sync-dns request, so nothing in it meets the command line's quoting.
    /// </summary>
    public static string EncodeApproval(IEnumerable<HostEntry> entries) =>
        Convert.ToBase64String(Serialize(HelperRequest.SyncDns(entries)));

    public static byte[] Serialize(HelperRequest request) =>
        JsonSerializer.SerializeToUtf8Bytes(request, HelperJsonContext.Default.HelperRequest);

    public static byte[] Serialize(HelperResponse response) =>
        JsonSerializer.SerializeToUtf8Bytes(response, HelperJsonContext.Default.HelperResponse);

    /// <exception cref="JsonException">The bytes are not a request.</exception>
    public static HelperRequest DeserializeRequest(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize(json, HelperJsonContext.Default.HelperRequest)
        ?? throw new JsonException("Empty request.");

    /// <exception cref="JsonException">The bytes are not a response.</exception>
    public static HelperResponse DeserializeResponse(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize(json, HelperJsonContext.Default.HelperResponse)
        ?? throw new JsonException("Empty response.");

    /// <summary>
    /// Normalizes the entries of a sync request: every name valid (one label allowed: service
    /// routes use names like "redis"), every address loopback, at most <see cref="MaxEntries"/>,
    /// duplicates removed, sorted by name then address. One bad entry rejects the whole list.
    /// </summary>
    public static bool TryNormalizeEntries(
        IReadOnlyList<HostEntry>? entries,
        [NotNullWhen(true)] out IReadOnlyList<HostEntry>? normalized,
        [NotNullWhen(false)] out string? error)
    {
        normalized = null;
        if (entries is null)
        {
            error = "A lista de entradas é obrigatória.";
            return false;
        }

        var result = new SortedSet<HostEntry>(EntryOrder.Instance);
        foreach (var entry in entries)
        {
            if (!TryNormalizeName(entry?.Name, out var name, out var reason))
            {
                error = $"Domínio inválido '{entry?.Name}': {reason}";
                return false;
            }
            if (!HostEntry.TryNormalizeAddress(entry!.Address, out var address))
            {
                error = $"Endereço não permitido para '{name}': {entry.Address}. Só endereços de loopback.";
                return false;
            }
            result.Add(new HostEntry(name, address));
        }

        if (result.Count > MaxEntries)
        {
            error = $"No máximo {MaxEntries} entradas.";
            return false;
        }

        normalized = [.. result];
        error = null;
        return true;
    }

    /// <summary>
    /// Normalizes DNS entries: every name valid (one label allowed), every address a host
    /// address of any scope (see <see cref="DnsAddress"/>), at most <see cref="MaxEntries"/>,
    /// duplicates removed, sorted. Whether a public address is approved is the Helper's check.
    /// </summary>
    public static bool TryNormalizeDnsEntries(
        IReadOnlyList<HostEntry>? entries,
        [NotNullWhen(true)] out IReadOnlyList<HostEntry>? normalized,
        [NotNullWhen(false)] out string? error)
    {
        normalized = null;
        if (entries is null)
        {
            error = "A lista de entradas é obrigatória.";
            return false;
        }

        var result = new SortedSet<HostEntry>(EntryOrder.Instance);
        foreach (var entry in entries)
        {
            if (!TryNormalizeName(entry?.Name, out var name, out var reason))
            {
                error = $"Domínio inválido '{entry?.Name}': {reason}";
                return false;
            }
            if (!DnsAddress.TryClassify(entry!.Address, out var address, out _))
            {
                error = $"Endereço inválido para '{name}': {entry.Address}.";
                return false;
            }
            result.Add(new HostEntry(name, address));
        }

        if (result.Count > MaxEntries)
        {
            error = $"No máximo {MaxEntries} entradas.";
            return false;
        }

        normalized = [.. result];
        error = null;
        return true;
    }

    /// <summary>
    /// A host name (one label allowed) or a wildcard like *.callfred.sev, which the Helper answers
    /// with its DNS server instead of writing it in the hosts.
    /// </summary>
    private static bool TryNormalizeName(string? input, [NotNullWhen(true)] out string? name, [NotNullWhen(false)] out string? reason) =>
        DomainName.IsWildcard(input)
            ? DomainName.TryNormalizeWildcard(input, out name, out reason)
            : DomainName.TryNormalize(input, out name, out reason, allowSingleLabel: true);

    /// <summary>By name, then address: "127.0.0.1" sorts before "::1", as the block always had it.</summary>
    private sealed class EntryOrder : IComparer<HostEntry>
    {
        public static readonly EntryOrder Instance = new();

        public int Compare(HostEntry? x, HostEntry? y)
        {
            var byName = string.CompareOrdinal(x?.Name, y?.Name);
            return byName != 0 ? byName : string.CompareOrdinal(x?.Address, y?.Address);
        }
    }
}

/// <param name="Line">edit-line: the line as the app read it, which must still be in the file.</param>
public sealed record HelperRequest(string Command, IReadOnlyList<HostEntry>? Entries = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Line = null)
{
    public static HelperRequest Ping() => new(HelperProtocol.PingCommand);
    public static HelperRequest Sync(IEnumerable<HostEntry> entries) => new(HelperProtocol.SyncCommand, [.. entries]);
    public static HelperRequest SyncDns(IEnumerable<HostEntry> entries) => new(HelperProtocol.SyncDnsCommand, [.. entries]);

    /// <summary>Replaces <paramref name="line"/> with one for <paramref name="entries"/> (one address), or comments it out when there are none.</summary>
    public static HelperRequest EditLine(string line, IEnumerable<HostEntry> entries) => new(HelperProtocol.EditLineCommand, [.. entries], line);

    /// <summary>Web route domains, each on both loopbacks.</summary>
    public static HelperRequest Sync(IEnumerable<string> domains) => Sync(HostEntry.ForDomains(domains));

    /// <summary>The distinct names in the request, for logs and tests; not sent.</summary>
    [JsonIgnore]
    public IReadOnlyList<string>? Domains => Entries?.Select(e => e.Name).Distinct(StringComparer.Ordinal).ToList();
}

/// <param name="Pending">Entries with a public address not approved yet: left out of the hosts until they are.</param>
public sealed record HelperResponse(bool Ok, string? Error, int ProtocolVersion, string? HelperVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<HostEntry>? Pending = null)
{
    public static HelperResponse Success(string helperVersion, IReadOnlyList<HostEntry>? pending = null) =>
        new(true, null, HelperProtocol.Version, helperVersion, pending is { Count: > 0 } ? pending : null);

    public static HelperResponse Failure(string error, string helperVersion, IReadOnlyList<HostEntry>? pending = null) =>
        new(false, error, HelperProtocol.Version, helperVersion, pending is { Count: > 0 } ? pending : null);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(HelperRequest))]
[JsonSerializable(typeof(HelperResponse))]
internal sealed partial class HelperJsonContext : JsonSerializerContext;
