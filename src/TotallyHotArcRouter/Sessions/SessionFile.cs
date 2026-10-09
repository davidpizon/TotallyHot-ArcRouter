using System.Buffers.Binary;
using System.Security.Cryptography;

namespace TotallyHot.ArcRouter.Sessions;

/// <summary>
/// One encrypted, append-mostly session file on disk (ADR-0019, format version 2). The header names the
/// <c>archive_session_id</c>; each frame holds one body for a turn as a run of sealed chunks, the last flagged
/// final. Callers hold the plaintext session key in memory and persist only its wrap blob in SQLite. The file
/// copies the key it is given, so disposing the file zeroes its own copy and never the caller's array. A frame
/// is written under a lock, chunk by chunk, and counts only once its final chunk is written; a frame cut short
/// by a crash has no final chunk and is cut away on the next open. Every chunk, including the lone chunk of a
/// missing-body marker, authenticates its frame header, the session id, the frame's zero-based position in the
/// file, its own index and its final flag as AES-GCM associated data, so chunks cannot be relabelled,
/// reordered, dropped, or moved between sessions without failing authentication.
/// </summary>
public sealed class SessionFile : IDisposable
{
    private const ushort FormatVersion = 2;
    private const int IdLength = 16;
    private const int VersionLength = 2;
    private const int FrameHeaderLength = 1 + 1 + 4 + IdLength; // kind | flags | turnSequence | archiveTurnId
    private const byte FlagMissing = 1;

    private static readonly byte[] Magic = "THSESS01"u8.ToArray();
    private static readonly int DataStart = Magic.Length + IdLength + VersionLength;

    private readonly FileStream _stream;
    private readonly byte[] _sessionKey;
    private readonly AesGcm _aes;
    private readonly Lock _gate = new();
    private ulong _frameCount;
    private bool _disposed;

    /// <summary>
    /// Wraps an already-positioned stream. Private so every instance comes from <see cref="Create"/> or
    /// <see cref="Open"/>, which validate the header and leave the stream at end-of-file.
    /// </summary>
    /// <param name="stream">The open file, positioned at end-of-file.</param>
    /// <param name="archiveSessionId">The id from the file header.</param>
    /// <param name="sessionKey">The key to copy; the caller's array is not retained.</param>
    /// <param name="frameCount">How many complete frames the file already holds.</param>
    private SessionFile(FileStream stream, Guid archiveSessionId, byte[] sessionKey, ulong frameCount)
    {
        _stream = stream;
        ArchiveSessionId = archiveSessionId;
        _sessionKey = (byte[])sessionKey.Clone();
        _aes = new AesGcm(_sessionKey, SessionKeyMaterial.TagLengthBytes);
        _frameCount = frameCount;
    }

    /// <summary>The stable cross-machine session id baked into the file header.</summary>
    public Guid ArchiveSessionId { get; }

