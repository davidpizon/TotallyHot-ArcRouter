using TotallyHot.ArcRouter.Sandbox.Capability;
using Moq;

namespace TotallyHot.ArcRouter.Sandbox.Tests;

/// <summary>Covers the capability probe's degradation logic against mocked host facts.</summary>
public class SandboxCapabilityProbeTests
{
    private static ISandboxHostFacts Facts(bool linux, bool cgroup, bool kvm, bool windows = false, bool jobObject = false)
    {
        var mock = new Mock<ISandboxHostFacts>();
        mock.SetupGet(f => f.IsLinux).Returns(linux);
        mock.SetupGet(f => f.IsWindows).Returns(windows);
        mock.SetupGet(f => f.IsCgroupV2Available).Returns(cgroup);
        mock.SetupGet(f => f.IsKvmAvailable).Returns(kvm);
        mock.SetupGet(f => f.IsJobObjectAvailable).Returns(jobObject);
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

    /// <summary>A host that is neither Linux nor Windows (macOS, BSD) keeps the original blunt reason.</summary>
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

    /// <summary>
    /// Windows with Job Objects reports the capability and a reason naming the missing runtime, rather
    /// than the blunt <c>host-not-linux</c> that used to cover every non-Linux host.
    /// </summary>
    [Fact]
    public void Probe_WindowsWithJobObjects_ReportsTheCapabilityAndTheMissingRuntime()
    {
        var probe = new SandboxCapabilityProbe(Facts(linux: false, cgroup: false, kvm: false, windows: true, jobObject: true));

        Assert.True(probe.IsJobObjectAvailable);
        Assert.Equal("windows-job-objects-detected-no-runtime", probe.DegradedReason);
    }

    /// <summary>Windows without Job Objects is a distinct, separately-named state - an environment problem, not a roadmap gap.</summary>
    [Fact]
    public void Probe_WindowsWithoutJobObjects_ReportsTheCapabilityAsUnavailable()
    {
        var probe = new SandboxCapabilityProbe(Facts(linux: false, cgroup: false, kvm: false, windows: true, jobObject: false));

        Assert.False(probe.IsJobObjectAvailable);
        Assert.False(probe.IsExecutionAvailable);
        Assert.Equal("windows-job-objects-unavailable", probe.DegradedReason);
    }

    /// <summary>
    /// The load-bearing invariant of the whole Windows-detection change: detecting Job Objects must never
    /// promote execution. No Windows Tier-1 runtime is registered (every runtime sits behind an
    /// <c>OperatingSystem.IsLinux()</c> guard in DI), so a true value here would only make TierSelector
    /// pick Tier1Jail and the executor fall through to a Tier-0 result stamped "no-runtime-registered" -
    /// swapping a truthful reason for a confusing one. If this test ever fails, a Windows runtime must
    /// have been added deliberately; do not "fix" it by loosening the assertion.
    /// </summary>
    [Fact]
    public void Probe_WindowsWithJobObjects_StillReportsExecutionUnavailable()
    {
        var probe = new SandboxCapabilityProbe(Facts(linux: false, cgroup: false, kvm: false, windows: true, jobObject: true));

        Assert.False(probe.IsExecutionAvailable);
    }

    /// <summary>A Linux host never reports the Windows capability, whatever the facts claim.</summary>
    [Fact]
    public void Probe_Linux_IsUnaffectedByTheWindowsCapability()
    {
        var probe = new SandboxCapabilityProbe(Facts(linux: true, cgroup: true, kvm: true));

        Assert.True(probe.IsExecutionAvailable);
        Assert.False(probe.IsJobObjectAvailable);
        Assert.Null(probe.DegradedReason);
    }
}

