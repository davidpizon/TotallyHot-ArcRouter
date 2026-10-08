using System.Buffers.Binary;
using System.Security.Cryptography;

namespace TotallyHot.ArcRouter.Sessions;

/// <summary>
/// One encrypted, append-mostly session file on disk (ADR-0019). The header names the
/// <c>archive_session_id</c>; each frame stores one sealed body for a turn. Callers hold the
/// plaintext session key in memory and persist only its wrap blob in SQLite. The file copies the
/// key it is given, so disposing the file zeroes its own copy and never the caller's array. Every
/// frame is written with a single write under a lock, and every frame's header fields are bound to
/// its ciphertext as AES-GCM associated data, so frames cannot be reordered, relabelled, or moved
/// between sessions without failing authentication.
/// </summary>
public sealed class SessionFile : IDisposable
{
    private const ushort FormatVersion = 1;
    private const int IdLength = 16;
    private const int VersionLength = 2;
    private const int FrameHeaderLength = 1 + 1 + 4 + IdLength; // kind | flags | turnSequence | archiveTurnId
    private const byte FlagMissing = 1;

    private static readonly byte[] Magic = "THSESS01"u8.ToArray();
    private static readonly int DataStart = Magic.Length + IdLength + VersionLength;

    private readonly FileStream _stream;
    private readonly byte[] _sessionKey;
    private readonly Lock _gate = new();
    private bool _disposed;

    private SessionFile(FileStream stream, Guid archiveSessionId, byte[] sessionKey)
    {
        _stream = stream;
        ArchiveSessionId = archiveSessionId;
        _sessionKey = (byte[])sessionKey.Clone();
    }

    /// <summary>The stable cross-machine session id baked into the file header.</summary>
    public Guid ArchiveSessionId { get; }

