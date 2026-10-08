using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Sessions;

/// <summary>One finished turn waiting to be written to its session file.</summary>
/// <param name="ClientSessionId">The client's own session id, resolved to an archive session by the writer.</param>
/// <param name="Turn">The turn, whose bodies may be spooled captures the writer disposes after the write.</param>
public sealed record SessionCaptureItem(string ClientSessionId, SessionTurnInput Turn);

/// <summary>
/// Writes captured turns to <see cref="SessionStore"/> off the request path: a bounded channel with one
/// consumer, shaped like ADR-0018's writer so the two can be merged later. Nothing the writer does can fail a
/// proxy response; a turn that cannot be stored is logged and lost, and its spools are always disposed. A
/// producer that finds the queue full waits (it does not drop the turn).
/// </summary>
public sealed class SessionCaptureWriter : BackgroundService
{
    private readonly SessionStore _store;
    private readonly SessionCaptureOptions _options;
    private readonly ILogger<SessionCaptureWriter> _logger;
    private readonly Channel<SessionCaptureItem> _channel;
    private int _inFlight;
    private volatile bool _abandonRemaining;

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionCaptureWriter"/> class.
    /// </summary>
    /// <param name="store">Where turns are written.</param>
    /// <param name="options">Queue size and shutdown deadline.</param>
    /// <param name="logger">Receives write failures; message templates are static.</param>
    public SessionCaptureWriter(
        SessionStore store, IOptions<SessionCaptureOptions> options, ILogger<SessionCaptureWriter> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _options = options.Value;
        _logger = logger;
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
        try
        {
            if (_channel.Writer.TryWrite(item)) return true;

            _logger.LogWarning("The session capture queue is full; waiting for the writer.");
            await _channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is ChannelClosedException or OperationCanceledException)
        {
            Complete(item);
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
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        if (!await WaitForIdleAsync(_options.ShutdownDrainTimeout).ConfigureAwait(false))
        {
            _logger.LogWarning(
                "Session capture shutdown timed out with {Abandoned} turns unwritten.", Volatile.Read(ref _inFlight));
            _abandonRemaining = true;
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Leave the thread that started the host before any blocking write.
        await Task.Yield();

        // Reads until the channel is completed by StopAsync, not until cancellation, so a stop drains first.
        await foreach (var item in _channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            Write(item);
        }
    }

    private void Write(SessionCaptureItem item)
    {
        try
        {
            if (_abandonRemaining) return;

            var archiveSessionId = _store.ResolveArchiveSessionId(item.ClientSessionId);
            _store.AppendTurn(archiveSessionId, item.ClientSessionId, item.Turn);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "A captured turn could not be written to session storage and was lost.");
        }
        finally
        {
            Complete(item);
        }
    }

    private void Complete(SessionCaptureItem item)
    {
        foreach (var body in item.Turn.Bodies) body.Spool?.Dispose();
        Interlocked.Decrement(ref _inFlight);
    }
}
