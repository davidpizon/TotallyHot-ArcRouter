using AwesomeAssertions;
using Bunit;
using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Services;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Component tests for <see cref="ToastHost"/>: the app-wide error-toast stack rendered once from
/// <c>Dashboard.razor</c>'s shell, driven entirely by <see cref="ToastService"/>.
/// </summary>
public sealed class ToastHostTests
{
    [Fact]
    public void Renders_nothing_when_there_are_no_toasts()
    {
        using var ctx = new Bunit.BunitContext();
        ctx.Services.AddSingleton(new ToastService());

        var cut = ctx.Render<ToastHost>();

        cut.FindAll(".ls-toast").Should().BeEmpty();
    }

    [Fact]
    public void A_shown_toast_renders_its_title_and_message()
    {
        using var ctx = new Bunit.BunitContext();
        var toasts = new ToastService();
        ctx.Services.AddSingleton(toasts);
        var cut = ctx.Render<ToastHost>();

        toasts.ShowError("Providers unreachable", "connection refused");

        cut.WaitForAssertion(() => cut.FindAll(".ls-toast").Should().ContainSingle());
        cut.Markup.Should().Contain("Providers unreachable");
        cut.Markup.Should().Contain("connection refused");
    }

    [Fact]
    public void Clicking_the_close_glyph_dismisses_the_toast()
    {
        using var ctx = new Bunit.BunitContext();
        var toasts = new ToastService();
        ctx.Services.AddSingleton(toasts);
        toasts.ShowError("title", "message");
        var cut = ctx.Render<ToastHost>();
        cut.WaitForAssertion(() => cut.FindAll(".ls-toast").Should().ContainSingle());

        cut.Find("button[aria-label='Dismiss notification']").Click();

        cut.FindAll(".ls-toast").Should().BeEmpty();
        toasts.Toasts.Should().BeEmpty();
    }

    [Fact]
    public void Multiple_toasts_stack()
    {
        using var ctx = new Bunit.BunitContext();
        var toasts = new ToastService();
        ctx.Services.AddSingleton(toasts);
        var cut = ctx.Render<ToastHost>();

        toasts.ShowError("First", "a");
        toasts.ShowError("Second", "b");

        cut.WaitForAssertion(() => cut.FindAll(".ls-toast").Should().HaveCount(2));
    }
}