    /// <summary>
    /// Creates a new session file with a fresh header. The caller owns key custody and must wrap
    /// <paramref name="sessionKey"/> before the file is considered durable. The key is copied; the
    /// caller's array is left untouched.
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
            Span<byte> header = stackalloc byte[Magic.Length + IdLength + VersionLength];
            Magic.CopyTo(header);
            archiveSessionId.TryWriteBytes(header[Magic.Length..]);
            BinaryPrimitives.WriteUInt16LittleEndian(header[(Magic.Length + IdLength)..], FormatVersion);
            stream.Write(header);
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
    /// <paramref name="expectedArchiveSessionId"/> when provided. A trailing partial frame left by a
    /// crash mid-append is truncated so later appends start on a frame boundary (ADR-0019: a body is
    /// complete or absent, never a prefix). The key is copied; the caller's array is left untouched.
    /// </summary>
    /// <param name="path">Absolute path of the session file.</param>
    /// <param name="sessionKey">Unwrapped session key.</param>
    /// <param name="expectedArchiveSessionId">Optional id check against the header.</param>
    /// <returns>An open session file positioned at end-of-file for appends.</returns>
    /// <exception cref="InvalidDataException">When the header is malformed, unsupported, or for another session.</exception>
    public static SessionFile Open(string path, byte[] sessionKey, Guid? expectedArchiveSessionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        SessionKeyMaterial.ValidateKeyLength(sessionKey);

        var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            Span<byte> header = stackalloc byte[Magic.Length + IdLength + VersionLength];
            if (!TryReadPartial(stream, header) || !header[..Magic.Length].SequenceEqual(Magic))
            {
                throw new InvalidDataException("Session file magic mismatch.");
            }

            var archiveSessionId = new Guid(header.Slice(Magic.Length, IdLength));
            if (expectedArchiveSessionId is { } expected && expected != archiveSessionId)
            {
                throw new InvalidDataException("Session file archive id does not match the index.");
            }

            var version = BinaryPrimitives.ReadUInt16LittleEndian(header[(Magic.Length + IdLength)..]);
            if (version != FormatVersion)
            {
                throw new InvalidDataException($"Unsupported session file version {version}.");
            }

            var end = FindEndOfLastCompleteFrame(stream);
            if (end < stream.Length)
            {
                stream.SetLength(end);
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
    /// Appends one sealed body frame and flushes to disk. The frame is sealed and assembled before
    /// anything is written, then written with a single call, so a failure never leaves a header
    /// without its payload.
    /// </summary>
    /// <param name="turnSequence">Zero-based turn order inside the session.</param>
    /// <param name="kind">Which body this frame holds.</param>
    /// <param name="plaintext">Raw body bytes; an empty span is a legitimately empty body. Use
    /// <see cref="AppendMissingBody"/> when capture failed.</param>
    /// <param name="archiveTurnId">The turn's stable archive id.</param>
    public void AppendBody(uint turnSequence, SessionBodyKind kind, ReadOnlySpan<byte> plaintext, Guid archiveTurnId)
    {
        ValidateKind(kind);

        Span<byte> header = stackalloc byte[FrameHeaderLength];
        WriteFrameHeader(header, kind, flags: 0, turnSequence, archiveTurnId);

        // Frame: header | nonce | tag | cipherLen(4) | cipher. Sealed before taking the lock: it is the expensive part.
        var sealedPayload = SessionRecordCodec.Seal(_sessionKey, plaintext, BuildAssociatedData(header));
        var frame = new byte[FrameHeaderLength + SessionKeyMaterial.NonceLengthBytes
            + SessionKeyMaterial.TagLengthBytes + sizeof(uint) + sealedPayload.Ciphertext.Length];
        var cursor = frame.AsSpan();
        header.CopyTo(cursor);
        cursor = cursor[FrameHeaderLength..];
        sealedPayload.Nonce.CopyTo(cursor);
        cursor = cursor[SessionKeyMaterial.NonceLengthBytes..];
        sealedPayload.Tag.CopyTo(cursor);
        cursor = cursor[SessionKeyMaterial.TagLengthBytes..];
        BinaryPrimitives.WriteUInt32LittleEndian(cursor, (uint)sealedPayload.Ciphertext.Length);
        sealedPayload.Ciphertext.CopyTo(cursor[sizeof(uint)..]);

        AppendFrame(frame);
    }

    /// <summary>
    /// Appends a missing-body marker so export can record absence without inventing empty content.
    /// </summary>
    /// <param name="turnSequence">Zero-based turn order inside the session.</param>
    /// <param name="kind">Which body is missing.</param>
    /// <param name="archiveTurnId">The turn's stable archive id.</param>
    public void AppendMissingBody(uint turnSequence, SessionBodyKind kind, Guid archiveTurnId)
    {
        ValidateKind(kind);

        var frame = new byte[FrameHeaderLength];
        WriteFrameHeader(frame, kind, FlagMissing, turnSequence, archiveTurnId);
        AppendFrame(frame);
    }

    /// <summary>
    /// Reads every body frame from the file start (after the header) for tests and export rebuilds.
    /// The stream is returned to end-of-file even when a frame fails to read or authenticate, so a
    /// later append can never overwrite existing frames.
    /// </summary>
    /// <returns>Decrypted frames in file order; missing bodies have <see langword="null"/> plaintext.</returns>
    /// <exception cref="InvalidDataException">When a frame is malformed.</exception>
    /// <exception cref="CryptographicException">When a frame fails authentication.</exception>
    public IReadOnlyList<SessionBodyFrame> ReadAllBodies()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var results = new List<SessionBodyFrame>();
            try
            {
                _stream.Seek(DataStart, SeekOrigin.Begin);

                Span<byte> header = stackalloc byte[FrameHeaderLength];
                Span<byte> lenBytes = stackalloc byte[sizeof(uint)];
                while (TryReadExact(_stream, header))
                {
                    var kind = (SessionBodyKind)header[0];
                    ValidateKind(kind);
                    var flags = header[1];
                    var turnSequence = BinaryPrimitives.ReadUInt32LittleEndian(header[2..6]);
                    var archiveTurnId = new Guid(header[6..FrameHeaderLength]);

                    if (flags == FlagMissing)
                    {
                        results.Add(new SessionBodyFrame(turnSequence, kind, archiveTurnId, Plaintext: null));
                        continue;
                    }

                    if (flags != 0)
                    {
                        throw new InvalidDataException($"Unknown session frame flags {flags}.");
                    }

                    var nonce = new byte[SessionKeyMaterial.NonceLengthBytes];
                    var tag = new byte[SessionKeyMaterial.TagLengthBytes];
                    ReadExact(_stream, nonce);
                    ReadExact(_stream, tag);
                    ReadExact(_stream, lenBytes);
                    var cipherLen = BinaryPrimitives.ReadUInt32LittleEndian(lenBytes);
                    if (cipherLen > _stream.Length - _stream.Position)
                    {
                        throw new InvalidDataException("Session frame length exceeds the file.");
                    }

                    var ciphertext = new byte[cipherLen];
                    ReadExact(_stream, ciphertext);

                    var plaintext = SessionRecordCodec.Open(
                        _sessionKey,
                        new EncryptedSessionPayload(nonce, tag, ciphertext),
                        BuildAssociatedData(header));
                    results.Add(new SessionBodyFrame(turnSequence, kind, archiveTurnId, plaintext));
                }
            }
            finally
            {
                _stream.Seek(0, SeekOrigin.End);
            }

            return results;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            CryptographicOperations.ZeroMemory(_sessionKey);
            _stream.Dispose();
        }
    }

