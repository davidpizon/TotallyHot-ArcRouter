using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Services;
using Bunit;
using AwesomeAssertions;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="ConsoleTab"/>: toolbar toggles, log-line rendering, and the
/// smart-disengage auto-scroll safeguard. Registers a real <see cref="LiveDataStore"/> (its
/// background gRPC loop never connects in-process - see the class's own remarks), driven purely
/// through <see cref="LiveDataStore.ClearLogLines"/> since it exposes no way to inject synthetic log
/// lines from outside the gRPC stream.
/// </summary>
public sealed class ConsoleTabTests
{
    private static Bunit.BunitContext NewContext()
    {
        var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(new LiveDataStore(serverAddress: "https://127.0.0.1:59992"));
        return ctx;
    }

    [Fact]
    public void Shows_the_waiting_placeholder_when_no_log_lines_have_arrived()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<ConsoleTab>();

        cut.Markup.Should().Contain("Waiting for log output");
    }

    [Fact]
    public void Auto_scroll_starts_on_and_toggles_off_on_click()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<ConsoleTab>();
        cut.Markup.Should().Contain("Auto-Scroll: ON");

        cut.FindAll("button").First(b => b.TextContent.Contains("Auto-Scroll")).Click();

        cut.Markup.Should().Contain("Auto-Scroll: OFF");
    }

    [Fact]
    public void Clicking_auto_scroll_twice_turns_it_back_on()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<ConsoleTab>();
        var toggle = () => cut.FindAll("button").First(b => b.TextContent.Contains("Auto-Scroll"));

        toggle().Click();
        toggle().Click();

        cut.Markup.Should().Contain("Auto-Scroll: ON");
    }

    [Fact]
    public void Clear_buffer_button_invokes_the_store_and_stays_empty()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<ConsoleTab>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Clear Buffer")).Click();

        cut.Markup.Should().Contain("Waiting for log output");
    }

    [Fact]
    public void Manual_scroll_up_callback_disengages_auto_scroll()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<ConsoleTab>();
        cut.Markup.Should().Contain("Auto-Scroll: ON");

        cut.InvokeAsync(() => cut.Instance.OnManualScrollUp());

        cut.Markup.Should().Contain("Auto-Scroll: OFF");
    }

    [Fact]
    public void Disposing_unsubscribes_from_the_store_without_throwing()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<ConsoleTab>();

        var act = () => cut.Dispose();
        act.Should().NotThrow();
    }
}

