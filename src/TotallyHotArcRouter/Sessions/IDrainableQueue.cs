namespace TotallyHot.ArcRouter.Sessions;

/// <summary>
/// A single-reader background queue that Clear and shutdown can wait on. Both the session-file writer and
/// the served-request writer implement it, so <see cref="SessionMaintenance"/> does not care which one is
/// registered.
/// </summary>
internal interface IDrainableQueue
{
    /// <summary>
    /// Waits until every item queued so far has been written or dropped.
    /// </summary>
    /// <param name="timeout">The longest to wait.</param>
    /// <returns><see langword="true"/> when the queue emptied in time.</returns>
    Task<bool> WaitForIdleAsync(TimeSpan timeout);
}
