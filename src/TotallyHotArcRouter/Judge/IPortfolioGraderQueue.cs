namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// A bounded, off-path work queue for Phase Q3's portfolio-grading jobs. Mirrors
/// <see cref="IJudgeShadowScoreQueue"/> exactly - <see cref="TryEnqueue"/> is non-blocking and sheds the job
/// when full, so the routing hot path is never back-pressured by a slow grader backbone.
/// </summary>
public interface IPortfolioGraderQueue
{
    /// <summary>The number of jobs dropped because the queue was full.</summary>
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
