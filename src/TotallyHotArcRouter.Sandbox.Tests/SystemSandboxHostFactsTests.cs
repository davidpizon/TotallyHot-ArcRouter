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
}

