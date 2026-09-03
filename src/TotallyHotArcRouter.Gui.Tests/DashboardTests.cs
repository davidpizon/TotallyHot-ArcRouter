using AwesomeAssertions;
using Bunit;
using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for the root <see cref="Dashboard"/> component: tab switching, the budget-alert status banner
/// (now driven off real per-provider budget state from <see cref="ProviderAdminStore"/>, which is
/// unreachable here so the banner reads "System Status: OK"), and the Settings modal toggle.
/// </summary>
public sealed class DashboardTests
{
    private static Bunit.BunitContext NewContext(PersistedSessionStore? persistedSessionStore = null)
    {
        var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(new LiveDataStore(serverAddress: "https://127.0.0.1:59996"));
        ctx.Services.AddSingleton(persistedSessionStore ?? new PersistedSessionStore(serverAddress: "https://127.0.0.1:59996"));
        ctx.Services.AddSingleton(new ProviderAdminStore(managementAddress: "http://127.0.0.1:59994"));
        ctx.Services.AddSingleton(new UsageStore(managementAddress: "http://127.0.0.1:59993"));
        ctx.Services.AddSingleton(new RouterSettingsAdminStore(serverAddress: "https://127.0.0.1:59995"));
        ctx.Services.AddSingleton(new UpdateStore(serverAddress: "https://127.0.0.1:59992"));
        ctx.Services.AddSingleton(new ToastService());
        var settingsPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        ctx.Services.AddSingleton<IGuiSettingsStore>(new GuiSettingsStore(settingsPath));
        ctx.Services.AddSingleton(_ => new TempFileCleanup(settingsPath));
        ctx.Services.GetRequiredService<TempFileCleanup>();
        return ctx;
    }

    [Fact]
    public void Renders_the_live_stream_tab_by_default()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<Dashboard>();

        cut.Markup.Should().Contain("Router Optimization Engine");
        cut.Markup.Should().Contain("No conversations yet.");
    }

    [Fact]
    public void Shows_ok_status_when_no_provider_budgets_are_breached()
    {
        // The budget banner is driven by real provider budget state; with the management API unreachable
        // there are no providers (and so no breaches), so the banner shows the nominal OK state.
        using var ctx = NewContext();

        var cut = ctx.Render<Dashboard>();

        cut.Markup.Should().Contain("System Status: OK");
        cut.Markup.Should().NotContain("BREACHED");
        cut.Markup.Should().NotContain("APPROACHING LIMIT");
    }

    [Fact]
    public void Clicking_a_tab_switches_the_active_workspace()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<Dashboard>();
        cut.FindAll("nav button").First(b => b.TextContent.Contains("Model Distribution")).Click();

        cut.Markup.Should().Contain("Token Volume Histogram");
    }

    [Fact]
    public void Clicking_Console_tab_renders_the_console()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<Dashboard>();
        cut.FindAll("nav button").First(b => b.TextContent.Contains("Console")).Click();

        cut.Markup.Should().Contain("Auto-Scroll");
    }

    [Fact]
    public void Clicking_Governance_tab_renders_the_providers_sub_view()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<Dashboard>();
        cut.FindAll("nav button").First(b => b.TextContent.Contains("Governance")).Click();

        // Governance now defaults to the Providers sub-view (ProvidersAdmin), whose two-way toggle is the
        // stable landmark regardless of whether the (unreachable) management API has answered yet.
        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().Contain(["Providers", "Price Sources"]);
    }

    [Fact]
    public void Settings_button_opens_the_modal_and_close_removes_it()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<Dashboard>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Settings")).Click();
        cut.Markup.Should().Contain("System Settings");

        // The modal's own close (X) button invokes OnClose, which Dashboard wires to hide it again.
        cut.FindAll(".fixed.inset-0 button").First().Click();

        cut.Markup.Should().NotContain("System Settings");
    }

    [Fact]
    public async Task Sessions_tab_shows_a_persisted_session_when_no_live_session_exists()
    {
        var client = new FakePersistedSessionsClient
        {
            Result = new PersistedSessionsResult(
                TranscriptCaptureEnabled: true,
                Transcripts: [CreateTranscript(sessionId: "persisted-only")]),
        };
        var store = new PersistedSessionStore(client);
        await store.LoadAsync(Xunit.TestContext.Current.CancellationToken);
        using var ctx = NewContext(store);

        var cut = ctx.Render<Dashboard>();

        cut.Markup.Should().Contain("Session persist");
    }

    [Fact]
    public async Task Sessions_tab_never_shows_No_conversations_yet_when_only_persisted_sessions_exist()
    {
        // Regression guard for the merge in Dashboard.MergedSessionConversations: a persisted-only
        // session must reach LiveStream even though LiveDataStore.Conversations is empty.
        var client = new FakePersistedSessionsClient
        {
            Result = new PersistedSessionsResult(
                TranscriptCaptureEnabled: true,
                Transcripts: [CreateTranscript(sessionId: "persisted-only")]),
        };
        var store = new PersistedSessionStore(client);
        await store.LoadAsync(Xunit.TestContext.Current.CancellationToken);
        using var ctx = NewContext(store);

        var cut = ctx.Render<Dashboard>();

        cut.Markup.Should().NotContain("No conversations yet.");
    }

    private static PersistedTranscriptDto CreateTranscript(string sessionId, int turnNumber = 1) => new(
        SessionId: sessionId,
        CorrelationId: $"{sessionId}:{turnNumber}",
        CreatedAtUtc: DateTimeOffset.UtcNow,
        RequestedModel: "gpt-5.4",
        RoutedModel: "kimi-k2.5",
        PromptText: "hello",
        ResponseText: "hi",
        CostUsd: 0.01m,
        InputTokens: 10,
        OutputTokens: 5,
        MemoryEntryId: null);

    private sealed class FakePersistedSessionsClient : IPersistedSessionsClient
    {
        public PersistedSessionsResult Result { get; set; } = new(true, []);

        public Task<PersistedSessionsResult> ListAsync(int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result);
    }
}

