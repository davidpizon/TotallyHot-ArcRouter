using TotallyHot.ArcRouter.Sandbox;
using TotallyHot.ArcRouter.Sandbox.Execution;
using Microsoft.Extensions.Options;
using Moq;

namespace TotallyHot.ArcRouter.Sandbox.Tests;

/// <summary>Covers tier selection across host capability and content.</summary>
public class TierSelectorTests
{
    private static ISandboxCapabilityProbe Probe(bool execution, bool kvm)
    {
        var mock = new Mock<ISandboxCapabilityProbe>();
        mock.SetupGet(p => p.IsExecutionAvailable).Returns(execution);
        mock.SetupGet(p => p.IsKvmAvailable).Returns(kvm);
        mock.SetupGet(p => p.DegradedReason).Returns(execution ? null : "host-not-linux");
        return mock.Object;
    }

    private static SandboxRequest Request(SandboxLanguage language, int codeLength = 10, SandboxTier? hint = null) =>
        new(new string('x', codeLength), language, "code_generation", "gpt-5.4", "corr", "sess", hint);

    private static TierSelector CreateSelector(ISandboxCapabilityProbe probe, SandboxOptions? options = null) =>
        new(probe, Options.Create(options ?? new SandboxOptions()));

    [Fact]
    public void Select_ExecutionUnavailable_Tier0()
    {
        var selector = CreateSelector(Probe(execution: false, kvm: false));

        Assert.Equal(SandboxTier.Tier0Static, selector.Select(Request(SandboxLanguage.Python)));
    }

    [Fact]
    public void Select_NonExecutableLanguage_Tier0()
    {
        var selector = CreateSelector(Probe(execution: true, kvm: true));

        Assert.Equal(SandboxTier.Tier0Static, selector.Select(Request(SandboxLanguage.CSharp)));
    }

    [Fact]
    public void Select_SmallExecutableSnippet_Tier1()
    {
        var selector = CreateSelector(Probe(execution: true, kvm: true));

        Assert.Equal(SandboxTier.Tier1Jail, selector.Select(Request(SandboxLanguage.Python, codeLength: 100)));
    }

    [Fact]
    public void Select_LargeSnippetWithKvm_Tier2()
    {
        var options = new SandboxOptions { Tier2MinCodeBytes = 50 };
        var selector = CreateSelector(Probe(execution: true, kvm: true), options);

        Assert.Equal(SandboxTier.Tier2MicroVm, selector.Select(Request(SandboxLanguage.Python, codeLength: 100)));
    }

    [Fact]
    public void Select_LargeSnippetWithoutKvm_StaysTier1()
    {
        var options = new SandboxOptions { Tier2MinCodeBytes = 50 };
        var selector = CreateSelector(Probe(execution: true, kvm: false), options);

        Assert.Equal(SandboxTier.Tier1Jail, selector.Select(Request(SandboxLanguage.Python, codeLength: 100)));
    }

    [Fact]
    public void Select_TierHint_IsHonored()
    {
        var selector = CreateSelector(Probe(execution: true, kvm: true));

        Assert.Equal(
            SandboxTier.Tier2MicroVm,
            selector.Select(Request(SandboxLanguage.Python, hint: SandboxTier.Tier2MicroVm)));
    }
}

