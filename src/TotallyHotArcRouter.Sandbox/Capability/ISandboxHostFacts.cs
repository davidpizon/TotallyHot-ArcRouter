namespace TotallyHot.ArcRouter.Sandbox.Capability;

/// <summary>
/// Low-level host facts consulted by the capability probe. Abstracted so the probe is unit-testable
/// without a real Linux/KVM host.
/// </summary>
public interface ISandboxHostFacts
{
    /// <summary>Whether the host OS is Linux.</summary>
    bool IsLinux { get; }

    /// <summary>Whether <c>/dev/kvm</c> is present (KVM available for Tier 2 microVMs).</summary>
    bool IsKvmAvailable { get; }

    /// <summary>Whether cgroups v2 is mounted (<c>/sys/fs/cgroup/cgroup.controllers</c> present).</summary>
    bool IsCgroupV2Available { get; }
}

