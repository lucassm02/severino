using System.Text;
using Severino.Contracts;

namespace Severino.Helper;

/// <summary>
/// Replaces the Severino block in the text of a hosts file and leaves every other byte alone.
/// </summary>
public static class HostsBlock
{
    // ASCII only: the block must not depend on the file's encoding.
    public const string StartMarker = "# >>> Severino managed block (do not edit)";
    public const string EndMarker = "# <<< Severino";

    private const string StartPrefix = "# >>> Severino";
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
    public static string Merge(string current, IReadOnlyCollection<HostEntry> entries)
    {
        var (rest, insertAt) = RemoveBlocks(current);
        if (entries.Count == 0)
            return rest;

        if (insertAt < 0)
        {
            if (rest.Length > 0 && rest[^1] != '\n')
                rest += Newline;
            insertAt = rest.Length;
        }

        return rest.Insert(insertAt, Render(entries));
    }

    private static (string Remainder, int InsertAt) RemoveBlocks(string text)
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
                if (line.StartsWith(EndMarker, StringComparison.Ordinal))
                    inBlock = false;
            }
            else if (line.StartsWith(StartPrefix, StringComparison.Ordinal))
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

    private static string Render(IEnumerable<HostEntry> entries)
    {
        var block = new StringBuilder();
        block.Append(StartMarker).Append(Newline);
        // The address column is padded as it always was, so existing blocks come out byte for byte.
        foreach (var entry in entries)
            block.Append(entry.Address.PadRight(Math.Max(11, entry.Address.Length + 2))).Append(entry.Name).Append(Newline);
        block.Append(EndMarker).Append(Newline);
        return block.ToString();
    }
}
