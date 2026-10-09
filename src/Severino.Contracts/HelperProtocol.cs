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
    public const int Version = 1;
    public const string PipeName = "Severino.Helper";
    public const int MaxMessageBytes = 1024 * 1024;
    public const int MaxDomains = 500;

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
    /// Normalizes the domains of a sync request: every one valid, at most <see cref="MaxDomains"/>,
    /// duplicates removed, sorted. One bad name rejects the whole list.
    /// </summary>
    public static bool TryNormalizeDomains(
        IReadOnlyList<string>? domains,
        [NotNullWhen(true)] out IReadOnlyList<string>? normalized,
        [NotNullWhen(false)] out string? error)
    {
        normalized = null;
        if (domains is null)
        {
            error = "A lista de domínios é obrigatória.";
            return false;
        }

        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var domain in domains)
        {
            if (!DomainName.TryNormalize(domain, out var name, out var reason))
            {
                error = $"Domínio inválido '{domain}': {reason}";
                return false;
            }
            result.Add(name);
        }

        if (result.Count > MaxDomains)
        {
            error = $"No máximo {MaxDomains} domínios.";
            return false;
        }

        normalized = [.. result];
        error = null;
        return true;
    }
}

public sealed record HelperRequest(string Command, IReadOnlyList<string>? Domains = null)
{
    public static HelperRequest Ping() => new(HelperProtocol.PingCommand);
    public static HelperRequest Sync(IEnumerable<string> domains) => new(HelperProtocol.SyncCommand, [.. domains]);
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
