namespace TotallyHot.ArcRouter.Sandbox;

/// <summary>
/// Reports, once at startup, whether real execution tiers (Tier 1/Tier 2) are available on this host.
/// When they are not, the executor degrades to Tier 0 static analysis only.
/// </summary>
public interface ISandboxCapabilityProbe
{
    /// <summary>Whether Tier 1/Tier 2 execution is available on this host.</summary>
    bool IsExecutionAvailable { get; }

    /// <summary>Whether KVM (required for Tier 2 microVMs) is available on this host.</summary>
    bool IsKvmAvailable { get; }

    /// <summary>The reason execution is unavailable (e.g. <c>host-not-linux</c>), or null when available.</summary>
    string? DegradedReason { get; }
}

