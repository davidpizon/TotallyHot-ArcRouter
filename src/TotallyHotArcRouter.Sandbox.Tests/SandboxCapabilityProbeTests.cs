using TotallyHot.ArcRouter.Sandbox.Capability;
using Moq;

namespace TotallyHot.ArcRouter.Sandbox.Tests;

/// <summary>Covers the capability probe's degradation logic against mocked host facts.</summary>
public class SandboxCapabilityProbeTests
{
    private static ISandboxHostFacts Facts(bool linux, bool cgroup, bool kvm)
    {
        var mock = new Mock<ISandboxHostFacts>();
        mock.SetupGet(f => f.IsLinux).Returns(linux);
        mock.SetupGet(f => f.IsCgroupV2Available).Returns(cgroup);
        mock.SetupGet(f => f.IsKvmAvailable).Returns(kvm);
        return mock.Object;
    }

    [Fact]
    public void Probe_LinuxWithCgroupAndKvm_ExecutionAvailable()
    {
        var probe = new SandboxCapabilityProbe(Facts(linux: true, cgroup: true, kvm: true));

        Assert.True(probe.IsExecutionAvailable);
        Assert.True(probe.IsKvmAvailable);
        Assert.Null(probe.DegradedReason);
    }

    [Fact]
    public void Probe_LinuxWithoutKvm_ExecutionStillAvailableButNoKvm()
    {
        var probe = new SandboxCapabilityProbe(Facts(linux: true, cgroup: true, kvm: false));

        Assert.True(probe.IsExecutionAvailable);
        Assert.False(probe.IsKvmAvailable);
        Assert.Null(probe.DegradedReason);
    }

    [Fact]
    public void Probe_NonLinux_DegradesToTier0()
    {
        var probe = new SandboxCapabilityProbe(Facts(linux: false, cgroup: false, kvm: false));

        Assert.False(probe.IsExecutionAvailable);
        Assert.Equal("host-not-linux", probe.DegradedReason);
    }

    [Fact]
    public void Probe_LinuxWithoutCgroupV2_Degrades()
    {
        var probe = new SandboxCapabilityProbe(Facts(linux: true, cgroup: false, kvm: false));

        Assert.False(probe.IsExecutionAvailable);
        Assert.Equal("cgroup-v2-unavailable", probe.DegradedReason);
    }
}

