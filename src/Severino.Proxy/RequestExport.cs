using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Severino.Proxy;

public enum CurlShell
{
    /// <summary><c>curl.exe</c> with single quotes, for PowerShell 7.</summary>
    PowerShell,
    Bash,
}

/// <summary>Copies and exports of logged requests: a curl command, a HAR file, and sending one again.</summary>
public static class RequestExport
{
    /// <summary>Not part of what a request means: the client sets them, or they belong to one hop.</summary>
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Connection", "Keep-Alive", "Transfer-Encoding", "Upgrade", "TE", "Proxy-Connection",
        "Accept-Encoding",
    };

    public static string UrlOf(RequestEntry entry) => $"{entry.Scheme}://{entry.Authority}{entry.PathAndQuery}";

    /// <summary>The body to send again: kept whole, or nothing to send. Null when there was one and it was not kept whole.</summary>
    private static byte[]? BodyToSend(RequestEntry entry) =>
        entry.Details is not { } details ? null
        : details.RequestBody is { Cut: false } body ? body.Bytes
        : details.RequestBytes == 0 ? [] : null;

    /// <summary>
    /// HTTP only, not a WebSocket, and with its body kept whole when it had one: what
    /// <see cref="ToCurl"/> and <see cref="ReplayAsync"/> need to send the same thing.
    /// </summary>
    public static bool CanReplay(RequestEntry entry) =>
        !entry.IsWebSocket && entry.Scheme is "http" or "https" && BodyToSend(entry) is not null;

    public static string ToCurl(RequestEntry entry, CurlShell shell)
    {
        string Quote(string value) => shell == CurlShell.PowerShell
            ? "'" + value.Replace("'", "''") + "'"
            : "'" + value.Replace("'", "'\\''") + "'";
        var lineBreak = shell == CurlShell.PowerShell ? " `\n  " : " \\\n  ";

        var parts = new List<string> { (shell == CurlShell.PowerShell ? "curl.exe " : "curl ") + Quote(UrlOf(entry)) };
        if (entry.Method != "GET")
            parts.Add("-X " + entry.Method);
        foreach (var (name, value) in entry.Details?.RequestHeaders ?? [])
        {
            if (!Skipped.Contains(name) && !name.StartsWith(':'))
                parts.Add("-H " + Quote($"{name}: {value}"));
        }
        if (BodyToSend(entry) is { Length: > 0 } body)
            parts.Add("--data-raw " + Quote(Encoding.UTF8.GetString(body)));
        return string.Join(lineBreak, parts);
    }

    /// <summary>A HAR 1.2 file with <paramref name="entries"/>, oldest first, as browsers' DevTools read it.</summary>
    public static string ToHar(IEnumerable<RequestEntry> entries, string creatorVersion)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteStartObject("log");
            json.WriteString("version", "1.2");
            json.WriteStartObject("creator");
            json.WriteString("name", "Severino");
            json.WriteString("version", creatorVersion);
            json.WriteEndObject();
            json.WriteStartArray("entries");
            foreach (var entry in entries.Where(e => e.Scheme is "http" or "https").OrderBy(e => e.Sequence))
                WriteEntry(json, entry);
            json.WriteEndArray();
            json.WriteEndObject();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteEntry(Utf8JsonWriter json, RequestEntry entry)
    {
        var details = entry.Details;
        var milliseconds = entry.Duration?.TotalMilliseconds ?? 0;
        json.WriteStartObject();
        json.WriteString("startedDateTime", entry.Time.ToString("o", CultureInfo.InvariantCulture));
        json.WriteNumber("time", Math.Round(milliseconds, 3));

        json.WriteStartObject("request");
        json.WriteString("method", entry.Method);
        json.WriteString("url", UrlOf(entry));
        json.WriteString("httpVersion", "HTTP/1.1");
        WriteHeaders(json, details?.RequestHeaders);
        json.WriteStartArray("queryString");
        var query = entry.PathAndQuery.IndexOf('?') is var at and >= 0 ? entry.PathAndQuery[at..] : "";
        foreach (var (name, values) in QueryHelpers.ParseQuery(query))
        {
            foreach (var value in values)
                WritePair(json, name, value ?? "");
        }
        json.WriteEndArray();
        json.WriteStartArray("cookies");
        json.WriteEndArray();
        json.WriteNumber("headersSize", -1);
        json.WriteNumber("bodySize", details?.RequestBytes ?? 0);
        if (details?.RequestBody is { } sent)
        {
            json.WriteStartObject("postData");
            json.WriteString("mimeType", details.Header(response: false, "Content-Type") ?? "");
            json.WriteString("text", Encoding.UTF8.GetString(sent.Bytes));
            json.WriteEndObject();
        }
        json.WriteEndObject();

        json.WriteStartObject("response");
        json.WriteNumber("status", entry.Status);
        json.WriteString("statusText", "");
        json.WriteString("httpVersion", "HTTP/1.1");
        WriteHeaders(json, details?.ResponseHeaders);
        json.WriteStartArray("cookies");
        json.WriteEndArray();
        json.WriteStartObject("content");
        var received = details?.ResponseBody is { } body ? CapturedBodies.Decode(body, details.Header(response: true, "Content-Encoding")) : null;
        json.WriteNumber("size", received?.Length ?? details?.ResponseBytes ?? 0);
        json.WriteString("mimeType", details?.Header(response: true, "Content-Type") ?? "");
        if (received is not null)
            json.WriteString("text", Encoding.UTF8.GetString(received));
        json.WriteEndObject();
        json.WriteString("redirectURL", details?.Header(response: true, "Location") ?? "");
        json.WriteNumber("headersSize", -1);
        json.WriteNumber("bodySize", details?.ResponseBytes ?? 0);
        json.WriteEndObject();

        json.WriteStartObject("cache");
        json.WriteEndObject();
        // Severino does not time DNS, connecting or sending apart: it is all waiting.
        json.WriteStartObject("timings");
        json.WriteNumber("send", 0);
        json.WriteNumber("wait", Math.Round(milliseconds, 3));
        json.WriteNumber("receive", 0);
        json.WriteEndObject();
        json.WriteEndObject();
    }

    private static void WriteHeaders(Utf8JsonWriter json, IReadOnlyList<KeyValuePair<string, string>>? headers)
    {
        json.WriteStartArray("headers");
        foreach (var (name, value) in headers ?? [])
            WritePair(json, name, value);
        json.WriteEndArray();
    }

    private static void WritePair(Utf8JsonWriter json, string name, string value)
    {
        json.WriteStartObject();
        json.WriteString("name", name);
        json.WriteString("value", value);
        json.WriteEndObject();
    }

    /// <summary>
    /// Sends <paramref name="entry"/> again through the proxy, on the loopback port of its scheme,
    /// with the same host, headers and body; it shows up in the log as a new request. Returns the status.
    /// </summary>
    /// <exception cref="InvalidOperationException">It cannot be sent again (see <see cref="CanReplay"/>), or the proxy is not listening for its scheme.</exception>
    /// <exception cref="HttpRequestException">The proxy did not answer.</exception>
    public static async Task<int> ReplayAsync(RequestEntry entry, int httpPort, int? httpsPort, CancellationToken cancellationToken = default)
    {
        if (!CanReplay(entry))
            throw new InvalidOperationException("This request cannot be sent again.");
        var port = entry.Scheme == "https" ? httpsPort ?? throw new InvalidOperationException("HTTPS is not listening.") : httpPort;

        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(IPAddress.Loopback, port, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
            // Talking to itself: the certificate is the local CA's, whether Windows trusts it yet or not.
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
        using var message = new HttpRequestMessage(new HttpMethod(entry.Method), UrlOf(entry));
        var body = BodyToSend(entry)!;
        if (body.Length > 0)
            message.Content = new ByteArrayContent(body);
        foreach (var (name, value) in entry.Details!.RequestHeaders)
        {
            if (Skipped.Contains(name) || name.StartsWith(':'))
                continue;
            if (!message.Headers.TryAddWithoutValidation(name, value))
                message.Content?.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return (int)response.StatusCode;
    }
}
