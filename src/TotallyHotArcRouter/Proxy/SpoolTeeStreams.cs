using TotallyHot.ArcRouter.Sessions;

namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// A read-through stream that copies every byte its consumer reads into a <see cref="SessionBodySpool"/>, so
/// the client's request is captured exactly as sent while <c>RequestInterceptor</c> decodes it. The spool never
/// throws for a capture failure (it abandons itself), so the read the proxy depends on is never affected by
/// capture. Disposing this wrapper leaves the wrapped stream open: the server owns the request body.
/// </summary>
/// <param name="inner">The stream being read.</param>
/// <param name="spool">Receives a copy of every byte read.</param>
internal sealed class SpoolTeeReadStream(Stream inner, SessionBodySpool spool) : Stream
{
    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override void Flush()
    {
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);
        if (read > 0) spool.TryWrite(buffer[..read]);
        return read;
    }

    /// <inheritdoc/>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0) spool.TryWrite(buffer.Span[..read]);
        return read;
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// A write-through stream that forwards every write to the client first and then copies the same bytes into a
/// <see cref="SessionBodySpool"/>, so the capture holds exactly what was relayed. Forwarding comes first and a
/// spool failure only abandons the capture, so capture can delay or break neither the relay nor a flush.
/// Disposing this wrapper leaves the wrapped stream open: the server owns the response body.
/// </summary>
/// <param name="inner">The client response body.</param>
/// <param name="spool">Receives a copy of every byte written.</param>
internal sealed class SpoolTeeWriteStream(Stream inner, SessionBodySpool spool) : Stream
{
    /// <inheritdoc/>
    public override bool CanRead => false;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => true;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override void Flush() => inner.Flush();

    /// <inheritdoc/>
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        spool.TryWrite(buffer);
    }

    /// <inheritdoc/>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc/>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        spool.TryWrite(buffer.Span);
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();
}
