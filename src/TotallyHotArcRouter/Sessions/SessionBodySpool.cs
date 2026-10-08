using System.IO.Compression;
using System.Security.Cryptography;

namespace TotallyHot.ArcRouter.Sessions;

/// <summary>
/// Captures one body on its way to a session file without holding it in memory (ADR-0019, "Bounded
/// capture"). Bytes pass through <see cref="StreamingSecretObscurer"/> and Brotli into sealed chunks in a
/// <c>.spool</c> file, so no plaintext reaches disk. The chunks are sealed under a random key that exists only
/// in this object, because the session key and the body's position in its session are not known until the
/// turn commits; commit re-seals them chunk by chunk under the session key. A crash therefore leaves a spool
/// nobody can read, which startup recovery deletes.
/// A capture that cannot finish in bounded memory or disk (<see cref="SessionCaptureAbandonedException"/>) or
/// hits an I/O failure is abandoned: the file is deleted and the body is recorded as missing, never kept as
/// a prefix. Not thread-safe; one writer drives it.
/// </summary>
public sealed class SessionBodySpool : IDisposable
{
    /// <summary>The extension the store's startup sweep treats as its own leftover spool.</summary>
    public const string FileExtension = ".spool";

    /// <summary>Free disk space below which a capture is abandoned unless overridden.</summary>
    public const long DefaultMinFreeBytes = 256L * 1024 * 1024;

    private static readonly byte[] AadPrefix = "THSPOOL1"u8.ToArray();

    private readonly string _path;
    private readonly byte[] _key = SessionKeyMaterial.CreateSessionKey();
    private readonly byte[] _aad;
    private readonly AesGcm _aes;
    private SpoolState _state = SpoolState.Writing;
    private FileStream? _file;
    private StreamingSecretObscurer? _obscurer;
    private BrotliStream? _brotli;
    private SealingStream? _sealing;

    private SessionBodySpool(string path, long minFreeBytes, Func<string, long> freeSpace, int windowChars)
    {
        _path = path;
        _aad = new byte[AadPrefix.Length + 16];
        AadPrefix.CopyTo(_aad, 0);
        Guid.NewGuid().TryWriteBytes(_aad.AsSpan(AadPrefix.Length));
        _aes = new AesGcm(_key, SessionKeyMaterial.TagLengthBytes);

        _file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        _sealing = new SealingStream(_file, _aes, _aad, path, minFreeBytes, freeSpace);
        _brotli = new BrotliStream(_sealing, CompressionLevel.Fastest, leaveOpen: true);
        _obscurer = new StreamingSecretObscurer(_brotli, leaveOpen: true, windowChars);
    }

    private enum SpoolState
    {
        Writing,
        Completed,
        Abandoned,
        Disposed,
    }

    /// <summary>Gets a value indicating whether the whole body was captured and can be committed.</summary>
    public bool IsComplete => _state == SpoolState.Completed;

    /// <summary>Gets a value indicating whether the capture was given up and the body must be recorded as missing.</summary>
    public bool IsAbandoned => _state == SpoolState.Abandoned;

