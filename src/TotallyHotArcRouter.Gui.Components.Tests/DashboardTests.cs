using AwesomeAssertions;
using Bunit;
using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;
using TestContext = Xunit.TestContext;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for the root <see cref="Dashboard"/> component: tab switching, the budget-alert status banner
/// (now driven off real per-provider budget state from <see cref="ProviderAdminStore"/>, which is
/// unreachable here so the indicator reads "Budget Status: OK"), and the Settings modal toggle.
/// </summary>
public sealed class DashboardTests
{
    private static BunitContext NewContext(
        PersistedSessionStore? persistedSessionStore = null,
        FakePasskeyAdminClient? passkeyClient = null,
        FakeWebAuthnCeremony? ceremony = null,
        ContentGrantStore? contentGrant = null,
        LiveDataStore? liveDataStore = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var unreachable = new StubRouterChannelProvider("https://127.0.0.1:59996");
        contentGrant ??= new ContentGrantStore();
        ctx.Services.AddSingleton(contentGrant);
        ctx.Services.AddSingleton(new PasskeyAdminStore(
            client: passkeyClient ?? new FakePasskeyAdminClient(),
            ceremony: ceremony ?? new FakeWebAuthnCeremony(),
            contentGrant: contentGrant));
        ctx.Services.AddSingleton(liveDataStore ?? new LiveDataStore(channelProvider: unreachable));
        ctx.Services.AddSingleton(persistedSessionStore ??
                                  new PersistedSessionStore(channelProvider: unreachable));
        ctx.Services.AddSingleton(new ProviderAdminStore(channelProvider: unreachable));
        ctx.Services.AddSingleton(new UsageStore(channelProvider: unreachable));
        ctx.Services.AddSingleton(new RouterSettingsAdminStore(channelProvider: unreachable));
        ctx.Services.AddSingleton(new UpdateStore(channelProvider: unreachable));
        ctx.Services.AddSingleton(new CostReconciliationStore(channelProvider: unreachable));
        ctx.Services.AddSingleton(new ManagementTokenAdminStore(channelProvider: unreachable));
        ctx.Services.AddSingleton(new ToastService());
        ctx.Services.AddSingleton<IClipboardService>(new FakeClipboardService());
        return ctx;
    }

    [Fact]
    public void Renders_the_live_stream_tab_by_default()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<Dashboard>();