    private void AppendFrame(ReadOnlySpan<byte> frame)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _stream.Write(frame);
            _stream.Flush(flushToDisk: true);
        }
    }

    /// <summary>
    /// Associated data binding a frame's ciphertext to its header and to this session, so frames
    /// cannot be relabelled, reordered, or transplanted from another session file.
    /// </summary>
    private byte[] BuildAssociatedData(ReadOnlySpan<byte> frameHeader)
    {
        var aad = new byte[IdLength + FrameHeaderLength];
        ArchiveSessionId.TryWriteBytes(aad);
        frameHeader.CopyTo(aad.AsSpan(IdLength));
        return aad;
    }

    private static void WriteFrameHeader(
        Span<byte> header, SessionBodyKind kind, byte flags, uint turnSequence, Guid archiveTurnId)
    {
        header[0] = (byte)kind;
        header[1] = flags;
        BinaryPrimitives.WriteUInt32LittleEndian(header[2..6], turnSequence);
        archiveTurnId.TryWriteBytes(header[6..FrameHeaderLength]);
    }

    private static void ValidateKind(SessionBodyKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new InvalidDataException($"Unknown session body kind {(byte)kind}.");
        }
    }

    /// <summary>
    /// Walks frame lengths (without decrypting) from the end of the file header and returns the offset
    /// just past the last complete frame. Anything after it is a torn append.
    /// </summary>
    private static long FindEndOfLastCompleteFrame(FileStream stream)
    {
        long good = DataStart;
        stream.Seek(DataStart, SeekOrigin.Begin);

        Span<byte> header = stackalloc byte[FrameHeaderLength];
        Span<byte> prefix = stackalloc byte[SessionKeyMaterial.NonceLengthBytes + SessionKeyMaterial.TagLengthBytes + sizeof(uint)];
        while (TryReadPartial(stream, header))
        {
            if (header[1] != FlagMissing)
            {
                if (!TryReadPartial(stream, prefix))
                {
                    break;
                }

                var cipherLen = BinaryPrimitives.ReadUInt32LittleEndian(
                    prefix[(SessionKeyMaterial.NonceLengthBytes + SessionKeyMaterial.TagLengthBytes)..]);
                if (cipherLen > stream.Length - stream.Position)
                {
                    break;
                }

                stream.Seek(cipherLen, SeekOrigin.Current);
            }

            good = stream.Position;
        }

        return good;
    }

    private static void ReadExact(Stream stream, Span<byte> buffer)
    {
        if (!TryReadExact(stream, buffer))
        {
            throw new EndOfStreamException();
        }
    }

    /// <summary>Fills <paramref name="buffer"/>; returns false on a clean EOF before the first byte.</summary>
    private static bool TryReadExact(Stream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer[read..]);
            if (n == 0)
            {
                if (read == 0) return false;
                throw new EndOfStreamException();
            }

            read += n;
        }

        return true;
    }

    /// <summary>Like <see cref="TryReadExact"/> but also returns false (instead of throwing) on a short read.</summary>
    private static bool TryReadPartial(Stream stream, Span<byte> buffer)
    {
        try
        {
            return TryReadExact(stream, buffer);
        }
        catch (EndOfStreamException)
        {
            return false;
        }
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
