using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// A bounded, off-path work queue for every LLM grader (G-Eval judge and the CodeJudge/ICE-Score/RACE
/// portfolio). <see cref="TryEnqueue"/> is non-blocking and sheds the job when full, so the routing hot
/// path is never back-pressured by a slow backbone. Jobs are partitioned into one lane per grader key and
/// each lane is drained independently, so a slow backbone for one grader can never stall the others.
/// </summary>
public interface IGraderQueue
{
    /// <summary>The number of jobs dropped because their grader's lane was full, summed across lanes.</summary>
    /// <remarks>
    /// Reported by <c>UnusedMemberInSuper.Global</c>: every call reaches this through the implementing type
    /// rather than this interface. It is not dead - see the declaration's callers. Narrowing the interface
    /// to match today's call sites is a design change, not a scan fix (ADR-0008's stop rules).
    /// </remarks>
    // ReSharper disable once UnusedMemberInSuper.Global
    long DroppedCount { get; }

    /// <summary>Attempts to enqueue a job onto its <see cref="GraderScoringJob.GraderKey"/> lane without blocking.</summary>
    /// <param name="job">The job to enqueue.</param>
    /// <returns><see langword="true"/> if enqueued; <see langword="false"/> if that lane was full (dropped).</returns>
    bool TryEnqueue(GraderScoringJob job);

    /// <summary>
    /// Asynchronously yields the jobs queued for <paramref name="graderKey"/> until the queue completes or
    /// is cancelled. Each lane is meant to have exactly one reader.
    /// </summary>
    /// <param name="graderKey">The grader whose lane to drain, e.g. <see cref="Quality.GraderKeys.Judge"/>.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>An async stream of that grader's queued jobs, in enqueue order.</returns>
    IAsyncEnumerable<GraderScoringJob> DequeueAllAsync(string graderKey, CancellationToken cancellationToken);
}

/// <summary>
/// An <see cref="IGraderQueue"/> backed by one bounded <see cref="Channel{T}"/> per grader key, each sized
/// by <see cref="JudgeOptions.QueueCapacity"/> - the same per-queue bound the separate judge and portfolio
/// queues had before they were collapsed. Lanes are created on first use by either side, so a writer and
/// its reader always meet on the same channel regardless of which touches the key first.
/// </summary>
public sealed class GraderQueue : IGraderQueue
{
    private readonly int _capacity;

    private readonly ConcurrentDictionary<string, Channel<GraderScoringJob>> _lanes =
        new(StringComparer.OrdinalIgnoreCase);

    private long _droppedCount;

    /// <summary>Initializes a new instance of the <see cref="GraderQueue"/> class.</summary>
    /// <param name="options">The judge options carrying the per-lane queue capacity.</param>
    public GraderQueue(IOptions<JudgeOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _capacity = Math.Max(1, val2: options.Value.QueueCapacity);
    }

    /// <inheritdoc/>
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    /// <inheritdoc/>
    public bool TryEnqueue(GraderScoringJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (Lane(job.GraderKey).Writer.TryWrite(job)) return true;

        Interlocked.Increment(ref _droppedCount);
        return false;
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<GraderScoringJob> DequeueAllAsync(string graderKey, CancellationToken cancellationToken)
    {
        return Lane(graderKey).Reader.ReadAllAsync(cancellationToken);
    }

    /// <summary>Returns the lane for <paramref name="graderKey"/>, creating it on first use.</summary>
    private Channel<GraderScoringJob> Lane(string graderKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graderKey);

        // BoundedChannelFullMode.Wait makes TryWrite return false (without blocking) when the channel is
        // full, which is what drop-on-full accounting needs - the Drop* modes would instead return true
        // while silently discarding an item, hiding the drop from DroppedCount.
        return _lanes.GetOrAdd(graderKey, static (_, capacity) => Channel.CreateBounded<GraderScoringJob>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            }), _capacity);
    }
}
