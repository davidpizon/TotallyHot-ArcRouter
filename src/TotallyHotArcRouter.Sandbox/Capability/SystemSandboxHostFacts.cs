using System.Runtime.InteropServices;

namespace TotallyHot.ArcRouter.Sandbox.Capability;

/// <summary>Reads real host facts from the operating system.</summary>
public sealed class SystemSandboxHostFacts : ISandboxHostFacts
{
    /// <inheritdoc />
    public bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    /// <inheritdoc />
    public bool IsKvmAvailable => IsLinux && File.Exists("/dev/kvm");

    /// <inheritdoc />
    public bool IsCgroupV2Available => IsLinux && File.Exists("/sys/fs/cgroup/cgroup.controllers");
}

