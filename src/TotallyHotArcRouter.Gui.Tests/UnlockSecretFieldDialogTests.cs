using TotallyHot.ArcRouter.Gui.Components;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components.Web;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Component tests for <see cref="UnlockSecretFieldDialog"/> - the Continue/Cancel confirmation
/// <see cref="SecretField"/> opens before clearing a locked value. Smaller-blast-radius sibling of
/// <see cref="RemoveProviderDialog"/>: a single field rather than a whole provider, so it needs no
/// type-to-confirm gate, just Continue/Cancel plus the same Escape/backdrop dismissal every dialog
/// built on <see cref="DialogShell"/> gets for free.
/// </summary>
public sealed class UnlockSecretFieldDialogTests
{
    private static IRenderedComponent<UnlockSecretFieldDialog> Render(
        BunitContext ctx,
        string? testId = null,
        Action? onConfirm = null,
        Action? onCancel = null) =>
        ctx.Render<UnlockSecretFieldDialog>(parameters =>
        {
            parameters.Add(p => p.TestId, testId);
            parameters.Add(p => p.OnConfirm, () => onConfirm?.Invoke());
            parameters.Add(p => p.OnCancel, () => onCancel?.Invoke());
        });

    private static AngleSharp.Dom.IElement FindContinueButton(IRenderedComponent<UnlockSecretFieldDialog> cut) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Continue");

    private static AngleSharp.Dom.IElement FindCancelButton(IRenderedComponent<UnlockSecretFieldDialog> cut) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel");

    [Fact]
    public void Renders_the_title_and_warning()
    {
        using var ctx = new BunitContext();

        var cut = Render(ctx);

        cut.Markup.Should().Contain("Unlock Value");
        cut.Markup.Should().Contain("locked and was never sent to");
    }

    [Fact]
    public void Continue_confirms()
    {
        using var ctx = new BunitContext();
        var confirmed = false;

        var cut = Render(ctx, onConfirm: () => confirmed = true);
        FindContinueButton(cut).Click();

        confirmed.Should().BeTrue();
    }

    [Fact]
    public void Cancel_closes_without_confirming()
    {
        using var ctx = new BunitContext();
        var confirmed = false;
        var cancelled = false;

        var cut = Render(ctx, onConfirm: () => confirmed = true, onCancel: () => cancelled = true);
        FindCancelButton(cut).Click();

        cancelled.Should().BeTrue();
        confirmed.Should().BeFalse();
    }

    [Fact]
    public void Escape_cancels_even_when_a_button_has_focus()
    {
        using var ctx = new BunitContext();
        var cancelled = false;

        var cut = Render(ctx, onCancel: () => cancelled = true);

        // DialogShell's EnableEscapeToClose handles Escape on the panel, so it fires regardless of which
        // focusable descendant is focused - here, the Continue button rather than a text input.
        FindContinueButton(cut).KeyDown(new KeyboardEventArgs { Key = "Escape" });

        cancelled.Should().BeTrue();
    }

    [Fact]
    public void Escape_never_confirms()
    {
        using var ctx = new BunitContext();
        var confirmed = false;

        var cut = Render(ctx, onConfirm: () => confirmed = true);
        FindContinueButton(cut).KeyDown(new KeyboardEventArgs { Key = "Escape" });

        confirmed.Should().BeFalse();
    }

    [Fact]
    public void The_close_button_has_an_accessible_name()
    {
        using var ctx = new BunitContext();

        var cut = Render(ctx);

        // The close control's only content is an <svg>, so without an aria-label a screen reader
        // announces an unnamed button. docs/gui/DESIGN.md 4.1 requires the label on every window.
        var close = cut.Find("button[aria-label]");
        close.GetAttribute("aria-label").Should().Contain("Close");
    }

    [Fact]
    public void Uses_the_shared_overlay_shell()
    {
        using var ctx = new BunitContext();

        var cut = Render(ctx);

        // docs/gui/DESIGN.md 4.1: new windows reuse the System Settings shell. These two classes are
        // what carry the entrance animation, so hand-rolled backdrop markup would silently lose it.
        cut.Find(".overlay-backdrop").Should().NotBeNull();
        cut.Find(".overlay-panel").Should().NotBeNull();
    }

    [Fact]
    public void TestId_suffixes_the_cancel_and_continue_buttons()
    {
        using var ctx = new BunitContext();

        var cut = Render(ctx, testId: "api-key");

        cut.Find("[data-testid='api-key-cancel']").TextContent.Trim().Should().Be("Cancel");
        cut.Find("[data-testid='api-key-continue']").TextContent.Trim().Should().Be("Continue");
    }

    [Fact]
    public void Omitting_TestId_omits_the_data_testid_attributes()
    {
        using var ctx = new BunitContext();

        var cut = Render(ctx);

        cut.FindAll("[data-testid]").Should().BeEmpty();
    }
}
