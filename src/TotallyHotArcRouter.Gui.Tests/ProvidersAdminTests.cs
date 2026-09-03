using AwesomeAssertions;
using Bunit;
using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Services;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="ProvidersAdmin"/>'s reachability states. No proxy is listening on the store's
/// address, so <c>OnInitializedAsync</c>'s <c>Store.LoadAsync()</c> always resolves to the
/// "unreachable" branch - <see cref="ProviderAdminStoreTests"/> covers the store's own contract for
/// that path; this only checks the component renders it correctly and can retry.
/// </summary>
public sealed class ProvidersAdminTests
{
    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.Services.AddSingleton(new ProviderAdminStore(managementAddress: "http://127.0.0.1:59995"));
        return ctx;
    }

    [Fact]
    public void Shows_loading_before_the_initial_load_completes()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<ProvidersAdmin>();

        cut.Markup.Should().Contain("Loading providers");
    }

    [Fact]
    public void Shows_the_unreachable_state_once_the_load_fails()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<ProvidersAdmin>();

        cut.WaitForAssertion(assertion: () => cut.Markup.Should().Contain("Proxy management API unreachable"),
            timeout: TimeSpan.FromSeconds(4));
        cut.Markup.Should().Contain(ProviderAdminStore.DefaultManagementAddress);
    }

    [Fact]
    public void Retry_button_re_triggers_a_load()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<ProvidersAdmin>();
        cut.WaitForAssertion(assertion: () => cut.Markup.Should().Contain("Proxy management API unreachable"),
            timeout: TimeSpan.FromSeconds(4));

        var act = () => cut.Find("button").Click();
        act.Should().NotThrow();

        cut.WaitForAssertion(assertion: () => cut.Markup.Should().Contain("Proxy management API unreachable"),
            timeout: TimeSpan.FromSeconds(4));
    }

    [Fact]
    public void Disposing_unsubscribes_from_the_store_without_throwing()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<ProvidersAdmin>();
        cut.WaitForAssertion(assertion: () => cut.Markup.Should().Contain("Proxy management API unreachable"),
            timeout: TimeSpan.FromSeconds(4));

        var act = () => cut.Dispose();
        act.Should().NotThrow();
    }
}