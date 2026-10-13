using System.IO.Compression;

namespace Severino.Proxy;

/// <summary>The first bytes of a request or response body, as they went through.</summary>
/// <param name="Cut">The body was longer than <see cref="RequestLog.BodyLimit"/>.</param>
public sealed record CapturedBody(byte[] Bytes, bool Cut);

/// <summary>What an HTTP request carried, beyond its line in the log. Memory only, like the log.</summary>
public sealed record RequestDetails
{
    /// <summary>As the browser sent them, before the route's transforms.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> RequestHeaders { get; init; } = [];

    public IReadOnlyList<KeyValuePair<string, string>> ResponseHeaders { get; init; } = [];

    /// <summary>Headers past <see cref="RequestLog.HeaderLimit"/> were left out.</summary>
    public bool HeadersCut { get; init; }

    /// <summary>The server the request went to, e.g. "http://localhost:3000/"; null when Severino answered.</summary>
    public string? Target { get; init; }

    public Guid? RouteId { get; init; }

    public long RequestBytes { get; init; }

    public long ResponseBytes { get; init; }

    /// <summary>Null unless bodies were being kept, or when the type is not text.</summary>
    public CapturedBody? RequestBody { get; init; }

    public CapturedBody? ResponseBody { get; init; }

    public string? Header(bool response, string name) =>
        (response ? ResponseHeaders : RequestHeaders).FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}

/// <summary>Reading captured bodies: which are text, and undoing the transfer compression.</summary>
public static class CapturedBodies
{
    /// <summary>The types worth keeping and showing; no type at all is kept too, it may be text.</summary>
    public static bool IsText(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return true;
        var type = contentType.Split(';')[0].Trim().ToLowerInvariant();
        return type.StartsWith("text/", StringComparison.Ordinal)
            || type.EndsWith("+json", StringComparison.Ordinal)
            || type.EndsWith("+xml", StringComparison.Ordinal)
            || type is "application/json" or "application/xml" or "application/javascript" or "application/x-javascript"
                or "application/x-www-form-urlencoded" or "application/graphql" or "application/x-ndjson";
    }

    /// <summary>
    /// The body as the server meant it: gzip, deflate and br undone. A cut body is undone as far as
    /// it goes; null when it cannot be read at all.
    /// </summary>
    public static byte[]? Decode(CapturedBody body, string? contentEncoding)
    {
        var encoding = (contentEncoding ?? "").Trim().ToLowerInvariant();
        if (encoding is "" or "identity")
            return body.Bytes;

        using var input = new MemoryStream(body.Bytes);
        using Stream? decoder = encoding switch
        {
            "gzip" or "x-gzip" => new GZipStream(input, CompressionMode.Decompress),
            "deflate" => new ZLibStream(input, CompressionMode.Decompress),
            "br" => new BrotliStream(input, CompressionMode.Decompress),
            _ => null,
        };
        if (decoder is null)
            return null;

        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        try
        {
            int read;
            while ((read = decoder.Read(buffer)) > 0)
            {
                output.Write(buffer, 0, read);
                if (output.Length > 4 * RequestLog.BodyLimit)
                    break;
            }
        }
        catch (InvalidDataException)
        {
            // A cut body ends mid-stream; what came out so far is still worth showing.
            if (output.Length == 0)
                return null;
        }
        return output.ToArray();
    }
}
