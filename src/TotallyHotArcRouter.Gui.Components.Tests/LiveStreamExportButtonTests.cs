using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Models;
using TotallyHot.ArcRouter.Gui.Services;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for the Sessions tab's Export button (#165 phase 3): it appears only when a host supplies
/// <see cref="LiveStream.OnExportRequested"/>, and pressing it runs that callback.
/// </summary>
public sealed class LiveStreamExportButtonTests
{
    private static Conversation MakeConversation() => new(
        Id: "s1",
        Title: "First",
        FirstTimestamp: "10:00:00",
        LastTimestamp: "10:05:00",
        0.01m,
        100,
        50,
        false,
        Turns:
        [
            new ConversationTurn(Id: "s1-t1", Agent: "Agent A", Model: "model-a", 1,
                100, 50, 0, 0.01m,
                0, 0, 100, 0,
                Timestamp: "10:00:00", RoutingSteps: [], RequestSummary: null,
                ResponseSummary: null, TimestampUtc: DateTimeOffset.MinValue)
        ]);

    [Fact]
    public void Has_no_Export_button_when_the_host_supplies_no_callback()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<LiveStream>(p => p
            .Add(parameterSelector: c => c.Conversations, value: [MakeConversation()])
            .Add(parameterSelector: c => c.SelectedId, value: string.Empty));

        cut.FindAll("[data-testid='sessions-export']").Should().BeEmpty();
    }

    [Fact]
    public async Task Pressing_Export_runs_the_callback()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var requested = 0;

        var cut = ctx.Render<LiveStream>(p => p
            .Add(c => c.Conversations, [MakeConversation()])
            .Add(c => c.SelectedId, string.Empty)
            .Add(c => c.OnExportRequested, () => requested++));
        await cut.InvokeAsync(() => cut.Find("[data-testid='sessions-export']").Click());

        cut.Find("[data-testid='sessions-export']").TextContent.Trim().Should().Be("Export");
        requested.Should().Be(1);
    }

    [Fact]
    public async Task Empty_list_still_shows_Export_when_a_callback_is_supplied()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        // Empty state renders ClientDropInPanel, which injects the clipboard service.
        ctx.Services.AddSingleton<IClipboardService>(new FakeClipboardService());
        var requested = 0;

        var cut = ctx.Render<LiveStream>(p => p
            .Add(c => c.Conversations, Array.Empty<Conversation>())
            .Add(c => c.SelectedId, string.Empty)
            .Add(c => c.OnExportRequested, () => requested++));
        await cut.InvokeAsync(() => cut.Find("[data-testid='sessions-export']").Click());

        requested.Should().Be(1);
    }
}
