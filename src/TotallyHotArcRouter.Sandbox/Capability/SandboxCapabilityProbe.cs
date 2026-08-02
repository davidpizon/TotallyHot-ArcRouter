using Microsoft.Extensions.Logging;

namespace TotallyHot.ArcRouter.Sandbox.Capability;

/// <summary>
/// Evaluates host facts once and reports whether real execution tiers are available. A non-Linux host or
/// a host without cgroups v2 fully degrades to Tier 0; a Linux host without KVM keeps Tier 1 but the tier
/// selector avoids Tier 2.
/// </summary>
public sealed class SandboxCapabilityProbe : ISandboxCapabilityProbe
{
    /// <summary>Initializes a new instance of the <see cref="SandboxCapabilityProbe"/> class.</summary>
    /// <param name="hostFacts">The host facts to evaluate.</param>
    /// <param name="logger">An optional logger.</param>
    public SandboxCapabilityProbe(ISandboxHostFacts hostFacts, ILogger<SandboxCapabilityProbe>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(hostFacts);

        if (!hostFacts.IsLinux)
        {
            DegradedReason = "host-not-linux";
        }
        else if (!hostFacts.IsCgroupV2Available)
        {
            DegradedReason = "cgroup-v2-unavailable";
        }

        IsExecutionAvailable = DegradedReason is null;
        IsKvmAvailable = hostFacts.IsKvmAvailable;

        logger?.LogInformation(
            "Sandbox capability probe: executionAvailable={Available}, kvm={Kvm}, reason={Reason}.",
            IsExecutionAvailable,
            IsKvmAvailable,
            DegradedReason ?? "(none)");
    }

    /// <inheritdoc />
    public bool IsExecutionAvailable { get; }

    /// <inheritdoc />
    public bool IsKvmAvailable { get; }

    /// <inheritdoc />
    public string? DegradedReason { get; }
}

