using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Sessions;

/// <summary>One finished turn waiting to be written to its session file.</summary>
/// <param name="ClientSessionId">The client's own session id, resolved to an archive session by the writer.</param>
/// <param name="Turn">The turn, whose bodies may be spooled captures the writer disposes after the write.</param>
/// <param name="Epoch">
/// The <see cref="CaptureEpoch"/> value the turn began under. The writer drops a turn whose epoch is no longer
/// current, because Clear has run since and the turn's request holds the history it wiped.
/// </param>
public sealed record SessionCaptureItem(string ClientSessionId, SessionTurnInput Turn, long Epoch = 0);

/// <summary>
/// Writes captured turns to <see cref="SessionStore"/> off the request path: a bounded channel with one
/// consumer, shaped like ADR-0018's writer so the two can be merged later. Nothing the writer does can fail a
/// proxy response; a turn that cannot be stored is logged and lost, and its spools are always disposed. A
/// producer that finds the queue full waits (it does not drop the turn). A lost turn is also reported to an
/// optional callback so its transcript row stops waiting for text; a callback that fails is logged and absorbed.
/// </summary>
/// <remarks>
/// A plain hosted service rather than a <see cref="BackgroundService"/>: on .NET 10 a <c>BackgroundService</c>
/// stopped right after it starts never runs <c>ExecuteAsync</c>, which would leave queued turns unwritten
/// until the shutdown deadline. This type starts its consumer in <see cref="StartAsync"/> and always runs it.
/// </remarks>
public sealed class SessionCaptureWriter : IHostedService, IDisposable
{
    private readonly Lazy<SessionStore> _store;
    private readonly SessionCaptureOptions _options;
    private readonly ILogger<SessionCaptureWriter> _logger;
    private readonly CaptureEpoch? _epoch;
    private readonly Action<Guid>? _onTurnDropped;
    private readonly Channel<SessionCaptureItem> _channel;
    private int _inFlight;
    private volatile bool _abandonRemaining;
    private volatile bool _stopping;
    private volatile bool _started;
    private Task? _consumer;

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionCaptureWriter"/> class.
    /// </summary>
    /// <param name="store">Where turns are written; opened on the first write, which creates the index and recovers.</param>
    /// <param name="options">Queue size and shutdown deadline.</param>
    /// <param name="logger">Receives write failures; message templates are static.</param>
    /// <param name="epoch">
    /// The Clear epoch, so a turn from before a Clear is dropped instead of written after it. Optional: without
    /// one every turn is written.
    /// </param>
    /// <param name="onTurnDropped">
    /// Marks the matching transcript row terminal when a turn is refused, epoch-dropped, abandoned, or fails to
    /// write (#165 phase 2 follow-up). Optional so hand-built writers in tests need no transcript store. A callback
    /// that throws is logged and ignored; it never reaches a caller or stops the consumer.
    /// </param>
    public SessionCaptureWriter(
        Lazy<SessionStore> store,
        IOptions<SessionCaptureOptions> options,
        ILogger<SessionCaptureWriter> logger,
        CaptureEpoch? epoch = null,
        Action<Guid>? onTurnDropped = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _options = options.Value;
        _logger = logger;
        _epoch = epoch;
        _onTurnDropped = onTurnDropped;
        _channel = Channel.CreateBounded<SessionCaptureItem>(new BoundedChannelOptions(Math.Max(1, _options.QueueCapacity))
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    /// <summary>
    /// Queues a turn for writing. The writer takes ownership of the turn's spools: it disposes them after the
    /// write, and this method disposes them itself when the turn is refused.
    /// </summary>
    /// <param name="item">The turn to write.</param>
    /// <param name="cancellationToken">Cancels the wait for queue space.</param>
    /// <returns><see langword="false"/> when the writer has stopped or the wait was cancelled, so the turn was dropped.</returns>
    public async ValueTask<bool> EnqueueAsync(SessionCaptureItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        Interlocked.Increment(ref _inFlight);
        if (_stopping)
        {
            Complete(item, dropped: true);
            return false;
        }

        try
        {
            if (_channel.Writer.TryWrite(item)) return true;

            _logger.LogWarning("The session capture queue is full; waiting for the writer.");
            await _channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is ChannelClosedException or OperationCanceledException)
        {
            Complete(item, dropped: true);
            return false;
        }
    }

    /// <summary>
    /// Waits until every turn queued so far has been written or dropped.
    /// </summary>
    /// <param name="timeout">The longest to wait.</param>
    /// <returns><see langword="true"/> when the queue emptied in time.</returns>
    public async Task<bool> WaitForIdleAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Volatile.Read(ref _inFlight) > 0)
        {
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(10).ConfigureAwait(false);
        }

        return true;
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        _channel.Writer.TryComplete();
        if (!_started)
        {
            // The writer was never started, so nothing will ever read the queue and waiting would only run out
            // the clock; release what is in it. A started writer is drained even if its consumer has not begun
            // running yet: StartAsync already started it, and it reads until the completed channel is empty.
            while (_channel.Reader.TryRead(out var stranded)) Complete(stranded, dropped: true);
        }

        if (!await WaitForIdleAsync(_options.ShutdownDrainTimeout).ConfigureAwait(false))
        {
            _logger.LogWarning(
                "Session capture shutdown timed out with {Abandoned} turns unwritten.", Volatile.Read(ref _inFlight));
            _abandonRemaining = true;
        }

        if (_consumer is null) return;

        try
        {
            await _consumer.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The host's shutdown deadline passed. Like a BackgroundService, stop waiting without failing the
            // host's stop; the consumer keeps draining (or skipping, once abandoned) what is queued.
        }
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _started = true;

        // Task.Run leaves the thread that started the host before any blocking write.
        _consumer ??= Task.Run(ConsumeAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose() => _channel.Writer.TryComplete();

    /// <summary>
    /// Writes queued turns until the channel is completed by <see cref="StopAsync"/> and drained, not until
    /// cancellation, so a stop finishes what was already queued.
    /// </summary>
    private async Task ConsumeAsync()
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            Write(item);
        }
    }

