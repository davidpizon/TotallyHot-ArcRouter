using AwesomeAssertions;
using Bunit;
using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="SettingsModal"/>: the Adaptive Routing toggle and Sample Size input
/// (docs/router/self-organizing-classification-plan.md Phase T6), the typed-confirmation gate on the
/// destructive Reset/Purge actions and what they actually clear, the close callbacks, and the
/// GUI/Router version footer.
/// </summary>
public sealed class SettingsModalTests
{
    private static BunitContext NewContext(
        out LiveDataStore liveDataStore,
        out RouterSettingsAdminStore routerSettingsStore,
        FakeRouterSettingsAdminClient? routerSettingsClient = null,
        FakeUpdateAdminClient? updateClient = null,
        FakeManagementTokenAdminClient? managementTokenClient = null,
        bool updateSupportsApply = true)
    {
        var ctx = new BunitContext();
        liveDataStore = new LiveDataStore(channelProvider: new NativeRouterChannelProvider("https://127.0.0.1:59996"));
        routerSettingsStore = new RouterSettingsAdminStore(routerSettingsClient ?? new FakeRouterSettingsAdminClient());
        ctx.Services.AddSingleton(liveDataStore);
        ctx.Services.AddSingleton(routerSettingsStore);
        ctx.Services.AddSingleton(new UpdateStore(client: updateClient ?? new FakeUpdateAdminClient(),
            applier: new FakeMsiUpdateApplier(), supportsApply: updateSupportsApply));
        ctx.Services.AddSingleton(new CostReconciliationStore(new FakeCostReconciliationAdminClient()));
        ctx.Services.AddSingleton(
            new ManagementTokenAdminStore(managementTokenClient ?? new FakeManagementTokenAdminClient()));
        ctx.Services.AddSingleton<IClipboardService>(new FakeClipboardService());
        return ctx;
    }

    [Fact]
    public void Version_footer_shows_the_GUI_version_from_its_own_assembly()
    {
        using var ctx = NewContext(liveDataStore: out _, routerSettingsStore: out _);

        var cut = ctx.Render<SettingsModal>();

        cut.Markup.Should().Contain($"GUI v{AppVersion.Current}");
    }

    [Fact]
    public void Version_footer_shows_the_version_the_router_reported()
    {
        var updateClient = new FakeUpdateAdminClient();
        updateClient.Status = updateClient.Status with { CurrentVersion = "1.0.2" };
        using var ctx = NewContext(liveDataStore: out _, routerSettingsStore: out _,
            updateClient: updateClient);

        var cut = ctx.Render<SettingsModal>();

        cut.Markup.Should().Contain("Router v1.0.2");
        cut.Markup.Should().NotContain("Router unknown");
    }

    [Fact]
    public void SoftwareUpdate_UpdateAvailable_SupportsApply_ShowsTheApplyButton()
    {
        var updateClient = new FakeUpdateAdminClient();
        updateClient.Status = updateClient.Status with { UpdateAvailable = true, LatestVersion = "2.0.0" };
        using var ctx = NewContext(liveDataStore: out _, routerSettingsStore: out _,
            updateClient: updateClient, updateSupportsApply: true);

        var cut = ctx.Render<SettingsModal>();

        cut.Markup.Should().Contain("Apply Update");
        cut.Markup.Should().NotContain(UpdateStore.ReleasesUrl);
    }

    [Fact]
    public void SoftwareUpdate_UpdateAvailable_DoesNotSupportApply_ShowsAReleaseLinkInstead()
    {
        // Regression coverage for a real bug: this host (the WASM dashboard) cannot launch the
        // downloaded MSI - Environment.Exit and MsiUpdateApplier's file/process APIs are unsupported in
        // the browser sandbox - but the shared SettingsModal used to show "Apply Update" unconditionally
        // regardless of host.
        var updateClient = new FakeUpdateAdminClient();
        updateClient.Status = updateClient.Status with { UpdateAvailable = true, LatestVersion = "2.0.0" };
        using var ctx = NewContext(liveDataStore: out _, routerSettingsStore: out _,
            updateClient: updateClient, updateSupportsApply: false);

        var cut = ctx.Render<SettingsModal>();

        cut.Markup.Should().NotContain("Apply Update");
        cut.Markup.Should().Contain(UpdateStore.ReleasesUrl);
    }

