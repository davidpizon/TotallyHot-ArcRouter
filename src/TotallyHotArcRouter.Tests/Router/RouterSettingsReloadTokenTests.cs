using TotallyHot.ArcRouter.Router;

namespace TotallyHot.ArcRouter.Tests.Router;

/// <summary>
/// Covers <see cref="RouterSettingsReloadToken"/>'s swap-and-signal contract
/// (docs/router/self-organizing-classification-plan.md Phase T6): every change token handed out before a
/// <see cref="RouterSettingsReloadToken.Trigger"/> call fires, and the next
/// <see cref="RouterSettingsReloadToken.GetChangeToken"/>
/// call returns a fresh, un-signaled token rather than the one that just fired.
/// </summary>
public sealed class RouterSettingsReloadTokenTests
{
    [Fact]
    public void Trigger_SignalsEveryPreviouslyIssuedChangeToken()
    {
        var token = new RouterSettingsReloadToken();
        var changeToken = token.GetChangeToken();
        var fired = false;
        using var subscription = changeToken.RegisterChangeCallback(callback: _ => fired = true, null);

        token.Trigger();

        Assert.True(fired);
        Assert.True(changeToken.HasChanged);
    }

    [Fact]
    public void GetChangeToken_AfterTrigger_ReturnsAFreshUnsignaledToken()
    {
        var token = new RouterSettingsReloadToken();
        token.GetChangeToken();
        token.Trigger();

        var freshToken = token.GetChangeToken();

        Assert.False(freshToken.HasChanged);
    }

    [Fact]
    public void Trigger_CalledTwice_DoesNotSignalTheSecondFreshToken()
    {
        var token = new RouterSettingsReloadToken();
        token.Trigger();
        var secondToken = token.GetChangeToken();

        token.Trigger();

        Assert.True(secondToken.HasChanged);

        // A third token issued after the second Trigger must itself be unsignaled - the reload token
        // never accumulates a "signaled forever" state.
        var thirdToken = token.GetChangeToken();
        Assert.False(thirdToken.HasChanged);
    }
}