namespace TotallyHot.ArcRouter.Sandbox;

/// <summary>
/// The isolation tier used to evaluate an extracted code snippet, escalating in isolation strength and
/// cost from in-process static analysis through to a hardware-isolated microVM.
/// </summary>
public enum SandboxTier
{
    /// <summary>
    /// In-process static analysis only (parse/compile validity). No execution; available on any OS.
    /// </summary>
    Tier0Static,

    /// <summary>
    /// Native Linux primitive jail (namespaces, cgroups v2, seccomp, tmpfs). Executes but shares the host kernel.
    /// </summary>
    Tier1Jail,

    /// <summary>
    /// Firecracker microVM restored from a snapshot. Hardware (KVM) isolation with a separate guest kernel.
    /// </summary>
    Tier2MicroVm,
}

