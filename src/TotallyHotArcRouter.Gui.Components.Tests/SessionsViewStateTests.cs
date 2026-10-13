using AwesomeAssertions;
using Bunit;
using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Models;
using TotallyHot.ArcRouter.Gui.Services;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Confirms <see cref="SessionsViewState"/> survives a fresh <see cref="LiveStream"/> mount the way
/// Dashboard's <c>@key="_activeTab"</c> teardown does (#176 §6.5 / §12).
/// </summary>
public sealed class SessionsViewStateTests
{
    [Fact]
    public void State_survives_a_re_render_of_a_fresh_LiveStream()
    {
        var state = new SessionsViewState
        {
            DetailsTab = SessionsDetailsTab.Routing,
            RailSearch = "alpha",
            InSessionSearchOpen = true,
            InSessionFilter = "hello",
            MessageSelection = new MessageSelection("s1", 1, MessageSide.Request)
        };

        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(state);
        ctx.Services.AddSingleton(TimeProvider.System);
        ctx.Services.AddSingleton<IClipboardService>(new FakeClipboardService());

        var conversation = new Conversation(
            "s1", "Alpha", "10:00:00", "10:00:00", 0.01m, 10, 20, false,
            [
                new ConversationTurn("s1-t1", "a", "m", 1, 10, 20, 0, 0.01m, 0, 0, 0, 0,
                    "10:00:00", [], RequestSummary: "hello", ResponseSummary: "world",
                    TimestampUtc: DateTimeOffset.UtcNow, IsLive: true)
            ],
            IsLive: true);

        var cut = ctx.Render<LiveStream>(p => p
            .Add(c => c.Conversations, [conversation])
            .Add(c => c.SelectedId, "s1"));

        cut.Find("input[aria-label='Search sessions']").GetAttribute("value").Should().Be("alpha");
        cut.Find(".ls-details-tab.active").TextContent.Should().Contain("Routing");

        // Tear down and remount with the same singleton — simulates @key tab switch.
        cut.Dispose();
        var cut2 = ctx.Render<LiveStream>(p => p
            .Add(c => c.Conversations, [conversation])
            .Add(c => c.SelectedId, "s1"));

        cut2.Find("input[aria-label='Search sessions']").GetAttribute("value").Should().Be("alpha");
        state.DetailsTab.Should().Be(SessionsDetailsTab.Routing);
        state.InSessionFilter.Should().Be("hello");
        state.MessageSelection.Should().NotBeNull();
        state.MessageSelection!.TurnNumber.Should().Be(1);
        state.MessageSelection.Side.Should().Be(MessageSide.Request);
        cut2.FindAll(".ls-msg-select[aria-pressed='true']").Should().ContainSingle()
            .Which.GetAttribute("aria-label").Should().Contain("turn 1");
    }
}
