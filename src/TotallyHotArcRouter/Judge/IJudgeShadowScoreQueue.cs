namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// A bounded, off-path work queue for shadow-judging jobs. <see cref="TryEnqueue"/> is non-blocking and
/// sheds the job (incrementing <see cref="DroppedCount"/>) when the queue is full, so the routing hot path
/// is never back-pressured by a slow judge backbone (docs/router/geval-shadow-scoring-plan.md's ground
/// rule: "the routing hot path never blocks on judging"). Mirrors
/// <see cref="Quality.Grading.IQualityQueue"/> exactly.
/// </summary>
public interface IJudgeShadowScoreQueue
{
    /// <summary>Attempts to enqueue a job without blocking.</summary>
    /// <param name="job">The job to enqueue.</param>
    /// <returns><see langword="true"/> if enqueued; <see langword="false"/> if the queue was full (dropped).</returns>
    bool TryEnqueue(JudgeShadowScoringJob job);

    /// <summary>Asynchronously yields queued jobs until the queue completes or is cancelled.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>An async stream of queued jobs.</returns>
    IAsyncEnumerable<JudgeShadowScoringJob> DequeueAllAsync(CancellationToken cancellationToken);

    /// <summary>The number of jobs dropped because the queue was full.</summary>
    long DroppedCount { get; }
}
