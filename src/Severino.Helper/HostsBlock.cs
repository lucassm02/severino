using System.Text;
using Severino.Contracts;

namespace Severino.Helper;

/// <summary>
/// Replaces one of Severino's blocks in the text of a hosts file and leaves every other byte
/// alone: the routes block (loopback, emptied when the app closes) or the DNS block (kept).
/// </summary>
public static class HostsBlock
{
    // ASCII only: the block must not depend on the file's encoding.
    public const string StartMarker = HostsText.RoutesStart;
    public const string EndMarker = HostsText.RoutesEnd;
    private const string Newline = "\r\n";

    /// <summary>Web route domains, each on 127.0.0.1 and ::1, sorted.</summary>
    public static string Merge(string current, IReadOnlyCollection<string> domains) =>
        Merge(current, [.. HostEntry.ForDomains(domains.Order(StringComparer.Ordinal))]);

    /// <summary>
    /// Returns <paramref name="current"/> with its Severino block replaced by one with
    /// <paramref name="entries"/>, in the given order. Without entries the block is removed.
    /// The new block takes the place of the old one, or goes at the end of the file.
    /// A block with no end marker runs to the end of the file. Idempotent.
    /// </summary>
    /// <param name="entries">Already validated, normalized and sorted (see HelperProtocol.TryNormalizeEntries).</param>
    public static string Merge(string current, IReadOnlyCollection<HostEntry> entries) =>
        Merge(current, entries, HostsText.RoutesStartPrefix, HostsText.RoutesStart, HostsText.RoutesEnd);

    /// <summary>The DNS block, with the same guarantees as the routes block.</summary>
    public static string MergeDns(string current, IReadOnlyCollection<HostEntry> entries) =>
        Merge(current, entries, HostsText.DnsStartPrefix, HostsText.DnsStart, HostsText.DnsEnd);

    private static string Merge(string current, IReadOnlyCollection<HostEntry> entries, string startPrefix, string startMarker, string endMarker)
    {
        var (rest, insertAt) = RemoveBlocks(current, startPrefix, endMarker);
        if (entries.Count == 0)
            return rest;

        if (insertAt < 0)
        {
            if (rest.Length > 0 && rest[^1] != '\n')
                rest += Newline;
            insertAt = rest.Length;
        }

        return rest.Insert(insertAt, Render(entries, startMarker, endMarker));
    }

    private static (string Remainder, int InsertAt) RemoveBlocks(string text, string startPrefix, string endMarker)
    {
        var rest = new StringBuilder(text.Length);
        var insertAt = -1;
        var inBlock = false;

        for (var pos = 0; pos < text.Length;)
        {
            var newline = text.IndexOf('\n', pos);
            var end = newline < 0 ? text.Length : newline + 1;
            var line = text.AsSpan(pos, end - pos).Trim();

            if (inBlock)
            {
                if (IsEnd(line, endMarker))
                    inBlock = false;
            }
            else if (line.StartsWith(startPrefix, StringComparison.Ordinal))
            {
                inBlock = true;
                if (insertAt < 0)
                    insertAt = rest.Length;
            }
            else
            {
                rest.Append(text, pos, end - pos);
            }

            pos = end;
        }

        return (rest.ToString(), insertAt);
    }

    // The routes end marker is a prefix of the DNS one: "# <<< Severino DNS" never ends the routes block.
    private static bool IsEnd(ReadOnlySpan<char> line, string endMarker) =>
        line.StartsWith(endMarker, StringComparison.Ordinal)
        && (endMarker != HostsText.RoutesEnd || !line.StartsWith(HostsText.DnsEnd, StringComparison.Ordinal));

    private static string Render(IEnumerable<HostEntry> entries, string startMarker, string endMarker)
    {
        var block = new StringBuilder();
        block.Append(startMarker).Append(Newline);
        // The address column is padded as it always was, so existing blocks come out byte for byte.
        foreach (var entry in entries)
            block.Append(entry.Address.PadRight(Math.Max(11, entry.Address.Length + 2))).Append(entry.Name).Append(Newline);
        block.Append(endMarker).Append(Newline);
        return block.ToString();
    }
}
