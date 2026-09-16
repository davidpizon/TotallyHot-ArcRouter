using AwesomeAssertions;
using Bunit;
using TotallyHot.ArcRouter.Gui.Components;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Component tests for <see cref="SecretField"/>. The behavior worth pinning down is the asymmetry
/// between the two directions of the padlock: locking is one click and non-destructive, while unlocking
/// opens a confirmation dialog and only clears the value once the user confirms. That is not a UI
/// flourish - a locked value is never returned to this process, so an unlocked field still showing one
/// would be a field the operator cannot correct. See <c>docs/gui/secret-field.md</c>.
/// </summary>
public sealed class SecretFieldTests
{
    private const string TestId = "secret";

    [Fact]
    public void Unlocked_field_is_an_ordinary_readable_text_box()
    {
        using var ctx = new BunitContext();

        var cut = Render(ctx: ctx, value: "hello", false);

        var input = cut.Find($"[data-testid='{TestId}']");
        input.GetAttribute("type").Should().Be("text");
        input.GetAttribute("value").Should().Be("hello");
        cut.Find($"[data-testid='{TestId}-lock']").GetAttribute("data-tip").Should().Be(SecretField.UnlockedTip);
    }

    [Fact]
    public void Locked_field_masks_its_value()
    {
        using var ctx = new BunitContext();

        var cut = Render(ctx: ctx, value: "s3cret", true);

        cut.Find($"[data-testid='{TestId}']").GetAttribute("type").Should().Be("password");
        cut.Find($"[data-testid='{TestId}-lock']").GetAttribute("data-tip").Should().Be(SecretField.LockedTip);
    }

    [Fact]
    public void Typing_raises_ValueChanged()
    {
        using var ctx = new BunitContext();

        string? value = null;
        var cut = Render(ctx: ctx, value: string.Empty, false, onValueChanged: v => value = v);

        cut.Find($"[data-testid='{TestId}']").Input("typed");

        value.Should().Be("typed");
    }

    [Fact]
    public void Locking_takes_one_click_and_keeps_the_value()
    {
        using var ctx = new BunitContext();

        bool? locked = null;
        var value = "hello";
        var cut = Render(ctx: ctx, value: "hello", false, onValueChanged: v => value = v,
            onLockedChanged: l => locked = l);

        cut.Find($"[data-testid='{TestId}-lock']").Click();

        locked.Should().BeTrue();
        value.Should().Be("hello");
    }

    [Fact]
    public void Clicking_a_locked_padlock_opens_a_confirmation_dialog()
    {
        using var ctx = new BunitContext();

        bool? locked = null;
        string? value = null;
        var cut = Render(ctx: ctx, value: "s3cret", true, onValueChanged: v => value = v,
            onLockedChanged: l => locked = l);

        cut.Find($"[data-testid='{TestId}-lock']").Click();

        // Nothing changes yet - only the dialog appears.
        locked.Should().BeNull();
        value.Should().BeNull();
        cut.Find($"[data-testid='{TestId}']").GetAttribute("type").Should().Be("password");
        cut.Find($"[data-testid='{TestId}-continue']").Should().NotBeNull();
    }

    [Fact]
    public void Cancelling_the_dialog_leaves_the_field_locked_and_unchanged()
    {
        using var ctx = new BunitContext();

        bool? locked = null;
        string? value = null;
        var cut = Render(ctx: ctx, value: "s3cret", true, onValueChanged: v => value = v,
            onLockedChanged: l => locked = l);

        cut.Find($"[data-testid='{TestId}-lock']").Click();
        cut.Find($"[data-testid='{TestId}-cancel']").Click();

        locked.Should().BeNull();
        value.Should().BeNull();
        cut.FindAll($"[data-testid='{TestId}-continue']").Should().BeEmpty();
        cut.Find($"[data-testid='{TestId}']").GetAttribute("type").Should().Be("password");
    }

    [Fact]
    public void Confirming_the_dialog_clears_the_value_and_unlocks()
    {
        using var ctx = new BunitContext();

        bool? locked = null;
        string? value = null;
        var cut = Render(ctx: ctx, value: "s3cret", true, onValueChanged: v => value = v,
            onLockedChanged: l => locked = l);

        cut.Find($"[data-testid='{TestId}-lock']").Click();
        cut.Find($"[data-testid='{TestId}-continue']").Click();

        locked.Should().BeFalse();
        value.Should().BeEmpty();
        cut.FindAll($"[data-testid='{TestId}-continue']").Should().BeEmpty();
    }

    [Fact]
    public void Disabled_field_disables_both_the_input_and_the_padlock()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<SecretField>(p => p
            .Add(parameterSelector: x => x.Value, value: "hello")
            .Add(parameterSelector: x => x.TestId, value: TestId)
            .Add(parameterSelector: x => x.Disabled, true));

        cut.Find($"[data-testid='{TestId}']").HasAttribute("disabled").Should().BeTrue();
        cut.Find($"[data-testid='{TestId}-lock']").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Without_a_TestId_neither_element_carries_one()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<SecretField>(p => p.Add(parameterSelector: x => x.Value, value: "hello"));

        cut.FindAll("[data-testid]").Should().BeEmpty();
    }

    private static IRenderedComponent<SecretField> Render(
        BunitContext ctx,
        string value,
        bool locked,
        Action<string>? onValueChanged = null,
        Action<bool>? onLockedChanged = null)
    {
        return ctx.Render<SecretField>(p =>
        {
            p.Add(parameterSelector: x => x.Value, value: value);
            p.Add(parameterSelector: x => x.Locked, value: locked);
            p.Add(parameterSelector: x => x.TestId, value: TestId);
            if (onValueChanged is not null) p.Add(parameterSelector: x => x.ValueChanged, callback: onValueChanged);

            if (onLockedChanged is not null) p.Add(parameterSelector: x => x.LockedChanged, callback: onLockedChanged);
        });
    }
}