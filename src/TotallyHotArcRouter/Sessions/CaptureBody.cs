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
        if (_dropped || bytes.IsEmpty) return;

        if (!_pump.TryReserve(bytes.Length))
        {
            Release();
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(bytes.Length);
        bytes.CopyTo(buffer);
        if (_pump.Post(new CaptureWork(this, CaptureOp.Write, buffer, bytes.Length, null))) return;

        ArrayPool<byte>.Shared.Return(buffer);
        _pump.Unreserve(bytes.Length);
        _dropped = true;
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
            ArrayPool<byte>.Shared.Return(buffer);
            _pump.Unreserve(length);
        }
    }

    private void ApplyComplete(TaskCompletionSource<SessionBodySpool?> completion)
    {
        try
        {
            if (!_dropped && !_completed)
            {
                // An empty body still needs a (complete, empty) spool so it is stored as empty, not missing.
                _spool ??= _createSpool();
                if (_spool.TryComplete())
                {
                    var finished = _spool;
                    _spool = null;
                    _completed = true;
                    completion.SetResult(finished);
                    return;
                }

                _dropped = true;
            }

            DisposeSpool();
            completion.SetResult(null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _dropped = true;
            _pump.LogFailure(ex);
            DisposeSpool();
            completion.SetResult(null);
        }
    }

    private void DisposeSpool()
    {
        _spool?.Dispose();
        _spool = null;
    }
}
