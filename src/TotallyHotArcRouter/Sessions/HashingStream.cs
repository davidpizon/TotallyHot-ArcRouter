using System.Security.Cryptography;

namespace TotallyHot.ArcRouter.Sessions;

/// <summary>
/// A write-only pass-through stream that SHA-256s every byte it forwards. Capture places it between the secret
/// obscurer and the compressor so the hash is of the stored plaintext (after obscuring) and is produced in the
/// same pass that stores it; export places it in front of a zip entry so a body is hashed as it is written.
/// Not thread-safe; one writer drives it.
/// </summary>
internal sealed class HashingStream : Stream
{
    private readonly Stream _output;
    private readonly bool _leaveOpen;
    private IncrementalHash? _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private byte[]? _digest;
    private long _length;

    /// <summary>
    /// Initializes a new instance of the <see cref="HashingStream"/> class.
    /// </summary>
    /// <param name="output">Receives the bytes written.</param>
    /// <param name="leaveOpen">Whether disposing this stream leaves <paramref name="output"/> open.</param>
    public HashingStream(Stream output, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
        _leaveOpen = leaveOpen;
    }

    /// <summary>Gets how many bytes have been written through the stream.</summary>
    public long BytesWritten => _length;

    /// <inheritdoc/>
    public override bool CanRead => false;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => _hash is not null;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// Ends the hash. Idempotent; further writes are refused once it has run.
    /// </summary>
    /// <returns>The 32-byte SHA-256 of everything written.</returns>
    public byte[] FinishHash()
    {
        if (_digest is not null) return _digest;
        ObjectDisposedException.ThrowIf(_hash is null, this);
        _digest = _hash.GetHashAndReset();
        _hash.Dispose();
        _hash = null;
        return _digest;
    }

    /// <inheritdoc/>
    public override void Flush() => _output.Flush();

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_hash is null, this);
        _output.Write(buffer);
        _hash.AppendData(buffer);
        _length += buffer.Length;
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash?.Dispose();
            _hash = null;
            if (!_leaveOpen) _output.Dispose();
        }

        base.Dispose(disposing);
    }
}
