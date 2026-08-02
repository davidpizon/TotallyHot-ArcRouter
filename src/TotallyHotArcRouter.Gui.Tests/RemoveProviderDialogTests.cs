using TotallyHot.ArcRouter.Gui.Components;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Component tests for <see cref="RemoveProviderDialog"/> - the type-to-confirm dialog behind the
/// Governance › Providers trashcan. Removal cascades to the provider's models, so these cover both
/// halves of the safeguard: the confirm gate (nothing fires until the key is typed exactly) and the
/// consequence disclosure (how many models go with it).
/// </summary>
public sealed class RemoveProviderDialogTests
{
    private const string Key = "openai";

    private static IRenderedComponent<RemoveProviderDialog> Render(
        BunitContext ctx,
        int modelCount = 0,
        Action? onConfirm = null,
        Action? onCancel = null) =>
        ctx.Render<RemoveProviderDialog>(parameters =>
        {
            parameters.Add(p => p.ProviderKey, Key);
            parameters.Add(p => p.ModelCount, modelCount);
            parameters.Add(p => p.OnConfirm, () => onConfirm?.Invoke());
            parameters.Add(p => p.OnCancel, () => onCancel?.Invoke());
        });

    private static AngleSharp.Dom.IElement FindRemoveButton(IRenderedComponent<RemoveProviderDialog> cut) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Remove");

    private static AngleSharp.Dom.IElement FindCancelButton(IRenderedComponent<RemoveProviderDialog> cut) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel");

    [Fact]
    public void Remove_is_disabled_until_the_key_is_typed()
    {
        using var ctx = new BunitContext();

        var cut = Render(ctx);

        FindRemoveButton(cut).HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Typing_the_exact_key_enables_remove_and_confirms()
    {
        using var ctx = new BunitContext();
        var confirmed = false;

        var cut = Render(ctx, onConfirm: () => confirmed = true);
        cut.Find("input").Input(Key);

        FindRemoveButton(cut).HasAttribute("disabled").Should().BeFalse();

        FindRemoveButton(cut).Click();
        confirmed.Should().BeTrue();
    }

    [Fact]
    public void A_near_miss_does_not_enable_remove()
    {
        using var ctx = new BunitContext();
        var confirmed = false;

        var cut = Render(ctx, onConfirm: () => confirmed = true);

        // Provider keys match case-insensitively everywhere else, but a confirmation the user can
        // satisfy with the wrong case isn't much of a confirmation.
        cut.Find("input").Input("OpenAI");

        FindRemoveButton(cut).HasAttribute("disabled").Should().BeTrue();
        confirmed.Should().BeFalse();
    }

    [Fact]
    public void Partial_text_does_not_enable_remove()
    {
        using var ctx = new BunitContext();

        var cut = Render(ctx);
        cut.Find("input").Input("open");

        FindRemoveButton(cut).HasAttribute("disabled").Should().BeTrue();
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
    public void States_how_many_models_go_with_the_provider()
    {
        using var ctx = new BunitContext();

        var cut = Render(ctx, modelCount: 3);

        cut.Markup.Should().Contain("also remove");
        cut.Markup.Should().Contain("3");
        cut.Markup.Should().Contain("models");
    }

    [Fact]
    public void Uses_the_singular_for_a_single_model()
    {
        using var ctx = new BunitContext();

        var cut = Render(ctx, modelCount: 1);

        cut.Markup.Should().Contain("configured model.");
        cut.Markup.Should().NotContain("configured models");
    }

    [Fact]
    public void Omits_the_model_warning_when_the_provider_has_none()
    {
        using var ctx = new BunitContext();

        var cut = Render(ctx, modelCount: 0);

        cut.Markup.Should().NotContain("also remove");
        // The irreversibility warning and the metrics reassurance still stand on their own.
        cut.Markup.Should().Contain("irreversible");
        cut.Markup.Should().Contain("Historical metrics are kept");
    }

    [Fact]
    public void Reassures_that_historical_metrics_survive()
    {
        using var ctx = new BunitContext();

        var cut = Render(ctx, modelCount: 2);

        cut.Markup.Should().Contain("Historical metrics are kept");
    }

    [Fact]
    public void Escape_cancels_from_the_confirmation_field()
    {
        using var ctx = new BunitContext();
        var cancelled = false;

        var cut = Render(ctx, onCancel: () => cancelled = true);
        cut.Find("input").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        cancelled.Should().BeTrue();
    }

    [Fact]
    public void Escape_cancels_even_when_a_button_has_focus()
    {
        using var ctx = new BunitContext();
        var cancelled = false;

        var cut = Render(ctx, onCancel: () => cancelled = true);

        // The regression this guards: Escape used to be bound on the input alone, so with focus on
        // Cancel/Remove/the close glyph it did nothing - contradicting OnCancel's documented contract.
        // Handling it on the panel picks the key up as it bubbles from any focused descendant.
        FindRemoveButton(cut).KeyDown(new KeyboardEventArgs { Key = "Escape" });

        cancelled.Should().BeTrue();
    }

    [Fact]
    public void Escape_never_confirms()
    {
        using var ctx = new BunitContext();
        var confirmed = false;

        var cut = Render(ctx, onConfirm: () => confirmed = true);
        cut.Find("input").Input(Key);
        cut.Find("input").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        confirmed.Should().BeFalse();
    }

    [Fact]
    public void Enter_confirms_exactly_once()
    {
        using var ctx = new BunitContext();
        var confirmCount = 0;

        var cut = Render(ctx, onConfirm: () => confirmCount++);
        cut.Find("input").Input(Key);

        // Enter is bound on the input only. If the panel handled it too, this single keypress would
        // fire twice as it bubbled through both handlers.
        cut.Find("input").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        confirmCount.Should().Be(1);
    }

    [Fact]
    public void Enter_does_not_confirm_before_the_key_is_typed()
    {
        using var ctx = new BunitContext();
        var confirmed = false;

        var cut = Render(ctx, onConfirm: () => confirmed = true);
        cut.Find("input").KeyDown(new KeyboardEventArgs { Key = "Enter" });

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
}

