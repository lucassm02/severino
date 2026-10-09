namespace Severino.Contracts;

/// <summary>Newline-terminated messages with a size cap, shared by the pipe client and server.</summary>
public static class MessageFraming
{
    public static async Task WriteAsync(Stream stream, byte[] message, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(message, cancellationToken);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Reads up to the next '\n', excluding it. Anything after the newline is dropped: each side
    /// sends a single message per connection.
    /// </summary>
    /// <exception cref="InvalidDataException">The message passes <paramref name="maxBytes"/> or the stream ends first.</exception>
    public static async Task<byte[]> ReadAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
                throw new InvalidDataException("A conexão terminou antes do fim da mensagem.");

            var newline = Array.IndexOf(chunk, (byte)'\n', 0, read);
            var take = newline >= 0 ? newline : read;
            if (buffer.Length + take > maxBytes)
                throw new InvalidDataException($"A mensagem passa de {maxBytes} bytes.");

            buffer.Write(chunk, 0, take);
            if (newline >= 0)
                return buffer.ToArray();
        }
    }
}
