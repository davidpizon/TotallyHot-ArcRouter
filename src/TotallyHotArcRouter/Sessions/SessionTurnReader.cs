namespace TotallyHot.ArcRouter.Sessions;

/// <summary>
/// One turn of a session opened for streaming by <see cref="SessionStore.OpenTurn"/>. While it is open it
/// holds the store's rotation lock (shared) and the session's gate, which are owned by the opening thread, so
/// it must be read and disposed on that thread and kept open only as long as one turn takes. Disposing it
/// releases both and closes the session file.
/// </summary>
public sealed class SessionTurnReader : IDisposable
{
    private readonly SessionFile _file;
    private readonly long _startOffset;
    private Action? _release;

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionTurnReader"/> class.
    /// </summary>
    /// <param name="turn">The turn's index row.</param>
    /// <param name="file">A read-only handle on the session file, owned by this reader from now on.</param>
    /// <param name="startOffset">Where the turn's first frame begins.</param>
    /// <param name="release">Releases the store locks the reader holds; runs once, on dispose.</param>
    internal SessionTurnReader(SessionTurnRow turn, SessionFile file, long startOffset, Action release)
    {
        Turn = turn;
        _file = file;
        _startOffset = startOffset;
        _release = release;
    }

    /// <summary>Gets the turn's index row.</summary>
    public SessionTurnRow Turn { get; }

    /// <summary>
    /// Reads the turn's frames from the start, one at a time. Dispose each frame before asking for the next.
    /// Calling it again reads the turn a second time, which is how export verifies a body before writing it.
    /// </summary>
    /// <returns>The turn's frames in file order.</returns>
    public IEnumerable<SessionFrameStream> ReadFrames() =>
        _file.ReadTurn(_startOffset, Turn.FirstFrameOrdinal, Turn.FrameCount);

    /// <inheritdoc/>
    public void Dispose()
    {
        var release = Interlocked.Exchange(ref _release, null);
        if (release is null) return;

        try
        {
            _file.Dispose();
        }
        finally
        {
            release();
        }
    }
}