    [Fact]
    public void CopyMcpToken_CopiesTheLoadedTokenToTheClipboard()
    {
        var tokenClient = new FakeManagementTokenAdminClient { Token = "the-real-token" };
        using var ctx = NewContext(liveDataStore: out _, routerSettingsStore: out _,
            managementTokenClient: tokenClient);
        var clipboard = (FakeClipboardService)ctx.Services.GetRequiredService<IClipboardService>();

        var cut = ctx.Render<SettingsModal>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Copy MCP token")).Click();

        clipboard.LastCopiedText.Should().Be("the-real-token");
    }

    [Fact]
    public void RegenerateMcpToken_OpensAConfirmDialog_AndDoesNotRegenerateUntilConfirmed()
    {
        var tokenClient = new FakeManagementTokenAdminClient { Token = "original-token" };
        using var ctx = NewContext(liveDataStore: out _, routerSettingsStore: out _,
            managementTokenClient: tokenClient);

        var cut = ctx.Render<SettingsModal>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Regenerate").Click();

        cut.Markup.Should().Contain("Regenerate Management Token");
        tokenClient.Token.Should().Be("original-token");
    }

    [Fact]
    public void RegenerateMcpToken_Confirmed_RotatesTheTokenAndClosesTheDialog()
    {
        // The token itself is never rendered into the markup - Copy is how an operator actually gets it -
        // so this asserts on the confirmation message, the dialog closing, and the underlying client
        // having been driven to mint a new value, not on the (deliberately absent) token text on screen.
        var tokenClient = new FakeManagementTokenAdminClient { Token = "original-token" };
        using var ctx = NewContext(liveDataStore: out _, routerSettingsStore: out _,
            managementTokenClient: tokenClient);
        var cut = ctx.Render<SettingsModal>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Regenerate").Click();

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Regenerate" && b.ClassList.Contains("btn-critical"))
            .Click();

        tokenClient.Token.Should().Be("fake-regenerated-token");
        cut.Markup.Should().Contain("Regenerated.");
        cut.Markup.Should().NotContain("Regenerate Management Token");
    }

    [Fact]
    public void Version_footer_shows_Router_unknown_when_the_router_is_not_running()
    {
        var updateClient = new FakeUpdateAdminClient
        {
            Failure = new GrpcAdminException(message: "router is down", isUnavailable: true)
        };
        using var ctx = NewContext(liveDataStore: out _, routerSettingsStore: out _,
            updateClient: updateClient);

        var cut = ctx.Render<SettingsModal>();

        cut.Markup.Should().Contain("Router unknown");
    }

    [Fact]
    public void Version_footer_shows_Router_unknown_when_the_router_reports_a_blank_version()
    {
        var updateClient = new FakeUpdateAdminClient();
        updateClient.Status = updateClient.Status with { CurrentVersion = "" };
        using var ctx = NewContext(liveDataStore: out _, routerSettingsStore: out _,
            updateClient: updateClient);

        var cut = ctx.Render<SettingsModal>();

        cut.Markup.Should().Contain("Router unknown");
    }

    // The whole point of reading the Router half from the Router rather than assuming it matches the
    // GUI's: an upgrade that left a stale Router behind has to be visible here.
    [Fact]
    public void Version_footer_shows_both_halves_independently_when_they_disagree()
    {
        var updateClient = new FakeUpdateAdminClient();
        updateClient.Status = updateClient.Status with { CurrentVersion = "0.9.9" };
        using var ctx = NewContext(liveDataStore: out _, routerSettingsStore: out _,
            updateClient: updateClient);

        var cut = ctx.Render<SettingsModal>();

        cut.Markup.Should().Contain($"GUI v{AppVersion.Current}");
        cut.Markup.Should().Contain("Router v0.9.9");
    }

