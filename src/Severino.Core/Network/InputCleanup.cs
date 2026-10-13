using System.Text.RegularExpressions;

namespace Severino.Core.Network;

/// <summary>A host field's text without the leftovers of a copied URL, and what was in it.</summary>
/// <param name="Scheme">"http" or "https" when the text began with one; null otherwise.</param>
/// <param name="Port">The port that came after the host, when it was a valid one.</param>
public readonly record struct CleanHost(string Host, string? Scheme, int? Port);

/// <summary>
/// Takes out what usually comes along when an address is copied from a browser, a terminal or a
/// config file: <c>http://</c> in front, a <c>:</c> left at the end of an IP, a path, a port typed
/// with its colon. The fields then receive only what they take, instead of refusing the value.
/// </summary>
public static partial class InputCleanup
{
    /// <summary>
    /// "https://10.0.0.5:8080/api" → 10.0.0.5, https, 8080. IPv6 in brackets keeps its port
    /// ("[::1]:5432"); a bare IPv6 ("fe80::1") is left whole, its colons are not a port.
    /// </summary>
    public static CleanHost Host(string? text)
    {
        var value = (text ?? "").Trim();
        string? scheme = null;
        if (SchemePrefix().Match(value) is { Success: true } match)
        {
            scheme = match.Groups["scheme"].Value.ToLowerInvariant();
            value = value[match.Length..];
        }
        // user:password@host
        if (value.IndexOf('@') is var at and >= 0 && at < Cut(value))
            value = value[(at + 1)..];
        value = value[..Cut(value)];

        int? port = null;
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            if (close > 0)
            {
                port = PortOf(value[(close + 1)..].TrimStart(':'));
                value = value[1..close];
            }
        }
        else if (value.Count(c => c == ':') == 1)
        {
            var colon = value.IndexOf(':');
            port = PortOf(value[(colon + 1)..]);
            value = value[..colon];
        }
        return new(value.TrimEnd('.'), scheme, port);
    }

    /// <summary>":8080", "8080/", " 8080 " and "http://x:8080" are all 8080. Anything else is kept, for the field to explain.</summary>
    public static string Port(string? text)
    {
        var value = (text ?? "").Trim();
        if (value.Contains("://", StringComparison.Ordinal) || value.Contains('.') || value.StartsWith('['))
            return Host(value).Port?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? value;
        return value.Trim(':', '/', ' ');
    }

    /// <summary>
    /// A route's address, which may carry a path: "http://meuapp.sev:3000/api?x=1" → "meuapp.sev/api".
    /// The port goes: a route answers on the proxy's ports.
    /// </summary>
    public static string Address(string? text)
    {
        var value = (text ?? "").Trim();
        if (SchemePrefix().Match(value) is { Success: true } match)
            value = value[match.Length..];
        var query = value.IndexOfAny(['?', '#']);
        if (query >= 0)
            value = value[..query];
        var slash = value.IndexOf('/');
        var path = slash >= 0 ? value[slash..].TrimEnd('/') : "";
        var host = Host(slash >= 0 ? value[..slash] : value).Host;
        return host + path;
    }

    /// <summary>Several names, one per line: each one cleaned like a host, without its port.</summary>
    public static string Names(string? text)
    {
        var names = (text ?? "").Split(['\n', '\r', ',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => Host(name).Host)
            .Where(name => name.Length > 0);
        return string.Join(System.Environment.NewLine, names);
    }

    private static int Cut(string value) => value.IndexOfAny(['/', '?', '#']) is var cut and >= 0 ? cut : value.Length;

    private static int? PortOf(string text) =>
        int.TryParse(text.Trim('/', ' '), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535 ? port : null;

    [GeneratedRegex(@"^(?<scheme>[a-zA-Z][a-zA-Z0-9+.\-]*)://")]
    private static partial Regex SchemePrefix();
}
