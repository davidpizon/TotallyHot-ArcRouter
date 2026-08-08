namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Configuration for provider cost reconciliation (§5.8), bound from the <c>CostTracking:Reconciliation</c>
/// section.
/// </summary>
public sealed class CostReconciliationOptions
{
    /// <summary>Gets the configuration section name used for reconciliation settings.</summary>
    public const string SectionName = "CostTracking:Reconciliation";

    /// <summary>Gets how often the background poll loop runs a reconciliation cycle. Default 1 hour.</summary>
    public int PollIntervalHours { get; init; } = 1;

    /// <summary>
    /// Gets the percentage difference between a provider's reported cost and the local estimate above
    /// which the delta is logged at Warning instead of Debug - a persistently large gap likely means the
    /// local price table is wrong for that provider's models, not that anything is broken. Default 20%.
    /// </summary>
    public decimal DeltaWarningPercent { get; init; } = 20m;

    /// <summary>
    /// Gets the per-provider reconciliation configuration, keyed by provider (matches
    /// <c>ModelRouting:Providers</c>). A provider absent here, or present with no
    /// <see cref="ProviderReconciliationOptions.AdminApiKeyEnvVar"/> resolving to a non-empty value, has no
    /// reconciler registered - reconciliation is optional per provider, per §5.8.
    /// </summary>
    public Dictionary<string, ProviderReconciliationOptions> Providers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Per-provider reconciliation configuration.</summary>
public sealed class ProviderReconciliationOptions
{
    /// <summary>
    /// Gets the name of the environment variable holding the provider's Admin API key - a distinct, more
    /// privileged credential than the provider's per-request inference key, following the
    /// <c>ValueEnvVar</c> naming convention already used by provider header entries.
    /// </summary>
    public string? AdminApiKeyEnvVar { get; init; }
}
