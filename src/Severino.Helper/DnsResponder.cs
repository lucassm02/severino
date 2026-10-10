using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Severino.Helper;

/// <summary>
/// The smallest DNS server that does the job: one question per message, A and AAAA answers for
/// the names <see cref="Lookup"/> knows, NODATA for other types of those names, and NXDOMAIN for
/// anything else. Windows only sends it the suffixes of the NRPT rules, so there is no recursion
/// and nothing is forwarded.
/// </summary>
public static class DnsResponder
{
    private const ushort TypeA = 1;
    private const ushort TypeAaaa = 28;
    private const ushort ClassIn = 1;
    private const uint Ttl = 5; // short: the person may change the entry any moment

    /// <summary>The addresses for a lowercase name, or null when it is not ours.</summary>
    public delegate IReadOnlyList<IPAddress>? Lookup(string name);

    /// <summary>The response for <paramref name="query"/>, or null when it is not a query worth answering.</summary>
    public static byte[]? Answer(ReadOnlySpan<byte> query, Lookup lookup)
    {
        if (query.Length < 12)
            return null;
        var flags = BinaryPrimitives.ReadUInt16BigEndian(query[2..]);
        var questions = BinaryPrimitives.ReadUInt16BigEndian(query[4..]);
        if ((flags & 0x8000) != 0 || questions != 1) // a response, or not exactly one question
            return null;

        var offset = 12;
        if (!TryReadName(query, ref offset, out var name) || offset + 4 > query.Length)
            return null;
        var type = BinaryPrimitives.ReadUInt16BigEndian(query[offset..]);
        var @class = BinaryPrimitives.ReadUInt16BigEndian(query[(offset + 2)..]);
        var questionEnd = offset + 4;

        var addresses = @class == ClassIn ? lookup(name.ToLowerInvariant()) : null;
        var family = type == TypeA ? AddressFamily.InterNetwork : type == TypeAaaa ? AddressFamily.InterNetworkV6 : (AddressFamily?)null;
        var answers = family is { } f && addresses is not null ? addresses.Where(a => a.AddressFamily == f).ToList() : [];

        using var response = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, BinaryPrimitives.ReadUInt16BigEndian(query));
        // QR, AA, and RD as asked; NXDOMAIN when the name is not ours at all.
        var responseFlags = (ushort)(0x8400 | (flags & 0x0100) | (addresses is null ? 3 : 0));
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], responseFlags);
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(header[6..], (ushort)answers.Count);
        response.Write(header);
        response.Write(query[12..questionEnd]);

        Span<byte> record = stackalloc byte[12];
        foreach (var address in answers)
        {
            BinaryPrimitives.WriteUInt16BigEndian(record, 0xC00C); // the name, pointing at the question
            BinaryPrimitives.WriteUInt16BigEndian(record[2..], type);
            BinaryPrimitives.WriteUInt16BigEndian(record[4..], ClassIn);
            BinaryPrimitives.WriteUInt32BigEndian(record[6..], Ttl);
            var bytes = address.GetAddressBytes();
            BinaryPrimitives.WriteUInt16BigEndian(record[10..], (ushort)bytes.Length);
            response.Write(record);
            response.Write(bytes);
        }
        return response.ToArray();
    }

    /// <summary>A name made of labels; compression pointers are not expected in a question and are refused.</summary>
    private static bool TryReadName(ReadOnlySpan<byte> message, ref int offset, out string name)
    {
        var builder = new StringBuilder();
        name = "";
        while (offset < message.Length)
        {
            var length = message[offset++];
            if (length == 0)
            {
                name = builder.ToString();
                return name.Length > 0;
            }
            if (length > 63 || offset + length > message.Length || builder.Length + length > 253)
                return false;
            if (builder.Length > 0)
                builder.Append('.');
            builder.Append(Encoding.ASCII.GetString(message.Slice(offset, length)));
            offset += length;
        }
        return false;
    }
}
