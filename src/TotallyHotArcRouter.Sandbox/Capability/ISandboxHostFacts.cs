namespace TotallyHot.ArcRouter.Sandbox.Capability;

/// <summary>
/// Low-level host facts consulted by the capability probe. Abstracted so the probe is unit-testable
/// without a real Linux/KVM host.
/// </summary>
public interface ISandboxHostFacts
{
    /// <summary>Whether the host OS is Linux.</summary>
    bool IsLinux { get; }

    /// <summary>Whether the host OS is Windows. The sibling of <see cref="IsLinux"/>; a host that is neither is treated as an ordinary unsupported platform (macOS, BSD).</summary>
    bool IsWindows { get; }

    /// <summary>Whether <c>/dev/kvm</c> is present (KVM available for Tier 2 microVMs).</summary>
    bool IsKvmAvailable { get; }

    /// <summary>Whether cgroups v2 is mounted (<c>/sys/fs/cgroup/cgroup.controllers</c> present).</summary>
    bool IsCgroupV2Available { get; }

    /// <summary>
    /// Whether this Windows host can create Job Objects - the Windows counterpart of
    /// <see cref="IsCgroupV2Available"/>, and the reason it is named to read as its sibling. A Job Object
    /// supplies the same resource leash cgroups v2 does on Linux: a memory cap, a CPU-time cap, an
    /// active-process cap, and kill-on-close cleanup of the whole process tree, which is what
    /// <c>SandboxOptions.MemoryMaxBytes</c>/<c>PidsMax</c>/<c>MaxWallClockMs</c> would bind to.
    /// </summary>
    /// <remarks>
    /// <b>Reported, not yet acted on.</b> Nothing executes on Windows today - see
    /// <see cref="SandboxCapabilityProbe"/>'s remarks for why this fact never promotes
    /// <c>IsExecutionAvailable</c>. It exists so the probe can say *which* Windows state a host is in
    /// rather than only "not Linux", and so a future Windows runtime starts from a measured fact.
    /// </remarks>
    bool IsJobObjectAvailable { get; }
}

