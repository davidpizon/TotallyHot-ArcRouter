namespace TotallyHot.ArcRouter.Update;

/// <summary>
/// Thread-safe in-memory <see cref="IUpdateStateStore"/>. A single field guarded by a lock is enough:
/// writes happen at most once per <see cref="UpdateOptions.PollInterval"/> tick (plus an explicit
/// "Check Now"), and reads are simple snapshot copies.
/// </summary>
public sealed class UpdateStateStore : IUpdateStateStore
{
    private readonly Lock _gate = new();
    private UpdateStateSnapshot _current = new(Result: null, CheckedAtUtc: null);

    /// <inheritdoc />
    public UpdateStateSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <inheritdoc />
    public void Record(ReleaseCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        lock (_gate)
        {
            _current = new UpdateStateSnapshot(result, DateTimeOffset.UtcNow);
        }
    }
}
