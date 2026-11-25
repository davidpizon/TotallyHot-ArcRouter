using System.Threading.Channels;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Proxy;

namespace TotallyHot.ArcRouter.Sessions;

/// <summary>
/// The single background consumer for a served request (ADR-0018) and its session file (ADR-0019). One
/// bounded channel, one reader. A full channel waits and logs a warning; it does not drop a spend row.
/// Shutdown drains until the deadline, then logs how many rows were abandoned. A plain hosted service, not
/// a <see cref="BackgroundService"/>: on .NET 10 a background service stopped immediately after start never
/// runs <c>ExecuteAsync</c>, which would leave the queue undrained.
/// </summary>
internal sealed class ServedRequestWriter : IHostedService, IDisposable, IDrainableQueue
{
    private readonly RequestTelemetryPublisher _publisher;
    private readonly Lazy<SessionStore> _store;
    private readonly SessionCaptureOptions _options;
    private readonly ILogger<ServedRequestWriter> _logger;
    private readonly CaptureEpoch? _epoch;
    private readonly Channel<ServedRequest> _channel;
    private int _inFlight;
    private volatile bool _abandonRemaining;
    private volatile bool _stopping;
    private volatile bool _started;
    private Task? _consumer;

    /// <summary>
    /// Initializes a new instance of the <see cref="ServedRequestWriter"/> class.
    /// </summary>
    /// <param name="publisher">Applies spend, the transcript index, and the live event.</param>
    /// <param name="store">Where a current-epoch turn's session file is appended. Opened on the first write.</param>
    /// <param name="options">Queue size and shutdown deadline. The same bounds the session writer used.</param>
    /// <param name="logger">Receives queue and write failures. Message templates are static.</param>
    /// <param name="epoch">The Clear epoch. A turn from before a Clear is not appended. Optional.</param>
    public ServedRequestWriter(
        RequestTelemetryPublisher publisher,
        Lazy<SessionStore> store,
        IOptions<SessionCaptureOptions> options,
        ILogger<ServedRequestWriter> logger,
        CaptureEpoch? epoch = null)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _publisher = publisher;
        _store = store;
        _options = options.Value;
        _logger = logger;
        _epoch = epoch;
        _channel = Channel.CreateBounded<ServedRequest>(new BoundedChannelOptions(Math.Max(1, _options.QueueCapacity))
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    /// <summary>
    /// Queues one served request. The gauge hold, when <paramref name="gauge"/> is supplied, is taken before
    /// this method returns so background work stays paused while the row waits.
    /// </summary>
    /// <param name="item">The request to write. This writer owns its spools and its gauge hold.</param>
    /// <param name="gauge">The in-flight gauge, or null when the host is not tracking requests.</param>
    /// <param name="cancellationToken">Cancels the wait for queue space.</param>
    /// <returns><see langword="false"/> when the writer has stopped or the wait was cancelled.</returns>
    public async ValueTask<bool> EnqueueAsync(ServedRequest item, InFlightRequestGauge? gauge = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.GaugeHold = gauge?.Track();

        Interlocked.Increment(ref _inFlight);
        if (_stopping)
        {
            Complete(item, dropped: true);
            return false;
        }

        try
        {
            if (_channel.Writer.TryWrite(item)) return true;

            _logger.LogWarning("The served-request queue is full; waiting for the writer.");
            await _channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is ChannelClosedException or OperationCanceledException)
        {
            Complete(item, dropped: true);
            return false;
        }
    }

    /// <inheritdoc/>
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
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _started = true;
        _consumer ??= Task.Run(ConsumeAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        _channel.Writer.TryComplete();
        if (!_started)
        {
            while (_channel.Reader.TryRead(out var stranded)) Complete(stranded, dropped: true);
        }

        if (!await WaitForIdleAsync(_options.ShutdownDrainTimeout).ConfigureAwait(false))
        {
            _logger.LogWarning(
                "Served-request shutdown timed out with {Abandoned} rows unwritten.", Volatile.Read(ref _inFlight));
            _abandonRemaining = true;
        }

        if (_consumer is null) return;

        try
        {
            await _consumer.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The host's shutdown deadline passed. Stop waiting without failing the host's stop.
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _channel.Writer.TryComplete();

    /// <summary>Reads until <see cref="StopAsync"/> completes the channel, then drains what was already queued.</summary>
    private async Task ConsumeAsync()
    {
        // Peek, write, then remove. The slot stays taken for the whole write, so a full channel waits until
        // the row finishes rather than until the reader merely starts it.
        while (await _channel.Reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
        {
            if (!_channel.Reader.TryPeek(out var item)) continue;
            await WriteAsync(item).ConfigureAwait(false);
            _channel.Reader.TryRead(out _);
        }
    }

    /// <summary>
    /// Appends the session file when the epoch is still current, then the transcript index, then spend.
    /// A failure is logged and swallowed so one bad item cannot stop the consumer.
    /// </summary>
    /// <param name="item">The queued request.</param>
    private async Task WriteAsync(ServedRequest item)
    {
        var dropped = item.Capture is not null;
        try
        {
            if (_abandonRemaining) return;

            var stale = item.Capture is not null && _epoch is not null && item.Capture.Epoch != _epoch.Current;
            if (item.Capture is { } capture && !stale)
            {
                try
                {
                    var store = _store.Value;
                    var archiveSessionId = store.ResolveArchiveSessionId(capture.ClientSessionId);
                    store.AppendTurn(archiveSessionId, capture.ClientSessionId, capture.Turn);
                    dropped = false;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "A captured turn could not be written to session storage and was lost.");
                }
            }
            else if (stale)
            {
                _logger.LogInformation("A captured turn that began before Clear was dropped instead of written.");
            }

            if (item.Work is not null)
            {
                await _publisher.ApplyQueuedAsync(
                    work: item.Work,
                    appendedFile: dropped == false && item.Capture is not null,
                    onIndexFailed: item.OnDropped).ConfigureAwait(false);
            }

            if (stale) item.OnDropped?.Invoke();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "A served request could not be written and was lost.");
        }
        finally
        {
            Complete(item, dropped);
        }
    }

    /// <summary>
    /// Disposes spools, tells the drop callback when the turn never reached the file, and releases the gauge.
    /// </summary>
    /// <param name="item">The item that was written, dropped, or refused.</param>
    /// <param name="dropped">Whether the session file was not appended, so the transcript row must be marked terminal.</param>
    private void Complete(ServedRequest item, bool dropped)
    {
        try
        {
            if (item.Capture is { } capture)
            {
                foreach (var body in capture.Turn.Bodies) body.Spool?.Dispose();
            }

            if (dropped) item.OnDropped?.Invoke();
        }
        finally
        {
            item.GaugeHold?.Dispose();
            Interlocked.Decrement(ref _inFlight);
        }
    }
}
