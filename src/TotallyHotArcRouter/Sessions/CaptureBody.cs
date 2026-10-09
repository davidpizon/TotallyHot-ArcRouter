using System.Buffers;

namespace TotallyHot.ArcRouter.Sessions;

/// <summary>
/// One captured body on its way to a <see cref="SessionBodySpool"/>. The proxy's tees call <see cref="Write"/>
/// from the relay path, which only copies the bytes into a queue; <see cref="CaptureBodyPump"/>'s worker owns
/// the spool and does the compression, encryption and disk writes. Ordering is the queue's: writes, then the
/// completion, are applied in the order they were posted. A body that is dropped (queue limit, spool failure,
/// or <see cref="Release"/>) is recorded as missing, never stored as a prefix.
/// </summary>
public sealed class CaptureBody
{
    /// <summary>
    /// The most bytes copied into one queued item. A larger write is split, so every rented buffer stays within
    /// what <see cref="ArrayPool{T}.Shared"/> actually pools (it allocates and drops anything over 1 MiB). It is
    /// also above the proxy's 80 KiB relay buffer, so a relay chunk is never split.
    /// </summary>
    private const int MaxChunkBytes = 128 * 1024;

    private readonly CaptureBodyPump _pump;
    private readonly Func<SessionBodySpool> _createSpool;

    // Touched only by the worker.
    private SessionBodySpool? _spool;

    // Set by the relay thread on overflow or release, and by the worker on failure; read by both.
    private volatile bool _dropped;

    // Touched only by the worker: the spool was handed over, so any later work is stale and must not make another.
    private bool _completed;

    /// <summary>
    /// Initializes a new instance of the <see cref="CaptureBody"/> class.
    /// </summary>
    /// <param name="pump">The pump that applies this body's work.</param>
    /// <param name="createSpool">Creates the spool, on the worker.</param>
    internal CaptureBody(CaptureBodyPump pump, Func<SessionBodySpool> createSpool)
    {
        _pump = pump;
        _createSpool = createSpool;
    }

    /// <summary>
    /// Queues the next bytes of the body. Never blocks and never throws: when the pump's queue is full or has
    /// stopped, the body is dropped instead.
    /// </summary>
    /// <param name="bytes">The next bytes exactly as sent or relayed; copied before this returns.</param>
    public void Write(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty && !_dropped)
        {
            var take = Math.Min(bytes.Length, MaxChunkBytes);
            if (!QueueChunk(bytes[..take])) return;

            bytes = bytes[take..];
        }
    }

    /// <summary>
    /// Ends the body: the worker finishes the spool once every earlier write has been applied.
    /// </summary>
    /// <returns>
    /// The finished spool, which the caller now owns, or <see langword="null"/> when the body was dropped or
    /// failed. Never completes if the pump was never started.
    /// </returns>
    public Task<SessionBodySpool?> CompleteAsync()
    {
        var completion = new TaskCompletionSource<SessionBodySpool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_pump.Post(new CaptureWork(this, CaptureOp.Complete, null, 0, completion))) return completion.Task;

        _dropped = true;
        return Task.FromResult<SessionBodySpool?>(null);
    }

    /// <summary>
    /// Gives up the body: later writes are ignored and the worker deletes the spool. Safe to call at any time,
    /// more than once, and after <see cref="CompleteAsync"/> (a spool already handed over is the caller's).
    /// </summary>
    public void Release()
    {
        _dropped = true;
        _pump.Post(new CaptureWork(this, CaptureOp.Release, null, 0, null));
    }

    /// <summary>Applies one queued item. Runs only on the pump's worker; never throws.</summary>
    /// <param name="work">The item to apply.</param>
    internal void Apply(CaptureWork work)
    {
        switch (work.Op)
        {
            case CaptureOp.Write:
                ApplyWrite(work.Buffer!, work.Length);
                break;
            case CaptureOp.Complete:
                ApplyComplete(work.Completion!);
                break;
            default:
                _dropped = true;
                DisposeSpool();
                break;
        }
    }

    /// <summary>
    /// Copies one chunk into a rented buffer and queues it. The queue's byte budget is charged first, so a chunk
    /// that would overflow it drops the whole body instead of queuing a prefix.
    /// </summary>
    /// <param name="chunk">The chunk, at most <see cref="MaxChunkBytes"/> long.</param>
    /// <returns><see langword="false"/> when the body was dropped and no more should be queued.</returns>
    private bool QueueChunk(ReadOnlySpan<byte> chunk)
    {
        if (!_pump.TryReserve(chunk.Length))
        {
            Release();
            return false;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(chunk.Length);
        chunk.CopyTo(buffer);
        if (_pump.Post(new CaptureWork(this, CaptureOp.Write, buffer, chunk.Length, null))) return true;

        ReturnBuffer(buffer, chunk.Length);
        _dropped = true;
        return false;
    }

    /// <summary>
    /// Feeds a queued chunk to the spool, creating the spool on the first one. A failure, or a spool that gave
    /// up on its own (disk reserve, an unterminated secret match), drops the body. The chunk's buffer and byte
    /// budget are always given back.
    /// </summary>
    /// <param name="buffer">The rented buffer holding the chunk.</param>
    /// <param name="length">How many bytes of <paramref name="buffer"/> are valid.</param>
    private void ApplyWrite(byte[] buffer, int length)
    {
        try
        {
            if (_dropped || _completed) return;

            _spool ??= _createSpool();
            if (!_spool.TryWrite(buffer.AsSpan(0, length))) _dropped = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _dropped = true;
            _pump.LogFailure(ex);
        }
        finally
        {
            ReturnBuffer(buffer, length);
        }
    }

    /// <summary>
    /// Ends the body on the worker and always settles <paramref name="completion"/>: with the finished spool
    /// when every earlier chunk was applied, or with <see langword="null"/> when the body was dropped or failed.
    /// An empty body still gets a complete, empty spool, so it is stored as empty and not as missing.
    /// </summary>
    /// <param name="completion">Receives the finished spool, or <see langword="null"/>.</param>
    private void ApplyComplete(TaskCompletionSource<SessionBodySpool?> completion)
    {
        SessionBodySpool? finished = null;
        try
        {
            if (!_dropped && !_completed)
            {
                _spool ??= _createSpool();
                if (_spool.TryComplete())
                {
                    finished = _spool;
                    _spool = null;
                    _completed = true;
                }
                else
                {
                    _dropped = true;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _dropped = true;
            _pump.LogFailure(ex);
        }
        finally
        {
            // Whatever happened above, the caller is waiting on this and must hear back.
            if (finished is null) DisposeSpool();
            completion.TrySetResult(finished);
        }
    }

    /// <summary>
    /// Disposes the spool the worker still holds, if any, which deletes its file. A disposal that fails is logged
    /// and swallowed, because the worker must go on to settle the caller's completion.
    /// </summary>
    private void DisposeSpool()
    {
        var spool = _spool;
        _spool = null;
        try
        {
            spool?.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _pump.LogFailure(ex);
        }
    }

    /// <summary>
    /// Returns a chunk's buffer to the pool and gives its bytes back to the queue's budget. The buffer holds the
    /// body before secrets were obscured, so it is cleared rather than left for the next renter.
    /// </summary>
    /// <param name="buffer">The rented buffer.</param>
    /// <param name="length">How many bytes were charged for it.</param>
    private void ReturnBuffer(byte[] buffer, int length)
    {
        ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        _pump.Unreserve(length);
    }
}
