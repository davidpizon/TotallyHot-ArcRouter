using Microsoft.Extensions.Logging;

namespace TotallyHot.ArcRouter.Sandbox.Capability;

/// <summary>
/// Evaluates host facts once and reports whether real execution tiers are available. A non-Linux host or
/// a host without cgroups v2 fully degrades to Tier 0; a Linux host without KVM keeps Tier 1 but the tier
/// selector avoids Tier 2.
/// </summary>
/// <remarks>
/// <para>
/// <b>Windows is diagnosed, not enabled.</b> A Windows host reports which of two states it is in - Job
/// Objects available (the cgroups v2 counterpart, see
/// <see cref="ISandboxHostFacts.IsJobObjectAvailable"/>) or not - instead of the single blunt
/// <c>host-not-linux</c> that used to cover every non-Linux machine. That is the whole of the change:
/// <see cref="IsExecutionAvailable"/> stays <see langword="false"/> on Windows either way.
/// </para>
/// <para>
/// It must stay false. <c>SandboxServiceCollectionExtensions</c> registers every Tier-1/Tier-2 runtime
/// behind an <c>OperatingSystem.IsLinux()</c> guard, so a Windows process has <em>no</em> runtimes
/// registered at all; promoting this flag would only make <c>TierSelector</c> choose <c>Tier1Jail</c> and
/// <c>SandboxExecutor</c> fall through to a Tier-0 result stamped <c>no-runtime-registered</c> - trading a
/// truthful reason for a misleading one. Detecting the capability is a prerequisite for a Windows runtime,
/// not a substitute for one.
/// </para>
/// </remarks>
public sealed class SandboxCapabilityProbe : ISandboxCapabilityProbe
{
    /// <summary>Initializes a new instance of the <see cref="SandboxCapabilityProbe"/> class.</summary>
    /// <param name="hostFacts">The host facts to evaluate.</param>
    /// <param name="logger">An optional logger.</param>
    public SandboxCapabilityProbe(ISandboxHostFacts hostFacts, ILogger<SandboxCapabilityProbe>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(hostFacts);

        IsJobObjectAvailable = hostFacts.IsJobObjectAvailable;

        if (hostFacts.IsWindows)
        {
            // Both branches degrade; they differ only in what they tell the operator. "Detected, no
            // runtime" says the host could host a jail and the software cannot yet build one - a roadmap
            // gap. "Unavailable" says the host itself cannot - an environment problem.
            DegradedReason = IsJobObjectAvailable
                ? "windows-job-objects-detected-no-runtime"
                : "windows-job-objects-unavailable";
        }
        else if (!hostFacts.IsLinux)
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
            "Sandbox capability probe: executionAvailable={Available}, kvm={Kvm}, windowsJobObjects={JobObjects}, reason={Reason}.",
            IsExecutionAvailable,
            IsKvmAvailable,
            IsJobObjectAvailable,
            DegradedReason ?? "(none)");
    }

    /// <inheritdoc />
    public bool IsExecutionAvailable { get; }

    /// <inheritdoc />
    public bool IsKvmAvailable { get; }

    /// <inheritdoc />
    public bool IsJobObjectAvailable { get; }

    /// <inheritdoc />
    public string? DegradedReason { get; }
}

