using System.Text.RegularExpressions;
using Severino.Core.Certificates;

namespace Severino.Core.Network;

/// <summary>The Windows proxy settings of the current user (HKCU Internet Settings).</summary>
/// <param name="Enabled"><c>ProxyEnable</c>. A <c>ProxyServer</c> left filled in with this off is common and harmless.</param>
/// <param name="Override"><c>ProxyOverride</c>: the bypass list, separated by ';'.</param>
/// <param name="AutoConfigUrl"><c>AutoConfigURL</c>: a PAC script, which decides on its own.</param>
public sealed record ProxySettings(bool Enabled, string? Server, string? Override, string? AutoConfigUrl)
{
    public static readonly ProxySettings None = new(false, null, null, null);

    /// <summary>A fixed proxy that browsers will send requests to.</summary>
    public bool HasFixedProxy => Enabled && !string.IsNullOrWhiteSpace(Server);

    public bool HasScript => !string.IsNullOrWhiteSpace(AutoConfigUrl);

    public IReadOnlyList<string> OverrideEntries =>
        [.. (Override ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}

/// <summary>
/// Whether the system proxy would take the routes' domains away from Severino, and which
/// <c>ProxyOverride</c> entries fix it.
/// </summary>
public static partial class ProxyBypass
{
    /// <summary>The domains a fixed proxy would get instead of Severino. Empty without a fixed proxy.</summary>
    public static IReadOnlyList<string> Uncovered(ProxySettings settings, IEnumerable<string> domains) =>
        settings.HasFixedProxy
            ? [.. domains.Where(d => !settings.OverrideEntries.Any(entry => Matches(entry, d)))]
            : [];

    /// <summary>
    /// WinINet's rules: case-insensitive, '*' matches anything, and the special <c>&lt;local&gt;</c>
    /// matches names without a dot only, so not "callfred.sev".
    /// </summary>
    public static bool Matches(string entry, string host)
    {
        if (entry.Equals("<local>", StringComparison.OrdinalIgnoreCase))
            return !host.Contains('.');
        // Entries may carry a scheme ("http://foo") or a port ("foo:80"); the host part decides.
        var pattern = SchemePrefix().Replace(entry, "");
        pattern = PortSuffix().Replace(pattern, "");
        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$";
        return Regex.IsMatch(host, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// The entries to add for <paramref name="domains"/>: <c>*.sev</c> for a TLD that does not
    /// exist on the internet, as the CA's coverage does, and the exact name otherwise.
    /// </summary>
    public static IReadOnlyList<string> EntriesFor(IEnumerable<string> domains, Func<string, bool?> tldExists) =>
        [.. CaCoverage.Compute(domains, tldExists).Select(name => name.Contains('.') ? name : "*." + name)];

    /// <summary>The list with <paramref name="entries"/> appended, skipping ones already there.</summary>
    public static string Add(string? current, IEnumerable<string> entries)
    {
        var list = (current ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        foreach (var entry in entries)
        {
            if (!list.Contains(entry, StringComparer.OrdinalIgnoreCase))
                list.Add(entry);
        }
        return string.Join(';', list);
    }

    /// <summary>The list without <paramref name="entries"/>, keeping everything else in its order.</summary>
    public static string Remove(string? current, IEnumerable<string> entries)
    {
        var drop = entries.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return string.Join(';', (current ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(e => !drop.Contains(e)));
    }

    [GeneratedRegex("^[a-z]+://", RegexOptions.IgnoreCase)]
    private static partial Regex SchemePrefix();

    [GeneratedRegex(@":\d+$")]
    private static partial Regex PortSuffix();
}