    [Fact]
    public void Renders_the_two_destructive_action_buttons_initially()
    {
        using var ctx = NewContext(liveDataStore: out _, routerSettingsStore: out _);

        var cut = ctx.Render<SettingsModal>();

        cut.Markup.Should().Contain("Reset Stats");
        cut.Markup.Should().Contain("Clear History");
    }

    [Fact]
    public void Clicking_the_backdrop_invokes_OnClose()
    {
        using var ctx = NewContext(liveDataStore: out _, routerSettingsStore: out _);
        var closed = false;

        var cut =
            ctx.Render<SettingsModal>(p => p.Add(parameterSelector: c => c.OnClose, callback: () => closed = true));
        cut.Find("div").Click();

        closed.Should().BeTrue();
    }

    [Fact]
    public void Clicking_the_close_x_invokes_OnClose()
    {
        using var ctx = NewContext(liveDataStore: out _, routerSettingsStore: out _);
        var closed = false;

        var cut =
            ctx.Render<SettingsModal>(p => p.Add(parameterSelector: c => c.OnClose, callback: () => closed = true));
        cut.FindAll("button").First().Click();

        closed.Should().BeTrue();
    }

    [Fact]
    public void Starting_reset_shows_the_confirmation_prompt_requiring_RESET()
    {
        using var ctx = NewContext(liveDataStore: out _, routerSettingsStore: out _);

        var cut = ctx.Render<SettingsModal>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Reset Stats")).Click();

        cut.Markup.Should().Contain("RESET");
        var confirm = cut.FindAll("button").First(b => b.TextContent.Contains("Confirm Reset"));
        confirm.HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Typing_the_exact_confirmation_word_enables_the_confirm_button()
    {
        using var ctx = NewContext(liveDataStore: out _, routerSettingsStore: out _);

        var cut = ctx.Render<SettingsModal>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Reset Stats")).Click();
        cut.FindAll("input").First(i => i.GetAttribute("type") == "text")
            .Input("RESET");

        var confirm = cut.FindAll("button").First(b => b.TextContent.Contains("Confirm Reset"));
        confirm.HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Confirming_a_reset_invokes_OnClose_and_clears_live_events_but_not_log_lines()
    {
        using var ctx = NewContext(liveDataStore: out var liveDataStore, routerSettingsStore: out _);
        var closed = false;
        var changedRaised = false;
        var logLinesChangedRaised = false;
        liveDataStore.Changed += () => changedRaised = true;
        liveDataStore.LogLinesChanged += () => logLinesChangedRaised = true;

        var cut =
            ctx.Render<SettingsModal>(p => p.Add(parameterSelector: c => c.OnClose, callback: () => closed = true));
        cut.FindAll("button").First(b => b.TextContent.Contains("Reset Stats")).Click();
        cut.FindAll("input").First(i => i.GetAttribute("type") == "text")
            .Input("RESET");
        cut.FindAll("button").First(b => b.TextContent.Contains("Confirm Reset")).Click();

        closed.Should().BeTrue();
        changedRaised.Should().BeTrue("Reset Stats clears live events, which raises LiveDataStore.Changed");
        logLinesChangedRaised.Should().BeFalse("Reset Stats must not touch the Console tab's log buffer");
    }

    [Fact]
    public void Confirming_a_purge_clears_both_live_events_and_log_lines()
    {
        using var ctx = NewContext(liveDataStore: out var liveDataStore, routerSettingsStore: out _);
        var changedRaised = false;
        var logLinesChangedRaised = false;
        liveDataStore.Changed += () => changedRaised = true;
        liveDataStore.LogLinesChanged += () => logLinesChangedRaised = true;

        var cut = ctx.Render<SettingsModal>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Clear History")).Click();
        cut.FindAll("input").First(i => i.GetAttribute("type") == "text")
            .Input("PURGE");
        cut.FindAll("button").First(b => b.TextContent.Contains("Confirm Purge")).Click();

        changedRaised.Should().BeTrue("Clear History clears live events, which raises LiveDataStore.Changed");
        logLinesChangedRaised.Should().BeTrue("Clear History also empties the Console tab's log buffer");
    }

    [Fact]
    public void Clicking_cancel_returns_to_the_initial_two_button_view()
    {
        using var ctx = NewContext(liveDataStore: out _, routerSettingsStore: out _);

        var cut = ctx.Render<SettingsModal>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Clear History")).Click();
        cut.Markup.Should().Contain("PURGE");

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Cancel").Click();

        cut.Markup.Should().Contain("Clear History");
        cut.Markup.Should().NotContain("PURGE");
    }

    [Fact]
    public void Purge_requires_the_word_PURGE_not_RESET()
    {
        using var ctx = NewContext(liveDataStore: out _, routerSettingsStore: out _);

        var cut = ctx.Render<SettingsModal>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Clear History")).Click();
        cut.FindAll("input").First(i => i.GetAttribute("type") == "text")
            .Input("RESET");

        cut.FindAll("button").First(b => b.TextContent.Contains("Confirm Purge")).HasAttribute("disabled").Should()
            .BeTrue();

        cut.FindAll("input").First(i => i.GetAttribute("type") == "text")
            .Input("PURGE");
        cut.FindAll("button").First(b => b.TextContent.Contains("Confirm Purge")).HasAttribute("disabled").Should()
            .BeFalse();
    }

    [Fact]
    public void Adaptive_routing_defaults_off_with_the_recommended_sample_size()
    {
        using var ctx = NewContext(liveDataStore: out _, routerSettingsStore: out _);

        var cut = ctx.Render<SettingsModal>();

        cut.Markup.Should().Contain("Off");
        cut.Find("#sample-size").GetAttribute("value").Should().Be("20000");
    }

    [Fact]
    public void Loads_a_persisted_adaptive_routing_toggle_and_sample_size()
    {
        var client = new FakeRouterSettingsAdminClient
        {
            Settings = new RouterSettingsInfo(true, 5_000, false, JudgeModelName: "",
                EligibleJudgeModels: ["free-judge"], true)
        };
        using var ctx = NewContext(liveDataStore: out _, routerSettingsStore: out _,
            routerSettingsClient: client);

        var cut = ctx.Render<SettingsModal>();

        cut.Markup.Should().Contain("On");
        cut.Find("#sample-size").GetAttribute("value").Should().Be("5000");
    }

    [Fact]
    public void Clicking_the_toggle_flips_it()
    {
        using var ctx = NewContext(liveDataStore: out _, routerSettingsStore: out _);

        var cut = ctx.Render<SettingsModal>();
        cut.Find("button[aria-label='Toggle adaptive routing']").Click();

        cut.Markup.Should().Contain("On");
    }

    [Fact]
    public void Transcription_capture_toggle_defaults_to_on()
    {
        using var ctx = NewContext(liveDataStore: out _, routerSettingsStore: out _);

        var cut = ctx.Render<SettingsModal>();

        cut.Find("button[aria-label='Toggle transcription capture']").TextContent.Trim().Should().Be("On");
    }

    [Fact]
    public void Clicking_the_transcription_capture_toggle_flips_it_and_saves_immediately()
    {
        var client = new FakeRouterSettingsAdminClient();
        using var ctx = NewContext(liveDataStore: out _, routerSettingsStore: out var routerSettingsStore, routerSettingsClient: client);
        var cut = ctx.Render<SettingsModal>();

        cut.Find("button[aria-label='Toggle transcription capture']").Click();

        cut.Find("button[aria-label='Toggle transcription capture']").TextContent.Trim().Should().Be("Off");
        routerSettingsStore.Settings!.TranscriptCaptureEnabled.Should().BeFalse();
    }

    [Fact]
    public void Clicking_clear_shows_a_confirmation_prompt_without_clearing_yet()
    {
        var client = new FakeRouterSettingsAdminClient();
        using var ctx = NewContext(liveDataStore: out _, routerSettingsStore: out _,
            routerSettingsClient: client);
        var cut = ctx.Render<SettingsModal>();

        cut.Find("button:contains('Clear')").Click();

        cut.Markup.Should().Contain("Are you sure you want to do this?");
        client.ClearTranscriptsCallCount.Should().Be(0);
    }

    [Fact]
    public void Cancelling_the_clear_confirmation_clears_nothing()
    {
        var client = new FakeRouterSettingsAdminClient();
        using var ctx = NewContext(liveDataStore: out _, routerSettingsStore: out _,
            routerSettingsClient: client);
        var cut = ctx.Render<SettingsModal>();
        cut.Find("button:contains('Clear')").Click();

        cut.Find("button:contains('Cancel')").Click();

        cut.Markup.Should().NotContain("Are you sure you want to do this?");
        client.ClearTranscriptsCallCount.Should().Be(0);
    }

    [Fact]
    public void Confirming_the_clear_deletes_the_data_and_reports_the_row_count()
    {
        var client = new FakeRouterSettingsAdminClient { ClearTranscriptsRowsDeleted = 3 };
        using var ctx = NewContext(liveDataStore: out _, routerSettingsStore: out _,
            routerSettingsClient: client);
        var cut = ctx.Render<SettingsModal>();
        cut.Find("button:contains('Clear')").Click();

        cut.Find("button:contains('Confirm Clear')").Click();

        client.ClearTranscriptsCallCount.Should().Be(1);
        cut.Markup.Should().Contain("Cleared 3 rows.");
        cut.Markup.Should().NotContain("Are you sure you want to do this?");
    }

    [Fact]
    public void The_judge_model_dropdown_offers_automatic_plus_every_eligible_free_model()
    {
        var client = new FakeRouterSettingsAdminClient
        {
            Settings = new RouterSettingsInfo(false, 20_000, true, JudgeModelName: "free-b",
                EligibleJudgeModels: ["free-a", "free-b"], true)
        };
        using var ctx = NewContext(liveDataStore: out _, routerSettingsStore: out _,
            routerSettingsClient: client);

        var cut = ctx.Render<SettingsModal>();

        var options = cut.Find("#judge-model").QuerySelectorAll("option").Select(o => o.GetAttribute("value"))
            .ToArray();
        options.Should().Equal("", "free-a", "free-b");
    }

    /// <summary>
    /// A stored pick that is no longer eligible shows as Automatic, matching what the selector will actually
    /// do - re-offering the dead name would let the operator save a choice the server now rejects.
    /// </summary>
    [Fact]
    public void An_ineligible_stored_judge_model_falls_back_to_automatic_in_the_dropdown()
    {
        var client = new FakeRouterSettingsAdminClient
        {
            Settings = new RouterSettingsInfo(false, 20_000, true, JudgeModelName: "gone-away",
                EligibleJudgeModels: ["free-a"], true)
        };
        using var ctx = NewContext(liveDataStore: out _, routerSettingsStore: out _,
            routerSettingsClient: client);

        var cut = ctx.Render<SettingsModal>();

        cut.Find("#judge-model").GetAttribute("value").Should().BeEmpty();
    }

    /// <summary>
    /// Selecting a model persists it immediately (no footer Save button for this section), which also
    /// carries the corrected Automatic fallback through to the server since the RPC writes every field
    /// together.
    /// </summary>
    [Fact]
    public void Selecting_a_judge_model_saves_immediately_and_carries_the_automatic_fallback_along()
    {
        var client = new FakeRouterSettingsAdminClient
        {
            Settings = new RouterSettingsInfo(false, 20_000, true, JudgeModelName: "gone-away",
                EligibleJudgeModels: ["free-a"], true)
        };
        using var ctx = NewContext(liveDataStore: out _, routerSettingsStore: out var routerSettingsStore, routerSettingsClient: client);

        var cut = ctx.Render<SettingsModal>();
        cut.Find("#judge-model").Change("free-a");

        routerSettingsStore.Settings!.JudgeModelName.Should().Be("free-a");
    }

    [Fact]
    public void With_no_free_provider_the_dropdown_is_disabled_and_explains_why()
    {
        var client = new FakeRouterSettingsAdminClient
        {
            Settings = new RouterSettingsInfo(false, 20_000, false, JudgeModelName: "", EligibleJudgeModels: [], true)
        };
        using var ctx = NewContext(liveDataStore: out _, routerSettingsStore: out _,
            routerSettingsClient: client);

        var cut = ctx.Render<SettingsModal>();

        cut.Find("#judge-model").HasAttribute("disabled").Should().BeTrue();
        cut.Markup.Should().Contain("No free model is configured");
    }

    [Fact]
    public void Toggling_the_judge_and_changing_its_model_each_save_immediately()
    {
        var client = new FakeRouterSettingsAdminClient
        {
            Settings = new RouterSettingsInfo(false, 20_000, false, JudgeModelName: "",
                EligibleJudgeModels: ["free-a", "free-b"], true)
        };
        using var ctx = NewContext(liveDataStore: out _, routerSettingsStore: out var routerSettingsStore, routerSettingsClient: client);

        var cut = ctx.Render<SettingsModal>();
        cut.Find("button[aria-label='Toggle shadow judge']").Click();

        routerSettingsStore.Settings!.JudgeEnabled.Should().BeTrue();

        cut.Find("#judge-model").Change("free-b");

        routerSettingsStore.Settings.JudgeModelName.Should().Be("free-b");
    }

    [Fact]
    public void Toggling_each_portfolio_grader_saves_immediately()
    {
        var client = new FakeRouterSettingsAdminClient
        {
            Settings = new RouterSettingsInfo(false, 20_000, false, JudgeModelName: "",
                EligibleJudgeModels: ["free-a"], true, CodeJudgeEnabled: false, IceScoreEnabled: false,
                RaceEnabled: false)
        };
        using var ctx = NewContext(liveDataStore: out _, routerSettingsStore: out var routerSettingsStore, routerSettingsClient: client);

        var cut = ctx.Render<SettingsModal>();
        cut.Find("button[aria-label='Toggle CodeJudge grader']").Click();
        routerSettingsStore.Settings!.CodeJudgeEnabled.Should().BeTrue();

        cut.Find("button[aria-label='Toggle ICE-Score grader']").Click();
        routerSettingsStore.Settings!.IceScoreEnabled.Should().BeTrue();

        cut.Find("button[aria-label='Toggle RACE grader']").Click();
        routerSettingsStore.Settings!.RaceEnabled.Should().BeTrue();
    }

    [Fact]
    public void Leaving_the_sample_size_field_clamps_it_into_bounds()
    {
        using var ctx = NewContext(liveDataStore: out _, routerSettingsStore: out _);

        var cut = ctx.Render<SettingsModal>();
        var input = cut.Find("#sample-size");
        input.Change("999999");

        cut.Find("#sample-size").GetAttribute("value").Should().Be("50000");
    }

    [Fact]
    public void The_warning_icon_appears_below_the_recommended_sample_size_and_not_at_or_above_it()
    {
        using var ctx = NewContext(liveDataStore: out _, routerSettingsStore: out _);

        var cut = ctx.Render<SettingsModal>();
        cut.Markup.Should().NotContain("a sample size of 20000 is recommended.");

        cut.Find("#sample-size").Input("19999");
        cut.Markup.Should().Contain("a sample size of 20000 is recommended.");

        cut.Find("#sample-size").Input("20000");
        cut.Markup.Should().NotContain("a sample size of 20000 is recommended.");
    }

    [Fact]
    public void Toggling_adaptive_routing_and_changing_sample_size_each_save_immediately()
    {
        using var ctx = NewContext(liveDataStore: out _, routerSettingsStore: out var routerSettingsStore);

        var cut = ctx.Render<SettingsModal>();

        cut.Find("button[aria-label='Toggle adaptive routing']").Click();
        cut.Find("#sample-size").Change("15000");

        routerSettingsStore.Settings!.AdaptiveRoutingEnabled.Should().BeTrue();
        routerSettingsStore.Settings.EmbeddingMemoryCapacity.Should().Be(15_000);
        cut.Markup.Should().Contain("Saved");
    }

    [Fact]
    public void Clearing_the_sample_size_field_to_type_a_new_value_does_not_snap_back()
    {
        using var ctx = NewContext(liveDataStore: out _, routerSettingsStore: out _);

        var cut = ctx.Render<SettingsModal>();
        cut.Find("#sample-size").Input("");

        cut.Find("#sample-size").GetAttribute("value").Should().BeEmpty();
    }

    [Fact]
    public void Typing_in_the_sample_size_field_clears_the_stale_outcome_message_before_the_blur_commits()
    {
        using var ctx = NewContext(liveDataStore: out _, routerSettingsStore: out _);

        var cut = ctx.Render<SettingsModal>();
        cut.Find("button[aria-label='Toggle adaptive routing']").Click();
        cut.Markup.Should().Contain("Saved");

        cut.Find("#sample-size").Input("15000");

        cut.Markup.Should().NotContain("Saved");
    }

    [Fact]
    public void Toggling_adaptive_routing_while_the_router_is_unreachable_reports_it()
    {
        var client = new FakeRouterSettingsAdminClient
        {
            Failure = new GrpcAdminException(
                message: "Could not save the router settings: the router is not reachable.", isUnavailable: true)
        };
        using var ctx = NewContext(liveDataStore: out _, routerSettingsStore: out _,
            routerSettingsClient: client);

        var cut = ctx.Render<SettingsModal>();
        cut.Find("button[aria-label='Toggle adaptive routing']").Click();

        cut.Markup.Should().Contain("Could not reach the router. Is the proxy running?");
    }

    /// <summary>
    /// A minimal <see cref="IUpdateAdminClient"/> double so <see cref="SettingsModal"/>'s Software Update section has
    /// something to load without a live proxy.
    /// </summary>
    private sealed class FakeUpdateAdminClient : IUpdateAdminClient
    {
        public UpdateStatusInfo Status { get; set; } = new(
            CurrentVersion: "1.0.0",
            LatestVersion: "1.0.0",
            false,
            null,
            UnavailableReason: UpdateUnavailableReasonInfo.None,
            null);

        /// <summary>When set, every call fails with it - how a test stands in for a Router that isn't running.</summary>
        public GrpcAdminException? Failure { get; set; }

        public Task<UpdateStatusInfo> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            return Failure is null ? Task.FromResult(Status) : Task.FromException<UpdateStatusInfo>(Failure);
        }

        public Task<UpdateStatusInfo> CheckNowAsync(CancellationToken cancellationToken = default)
        {
            return Failure is null ? Task.FromResult(Status) : Task.FromException<UpdateStatusInfo>(Failure);
        }

        public Task<NotifyApplyStartingInfo> NotifyApplyStartingAsync(string version,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new NotifyApplyStartingInfo(true));
        }
    }

