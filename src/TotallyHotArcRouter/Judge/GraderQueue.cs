using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// A bounded, off-path work queue for every LLM grader (G-Eval judge and the CodeJudge/ICE-Score/RACE
/// portfolio). <see cref="TryEnqueue"/> is non-blocking and sheds the job when full, so the routing hot
/// path is never back-pressured by a slow backbone. Jobs are partitioned into one lane per grader key and
/// each lane is drained independently, so a slow backbone for one grader can never stall the others'
/// scoring. Capacity is still one shared bound across all lanes, so the total backlog stays capped.
/// </summary>
public interface IGraderQueue
{
    /// <summary>The number of jobs dropped because the queue was at capacity.</summary>
    /// <remarks>
    /// Reported by <c>UnusedMemberInSuper.Global</c>: every call reaches this through the implementing type
    /// rather than this interface. It is not dead - see the declaration's callers. Narrowing the interface
    /// to match today's call sites is a design change, not a scan fix (ADR-0008's stop rules).
    /// </remarks>
    // ReSharper disable once UnusedMemberInSuper.Global
    long DroppedCount { get; }

    /// <summary>Attempts to enqueue a job onto its <see cref="GraderScoringJob.GraderKey"/> lane without blocking.</summary>
    /// <param name="job">The job to enqueue.</param>
    /// <returns><see langword="true"/> if enqueued; <see langword="false"/> if the queue was at capacity (dropped).</returns>
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
/// An <see cref="IGraderQueue"/> backed by one <see cref="Channel{T}"/> per grader key, with a single
/// <see cref="JudgeOptions.QueueCapacity"/> bound on the number of jobs waiting across every lane combined.
/// The shared bound keeps worst-case memory fixed no matter how many graders are registered, while the
/// per-key lanes keep execution independent. Lanes are created on first use by either side, so a writer
/// and its reader always meet on the same channel regardless of which touches the key first.
/// </summary>
public sealed class GraderQueue : IGraderQueue
{
    private readonly int _capacity;

    private readonly ConcurrentDictionary<string, Channel<GraderScoringJob>> _lanes =
        new(StringComparer.OrdinalIgnoreCase);

    private long _droppedCount;

    private int _queuedCount;

    /// <summary>Initializes a new instance of the <see cref="GraderQueue"/> class.</summary>
    /// <param name="options">The judge options carrying the shared queue capacity.</param>
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

        var lane = Lane(job.GraderKey);

        // Reserve a slot in the shared bound before writing, and release it if either the bound or the
        // write refuses, so concurrent enqueuers can never overshoot the capacity between check and write.
        if (Interlocked.Increment(ref _queuedCount) <= _capacity && lane.Writer.TryWrite(job)) return true;

        Interlocked.Decrement(ref _queuedCount);
        Interlocked.Increment(ref _droppedCount);
        return false;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<GraderScoringJob> DequeueAllAsync(string graderKey,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var job in Lane(graderKey).Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            // Released on dequeue, not on completion: the bound caps jobs waiting, and a job a consumer
            // is already scoring no longer occupies queue memory.
            Interlocked.Decrement(ref _queuedCount);
            yield return job;
        }
    }

    /// <summary>Returns the lane for <paramref name="graderKey"/>, creating it on first use.</summary>
    private Channel<GraderScoringJob> Lane(string graderKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graderKey);

        // Unbounded per lane because the shared _queuedCount reservation in TryEnqueue is the real bound;
        // a per-lane channel bound on top of it could only ever be looser.
        return _lanes.GetOrAdd(graderKey, static _ => Channel.CreateUnbounded<GraderScoringJob>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false }));
    }
}