        cut.Markup.Should().Contain("img/logo.svg");
        cut.Markup.Should().Contain("TotallyHot Arc Router");
        cut.Markup.Should().NotContain("Router Optimization Engine");
        cut.Markup.Should().Contain("No conversations yet.");
        cut.Markup.Should().Contain(OpenAiCompatibleDropIn.BaseUrl);
    }

    [Fact]
    public void Shows_ok_status_when_no_provider_budgets_are_breached()
    {
        // The budget status indicator is driven by real provider budget state; with the management API unreachable
        // there are no providers (and so no breaches), so it shows the nominal OK state.
        using var ctx = NewContext();

        var cut = ctx.Render<Dashboard>();

        cut.Markup.Should().Contain("Budget Status:");
        cut.Find("header").TextContent.Should().MatchRegex(@"Budget Status:\s*OK");
        cut.Markup.Should().NotContain("BREACHED");
        cut.Markup.Should().NotContain("APPROACHING LIMIT");
    }

    [Fact]
    public async Task Clicking_a_tab_switches_the_active_workspace()
    {
        await using var ctx = NewContext();

        var cut = ctx.Render<Dashboard>();
        // InvokeAsync makes Find-then-Click atomic on the renderer's synchronization context: Dashboard
        // constructs several stores against a deliberately-unreachable StubRouterChannelProvider
        // (see NewContext), and any of their background connection-failure continuations can
        // re-render between a plain Find() and Click(), leaving Click() dispatching against an event
        // handler ID the re-render already invalidated (Bunit.Rendering.UnknownEventHandlerIdException).
        await cut.InvokeAsync(() =>
            cut.FindAll("nav button").First(b => b.TextContent.Contains("Model Distribution")).Click());

        cut.Markup.Should().Contain("Token Volume Histogram");

        await cut.InvokeAsync(() =>
            cut.FindAll("nav button").First(b => b.TextContent.Contains("Report Card")).Click());

        await cut.WaitForAssertionAsync(assertion: () => cut.Markup.Should().Contain("Spend by Model"),
            timeout: TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Clicking_Console_tab_renders_the_console()
    {
        await using var ctx = NewContext();

        var cut = ctx.Render<Dashboard>();
        // See Clicking_a_tab_switches_the_active_workspace's remarks on why this is InvokeAsync-wrapped.
        await cut.InvokeAsync(() => cut.FindAll("nav button").First(b => b.TextContent.Contains("Console")).Click());

        cut.Markup.Should().Contain("Auto-Scroll");
    }

    [Fact]
    public async Task Clicking_Governance_tab_renders_the_providers_sub_view()
    {
        await using var ctx = NewContext();

        var cut = ctx.Render<Dashboard>();
        // See Clicking_a_tab_switches_the_active_workspace's remarks on why this is InvokeAsync-wrapped.
        await cut.InvokeAsync(() => cut.FindAll("nav button").First(b => b.TextContent.Contains("Governance")).Click());

        // Governance now defaults to the Providers sub-view (ProvidersAdmin), whose two-way toggle is the
        // stable landmark regardless of whether the (unreachable) management API has answered yet.
        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().Contain(["Providers", "Price Sources"]);
    }

    [Fact]
    public async Task Settings_button_opens_the_modal_and_close_removes_it()
    {
        await using var ctx = NewContext();

        var cut = ctx.Render<Dashboard>();
        // See Clicking_a_tab_switches_the_active_workspace's remarks on why this is InvokeAsync-wrapped.
        await cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Contains("Settings")).Click());
        cut.Markup.Should().Contain("System Settings");

        // The modal's own close (X) button invokes OnClose, which Dashboard wires to hide it again.
        await cut.InvokeAsync(() => cut.FindAll(".fixed.inset-0 button").First().Click());

        cut.Markup.Should().NotContain("System Settings");
    }

    [Fact]
    public async Task Sessions_tab_shows_a_persisted_session_when_no_live_session_exists()
    {
        var client = new FakePersistedSessionsClient
        {
            Result = new PersistedSessionsResult(
                true,
                Transcripts: [CreateTranscript(sessionId: "persisted-only")])
        };
        var store = new PersistedSessionStore(client);
        await store.LoadAsync(TestContext.Current.CancellationToken);
        await using var ctx = NewContext(store);

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
                true,
                Transcripts: [CreateTranscript(sessionId: "persisted-only")])
        };
        var store = new PersistedSessionStore(client);
        await store.LoadAsync(TestContext.Current.CancellationToken);
        await using var ctx = NewContext(store);

        var cut = ctx.Render<Dashboard>();

        cut.Markup.Should().NotContain("No conversations yet.");
    }

    private static async Task<PersistedSessionStore> LoadedStoreAsync(ContentGrantStore? grant = null)
    {
        var client = new FakePersistedSessionsClient
        {
            Result = new PersistedSessionsResult(true, Transcripts: [CreateTranscript(sessionId: "persisted-only")])
        };
        var store = grant is null ? new PersistedSessionStore(client) : new PersistedSessionStore(client, grant);
        await store.LoadAsync(TestContext.Current.CancellationToken);
        return store;
    }

    [Fact]
    public async Task Sessions_tab_is_locked_by_default_and_offers_the_passkey_unlock()
    {
        var store = await LoadedStoreAsync();
        await using var ctx = NewContext(store);

        var cut = ctx.Render<Dashboard>();

        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid='content-locked']").TextContent
            .Should().Contain("Conversation text is locked."));
        cut.Find("[data-testid='content-unlock']").TextContent.Should().Contain("Unlock with passkey");
        cut.FindAll("[data-testid='content-enrollment-hint']").Should().BeEmpty();
        cut.FindAll("[data-testid='content-lock']").Should().BeEmpty();
    }

    [Fact]
    public async Task Sessions_tab_names_the_enrollment_command_when_no_passkey_is_enrolled()
    {
        var passkeys = new FakePasskeyAdminClient { Passkeys = [] };
        passkeys.Status = passkeys.Status with { Enrolled = false };
        var store = await LoadedStoreAsync();
        await using var ctx = NewContext(store, passkeyClient: passkeys);

        var cut = ctx.Render<Dashboard>();

        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid='content-enrollment-hint']").TextContent
            .Should().Contain("--mint-passkey-enrollment-code"));
        cut.FindAll("[data-testid='content-unlock']").Should().BeEmpty("an unlock cannot succeed before enrollment");
        cut.Find("[data-testid='content-add-passkey']").Should().NotBeNull();
    }

    [Fact]
    public async Task Unlocking_with_a_passkey_holds_the_grant_closes_the_dialog_and_offers_Lock()
    {
        var passkeys = new FakePasskeyAdminClient();
        var ceremony = new FakeWebAuthnCeremony();
        var grant = new ContentGrantStore();
        var store = await LoadedStoreAsync();
        await using var ctx = NewContext(store, passkeyClient: passkeys, ceremony: ceremony, contentGrant: grant);
        var cut = ctx.Render<Dashboard>();
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid='content-unlock']"));

        await cut.InvokeAsync(() => cut.Find("[data-testid='content-unlock']").Click());
        cut.Markup.Should().Contain("Unlock Conversations");
        await cut.InvokeAsync(() => cut.Find("[data-testid='unlock-confirm']").Click());

        grant.IsActive.Should().BeTrue();
        ceremony.GetOptions.Should().ContainSingle();
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid='content-unlocked']"));
        cut.Markup.Should().NotContain("Unlock Conversations", "the dialog closes once the grant is held");
    }

    [Fact]
    public async Task A_dismissed_unlock_prompt_keeps_the_dialog_open_with_the_reason_and_stays_locked()
    {
        var ceremony = new FakeWebAuthnCeremony
        {
            Failure = new WebAuthnCeremonyException("The passkey prompt was dismissed or timed out.")
        };
        var grant = new ContentGrantStore();
        var store = await LoadedStoreAsync();
        await using var ctx = NewContext(store, ceremony: ceremony, contentGrant: grant);
        var cut = ctx.Render<Dashboard>();
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid='content-unlock']"));
        await cut.InvokeAsync(() => cut.Find("[data-testid='content-unlock']").Click());

        await cut.InvokeAsync(() => cut.Find("[data-testid='unlock-confirm']").Click());

        cut.Find("[data-testid='unlock-error']").TextContent.Should().Contain("dismissed or timed out");
        grant.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Lock_clears_the_grant_and_every_cached_conversation_text()
    {
        var grant = new ContentGrantStore();
        var passkeys = new FakePasskeyAdminClient();
        passkeys.Status = passkeys.Status with { GrantActive = true };
        var store = await LoadedStoreAsync(grant);
        store.Sessions.Single().Turns.Single().RequestSummary.Should().Be("hello");
        grant.SetGrant("tok", DateTimeOffset.UtcNow.AddMinutes(10));
        await using var ctx = NewContext(store, passkeyClient: passkeys, contentGrant: grant);
        var cut = ctx.Render<Dashboard>();
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid='content-unlocked']"));

        await cut.InvokeAsync(() => cut.Find("[data-testid='content-lock']").Click());

        grant.IsActive.Should().BeFalse();
        passkeys.LockCalls.Should().Be(1);
        store.Sessions.Single().Turns.Single().RequestSummary.Should().BeNull();
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid='content-locked']"));
    }

    [Fact]
    public async Task The_grant_expiring_locks_the_view_and_clears_the_text_without_any_click()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var grant = new ContentGrantStore(clock);
        var passkeys = new FakePasskeyAdminClient();
        passkeys.Status = passkeys.Status with { GrantActive = true };
        var store = await LoadedStoreAsync(grant);
        grant.SetGrant("tok", clock.GetUtcNow().AddMinutes(15));
        await using var ctx = NewContext(store, passkeyClient: passkeys, contentGrant: grant);
        var cut = ctx.Render<Dashboard>();
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid='content-unlocked']"));

        clock.Advance(TimeSpan.FromMinutes(15));

        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid='content-locked']"));
        store.Sessions.Single().Turns.Single().RequestSummary.Should().BeNull();
    }

    [Fact]
    public async Task Opening_a_locked_session_says_the_text_is_locked_rather_than_not_captured()
    {
        var client = new FakePersistedSessionsClient
        {
            Result = new PersistedSessionsResult(true, Transcripts:
                [CreateTranscript(sessionId: "persisted-only") with { PromptText = null, ResponseText = null }])
        };
        var store = new PersistedSessionStore(client);
        await store.LoadAsync(TestContext.Current.CancellationToken);
        await using var ctx = NewContext(store);
        var cut = ctx.Render<Dashboard>();
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid='content-unlock']"));

        await cut.InvokeAsync(() => cut.FindAll("button")
            .First(b => b.TextContent.Contains("Session persist", StringComparison.Ordinal)).DoubleClick());

        cut.Markup.Should().Contain("Locked - unlock with a passkey to view");
        cut.Markup.Should().NotContain("No request captured");
    }

    [Fact]
    public async Task Opening_a_session_loads_its_text_and_locking_clears_it()
    {
        var grant = new ContentGrantStore();
        grant.SetGrant("tok", DateTimeOffset.UtcNow.AddMinutes(10));
        var client = new FakePersistedSessionsClient
        {
            Result = new PersistedSessionsResult(true, Transcripts:
            [
                CreateTranscript("persisted-only") with
                {
                    PromptText = null,
                    ResponseText = null,
                    TranscriptId = 9
                }
            ]),
            Texts = [new PersistedTurnText(9, true, "opened words", "opened reply", false, false)]
        };
        var store = new PersistedSessionStore(client, grant);
        await store.LoadAsync(TestContext.Current.CancellationToken);
        store.Sessions.Single().Turns.Single().RequestSummary.Should().BeNull();
        var passkeys = new FakePasskeyAdminClient();
        passkeys.Status = passkeys.Status with { GrantActive = true };
        await using var ctx = NewContext(store, passkeyClient: passkeys, contentGrant: grant);
        var cut = ctx.Render<Dashboard>();
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid='content-unlocked']"));

        await cut.InvokeAsync(() => cut.FindAll("button")
            .First(button => button.TextContent.Contains("Session persist", StringComparison.Ordinal))
            .DoubleClick());

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("opened words"));

        await cut.InvokeAsync(() => cut.Find("[data-testid='content-lock']").Click());

        store.Sessions.Single().Turns.Single().RequestSummary.Should().BeNull();
        await cut.WaitForAssertionAsync(() => cut.Markup.Should().NotContain("opened words"));
    }

    private static PersistedTranscriptDto CreateTranscript(string sessionId, int turnNumber = 1)
    {
        return new PersistedTranscriptDto(
            SessionId: sessionId,
            CorrelationId: $"{sessionId}:{turnNumber}",
            CreatedAtUtc: DateTimeOffset.UtcNow,
            RequestedModel: "gpt-5.4",
            RoutedModel: "kimi-k2.5",
            PromptText: "hello",
            ResponseText: "hi",
            0.01m,
            10,
            5,
            null);
    }

    private sealed class FakePersistedSessionsClient : IPersistedSessionsClient
    {
        public PersistedSessionsResult Result { get; init; } = new(true, Transcripts: []);

        public IReadOnlyList<PersistedTurnText> Texts { get; init; } = [];

        public Task<PersistedSessionsResult> ListAsync(int limit, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result);
        }

        public Task<IReadOnlyList<PersistedTurnText>> GetTurnTextsAsync(
            IReadOnlyList<long> transcriptIds, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Texts);
        }
    }
}