    /// <summary>A minimal <see cref="IMsiUpdateApplier"/> double; none of these tests trigger an apply.</summary>
    private sealed class FakeMsiUpdateApplier : IMsiUpdateApplier
    {
        public Task<MsiApplyResult> ApplyAsync(string assetDownloadUrl, string assetSha256, string latestVersion,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(MsiApplyResult.Failure("not used in tests"));
        }
    }

    /// <summary>
    /// A controllable <see cref="IRouterSettingsAdminClient"/> double, mirroring the router-side default (adaptive
    /// routing off, capacity 20000, transcript capture on).
    /// </summary>
    private sealed class FakeRouterSettingsAdminClient : IRouterSettingsAdminClient
    {
        public RouterSettingsInfo Settings { get; set; } = new(
            false,
            20_000,
            false,
            JudgeModelName: "",
            EligibleJudgeModels: ["free-judge"],
            true);

        public GrpcAdminException? Failure { get; set; }

        public int ClearTranscriptsCallCount { get; private set; }

        public int ClearTranscriptsRowsDeleted { get; set; }

        public Task<RouterSettingsInfo> GetAsync(CancellationToken cancellationToken = default)
        {
            return Failure is null ? Task.FromResult(Settings) : Task.FromException<RouterSettingsInfo>(Failure);
        }