    /// <summary>
    /// How many complete frames the file holds, counting any appended since it was opened. The index
    /// records this beside <see cref="Length"/> when a turn commits, so recovery can tell frames that
    /// were committed from frames a crash left behind.
    /// </summary>
    public ulong FrameCount
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _frameCount;
            }
        }
    }

    /// <summary>
    /// The file's length in bytes, which after a complete append is the end of the last frame. The index
    /// records it when a turn commits (ADR-0019, "Commit order and recovery").
    /// </summary>
    public long Length
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _stream.Length;
            }
        }
    }

    /// <summary>
    /// Cuts the file back to a length the index committed, discarding frames that were flushed but whose
    /// index rows never committed. Appends resume from the new end. <paramref name="length"/> must be a
    /// frame boundary taken from <see cref="Length"/> after an append, which is what the index records.
    /// </summary>
    /// <param name="length">The committed length in bytes, no smaller than the header and no larger than the file.</param>
    /// <param name="frameCount">How many frames the file held at that length.</param>
    /// <exception cref="ArgumentOutOfRangeException">When the length is outside the file's header-to-end range.</exception>
    public void TruncateTo(long length, ulong frameCount)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentOutOfRangeException.ThrowIfLessThan(length, DataStart);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(length, _stream.Length);

            _stream.SetLength(length);
            _stream.Seek(0, SeekOrigin.End);
            _frameCount = frameCount;
        }
    }

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
            return new SessionFile(stream, archiveSessionId, sessionKey, frameCount: 0);
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

            var (end, frameCount) = FindEndOfLastCompleteFrame(stream);
            if (end < stream.Length)
            {
                stream.SetLength(end);
            }

            stream.Seek(0, SeekOrigin.End);
            return new SessionFile(stream, archiveSessionId, sessionKey, frameCount);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Appends one body frame and flushes to disk. The body is obscured and compressed in memory, then
    /// written as sealed chunks, all under the file lock because the frame's position is part of its
    /// authenticated data. A failure part-way leaves a frame without its final chunk, which
    /// <see cref="Open"/> and the store's rollback cut away.
    /// </summary>
    /// <param name="turnSequence">Zero-based turn order inside the session.</param>
    /// <param name="kind">Which body this frame holds.</param>
    /// <param name="plaintext">Raw body bytes; an empty span is a legitimately empty body. Use
    /// <see cref="AppendMissingBody"/> when capture failed.</param>
    /// <param name="archiveTurnId">The turn's stable archive id.</param>
    public void AppendBody(uint turnSequence, SessionBodyKind kind, ReadOnlySpan<byte> plaintext, Guid archiveTurnId)
    {
        var compressed = SessionRecordCodec.ObscureAndCompress(plaintext);
        AppendFrame(turnSequence, kind, flags: 0, archiveTurnId, writeChunks: write =>
        {
            var offset = 0;
            do
            {
                var length = Math.Min(SessionChunkCodec.ChunkBytes, compressed.Length - offset);
                var final = offset + length >= compressed.Length;
                write(compressed.AsSpan(offset, length), final);
                offset += length;
            }
            while (offset < compressed.Length);
        });
    }

    /// <summary>
    /// Appends a body that a capture spooled to disk, re-sealing its chunks under this file's key one at a
    /// time so memory stays bounded however large the body is. The spool already holds obscured, compressed
    /// bytes, so nothing is recompressed.
    /// </summary>
    /// <param name="turnSequence">Zero-based turn order inside the session.</param>
    /// <param name="kind">Which body this frame holds.</param>
    /// <param name="spool">A completed spool; the caller keeps ownership and disposes it after the turn commits.</param>
    /// <param name="archiveTurnId">The turn's stable archive id.</param>
    /// <exception cref="InvalidOperationException">When the spool did not complete.</exception>
    public void AppendBodyFromSpool(uint turnSequence, SessionBodyKind kind, SessionBodySpool spool, Guid archiveTurnId)
    {
        ArgumentNullException.ThrowIfNull(spool);
        AppendFrame(turnSequence, kind, flags: 0, archiveTurnId, writeChunks: write =>
            spool.Replay(write));
    }

    /// <summary>
    /// Appends a missing-body marker so export can record absence without inventing empty content.
    /// The marker is a sealed empty body flagged as missing, so its kind, turn, and position are
    /// authenticated like any other frame.
    /// </summary>
    /// <param name="turnSequence">Zero-based turn order inside the session.</param>
    /// <param name="kind">Which body is missing.</param>
    /// <param name="archiveTurnId">The turn's stable archive id.</param>
    public void AppendMissingBody(uint turnSequence, SessionBodyKind kind, Guid archiveTurnId) =>
        AppendFrame(turnSequence, kind, FlagMissing, archiveTurnId, writeChunks: write => write([], true));

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
            ulong ordinal = 0;
            try
            {
                _stream.Seek(DataStart, SeekOrigin.Begin);

                Span<byte> header = stackalloc byte[FrameHeaderLength];
                while (TryReadExact(_stream, header))
                {
                    var kind = (SessionBodyKind)header[0];
                    ValidateKind(kind);
                    var flags = header[1];
                    var turnSequence = BinaryPrimitives.ReadUInt32LittleEndian(header[2..6]);
                    var archiveTurnId = new Guid(header[6..FrameHeaderLength]);

                    if (flags is not (0 or FlagMissing))
                    {
                        throw new InvalidDataException($"Unknown session frame flags {flags}.");
                    }

                    using var compressed = new MemoryStream();
                    var frameAad = BuildAssociatedData(header, ordinal++);
                    uint chunkIndex = 0;
                    bool final;
                    do
                    {
                        compressed.Write(SessionChunkCodec.ReadRecord(_stream, _aes, frameAad, chunkIndex++, out final));
                    }
                    while (!final);

                    var plaintext = flags == FlagMissing ? null : SessionRecordCodec.Decompress(compressed.ToArray());
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
            _aes.Dispose();
            CryptographicOperations.ZeroMemory(_sessionKey);
            _stream.Dispose();
        }
    }

    /// <summary>
    /// Writes one frame: its plaintext header, then the sealed chunks <paramref name="writeChunks"/> supplies,
    /// the last flagged final, and flushes. Everything is authenticated against the next frame position, so
    /// the work happens under the file lock. The frame count advances only once the final chunk is written.
    /// </summary>
    /// <param name="turnSequence">Zero-based turn order inside the session.</param>
    /// <param name="kind">Which body this frame holds.</param>
    /// <param name="flags">0 for a body, <see cref="FlagMissing"/> for a missing-body marker.</param>
    /// <param name="archiveTurnId">The turn's stable archive id.</param>
    /// <param name="writeChunks">Hands each chunk, in order, to the sink it is given; the last call passes <c>final: true</c>.</param>
    /// <exception cref="InvalidOperationException">When the producer supplies no final chunk.</exception>
    private void AppendFrame(uint turnSequence, SessionBodyKind kind, byte flags, Guid archiveTurnId, ChunkProducer writeChunks)
    {
        ValidateKind(kind);

        byte[] header = new byte[FrameHeaderLength];
        WriteFrameHeader(header, kind, flags, turnSequence, archiveTurnId);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var frameAad = BuildAssociatedData(header, _frameCount);
            var frameStart = _stream.Length;
            try
            {
                _stream.Write(header);
                uint chunkIndex = 0;
                var sawFinal = false;
                writeChunks((chunk, final) =>
                {
                    if (sawFinal) throw new InvalidOperationException("A chunk followed the final chunk.");
                    SessionChunkCodec.WriteRecord(_stream, _aes, frameAad, chunkIndex++, final, chunk);
                    sawFinal = final;
                });

                if (!sawFinal) throw new InvalidOperationException("The body ended without a final chunk.");
                _stream.Flush(flushToDisk: true);
            }
            catch
            {
                // Chunks are written one at a time, so a failure part-way leaves a header and some chunks
                // behind. Cut them away here, so this instance stays appendable and a caller that does not
                // roll back to a committed extent (the store does) is not left with a torn frame.
                CutBackToFrameStart(frameStart);
                throw;
            }

            _frameCount++;
        }
    }

    /// <summary>
    /// Removes what a failed append wrote and leaves the stream at end-of-file. A failure to cut back is
    /// swallowed so it cannot replace the exception that caused the cut; <see cref="Open"/> repeats the cut.
    /// </summary>
    /// <param name="frameStart">The file length before the failed frame's header was written.</param>
    private void CutBackToFrameStart(long frameStart)
    {
        try
        {
            _stream.SetLength(frameStart);
            _stream.Seek(0, SeekOrigin.End);
        }
        catch (IOException)
        {
            // Open drops a frame without a final chunk the next time the file is opened.
        }
    }

    /// <summary>
    /// Associated data binding a frame's ciphertext to its header, to this session, and to its
    /// zero-based position in the file, so frames cannot be relabelled, reordered, removed from the
    /// middle, or transplanted from another session file.
    /// </summary>
    /// <param name="frameHeader">The frame's plaintext header bytes.</param>
    /// <param name="ordinal">How many frames precede this one in the file.</param>
    /// <returns>The bytes to authenticate: session id, ordinal, then header.</returns>
    private byte[] BuildAssociatedData(ReadOnlySpan<byte> frameHeader, ulong ordinal)
    {
        var aad = new byte[IdLength + sizeof(ulong) + FrameHeaderLength];
        ArchiveSessionId.TryWriteBytes(aad);
        BinaryPrimitives.WriteUInt64LittleEndian(aad.AsSpan(IdLength), ordinal);
        frameHeader.CopyTo(aad.AsSpan(IdLength + sizeof(ulong)));
        return aad;
    }

    /// <summary>
    /// Serialises a frame header as <c>kind | flags | turnSequence | archiveTurnId</c>.
    /// </summary>
    /// <param name="header">Destination, exactly <see cref="FrameHeaderLength"/> bytes.</param>
    /// <param name="kind">Which body the frame holds.</param>
    /// <param name="flags">0 for a body, <see cref="FlagMissing"/> for a missing-body marker.</param>
    /// <param name="turnSequence">Zero-based turn order inside the session.</param>
    /// <param name="archiveTurnId">The turn's stable archive id.</param>
    private static void WriteFrameHeader(
        Span<byte> header, SessionBodyKind kind, byte flags, uint turnSequence, Guid archiveTurnId)
    {
        header[0] = (byte)kind;
        header[1] = flags;
        BinaryPrimitives.WriteUInt32LittleEndian(header[2..6], turnSequence);
        archiveTurnId.TryWriteBytes(header[6..FrameHeaderLength]);
    }

    /// <summary>
    /// Rejects a body kind that is not a defined <see cref="SessionBodyKind"/> value.
    /// </summary>
    /// <param name="kind">The kind to check.</param>
    /// <exception cref="InvalidDataException">When the kind is undefined.</exception>
    private static void ValidateKind(SessionBodyKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new InvalidDataException($"Unknown session body kind {(byte)kind}.");
        }
    }

    /// <summary>
    /// Walks the frame headers and chunk records (without decrypting) from the end of the file header and
    /// returns the offset just past the last frame whose final chunk is present, plus how many complete frames
    /// precede it. Anything after the offset is a torn append.
    /// </summary>
    /// <param name="stream">The open session file.</param>
    /// <returns>The end offset of the last complete frame and the number of complete frames.</returns>
    private static (long End, ulong FrameCount) FindEndOfLastCompleteFrame(FileStream stream)
    {
        long good = DataStart;
        ulong count = 0;
        stream.Seek(DataStart, SeekOrigin.Begin);

        Span<byte> header = stackalloc byte[FrameHeaderLength];
        while (TryReadPartial(stream, header))
        {
            bool final;
            do
            {
                if (!SessionChunkCodec.TrySkipRecord(stream, out final)) return (good, count);
            }
            while (!final);

            good = stream.Position;
            count++;
        }

        return (good, count);
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
