namespace TotallyHot.ArcRouter.Quality.Grading;

/// <summary>
/// A bounded, off-path work queue for grading requests. Enqueue is non-blocking and drops the request
/// (incrementing <see cref="DroppedCount"/>) when the queue is full, so the proxy hot path is never
/// back-pressured by a slow grader.
/// </summary>
public interface IQualityQueue
{
    /// <summary>The number of requests dropped because the queue was full.</summary>
    /// <remarks>
    /// Reported by <c>UnusedMemberInSuper.Global</c>: every call reaches this through the implementing type
    /// rather than this interface. It is not dead - see the declaration's callers. Narrowing the interface
    /// to match today's call sites is a design change, not a scan fix (ADR-0008's stop rules).
    /// </remarks>
    // ReSharper disable once UnusedMemberInSuper.Global
    long DroppedCount { get; }

    /// <summary>Attempts to enqueue a request without blocking.</summary>
    /// <param name="request">The request to enqueue.</param>
    /// <returns><see langword="true"/> if enqueued; <see langword="false"/> if the queue was full (dropped).</returns>
    bool TryEnqueue(QualityRequest request);

    /// <summary>Asynchronously yields queued requests until the queue completes or is cancelled.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>An async stream of queued requests.</returns>
    IAsyncEnumerable<QualityRequest> DequeueAllAsync(CancellationToken cancellationToken);
}