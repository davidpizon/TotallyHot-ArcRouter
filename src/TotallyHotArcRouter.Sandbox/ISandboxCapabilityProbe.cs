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

    /// <summary>
    /// Whether this Windows host can create Job Objects - the Windows counterpart of the cgroups v2 check
    /// that gates <see cref="IsExecutionAvailable"/> on Linux. Always <see langword="false"/> off Windows.
    /// </summary>
    /// <remarks>
    /// <b>Detected and reported, not acted on.</b> This never promotes <see cref="IsExecutionAvailable"/>,
    /// because no Windows execution runtime exists to promote it to; see
    /// <see cref="Capability.SandboxCapabilityProbe"/>'s remarks. It is here so the capability is a
    /// measured fact a future Windows runtime can build on, rather than something rediscovered later.
    /// </remarks>
    bool IsJobObjectAvailable { get; }

    /// <summary>The reason execution is unavailable (e.g. <c>host-not-linux</c>), or null when available.</summary>
    string? DegradedReason { get; }
}