    /// <summary>
    /// Writes one queued turn and releases its spools. A failure is logged and the turn is lost; it never
    /// escapes, so one bad turn cannot stop the consumer.
    /// </summary>
    /// <param name="item">The turn to write.</param>
    private void Write(SessionCaptureItem item)
    {
        var dropped = true;
        try
        {
            if (_abandonRemaining) return;

            // Clear ran after this turn began: its request holds the history Clear wiped, so it is not written.
            if (_epoch is not null && item.Epoch != _epoch.Current)
            {
                _logger.LogInformation("A captured turn that began before Clear was dropped instead of written.");
                return;
            }

            var store = _store.Value;
            var archiveSessionId = store.ResolveArchiveSessionId(item.ClientSessionId);
            store.AppendTurn(archiveSessionId, item.ClientSessionId, item.Turn);
            dropped = false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "A captured turn could not be written to session storage and was lost.");
        }
        finally
        {
            Complete(item, dropped);
        }
    }

    /// <summary>
    /// Disposes the turn's spools, tells the drop callback when the turn was lost, and counts the turn as no longer
    /// in flight. The count is released however the first two steps end, because <see cref="WaitForIdleAsync"/>,
    /// <see cref="StopAsync"/> and Clear all wait on it.
    /// </summary>
    /// <param name="item">The turn that was written, dropped or refused.</param>
    /// <param name="dropped">
    /// Whether the turn never reached the session file, so its transcript row must be marked terminal.
    /// </param>
    private void Complete(SessionCaptureItem item, bool dropped)
    {
        try
        {
            foreach (var body in item.Turn.Bodies) body.Spool?.Dispose();
            if (dropped) NotifyTurnDropped(item.Turn.ArchiveTurnId);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    /// <summary>
    /// Tells the drop callback that a turn will never reach a session file. The callback writes to the transcript
    /// database, which is unwell exactly when a write has just failed (locked, disk full), and this runs on the
    /// single consumer and on producers. A failure is therefore logged and absorbed: it must not stop the consumer,
    /// strand the turn as in flight, or fail a request. The row it would have marked is covered by the
    /// missing-extract grace window instead.
    /// </summary>
    /// <param name="archiveTurnId">The dropped turn's archive turn id.</param>
    private void NotifyTurnDropped(Guid archiveTurnId)
    {
        if (_onTurnDropped is null) return;

        try
        {
            _onTurnDropped(archiveTurnId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex, "A dropped turn's transcript row could not be marked unavailable; the missing-extract grace window will cover it.");
        }
    }
}
