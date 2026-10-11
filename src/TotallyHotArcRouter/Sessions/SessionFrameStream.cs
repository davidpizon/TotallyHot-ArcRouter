namespace TotallyHot.ArcRouter.Sessions;

/// <summary>
/// One frame of a turn as <see cref="SessionFile.ReadTurn"/> yields it: its identity and a stream over the
/// body's obscured plaintext that decrypts and decompresses as it is read. Dispose it before asking for the
/// next frame; a body that was never read is stepped over without decrypting.
/// </summary>
public sealed class SessionFrameStream : IDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SessionFrameStream"/> class.
    /// </summary>
    /// <param name="turnSequence">Zero-based turn order inside the session.</param>
    /// <param name="kind">Which body this frame holds.</param>
    /// <param name="archiveTurnId">The turn's stable archive id.</param>
    /// <param name="body">The plaintext stream, or <see langword="null"/> for a missing-body marker.</param>
    internal SessionFrameStream(uint turnSequence, SessionBodyKind kind, Guid archiveTurnId, Stream? body)
    {
        TurnSequence = turnSequence;
        Kind = kind;
        ArchiveTurnId = archiveTurnId;
        Body = body;
    }

    /// <summary>Gets the zero-based turn order inside the session.</summary>
    public uint TurnSequence { get; }

    /// <summary>Gets which body this frame holds.</summary>
    public SessionBodyKind Kind { get; }

    /// <summary>Gets the turn's stable archive id.</summary>
    public Guid ArchiveTurnId { get; }

    /// <summary>Gets a value indicating whether capture failed and this frame only records that the body is missing.</summary>
    public bool IsMissing => Body is null;

    /// <summary>
    /// Gets the body's obscured plaintext, read lazily, or <see langword="null"/> when the body is missing. A
    /// chunk that fails authentication throws <see cref="System.Security.Cryptography.CryptographicException"/>
    /// from a read.
    /// </summary>
    public Stream? Body { get; }

    /// <inheritdoc/>
    public void Dispose() => Body?.Dispose();
}
