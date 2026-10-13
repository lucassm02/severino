namespace Severino.Proxy;

/// <summary>
/// Passes everything through to <paramref name="inner"/>, read or written, counting the bytes and,
/// with <paramref name="captureLimit"/> above zero, keeping a copy of the first ones. Never waits
/// for more data than the caller asked for: streaming, SSE and HMR go through as before.
/// </summary>
internal sealed class TapStream(Stream inner, int captureLimit) : Stream
{
    private readonly MemoryStream? _copy = captureLimit > 0 ? new() : null;

    public long Count { get; private set; }

    /// <summary>More came than <see cref="Captured"/> holds.</summary>
    public bool Cut { get; private set; }

    /// <summary>The first bytes, up to the limit; null when not capturing.</summary>
    public byte[]? Captured => _copy?.ToArray();

    public override bool CanRead => inner.CanRead;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);
        Tap(buffer[..read]);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        Tap(buffer.Span[..read]);
        return read;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Tap(buffer);
        inner.Write(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Tap(buffer.Span);
        return inner.WriteAsync(buffer, cancellationToken);
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    private void Tap(ReadOnlySpan<byte> data)
    {
        Count += data.Length;
        if (_copy is null || data.Length == 0)
            return;
        var room = captureLimit - (int)_copy.Length;
        var take = Math.Min(Math.Max(room, 0), data.Length);
        if (take > 0)
            _copy.Write(data[..take]);
        if (take < data.Length)
            Cut = true;
    }
}
