using AwesomeAssertions;
using Bunit;
using TotallyHot.ArcRouter.Gui.Components;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Component tests for <see cref="DialogShell"/>'s backdrop dismissal. A click-outside closes the
/// dialog, but a text selection that starts inside the panel and is released on the backdrop must
/// not: the browser delivers that <c>mouseup</c> to the backdrop.
/// </summary>
public sealed class DialogShellTests
{
    private static (IRenderedComponent<DialogShell> Cut, Func<int> ClosedCount) Render(BunitContext ctx)
    {
        var closed = 0;
        var cut = ctx.Render<DialogShell>(parameters => parameters
            .Add(parameterSelector: p => p.Title, value: "System Settings")
            .Add(parameterSelector: p => p.CloseAriaLabel, value: "Close system settings")
            .Add(parameterSelector: p => p.OnClose, callback: () => closed++)
            .AddChildContent("<p>Selectable text</p>"));
        return (cut, () => closed);
    }

    [Fact]
    public void Press_and_release_on_the_backdrop_closes()
    {
        using var ctx = new BunitContext();
        var (cut, closed) = Render(ctx);

        var backdrop = cut.Find(".overlay-backdrop");
        backdrop.MouseDown();
        backdrop.MouseUp();

        closed().Should().Be(1);
    }

    [Fact]
    public void Releasing_on_the_backdrop_after_a_press_inside_the_panel_does_not_close()
    {
        using var ctx = new BunitContext();
        var (cut, closed) = Render(ctx);

        cut.Find(".overlay-panel").MouseDown();
        cut.Find(".overlay-backdrop").MouseUp();

        closed().Should().Be(0);
    }

    [Fact]
    public void Releasing_inside_the_panel_after_a_backdrop_press_does_not_close()
    {
        using var ctx = new BunitContext();
        var (cut, closed) = Render(ctx);

        cut.Find(".overlay-backdrop").MouseDown();
        cut.Find(".overlay-panel").MouseUp();

        closed().Should().Be(0);
    }

    [Fact]
    public void Close_glyph_still_closes()
    {
        using var ctx = new BunitContext();
        var (cut, closed) = Render(ctx);

        cut.Find("button").Click();

        closed().Should().Be(1);
    }
}
