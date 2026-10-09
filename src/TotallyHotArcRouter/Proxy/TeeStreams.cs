namespace TotallyHot.ArcRouter.Proxy;

/// <summary>Receives a copy of bytes as they pass through a tee; must not retain the span.</summary>
/// <param name="bytes">The bytes that just passed.</param>
internal delegate void ByteSink(ReadOnlySpan<byte> bytes);

/// <summary>
/// A read-through stream that copies every byte its consumer reads to a <see cref="ByteSink"/>, so the client's
/// request is captured exactly as sent while <c>RequestInterceptor</c> decodes it, and a translated turn's
/// provider response is captured as the translator reads it. The sink must not throw, so the read the proxy
/// depends on is never affected by capture. Disposing this wrapper leaves the wrapped stream open: its owner
/// closes it.
/// </summary>
/// <param name="inner">The stream being read.</param>
/// <param name="sink">Receives a copy of every byte read.</param>
internal sealed class TeeReadStream(Stream inner, ByteSink sink) : Stream
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
        if (read > 0) sink(buffer[..read]);
        return read;
    }

    /// <inheritdoc/>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0) sink(buffer.Span[..read]);
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
/// A write-through stream that forwards every write to the client first and then copies the same bytes to a
/// <see cref="ByteSink"/>, so the capture holds exactly what was relayed. Forwarding comes first and the sink
/// only queues the copy, so capture can delay or break neither the relay nor a flush. A write the client
/// side rejects (the client left, the connection reset) sets <see cref="Faulted"/> and is rethrown unchanged,
/// which is how capture learns that the relayed body is a prefix. Disposing this wrapper leaves the wrapped
/// stream open: the server owns the response body.
/// </summary>
/// <param name="inner">The client response body.</param>
/// <param name="sink">Receives a copy of every byte written.</param>
internal sealed class TeeWriteStream(Stream inner, ByteSink sink) : Stream
{
    /// <summary>Gets a value indicating whether a write or flush to the client failed, so the relay is incomplete.</summary>
    public bool Faulted { get; private set; }

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
    public override void Flush()
    {
        try
        {
            inner.Flush();
        }
        catch
        {
            Faulted = true;
            throw;
        }
    }

    /// <inheritdoc/>
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        try
        {
            await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Faulted = true;
            throw;
        }
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        try
        {
            inner.Write(buffer);
        }
        catch
        {
            Faulted = true;
            throw;
        }

        sink(buffer);
    }

    /// <inheritdoc/>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc/>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Faulted = true;
            throw;
        }

        sink(buffer.Span);
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();
}
