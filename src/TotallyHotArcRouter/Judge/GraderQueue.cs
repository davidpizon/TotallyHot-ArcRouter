using Microsoft.Extensions.Options;
using System.Threading.Channels;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// A bounded, off-path work queue for every LLM grader (G-Eval judge and the CodeJudge/ICE-Score/RACE
/// portfolio). <see cref="TryEnqueue"/> is non-blocking and sheds the job when full, so the routing hot
/// path is never back-pressured by a slow backbone.
/// </summary>
public interface IGraderQueue
{
    /// <summary>The number of jobs dropped because the queue was full.</summary>
    long DroppedCount { get; }

    /// <summary>Attempts to enqueue a job without blocking.</summary>
    /// <param name="job">The job to enqueue.</param>
    /// <returns><see langword="true"/> if enqueued; <see langword="false"/> if the queue was full (dropped).</returns>
    bool TryEnqueue(GraderScoringJob job);

    /// <summary>Asynchronously yields queued jobs until the queue completes or is cancelled.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>An async stream of queued jobs.</returns>
    IAsyncEnumerable<GraderScoringJob> DequeueAllAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A bounded <see cref="Channel{T}"/>-backed <see cref="IGraderQueue"/>, sized by
/// <see cref="JudgeOptions.QueueCapacity"/>.
/// </summary>
public sealed class GraderQueue : IGraderQueue
{
    private readonly Channel<GraderScoringJob> _channel;
    private long _droppedCount;

    /// <summary>Initializes a new instance of the <see cref="GraderQueue"/> class.</summary>
    /// <param name="options">The judge options carrying the queue capacity.</param>
    public GraderQueue(IOptions<JudgeOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var capacity = Math.Max(1, val2: options.Value.QueueCapacity);
        // BoundedChannelFullMode.Wait makes TryWrite return false (without blocking) when the channel is
        // full, which is what drop-on-full accounting needs - the Drop* modes would instead return true
        // while silently discarding an item, hiding the drop from DroppedCount.
        _channel = Channel.CreateBounded<GraderScoringJob>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
    }

    /// <inheritdoc/>
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    /// <inheritdoc/>
    public bool TryEnqueue(GraderScoringJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (_channel.Writer.TryWrite(job)) return true;

        Interlocked.Increment(ref _droppedCount);
        return false;
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<GraderScoringJob> DequeueAllAsync(CancellationToken cancellationToken)
    {
        return _channel.Reader.ReadAllAsync(cancellationToken);
    }
}
