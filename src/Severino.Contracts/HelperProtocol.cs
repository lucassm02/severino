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
    /// <summary>2: sync carries name and address pairs instead of bare domains.</summary>
    public const int Version = 2;
    public const string PipeName = "Severino.Helper";
    public const int MaxMessageBytes = 1024 * 1024;

    /// <summary>Lines in the block. A cluster import is about 120 services with 4 names each.</summary>
    public const int MaxEntries = 2000;

    public const string PingCommand = "ping";
    public const string SyncCommand = "sync";

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
            if (!DomainName.TryNormalize(entry?.Name, out var name, out var reason, allowSingleLabel: true))
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

public sealed record HelperRequest(string Command, IReadOnlyList<HostEntry>? Entries = null)
{
    public static HelperRequest Ping() => new(HelperProtocol.PingCommand);
    public static HelperRequest Sync(IEnumerable<HostEntry> entries) => new(HelperProtocol.SyncCommand, [.. entries]);

    /// <summary>Web route domains, each on both loopbacks.</summary>
    public static HelperRequest Sync(IEnumerable<string> domains) => Sync(HostEntry.ForDomains(domains));

    /// <summary>The distinct names in the request, for logs and tests; not sent.</summary>
    [JsonIgnore]
    public IReadOnlyList<string>? Domains => Entries?.Select(e => e.Name).Distinct(StringComparer.Ordinal).ToList();
}

public sealed record HelperResponse(bool Ok, string? Error, int ProtocolVersion, string? HelperVersion)
{
    public static HelperResponse Success(string helperVersion) => new(true, null, HelperProtocol.Version, helperVersion);
    public static HelperResponse Failure(string error, string helperVersion) => new(false, error, HelperProtocol.Version, helperVersion);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(HelperRequest))]
[JsonSerializable(typeof(HelperResponse))]
internal sealed partial class HelperJsonContext : JsonSerializerContext;