    /// <summary>
    /// Starts a spool in <paramref name="folder"/>.
    /// </summary>
    /// <param name="folder">The session folder, whose startup sweep removes a spool a crash left behind.</param>
    /// <param name="minFreeBytes">Free space below which the capture is abandoned.</param>
    /// <param name="freeSpace">Reports free bytes at a path; null reads the drive, treating an unreadable drive as plenty.</param>
    /// <param name="windowChars">The obscurer's look-back window; a test passes a small one.</param>
    /// <returns>A spool ready for <see cref="TryWrite"/>.</returns>
    public static SessionBodySpool Create(
        string folder,
        long minFreeBytes = DefaultMinFreeBytes,
        Func<string, long>? freeSpace = null,
        int windowChars = StreamingSecretObscurer.DefaultWindowChars)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        Directory.CreateDirectory(folder);
        return new SessionBodySpool(
            Path.Combine(folder, $"{Guid.NewGuid():N}{FileExtension}"), minFreeBytes, freeSpace ?? DriveFreeBytes, windowChars);
    }

    /// <summary>
    /// Adds the next bytes of the body. Never throws for a capture failure: it abandons the spool and returns
    /// <see langword="false"/>, so a tee on the client's relay can ignore the result and keep relaying.
    /// </summary>
    /// <param name="body">The next bytes exactly as sent or relayed.</param>
    /// <returns><see langword="false"/> once the capture has been abandoned.</returns>
    public bool TryWrite(ReadOnlySpan<byte> body)
    {
        if (_state != SpoolState.Writing) return false;

        try
        {
            _obscurer!.Write(body);
            return true;
        }
        catch (Exception ex) when (IsCaptureFailure(ex))
        {
            Abandon();
            return false;
        }
    }

    /// <summary>
    /// Ends the body: flushes the obscurer's held tail, finishes compression, seals the last chunk and
    /// closes the file.
    /// </summary>
    /// <returns><see langword="true"/> when the spool now holds the whole body; <see langword="false"/> when it was abandoned.</returns>
    public bool TryComplete()
    {
        if (_state == SpoolState.Completed) return true;
        if (_state != SpoolState.Writing) return false;

        try
        {
            _obscurer!.Finish();
            _obscurer.Dispose();
            _brotli!.Dispose();
            _sealing!.FinishBody();
            _file!.Flush(flushToDisk: true);
            _file.Dispose();
            _file = null;
            _obscurer = null;
            _brotli = null;
            _sealing = null;
            _state = SpoolState.Completed;
            return true;
        }
        catch (Exception ex) when (IsCaptureFailure(ex))
        {
            Abandon();
            return false;
        }
    }

    /// <summary>
    /// Gives up the capture and deletes the file, so the body is recorded as missing. Idempotent.
    /// </summary>
    public void Abandon()
    {
        if (_state is SpoolState.Abandoned or SpoolState.Disposed) return;
        _state = SpoolState.Abandoned;
        CloseWriters();
        DeleteQuietly();
    }

    /// <summary>
    /// Releases the key and deletes the file. Called by whoever owns the spool once its turn has committed or
    /// been dropped; safe after <see cref="Abandon"/>.
    /// </summary>
    public void Dispose()
    {
        if (_state == SpoolState.Disposed) return;
        _state = SpoolState.Disposed;
        CloseWriters();
        DeleteQuietly();
        _aes.Dispose();
        CryptographicOperations.ZeroMemory(_key);
    }

    /// <summary>
    /// Opens each sealed chunk in order and hands it to <paramref name="sink"/>, which re-seals it. Memory
    /// stays at one chunk.
    /// </summary>
    /// <param name="sink">Receives each chunk of the obscured, compressed body.</param>
    /// <exception cref="InvalidOperationException">When the spool is not complete.</exception>
    internal void Replay(ChunkSink sink)
    {
        if (_state != SpoolState.Completed) throw new InvalidOperationException("The spool is not complete.");

        using var reader = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        uint chunkIndex = 0;
        bool final;
        do
        {
            var chunk = SessionChunkCodec.ReadRecord(reader, _aes, _aad, chunkIndex++, out final);
            sink(chunk, final);
        }
        while (!final);
    }

    private static bool IsCaptureFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or CryptographicException or ObjectDisposedException
            or InvalidDataException;

    private static long DriveFreeBytes(string path)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return long.MaxValue;
        }
    }

    private void CloseWriters()
    {
        // Disposing the obscurer or compressor here would flush more output; the spool is being discarded, so
        // only the file handle matters.
        _file?.Dispose();
        _file = null;
        _obscurer = null;
        _brotli = null;
        _sealing = null;
    }

    private void DeleteQuietly()
    {
        try
        {
            File.Delete(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Startup recovery deletes any .spool file no index row names.
        }
    }

    /// <summary>
    /// Buffers compressed bytes into fixed-size chunks and seals each one into the spool file, checking the
    /// disk reserve as it goes. Write-only.
    /// </summary>
    private sealed class SealingStream : Stream
    {
        private const int ReserveCheckEveryChunks = 16;

        private readonly FileStream _file;
        private readonly AesGcm _aes;
        private readonly byte[] _aad;
        private readonly string _path;
        private readonly long _minFreeBytes;
        private readonly Func<string, long> _freeSpace;
        private readonly byte[] _buffer = new byte[SessionChunkCodec.ChunkBytes];
        private int _buffered;
        private uint _chunkIndex;

        public SealingStream(
            FileStream file, AesGcm aes, byte[] aad, string path, long minFreeBytes, Func<string, long> freeSpace)
        {
            _file = file;
            _aes = aes;
            _aad = aad;
            _path = path;
            _minFreeBytes = minFreeBytes;
            _freeSpace = freeSpace;
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            while (!buffer.IsEmpty)
            {
                var take = Math.Min(buffer.Length, _buffer.Length - _buffered);
                buffer[..take].CopyTo(_buffer.AsSpan(_buffered));
                _buffered += take;
                buffer = buffer[take..];
                if (_buffered == _buffer.Length) SealBuffered(final: false);
            }
        }

        /// <summary>Seals whatever remains (possibly nothing) as the body's final chunk.</summary>
        public void FinishBody() => SealBuffered(final: true);

        private void SealBuffered(bool final)
        {
            if (_chunkIndex % ReserveCheckEveryChunks == 0 && _freeSpace(_path) < _minFreeBytes)
            {
                throw new SessionCaptureAbandonedException("Free disk space fell below the capture reserve.");
            }

            SessionChunkCodec.WriteRecord(_file, _aes, _aad, _chunkIndex++, final, _buffer.AsSpan(0, _buffered));
            _buffered = 0;
        }
    }
}
