using System.Buffers.Binary;

namespace TotallyHot.ArcRouter.Sessions;

/// <summary>
/// One encrypted, append-only session file on disk (ADR-0019). The header names the
/// <c>archive_session_id</c>; each frame stores one sealed body for a turn. Callers hold the
/// plaintext session key in memory and persist only its wrap blob in SQLite.
/// </summary>
public sealed class SessionFile : IDisposable
{
    private static readonly byte[] Magic = "THSESS01"u8.ToArray();

    private readonly FileStream _stream;
    private readonly byte[] _sessionKey;
    private bool _disposed;

    private SessionFile(FileStream stream, Guid archiveSessionId, byte[] sessionKey)
    {
        _stream = stream;
        ArchiveSessionId = archiveSessionId;
        _sessionKey = sessionKey;
    }

    /// <summary>The stable cross-machine session id baked into the file header.</summary>
    public Guid ArchiveSessionId { get; }

    /// <summary>
    /// Creates a new session file with a fresh header. The caller owns key custody and must wrap
    /// <paramref name="sessionKey"/> before the file is considered durable.
    /// </summary>
    /// <param name="path">Absolute path under the protected sessions folder.</param>
    /// <param name="archiveSessionId">The session's archive id.</param>
    /// <param name="sessionKey">32-byte AES session key.</param>
    /// <returns>An open writer positioned after the header.</returns>
    public static SessionFile Create(string path, Guid archiveSessionId, byte[] sessionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        SessionKeyMaterial.ValidateKeyLength(sessionKey);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        try
        {
            stream.Write(Magic);
            Span<byte> idBytes = stackalloc byte[16];
            archiveSessionId.TryWriteBytes(idBytes);
            stream.Write(idBytes);
            Span<byte> versionBytes = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(versionBytes, 1);
            stream.Write(versionBytes);
            stream.Flush(flushToDisk: true);
            return new SessionFile(stream, archiveSessionId, sessionKey);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens an existing session file for append and read. The header's archive id must match
    /// <paramref name="expectedArchiveSessionId"/> when provided.
    /// </summary>
    /// <param name="path">Absolute path of the session file.</param>
    /// <param name="sessionKey">Unwrapped session key.</param>
    /// <param name="expectedArchiveSessionId">Optional id check against the header.</param>
    /// <returns>An open session file positioned at end-of-file for appends.</returns>
    public static SessionFile Open(string path, byte[] sessionKey, Guid? expectedArchiveSessionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        SessionKeyMaterial.ValidateKeyLength(sessionKey);

        var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            Span<byte> magic = stackalloc byte[8];
            ReadExact(stream, magic);
            if (!magic.SequenceEqual(Magic))
            {
                throw new InvalidDataException("Session file magic mismatch.");
            }

            Span<byte> idBytes = stackalloc byte[16];
            ReadExact(stream, idBytes);
            var archiveSessionId = new Guid(idBytes);
            if (expectedArchiveSessionId is { } expected && expected != archiveSessionId)
            {
                throw new InvalidDataException("Session file archive id does not match the index.");
            }

            Span<byte> versionBytes = stackalloc byte[2];
            ReadExact(stream, versionBytes);
            var version = BinaryPrimitives.ReadUInt16LittleEndian(versionBytes);
            if (version != 1)
            {
                throw new InvalidDataException($"Unsupported session file version {version}.");
            }

            stream.Seek(0, SeekOrigin.End);
            return new SessionFile(stream, archiveSessionId, sessionKey);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Appends one sealed body frame and flushes to disk. A missing body writes a flag-only frame
    /// with no ciphertext (ADR-0019: complete or absent, never a prefix).
    /// </summary>
    /// <param name="turnSequence">Zero-based turn order inside the session.</param>
    /// <param name="kind">Which body this frame holds.</param>
    /// <param name="plaintext">Raw body bytes, or <see langword="null"/> when capture failed.</param>
    /// <param name="archiveTurnId">The turn's stable archive id.</param>
    public void AppendBody(uint turnSequence, SessionBodyKind kind, ReadOnlySpan<byte> plaintext, Guid archiveTurnId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Frame: kind(1) | flags(1) | turnSequence(4) | archiveTurnId(16) | nonce|tag|cipherLen|cipher
        Span<byte> header = stackalloc byte[1 + 1 + 4 + 16];
        header[0] = (byte)kind;
        header[1] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(header[2..6], turnSequence);
        archiveTurnId.TryWriteBytes(header[6..22]);
        _stream.Write(header);

        var sealedPayload = SessionRecordCodec.Seal(_sessionKey, plaintext);
        _stream.Write(sealedPayload.Nonce);
        _stream.Write(sealedPayload.Tag);
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)sealedPayload.Ciphertext.Length);
        _stream.Write(len);
        _stream.Write(sealedPayload.Ciphertext);
        _stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Appends a missing-body marker so export can record absence without inventing empty content.
    /// </summary>
    /// <param name="turnSequence">Zero-based turn order inside the session.</param>
    /// <param name="kind">Which body is missing.</param>
    /// <param name="archiveTurnId">The turn's stable archive id.</param>
    public void AppendMissingBody(uint turnSequence, SessionBodyKind kind, Guid archiveTurnId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Span<byte> header = stackalloc byte[1 + 1 + 4 + 16];
        header[0] = (byte)kind;
        header[1] = 1; // missing
        BinaryPrimitives.WriteUInt32LittleEndian(header[2..6], turnSequence);
        archiveTurnId.TryWriteBytes(header[6..22]);
        _stream.Write(header);
        _stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Reads every body frame from the file start (after the header) for tests and export rebuilds.
    /// </summary>
    /// <returns>Decrypted frames in file order; missing bodies have <see langword="null"/> plaintext.</returns>
    public IReadOnlyList<SessionBodyFrame> ReadAllBodies()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var results = new List<SessionBodyFrame>();
        var dataStart = Magic.Length + 16 + 2;
        _stream.Seek(dataStart, SeekOrigin.Begin);

        Span<byte> header = stackalloc byte[22];
        while (TryReadExact(_stream, header))
        {
            var kind = (SessionBodyKind)header[0];
            var missing = header[1] == 1;
            var turnSequence = BinaryPrimitives.ReadUInt32LittleEndian(header[2..6]);
            var archiveTurnId = new Guid(header[6..22]);

            if (missing)
            {
                results.Add(new SessionBodyFrame(turnSequence, kind, archiveTurnId, Plaintext: null));
                continue;
            }

            var nonce = new byte[SessionKeyMaterial.NonceLengthBytes];
            var tag = new byte[SessionKeyMaterial.TagLengthBytes];
            Span<byte> lenBytes = stackalloc byte[4];
            ReadExact(_stream, nonce);
            ReadExact(_stream, tag);
            ReadExact(_stream, lenBytes);
            var cipherLen = BinaryPrimitives.ReadUInt32LittleEndian(lenBytes);
            var ciphertext = new byte[cipherLen];
            ReadExact(_stream, ciphertext);

            var plaintext = SessionRecordCodec.Open(
                _sessionKey,
                new EncryptedSessionPayload(nonce, tag, ciphertext));
            results.Add(new SessionBodyFrame(turnSequence, kind, archiveTurnId, plaintext));
        }

        _stream.Seek(0, SeekOrigin.End);
        return results;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicClear(_sessionKey);
        _stream.Dispose();
    }

    private static void CryptographicClear(byte[] key) =>
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);

    private static void ReadExact(Stream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer[read..]);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
    }

    private static bool TryReadExact(Stream stream, Span<byte> buffer)
    {
        var first = stream.Read(buffer[..1]);
        if (first == 0) return false;
        if (buffer.Length == 1) return true;
        ReadExact(stream, buffer[1..]);
        return true;
    }
}

/// <summary>
/// One decrypted body frame read from a <see cref="SessionFile"/>.
/// </summary>
/// <param name="TurnSequence">Zero-based turn order.</param>
/// <param name="Kind">Which body this is.</param>
/// <param name="ArchiveTurnId">Stable turn id.</param>
/// <param name="Plaintext">Obscured body bytes, or <see langword="null"/> when missing.</param>
public sealed record SessionBodyFrame(
    uint TurnSequence,
    SessionBodyKind Kind,
    Guid ArchiveTurnId,
    byte[]? Plaintext);
