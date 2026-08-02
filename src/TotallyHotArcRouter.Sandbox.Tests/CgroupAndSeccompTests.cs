using TotallyHot.ArcRouter.Sandbox;
using TotallyHot.ArcRouter.Sandbox.Tier1;

namespace TotallyHot.ArcRouter.Sandbox.Tests;

/// <summary>Covers cgroup limit rendering and the seccomp allowlist policy surface.</summary>
public class CgroupAndSeccompTests
{
    [Fact]
    public void CgroupLimits_RenderConfiguredValues()
    {
        var options = new SandboxOptions { MemoryMaxBytes = 134217728, PidsMax = 64 };

        Assert.Equal("134217728", CgroupLimits.MemoryMax(options));
        Assert.Equal("64", CgroupLimits.PidsMax(options));
        Assert.Equal("0", CgroupLimits.SwapMax);
        Assert.Equal("100000 100000", CgroupLimits.CpuMax);
    }

    [Fact]
    public void SeccompAllowlist_PermitsInterpreterSyscalls()
    {
        Assert.Contains("read", SeccompAllowlist.AllowedSyscalls);
        Assert.Contains("write", SeccompAllowlist.AllowedSyscalls);
        Assert.Contains("openat", SeccompAllowlist.AllowedSyscalls);
        Assert.Contains("execve", SeccompAllowlist.AllowedSyscalls);
    }

    [Fact]
    public void SeccompAllowlist_DeniesNetworkAndPtrace()
    {
        Assert.DoesNotContain("socket", SeccompAllowlist.AllowedSyscalls);
        Assert.DoesNotContain("connect", SeccompAllowlist.AllowedSyscalls);
        Assert.DoesNotContain("ptrace", SeccompAllowlist.AllowedSyscalls);
        Assert.Equal("SCMP_ACT_KILL_PROCESS", SeccompAllowlist.DefaultAction);
    }
}

