namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// A bounded, off-path work queue for Phase Q3's portfolio-grading jobs. Mirrors
/// <see cref="IJudgeShadowScoreQueue"/> exactly - <see cref="TryEnqueue"/> is non-blocking and sheds the job
/// when full, so the routing hot path is never back-pressured by a slow grader backbone.
/// </summary>
public interface IPortfolioGraderQueue
{
    /// <summary>The number of jobs dropped because the queue was full.</summary>
    /// <remarks>
    /// Reported by <c>UnusedMemberInSuper.Global</c>: every call reaches this through the implementing type
    /// rather than this interface. It is not dead - see the declaration's callers. Narrowing the interface
    /// to match today's call sites is a design change, not a scan fix (ADR-0008's stop rules).
    /// </remarks>
    // ReSharper disable once UnusedMemberInSuper.Global
    long DroppedCount { get; }

    /// <summary>Attempts to enqueue a job without blocking.</summary>
    /// <param name="job">The job to enqueue.</param>
    /// <returns><see langword="true"/> if enqueued; <see langword="false"/> if the queue was full (dropped).</returns>
    bool TryEnqueue(PortfolioGraderJob job);

    /// <summary>Asynchronously yields queued jobs until the queue completes or is cancelled.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>An async stream of queued jobs.</returns>
    IAsyncEnumerable<PortfolioGraderJob> DequeueAllAsync(CancellationToken cancellationToken);
}
