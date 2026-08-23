using TotallyHot.ArcRouter.Sandbox.Capability;

namespace TotallyHot.ArcRouter.Sandbox.Tests;

/// <summary>
/// Covers <see cref="SystemSandboxHostFacts"/>'s real OS-facing checks. Runs on any OS: on non-Linux hosts
/// (e.g. this Windows dev machine), <see cref="SystemSandboxHostFacts.IsLinux"/> is expected false, which
/// short-circuits the KVM/cgroup checks to false too - exercising that short-circuit is itself the behavior
/// under test.
/// </summary>
public class SystemSandboxHostFactsTests
{
    [Fact]
    public void IsLinux_MatchesRuntimeInformation()
    {
        var facts = new SystemSandboxHostFacts();

        Assert.Equal(OperatingSystem.IsLinux(), facts.IsLinux);
    }

    [Fact]
    public void IsKvmAvailable_FalseWhenNotLinux()
    {
        var facts = new SystemSandboxHostFacts();

        if (!OperatingSystem.IsLinux())
        {
            Assert.False(facts.IsKvmAvailable);
        }
    }

    [Fact]
    public void IsCgroupV2Available_FalseWhenNotLinux()
    {
        var facts = new SystemSandboxHostFacts();

        if (!OperatingSystem.IsLinux())
        {
            Assert.False(facts.IsCgroupV2Available);
        }
    }

    [Fact]
    public void IsWindows_MatchesRuntimeInformation()
    {
        var facts = new SystemSandboxHostFacts();

        Assert.Equal(OperatingSystem.IsWindows(), facts.IsWindows);
    }

    /// <summary>
    /// On Windows this makes a real <c>CreateJobObjectW</c> call, so the assertion is genuine rather than
    /// mocked: Job Objects have shipped in every supported Windows release, and a host that cannot create
    /// one is a host the sandbox could never jail on. Off Windows the property must short-circuit to false
    /// without touching kernel32 at all - exercising that short-circuit is the behavior under test there,
    /// mirroring how the cgroup/KVM tests above are written.
    /// </summary>
    [Fact]
    public void IsJobObjectAvailable_TrueOnWindows_FalseElsewhere()
    {
        var facts = new SystemSandboxHostFacts();

        Assert.Equal(OperatingSystem.IsWindows(), facts.IsJobObjectAvailable);
    }

    /// <summary>The probe is cached, so repeated reads must agree rather than re-entering the kernel each time.</summary>
    [Fact]
    public void IsJobObjectAvailable_IsStableAcrossReadsAndInstances()
    {
        var first = new SystemSandboxHostFacts().IsJobObjectAvailable;
        var second = new SystemSandboxHostFacts().IsJobObjectAvailable;

        Assert.Equal(first, second);
    }
}

