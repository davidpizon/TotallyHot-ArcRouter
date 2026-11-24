namespace TotallyHot.ArcRouter.Update;

/// <summary>
/// Thread-safe in-memory <see cref="IUpdateStateStore"/>. A single field guarded by a lock is enough:
/// writes happen at most once per <see cref="UpdateOptions.PollInterval"/> tick (plus an explicit
/// "Check Now"), and reads are simple snapshot copies.
/// </summary>
public sealed class UpdateStateStore : IUpdateStateStore
{
    private readonly Lock _gate = new();
    private UpdateStateSnapshot _current = new(null, null);
    private PendingUpdateApply? _pending;

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public void Record(ReleaseCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        lock (_gate)
        {
            if (result is not { IsUpdateAvailable: true }) _pending = null;

            _current = new UpdateStateSnapshot(
                Result: result,
                CheckedAtUtc: DateTimeOffset.UtcNow,
                ApplyPending: _pending is not null);
        }
    }

    /// <inheritdoc/>
    public (bool Created, PendingUpdateApply Pending) TryRequestApply()
    {
        lock (_gate)
        {
            if (_pending is { } existing) return (Created: false, Pending: existing);

            if (_current.Result is not
                {
                    IsUpdateAvailable: true,
                    AssetDownloadUrl: { } url,
                    AssetSha256: { } sha,
                    LatestVersion: { } version
                })
            {
                throw new InvalidOperationException("No verified update is available to install.");
            }

            _pending = new PendingUpdateApply(LatestVersion: version, AssetDownloadUrl: url, AssetSha256: sha);
            _current = _current with { ApplyPending = true };
            return (Created: true, Pending: _pending);
        }
    }

    /// <inheritdoc/>
    public PendingUpdateApply? TryClaimApply()
    {
        lock (_gate)
        {
            var pending = _pending;
            _pending = null;
            if (pending is not null) _current = _current with { ApplyPending = false };

            return pending;
        }
    }
}