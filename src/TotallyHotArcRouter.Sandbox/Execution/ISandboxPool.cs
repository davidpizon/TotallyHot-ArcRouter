namespace TotallyHot.ArcRouter.Sandbox.Execution;

/// <summary>
/// A pool of pre-warmed, pristine isolates for one tier. Leasing takes an isolate; disposing the lease
/// resets it to a pristine state and returns it to the pool (see architecture doc §4).
/// </summary>
public interface ISandboxPool
{
    /// <summary>The tier this pool serves.</summary>
    SandboxTier Tier { get; }

    /// <summary>Leases a pristine isolate, waiting if the pool is momentarily exhausted.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A lease that must be disposed to return (and reset) the isolate.</returns>
    ValueTask<ISandboxLease> LeaseAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A leased isolate. Exposes the writable working directory the snippet runs in; disposal resets the
/// isolate (discards the per-lease tmpfs/overlay) and returns it to the pool.
/// </summary>
public interface ISandboxLease : IAsyncDisposable
{
    /// <summary>The tier of the leased isolate.</summary>
    SandboxTier Tier { get; }

    /// <summary>The writable working directory (per-lease, discarded on reset).</summary>
    string WorkingDirectory { get; }
}

