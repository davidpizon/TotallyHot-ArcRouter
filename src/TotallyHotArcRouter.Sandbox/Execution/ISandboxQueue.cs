namespace TotallyHot.ArcRouter.Sandbox.Execution;

/// <summary>
/// A bounded, off-path work queue for sandbox requests. Enqueue is non-blocking and drops the request
/// (incrementing <see cref="DroppedCount"/>) when the queue is full, so the proxy hot path is never
/// back-pressured by a slow sandbox.
/// </summary>
public interface ISandboxQueue
{
    /// <summary>Attempts to enqueue a request without blocking.</summary>
    /// <param name="request">The request to enqueue.</param>
    /// <returns><see langword="true"/> if enqueued; <see langword="false"/> if the queue was full (dropped).</returns>
    bool TryEnqueue(SandboxRequest request);

    /// <summary>Asynchronously yields queued requests until the queue completes or is cancelled.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>An async stream of queued requests.</returns>
    IAsyncEnumerable<SandboxRequest> DequeueAllAsync(CancellationToken cancellationToken);

    /// <summary>The number of requests dropped because the queue was full.</summary>
    long DroppedCount { get; }
}

