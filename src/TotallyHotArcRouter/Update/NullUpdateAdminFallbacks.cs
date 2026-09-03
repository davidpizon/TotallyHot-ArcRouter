namespace TotallyHot.ArcRouter.Update;

/// <summary>
/// Fallback <see cref="IReleaseCheckClient"/> used only when <see cref="Proxy.ProxyServer"/> is
/// constructed without a <see cref="Proxy.UpdateAdminDependencies"/> group (e.g. a minimal test harness
/// that doesn't care about the update feature). <see cref="Update.UpdateAdminGrpcService"/> is mapped
/// unconditionally - see <see cref="Proxy.ProxyServerDependencies.UpdateAdmin"/>'s remarks - so it must
/// always have something constructible to resolve, even when nothing real backs it.
/// </summary>
public sealed class NullReleaseCheckClient : IReleaseCheckClient
{
    /// <inheritdoc/>
    public Task<ReleaseCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(ReleaseCheckResult.Unavailable(
            currentVersion: "0.0.0",
            reason: ReleaseCheckUnavailableReason.NetworkOrApiFailure,
            detail: "Update checking was not configured for this server instance."));
    }
}