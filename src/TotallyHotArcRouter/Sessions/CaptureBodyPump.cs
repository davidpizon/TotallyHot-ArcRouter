using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Sessions;

/// <summary>What the pump's worker does with one queued item.</summary>
internal enum CaptureOp
{
    /// <summary>Feed the item's bytes to the body's spool, creating the spool first if needed.</summary>
    Write,

    /// <summary>Finish the body's spool and hand it to the waiting caller.</summary>
    Complete,

    /// <summary>Abandon the body's spool and delete its file.</summary>
    Release,
}

/// <summary>One unit of work for the pump's worker.</summary>
/// <param name="Body">The body the work applies to.</param>
/// <param name="Op">What to do.</param>
/// <param name="Buffer">A rented buffer holding the bytes of a <see cref="CaptureOp.Write"/>; the worker returns it.</param>
/// <param name="Length">How many bytes of <paramref name="Buffer"/> are valid.</param>
/// <param name="Completion">Receives the finished spool of a <see cref="CaptureOp.Complete"/>.</param>
internal readonly record struct CaptureWork(
    CaptureBody Body,
    CaptureOp Op,
    byte[]? Buffer,
    int Length,
    TaskCompletionSource<SessionBodySpool?>? Completion);

/// <summary>
/// Moves every captured body's compression, encryption and disk writes off the proxy's relay path (#165 review
/// finding): a tee on the client's request or response hands its bytes to this pump, which has one worker
/// that feeds the spools in order, so the relay pays a memory copy and never waits on the disk. The queue is
/// bounded by bytes. A body that would push it over <see cref="SessionCaptureOptions.PumpMaxQueuedBytes"/>
/// is abandoned and recorded as missing, because slowing the client's stream is the one thing capture must
/// never do (ADR-0019: a body is complete or absent, never a prefix).
/// </summary>
/// <remarks>
/// This is a plain hosted service rather than a <see cref="BackgroundService"/>. On .NET 10 a
/// <c>BackgroundService</c> starts <c>ExecuteAsync</c> on a pool thread and cancels it if the host stops first,
/// in which case <c>ExecuteAsync</c> never runs and anything already queued is stranded; the pump must instead
/// always run what it accepted, so it owns its worker and drains it on stop.
/// </remarks>
public sealed class CaptureBodyPump : IHostedService, IDisposable
{
    private readonly Channel<CaptureWork> _channel = Channel.CreateUnbounded<CaptureWork>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly long _maxQueuedBytes;
    private readonly ILogger<CaptureBodyPump> _logger;
    private Task? _worker;
    private long _queuedBytes;

    /// <summary>
    /// Initializes a new instance of the <see cref="CaptureBodyPump"/> class.
    /// </summary>
    /// <param name="options">Supplies the queue's byte limit.</param>
    /// <param name="logger">Receives failures in the worker; message templates are static.</param>
    public CaptureBodyPump(IOptions<SessionCaptureOptions> options, ILogger<CaptureBodyPump> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _maxQueuedBytes = Math.Max(1, options.Value.PumpMaxQueuedBytes);
        _logger = logger;
    }

    /// <summary>
    /// Starts a body whose spool is created by <paramref name="createSpool"/> on the worker, at the first byte
    /// (or at completion for an empty body), so nothing touches the disk on the caller's thread.
    /// </summary>
    /// <param name="createSpool">Creates the spool; runs on the worker.</param>
    /// <returns>A body to write to, complete or release.</returns>
    public CaptureBody CreateBody(Func<SessionBodySpool> createSpool)
    {
        ArgumentNullException.ThrowIfNull(createSpool);
        return new CaptureBody(this, createSpool);
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // The worker does blocking file, compression and encryption work for as long as the router runs, so it
        // gets a thread of its own instead of pinning a thread-pool thread that request handling also needs.
        _worker ??= Task.Factory.StartNew(
            Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Completing the channel lets the worker drain what is queued and then end; nothing new is accepted.
        _channel.Writer.TryComplete();
        if (_worker is not null) await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void Dispose() => _channel.Writer.TryComplete();

    /// <summary>Counts <paramref name="bytes"/> against the queue limit.</summary>
    /// <param name="bytes">The size of a write about to be queued.</param>
    /// <returns><see langword="false"/> when queuing it would exceed the limit, in which case nothing is counted.</returns>
    internal bool TryReserve(int bytes)
    {
        if (Interlocked.Add(ref _queuedBytes, bytes) <= _maxQueuedBytes) return true;

        Interlocked.Add(ref _queuedBytes, -bytes);
        return false;
    }

    /// <summary>Gives back bytes counted by <see cref="TryReserve"/>.</summary>
    /// <param name="bytes">The size of a write that was processed or never queued.</param>
    internal void Unreserve(int bytes) => Interlocked.Add(ref _queuedBytes, -bytes);

    /// <summary>Queues work for the worker.</summary>
    /// <param name="work">The work.</param>
    /// <returns><see langword="false"/> once the pump has stopped accepting work.</returns>
    internal bool Post(CaptureWork work) => _channel.Writer.TryWrite(work);

    /// <summary>Logs a failure inside the worker.</summary>
    /// <param name="exception">What went wrong.</param>
    internal void LogFailure(Exception exception) =>
        _logger.LogWarning(exception, "A captured body could not be written and was recorded as missing.");

    /// <summary>
    /// Applies queued work in order until the channel is completed by <see cref="StopAsync"/> and drained, so a
    /// stop finishes what was already queued.
    /// </summary>
    private void Run()
    {
        while (_channel.Reader.WaitToReadAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult())
        {
            while (_channel.Reader.TryRead(out var work))
            {
                try
                {
                    work.Body.Apply(work);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Apply handles its own failures; this keeps one unforeseen fault from ending the worker.
                    LogFailure(ex);
                }
            }
        }
    }
}
