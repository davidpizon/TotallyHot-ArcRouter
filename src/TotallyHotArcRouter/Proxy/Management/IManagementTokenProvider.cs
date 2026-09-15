namespace TotallyHot.ArcRouter.Proxy.Management;

/// <summary>
/// Supplies the single shared management token (<see cref="ManagementAccessToken"/>'s persisted value)
/// to every management surface - MCP, the TLS gRPC endpoint, and (web GUI migration plan Phase P4) the
/// web port's token-login path - and lets an already-authenticated caller rotate it without a process
/// restart. Introduced by Phase P4 so all four call sites the plan names
/// (<c>ProxyServiceCollectionExtensions</c>, <c>McpHostedService</c>, <see cref="TotallyHot.ArcRouter.Telemetry.TelemetryAuthInterceptor"/>,
/// <see cref="TotallyHot.ArcRouter.Mcp.McpBearerAuthMiddleware"/>) share one live value instead of each capturing its own
/// copy of the token string at startup.
/// </summary>
/// <remarks>
/// Registered as a single outer-container singleton (see
/// <see cref="ProxyServiceCollectionExtensions.AddManagement"/>) and handed by reference into the proxy
/// inner host via <see cref="ProxyServerDependencies.ManagementTokenProvider"/>, so a
/// <see cref="Regenerate"/> call from either host is visible to both immediately - they observe the same
/// object, not a copy. <see cref="Generation"/> lets session-ticket validation (Phase P4's loopback/login
/// cookies) invalidate every outstanding session on rotation without maintaining a session table.
/// </remarks>
public interface IManagementTokenProvider
{
    /// <summary>Gets the current token value. Changes when <see cref="Regenerate"/> is called.</summary>
    string CurrentToken { get; }

    /// <summary>
    /// Gets a counter that increments every time <see cref="Regenerate"/> succeeds. Session tickets embed
    /// the generation they were issued under; a mismatch against the current value means the token has
    /// rotated since and the ticket is no longer valid.
    /// </summary>
    int Generation { get; }

    /// <summary>
    /// Verifies <paramref name="presented"/> against <see cref="CurrentToken"/> using the same
    /// constant-time comparison as <see cref="ManagementAccessToken.Verify"/>.
    /// </summary>
    bool Verify(string? presented);

    /// <summary>
    /// Generates a fresh token, persists it, and increments <see cref="Generation"/>, invalidating every
    /// session ticket issued under the previous value.
    /// </summary>
    /// <returns>The new token.</returns>
    string Regenerate();
}