        public Task<RouterSettingsInfo> UpdateAsync(
            bool adaptiveRoutingEnabled,
            int embeddingMemoryCapacity,
            bool judgeEnabled,
            string judgeModelName,
            bool transcriptCaptureEnabled,
            bool codeJudgeEnabled = false,
            bool iceScoreEnabled = false,
            bool raceEnabled = false,
            CancellationToken cancellationToken = default)
        {
            if (Failure is not null) return Task.FromException<RouterSettingsInfo>(Failure);

            Settings = new RouterSettingsInfo(
                AdaptiveRoutingEnabled: adaptiveRoutingEnabled,
                EmbeddingMemoryCapacity: embeddingMemoryCapacity,
                JudgeEnabled: judgeEnabled,
                JudgeModelName: judgeModelName,
                EligibleJudgeModels: Settings.EligibleJudgeModels,
                TranscriptCaptureEnabled: transcriptCaptureEnabled,
                CodeJudgeEnabled: codeJudgeEnabled,
                IceScoreEnabled: iceScoreEnabled,
                RaceEnabled: raceEnabled);
            return Task.FromResult(Settings);
        }

        public Task<int> ClearTranscriptsAsync(CancellationToken cancellationToken = default)
        {
            ClearTranscriptsCallCount++;
            return Failure is null
                ? Task.FromResult(ClearTranscriptsRowsDeleted)
                : Task.FromException<int>(Failure);
        }
    }

