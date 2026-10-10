using System.Net;
using System.Text;

namespace Severino.Contracts;

/// <summary>
/// A line of the hosts file outside Severino's blocks, as the DNS tab shows it.
/// </summary>
/// <param name="Text">The line as it is in the file, without the line break: what the Helper checks before changing it.</param>
/// <param name="Origin">The comment that opens the group the line is in, like "# Added by Docker Desktop".</param>
/// <param name="Note">Severino's comment above the line, when it was edited or removed through Severino.</param>
/// <param name="Removed">Commented out by Severino; shown so the person knows where it went.</param>
public sealed record HostsLine(string Text, string Address, IReadOnlyList<string> Names, string? Origin, string? Note, bool Removed);

/// <summary>
/// The hosts file as text: Severino's two blocks, the lines outside them, and how Severino
/// changes one of those lines when the person asks, always leaving a comment above it.
/// ASCII-only markers, so nothing depends on the file's encoding.
/// </summary>
public static class HostsText
{
    public const string RoutesStart = "# >>> Severino managed block (do not edit)";
    public const string RoutesEnd = "# <<< Severino";
    public const string RoutesStartPrefix = "# >>> Severino managed";

    public const string DnsStart = "# >>> Severino DNS (do not edit)";
    public const string DnsEnd = "# <<< Severino DNS";
    public const string DnsStartPrefix = "# >>> Severino DNS";

    /// <summary>Starts the comment Severino leaves above a line it changed that was not its own.</summary>
    public const string NotePrefix = "# Severino:";

    /// <summary>The lines with an address and names outside Severino's blocks, in file order.</summary>
    public static IReadOnlyList<HostsLine> ExternalLines(string text)
    {
        var result = new List<HostsLine>();
        string? origin = null;
        string? note = null;
        foreach (var current in Lines(text))
        {
            var line = current.Text;
            var trimmed = line.Trim();
            if (current.InBlock)
            {
                note = null;
                continue;
            }
            if (trimmed.Length == 0)
            {
                origin = null;
                note = null;
                continue;
            }
            if (trimmed.StartsWith(NotePrefix, StringComparison.Ordinal))
            {
                note = trimmed;
                continue;
            }
            if (trimmed.StartsWith('#'))
            {
                // A commented-out line under Severino's note is one it removed.
                if (note is not null && TryParseEntry(trimmed.TrimStart('#').Trim(), out var removedAddress, out var removedNames))
                {
                    result.Add(new HostsLine(line, removedAddress, removedNames, origin, note, Removed: true));
                    note = null;
                    continue;
                }
                origin = trimmed;
                note = null;
                continue;
            }
            if (TryParseEntry(trimmed, out var address, out var names))
                result.Add(new HostsLine(line, address, names, origin, note, Removed: false));
            note = null;
        }
        return result;
    }

    /// <summary>
    /// <paramref name="text"/> with the line exactly equal to <paramref name="original"/> (outside
    /// Severino's blocks) replaced by <paramref name="replacement"/>, or commented out when it is
    /// null, under a note that says the line was not Severino's, what happened and when. A note
    /// already there is updated and keeps the line as it first was. Null when the line is not
    /// there any more: the file changed since it was read.
    /// </summary>
    public static string? ReplaceLine(string text, string original, string? replacement, DateTime when)
    {
        var lines = Lines(text).ToList();
        var index = lines.FindIndex(l => !l.InBlock && l.Text == original);
        if (index < 0 || original.Trim().StartsWith('#'))
            return null;

        var newline = LineBreak(lines, index);
        var hasNote = index > 0 && !lines[index - 1].InBlock && lines[index - 1].Text.Trim().StartsWith(NotePrefix, StringComparison.Ordinal);
        var before = (hasNote ? FirstVersion(lines[index - 1].Text) : null) ?? original.Trim();
        var stamp = when.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var note = replacement is null
            ? $"{NotePrefix} esta linha nao foi criada pelo Severino; removida em {stamp}. Antes: {before}"
            : $"{NotePrefix} esta linha nao foi criada pelo Severino; editada em {stamp}. Antes: {before}";
        var newLine = replacement ?? "# " + original.Trim();

        var output = new StringBuilder(text.Length + note.Length + 8);
        for (var i = 0; i < lines.Count; i++)
        {
            if (hasNote && i == index - 1)
                continue;
            if (i == index)
            {
                output.Append(note).Append(newline);
                output.Append(newLine).Append(lines[i].Break.Length > 0 ? lines[i].Break : "");
                continue;
            }
            output.Append(lines[i].Text).Append(lines[i].Break);
        }
        return output.ToString();
    }

    /// <summary>One hosts line for an address and its names, padded like the blocks.</summary>
    public static string Render(string address, IEnumerable<string> names) =>
        address.PadRight(Math.Max(11, address.Length + 2)) + string.Join(' ', names);

    private static string? FirstVersion(string note)
    {
        var at = note.IndexOf("Antes: ", StringComparison.Ordinal);
        return at < 0 ? null : note[(at + "Antes: ".Length)..].Trim();
    }

    private static bool TryParseEntry(string line, out string address, out IReadOnlyList<string> names)
    {
        address = "";
        names = [];
        var hash = line.IndexOf('#');
        var content = hash >= 0 ? line[..hash] : line;
        var parts = content.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !IPAddress.TryParse(parts[0], out _))
            return false;
        address = parts[0];
        names = parts[1..];
        return true;
    }

    private static string LineBreak(List<Line> lines, int index)
    {
        // The file's own style: the break of this line, or of any line, or CRLF.
        return lines[index].Break.Length > 0 ? lines[index].Break
            : lines.FirstOrDefault(l => l.Break.Length > 0)?.Break ?? "\r\n";
    }

    private sealed record Line(string Text, string Break, bool InBlock);

    /// <summary>Each line without its break, and whether it belongs to one of Severino's blocks.</summary>
    private static IEnumerable<Line> Lines(string text)
    {
        string? blockEnd = null;
        for (var pos = 0; pos < text.Length;)
        {
            var newline = text.IndexOf('\n', pos);
            var end = newline < 0 ? text.Length : newline + 1;
            var raw = text[pos..end];
            var content = raw.TrimEnd('\r', '\n');
            var lineBreak = raw[content.Length..];
            var trimmed = content.Trim();

            if (blockEnd is not null)
            {
                yield return new Line(content, lineBreak, true);
                if (trimmed == blockEnd || (blockEnd == RoutesEnd && trimmed.StartsWith(RoutesEnd, StringComparison.Ordinal) && !trimmed.StartsWith(DnsEnd, StringComparison.Ordinal)))
                    blockEnd = null;
            }
            else if (trimmed.StartsWith(DnsStartPrefix, StringComparison.Ordinal))
            {
                blockEnd = DnsEnd;
                yield return new Line(content, lineBreak, true);
            }
            else if (trimmed.StartsWith(RoutesStartPrefix, StringComparison.Ordinal))
            {
                blockEnd = RoutesEnd;
                yield return new Line(content, lineBreak, true);
            }
            else
            {
                yield return new Line(content, lineBreak, false);
            }
            pos = end;
        }
    }
}