    /// <summary>A controllable <see cref="ICostReconciliationAdminClient"/> double; empty provider list by default.</summary>
    private sealed class FakeCostReconciliationAdminClient : ICostReconciliationAdminClient
    {
        public IReadOnlyList<ProviderReconciliationStatus> Status { get; set; } = [];

        public GrpcAdminException? Failure { get; set; }

        public Task<IReadOnlyList<ProviderReconciliationStatus>> GetStatusAsync(
            CancellationToken cancellationToken = default)
        {
            return Failure is null
                ? Task.FromResult(Status)
                : Task.FromException<IReadOnlyList<ProviderReconciliationStatus>>(Failure);
        }

        public Task<IReadOnlyList<ProviderReconciliationStatus>> RunNowAsync(
            CancellationToken cancellationToken = default)
        {
            return Failure is null
                ? Task.FromResult(Status)
                : Task.FromException<IReadOnlyList<ProviderReconciliationStatus>>(Failure);
        }
    }

    private sealed class FakeManagementTokenAdminClient : IManagementTokenAdminClient
    {
        public GrpcAdminException? Failure { get; set; }
        public string Token { get; set; } = "fake-management-token";

        public Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
        {
            return Failure is null ? Task.FromResult(Token) : Task.FromException<string>(Failure);
        }

        public Task<string> RegenerateAsync(CancellationToken cancellationToken = default)
        {
            if (Failure is not null) return Task.FromException<string>(Failure);

            Token = "fake-regenerated-token";
            return Task.FromResult(Token);
        }
    }
}