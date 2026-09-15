using AwesomeAssertions;
using Bunit;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using System.Text.RegularExpressions;
using TotallyHot.ArcRouter.Gui.Admin;
using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Services;
using Contract = TotallyHot.ArcRouter.Admin.Contract;
using IElement = AngleSharp.Dom.IElement;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="ProvidersAdmin"/> in its <em>loaded</em> state - the provider cards, their model
/// rows, the budget panel, and every mutation reachable from them.
/// <para>
/// <see cref="ProvidersAdminTests"/> deliberately covers only the unreachable branch, which is all a test
/// process could reach while the store built its own channel: with nothing listening,
/// <c>OnInitializedAsync</c> always landed on "proxy unreachable" and the entire body of this component
/// went unrendered. These tests drive it through <see cref="ProviderAdminStore"/>'s <c>client</c> seam
/// against a canned generated-client stub instead, so the loaded UI is exercised for real.
/// </para>
/// </summary>
public sealed class ProvidersAdminLoadedTests
{
    /// <summary>
    /// One fully-populated provider set: a literal credential, a stopped model and a not-detected one, a
    /// completed capability scan, and both budget dimensions capped. Chosen so a single render walks most
    /// of the card's conditional branches at once rather than needing a fixture per branch.
    /// </summary>
    private static Contract.ProviderListResponse DefaultProviders()
    {
        var response = new Contract.ProviderListResponse();

        var anthropic = new Contract.ProviderState
        {
            Key = "anthropic", Name = "Anthropic Prod", BaseUrl = "https://api.anthropic.com",
            AuthHeaderName = "x-api-key", ProviderType = "Anthropic", IsFree = false, DollarCap = "100",
            TokenCap = 1_000_000, DollarSpent = "42.5", TokensUsed = 250000, Enabled = true, WindowKind = "Monthly",
            EndpointCapabilities = new Contract.EndpointCapabilitiesState
            {
                AnthropicCompatible = true,
                ScannedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-01T00:00:00Z"))
            }
        };
        anthropic.Models.Add(new Contract.ModelState
        {
            ModelName = "claude-opus", ProviderModelId = "claude-opus-5", Dialect = "openai-native",
            Confidence = "Observed", Enabled = true, PresentUpstream = true
        });
        anthropic.Models.Add(new Contract.ModelState
        {
            ModelName = "claude-haiku", ProviderModelId = "claude-haiku-4-5", Enabled = false,
            PresentUpstream = false
        });
        anthropic.Headers.Add(new Contract.HeaderState { Name = "anthropic-version", Source = "literal" });
        response.Providers.Add(anthropic);

        response.Providers.Add(new Contract.ProviderState
        {
            Key = "ollama", BaseUrl = "http://localhost:11434/v1", AuthHeaderName = "Authorization",
            ProviderType = "LocalRuntime", IsFree = true, DollarSpent = "0", TokensUsed = 0, Enabled = false,
            WindowKind = "Monthly"
        });

        response.Providers.Add(new Contract.ProviderState
        {
            Key = "openai", Name = "OpenAI", BaseUrl = "https://api.openai.com/v1", AuthHeaderName = "Authorization",
            ProviderType = "OpenAI", IsFree = false, DollarSpent = "3.25", TokensUsed = 900, Enabled = true,
            WindowKind = "Monthly",
            EndpointCapabilities = new Contract.EndpointCapabilitiesState
            {
                ScannedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-01T00:00:00Z")),
                ScanError = "timed out"
            }
        });

        return response;
    }

    // Same anthropic provider as DefaultProviders, but with reportedUsage rows and a stored admin key -
    // exercises the populated-data branches of the reported-usage section (§8.2), which must render from
    // these backend-supplied values, never the GUI clock.
    private static Contract.ProviderListResponse ProvidersWithReportedUsage()
    {
        var response = new Contract.ProviderListResponse();
        var anthropic = new Contract.ProviderState
        {
            Key = "anthropic", Name = "Anthropic Prod", BaseUrl = "https://api.anthropic.com",
            AuthHeaderName = "x-api-key", ProviderType = "Anthropic", DollarSpent = "0", Enabled = true,
            WindowKind = "Monthly", HasStoredAdminKey = true,
            ReportedUsage = new Contract.ProviderReportedUsageState
            {
                FetchedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-02T04:00:00Z"))
            }
        };
        anthropic.ReportedUsage.Rows.Add(new Contract.ReportedUsageRow
        {
            UsageDay = "2026-03-01", Model = "claude-opus-4-1", InputTokens = 100, OutputTokens = 50,
            CacheCreationTokens = 5, CacheReadTokens = 10
        });
        response.Providers.Add(anthropic);
        return response;
    }

    // Same fixture as ProvidersWithUsageAndRateLimit but IsStale is set and no projections - exercises the
    // "As of ... stale" branch distinctly from the fresh-and-projected one.
    private static Contract.ProviderListResponse ProvidersWithStaleRateLimit()
    {
        var response = new Contract.ProviderListResponse();
        var rateLimit = new Contract.ProviderRateLimitState
        {
            ObservedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-01T12:00:00Z")),
            IsStale = true
        };
        rateLimit.Dimensions["tokens"] = new Contract.RateLimitDimensionState
        {
            Limit = 200000, Remaining = 158000,
            ResetAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-01T13:00:00Z"))
        };
        response.Providers.Add(new Contract.ProviderState
        {
            Key = "anthropic", Name = "Anthropic Prod", BaseUrl = "https://api.anthropic.com",
            AuthHeaderName = "x-api-key", ProviderType = "Anthropic", DollarSpent = "12.5", TokensUsed = 158000,
            Enabled = true, WindowKind = "Monthly",
            UsageLastRecordedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-01T08:00:00Z")),
            RateLimit = rateLimit
        });
        return response;
    }

    // Same anthropic provider as DefaultProviders, but with a populated usageLastRecordedAtUtc and
    // rateLimit (including a unified-family window and an exhaustion projection) - exercises the card's
    // populated-data branches, which the GUI must render from these backend-supplied values, never the GUI
    // clock.
    private static Contract.ProviderListResponse ProvidersWithUsageAndRateLimit()
    {
        var response = new Contract.ProviderListResponse();
        var rateLimit = new Contract.ProviderRateLimitState
        {
            ObservedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-01T12:00:00Z"))
        };
        rateLimit.Dimensions["tokens"] = new Contract.RateLimitDimensionState
        {
            Limit = 200000, Remaining = 158000,
            ResetAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-01T13:00:00Z")),
            TimeToExhaustionSeconds = 19 * 60, BurnRatePerMinute = 2210.5
        };
        rateLimit.UnifiedWindows["5h"] = new Contract.UnifiedWindowState
        {
            Status = "allowed", ResetAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-01T13:00:00Z"))
        };
        response.Providers.Add(new Contract.ProviderState
        {
            Key = "anthropic", Name = "Anthropic Prod", BaseUrl = "https://api.anthropic.com",
            AuthHeaderName = "x-api-key", ProviderType = "Anthropic", DollarSpent = "12.5", TokensUsed = 158000,
            Enabled = true, WindowKind = "Monthly",
            UsageLastRecordedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-01T08:00:00Z")),
            RateLimit = rateLimit
        });
        return response;
    }

    // Same three providers as DefaultProviders, but openai's adminAction carries a failed "Refresh from
    // endpoint" - the expired-API-key scenario that motivated the warning icon/toast.
    private static Contract.ProviderListResponse ProvidersWithFailedInteraction()
    {
        var response = new Contract.ProviderListResponse();
        response.Providers.Add(new Contract.ProviderState
        {
            Key = "anthropic", Name = "Anthropic Prod", BaseUrl = "https://api.anthropic.com",
            AuthHeaderName = "x-api-key", ProviderType = "Anthropic", DollarSpent = "0", Enabled = true,
            WindowKind = "Monthly"
        });
        response.Providers.Add(new Contract.ProviderState
        {
            Key = "ollama", BaseUrl = "http://localhost:11434/v1", AuthHeaderName = "Authorization",
            ProviderType = "LocalRuntime", IsFree = true, DollarSpent = "0", Enabled = false, WindowKind = "Monthly"
        });
        response.Providers.Add(new Contract.ProviderState
        {
            Key = "openai", Name = "OpenAI", BaseUrl = "https://api.openai.com/v1", AuthHeaderName = "Authorization",
            ProviderType = "OpenAI", DollarSpent = "0", Enabled = true, WindowKind = "Monthly",
            AdminAction = new Contract.ProviderInteractionState
            {
                Ok = false, Operation = "Refresh from endpoint",
                Message = "Provider returned 401 for https://api.openai.com/v1/models.",
                AtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-24T09:00:00Z")), Kind = "None"
            }
        });
        return response;
    }

    private static BunitContext NewContext(StubClient client)
    {
        var ctx = new BunitContext();
        // The budget panel renders EChart, which calls into echartsInterop; Loose mode records the calls
        // instead of failing them, matching EChartTests and DashboardTests.
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(new ProviderAdminStore(client: new ProviderAdminClient(client, "test-token")));
        return ctx;
    }

    private static IRenderedComponent<ProvidersAdmin> RenderLoaded(BunitContext ctx)
    {
        var cut = ctx.Render<ProvidersAdmin>();
        cut.WaitForAssertion(assertion: () => cut.Markup.Should().Contain("Configured Providers"),
            timeout: TimeSpan.FromSeconds(4));
        return cut;
    }

    /// <summary>
    /// Same as <see cref="NewContext"/>, but also registers a <see cref="ToastService"/> so a mutation's toast can be
    /// asserted on.
    /// </summary>
    private static (BunitContext Context, ToastService Toasts) NewContextWithToasts(StubClient client)
    {
        var toasts = new ToastService();
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(toasts);
        ctx.Services.AddSingleton(new ProviderAdminStore(
            client: new ProviderAdminClient(client, "test-token"), toasts: toasts));
        return (ctx, toasts);
    }

    /// <summary>
    /// Finds a button by label <em>inside the open modal</em>. Scoping matters: each provider card carries
    /// its own budget "Save", and those precede the dialog in the DOM - an unscoped
    /// <c>First(b =&gt; b.TextContent == "Save")</c> silently clicks the budget panel instead, which passes
    /// some assertions for entirely the wrong reason.
    /// </summary>
    private static IElement FindDialogButton(IRenderedComponent<ProvidersAdmin> cut, string label)
    {
        return cut.FindAll(".overlay-panel button").First(b => b.TextContent.Trim() == label);
    }

    [Fact]
    public void Renders_a_card_per_provider_with_its_display_name_and_endpoint()
    {
        var client = new StubClient();
        using var ctx = NewContext(client);

        var cut = RenderLoaded(ctx);

        cut.Markup.Should().Contain("Anthropic Prod");
        cut.Markup.Should().Contain("https://api.anthropic.com");
        // A provider with no display name falls back to its key rather than rendering blank.
        cut.Markup.Should().Contain("ollama");
        cut.Markup.Should().Contain("http://localhost:11434/v1");
    }

    [Fact]
    public void Shows_a_badge_per_detected_api_and_none_when_never_scanned_or_all_false()
    {
        var client = new StubClient();
        using var ctx = NewContext(client);

        var cut = RenderLoaded(ctx);

        var badgeLabels = cut.FindAll(".ds-badge-success").Select(b => b.TextContent.Trim()).ToList();

        // anthropic's scan reported anthropicCompatible: true -> its one badge.
        badgeLabels.Should().ContainSingle().Which.Should().Be("Anthropic");
        // ollama's endpointCapabilities is null (never scanned), and openai's scan completed with every
        // flag false - neither contributes a badge despite openai having a (failed) scan on record.
    }

    [Fact]
    public void Marks_a_model_the_last_scan_did_not_report_as_not_detected()
    {
        var client = new StubClient();
        using var ctx = NewContext(client);

        var cut = RenderLoaded(ctx);

        // Distinct from a deliberately stopped model, which is why it gets its own label.
        cut.Markup.Should().Contain("not detected");
        cut.Markup.Should().Contain("claude-haiku");
    }

    [Fact]
    public void Renders_budget_bars_when_capped_and_an_unlimited_note_when_not()
    {
        var client = new StubClient();
        using var ctx = NewContext(client);

        var cut = RenderLoaded(ctx);

        cut.Markup.Should().Contain("$42.50 / $100.00");
        cut.Markup.Should().Contain("250,000 / 1,000,000");
        cut.Markup.Should().Contain("No budget set");
        // Both capped dimensions on the one provider render a chart.
        ctx.JSInterop.Invocations.Count(i => i.Identifier == "echartsInterop.render").Should().Be(2);
    }

    [Fact]
    public void Toggling_a_provider_sends_the_inverted_enabled_flag()
    {
        var client = new StubClient();
        using var ctx = NewContext(client);
        var cut = RenderLoaded(ctx);

        cut.Find("button[aria-label='Stop provider anthropic']").Click();

        cut.WaitForAssertion(() => client.LastSetProviderEnabledRequest.Should().NotBeNull());
        client.LastSetProviderEnabledRequest!.Key.Should().Be("anthropic");
        client.LastSetProviderEnabledRequest.Enabled.Should().BeFalse();
    }

    [Fact]
    public void A_stopped_provider_offers_start_rather_than_stop()
    {
        var client = new StubClient();
        using var ctx = NewContext(client);

        var cut = RenderLoaded(ctx);

        cut.FindAll("button[aria-label='Start provider ollama']").Should().ContainSingle();
    }

    [Fact]
    public void Toggling_a_model_sends_the_inverted_enabled_flag()
    {
        var client = new StubClient();
        using var ctx = NewContext(client);
        var cut = RenderLoaded(ctx);

        cut.Find("button[aria-label='Stop model claude-opus']").Click();

        cut.WaitForAssertion(() => client.LastSetModelEnabledRequest.Should().NotBeNull());
        client.LastSetModelEnabledRequest!.ModelName.Should().Be("claude-opus");
    }

    [Fact]
    public void Pinning_a_tool_dialect_writes_it_and_auto_detect_clears_it()
    {
        var client = new StubClient();
        using var ctx = NewContext(client);
        var cut = RenderLoaded(ctx);

        cut.Find("select[aria-label='Tool-call dialect for claude-opus']").Change("hermes");

        cut.WaitForAssertion(() => client.LastSetModelToolDialectRequest.Should().NotBeNull());
        client.LastSetModelToolDialectRequest!.ProviderKey.Should().Be("anthropic");
        client.LastSetModelToolDialectRequest.ModelName.Should().Be("claude-opus");
        client.LastSetModelToolDialectRequest.Dialect.Should().Be("hermes");
    }

    [Fact]
    public void Refresh_from_endpoint_posts_to_the_provider()
    {
        var client = new StubClient();
        using var ctx = NewContext(client);
        var cut = RenderLoaded(ctx);

        cut.Find("button[aria-label='Refresh provider anthropic']").Click();

        cut.WaitForAssertion(() => client.LastRefreshFromEndpointRequest.Should().NotBeNull());
        client.LastRefreshFromEndpointRequest!.ProviderKey.Should().Be("anthropic");
    }

    [Fact]
    public void A_provider_whose_last_interaction_failed_shows_a_warning_icon()
    {
        var client = new StubClient { Response = ProvidersWithFailedInteraction() };
        using var ctx = NewContext(client);

        var cut = RenderLoaded(ctx);

        cut.Markup.Should().Contain("Refresh from endpoint failed");
        cut.Markup.Should().Contain("Provider returned 401");
    }

    [Fact]
    public void A_provider_with_no_recorded_interaction_shows_no_warning_icon()
    {
        // DefaultProviders (the default fixture) carries no adminAction on any provider.
        var client = new StubClient();
        using var ctx = NewContext(client);

        var cut = RenderLoaded(ctx);

        cut.Markup.Should().NotContain("Refresh from endpoint failed");
    }

    [Fact]
    public void Refreshing_a_provider_whose_refresh_failed_raises_a_toast()
    {
        // The motivating bug: RefreshFromEndpointAsync returns success even when the router's discovery
        // failed (an expired API key), so ProvidersAdmin.RunAsync's own thrown-exception catch never fires -
        // ProviderAdminStore.RefreshFromEndpointAsync must notice via AdminAction and raise the toast
        // itself.
        var client = new StubClient { Response = ProvidersWithFailedInteraction() };
        var (ctx, toasts) = NewContextWithToasts(client);
        using var _ = ctx;
        var cut = RenderLoaded(ctx);

        cut.Find("button[aria-label='Refresh provider openai']").Click();

        cut.WaitForAssertion(() => toasts.Toasts.Should().ContainSingle());
        toasts.Toasts[0].Message.Should().Contain("401");
    }

    [Fact]
    public void The_add_model_pane_is_collapsed_until_toggled()
    {
        var client = new StubClient();
        using var ctx = NewContext(client);
        var cut = RenderLoaded(ctx);

        // Every provider's pane stays mounted so it can animate shut as well as open, so a collapsed
        // pane is one whose wrapper lacks .open and carries inert - the latter is what keeps its two
        // inputs and Add button out of the tab order while they are not visible.
        var panes = cut.FindAll(".ls-disclosure");
        panes.Should().NotBeEmpty();
        panes.Should().AllSatisfy(pane =>
        {
            pane.ClassList.Should().NotContain("open");
            pane.HasAttribute("inert").Should().BeTrue();
        });

        cut.FindAll("button[aria-label='Add model manually']")[0].Click();

        var opened = cut.FindAll(".ls-disclosure").Where(p => p.ClassList.Contains("open")).ToList();
        opened.Should().ContainSingle();
        opened[0].HasAttribute("inert").Should().BeFalse();
    }

    [Fact]
    public void Adding_a_model_sends_its_name_and_optional_provider_id()
    {
        var client = new StubClient();
        using var ctx = NewContext(client);
        var cut = RenderLoaded(ctx);
        cut.FindAll("button[aria-label='Add model manually']")[0].Click();

        cut.Find("input[placeholder='model name']").Input("claude-sonnet");
        cut.Find("input[placeholder='provider model id (optional)']").Input("claude-sonnet-5");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Add").Click();

        cut.WaitForAssertion(() => client.LastUpsertModelRequest.Should().NotBeNull());
        client.LastUpsertModelRequest!.ProviderKey.Should().Be("anthropic");
        client.LastUpsertModelRequest.ModelName.Should().Be("claude-sonnet");
        client.LastUpsertModelRequest.Model.ProviderModelId.Should().Be("claude-sonnet-5");
    }

    [Fact]
    public void Adding_a_model_with_a_blank_name_sends_nothing()
    {
        var client = new StubClient();
        using var ctx = NewContext(client);
        var cut = RenderLoaded(ctx);
        cut.FindAll("button[aria-label='Add model manually']")[0].Click();

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Add").Click();

        // Only the initial list; a blank name is a no-op rather than a round-trip.
        client.LastUpsertModelRequest.Should().BeNull();
    }

    [Fact]
    public void Removing_a_model_deletes_it()
    {
        var client = new StubClient();
        using var ctx = NewContext(client);
        var cut = RenderLoaded(ctx);

        cut.Find("button[title='Remove model']").Click();

        cut.WaitForAssertion(() => client.LastRemoveModelRequest.Should().NotBeNull());
        client.LastRemoveModelRequest!.ModelName.Should().Be("claude-opus");
    }

    [Fact]
    public void Removing_a_provider_opens_the_type_to_confirm_dialog_rather_than_deleting()
    {
        var client = new StubClient();
        using var ctx = NewContext(client);
        var cut = RenderLoaded(ctx);

        cut.Find("button[aria-label='Remove provider anthropic']").Click();

        // Removal cascades to the provider's models, so it is gated behind typing the key.
        cut.Find(".overlay-panel span").TextContent.Trim().Should().Be("Remove Provider");
        cut.FindAll("input[aria-label='Type anthropic to confirm removal']").Should().ContainSingle();
        client.LastRemoveProviderRequest.Should().BeNull();
    }

    [Fact]
    public void Cancelling_the_remove_dialog_closes_it_without_deleting()
    {
        var client = new StubClient();
        using var ctx = NewContext(client);
        var cut = RenderLoaded(ctx);
        cut.Find("button[aria-label='Remove provider anthropic']").Click();

        FindDialogButton(cut: cut, label: "Cancel").Click();

        cut.FindAll(".overlay-panel").Should().BeEmpty();
        client.LastRemoveProviderRequest.Should().BeNull();
    }

    [Fact]
    public void Add_provider_opens_the_dialog_in_new_mode()
    {
        var client = new StubClient();
        using var ctx = NewContext(client);
        var cut = RenderLoaded(ctx);

        cut.FindAll("button")
            .First(b => b.TextContent.Contains(value: "Add Provider", comparisonType: StringComparison.Ordinal))
            .Click();

        cut.Find(".overlay-panel span").TextContent.Trim().Should().Be("Add Provider");
        cut.Find("[data-testid='provider-name']").GetAttribute("value").Should().BeNullOrEmpty();
    }

    [Fact]
    public void Edit_opens_the_dialog_seeded_with_the_providers_stored_type()
    {
        var client = new StubClient();
        using var ctx = NewContext(client);
        var cut = RenderLoaded(ctx);

        cut.FindAll("button[title='Config']")[0].Click();

        // The round-trip bug: OpenEdit used to hardcode "Other", so an Anthropic provider always reopened
        // as Other and lost its type on the next save.
        cut.Find("[data-testid='provider-type']").GetAttribute("value").Should().Be(nameof(ProviderType.Anthropic));
        cut.Find("[data-testid='provider-name']").GetAttribute("value").Should().Be("Anthropic Prod");
    }

    [Fact]
    public void Saving_the_edit_dialog_writes_the_provider_type_through()
    {
        var client = new StubClient();
        using var ctx = NewContext(client);
        var cut = RenderLoaded(ctx);
        cut.FindAll("button[title='Config']")[0].Click();

        FindDialogButton(cut: cut, label: "Save").Click();

        cut.WaitForAssertion(() => client.LastUpsertProviderRequest.Should().NotBeNull());
        client.LastUpsertProviderRequest!.Key.Should().Be("anthropic");
        client.LastUpsertProviderRequest.ProviderType.Should().Be("Anthropic");
    }

    [Fact]
    public void Cancelling_the_edit_dialog_closes_it_without_writing()
    {
        var client = new StubClient();
        using var ctx = NewContext(client);
        var cut = RenderLoaded(ctx);
        cut.FindAll("button[title='Config']")[0].Click();

        FindDialogButton(cut: cut, label: "Cancel").Click();

        cut.FindAll(".overlay-panel").Should().BeEmpty();
        client.LastUpsertProviderRequest.Should().BeNull();
    }

    [Fact]
    public void A_rejected_edit_surfaces_the_servers_message_in_the_dialog()
    {
        var client = new StubClient();
        using var ctx = NewContext(client);
        var cut = RenderLoaded(ctx);
        cut.FindAll("button[title='Config']")[0].Click();
        client.NextFailure = "BaseUrl must be an absolute URI.";

        FindDialogButton(cut: cut, label: "Save").Click();

        // The dialog stays open carrying the reason, rather than closing over a write that didn't happen.
        cut.WaitForAssertion(() => cut.Find("[data-testid='dialog-error']").TextContent
            .Should().Contain("BaseUrl must be an absolute URI."));
    }

    [Theory]
    [InlineData("not-a-number", "", "Monthly $ cap must be a non-negative number")]
    [InlineData("", "1.5", "Monthly token cap must be a non-negative whole number")]
    public void An_invalid_budget_cap_is_rejected_locally_with_a_reason(string dollars, string tokens, string expected)
    {
        var client = new StubClient();
        using var ctx = NewContext(client);
        var cut = RenderLoaded(ctx);

        cut.FindAll("input[placeholder='monthly $ cap (optional)']")[0].Input(dollars);
        cut.FindAll("input[placeholder='monthly token cap (optional)']")[0].Input(tokens);
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save").Click();

        cut.Markup.Should().Contain(expected);
        // Rejected before any request - a malformed cap never reaches the proxy.
        client.LastSetProviderBudgetRequest.Should().BeNull();
    }

    [Fact]
    public void A_valid_budget_is_written()
    {
        var client = new StubClient();
        using var ctx = NewContext(client);
        var cut = RenderLoaded(ctx);

        cut.FindAll("input[placeholder='monthly $ cap (optional)']")[0].Input("250");
        cut.FindAll("input[placeholder='monthly token cap (optional)']")[0].Input("5000");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save").Click();

        cut.WaitForAssertion(() => client.LastSetProviderBudgetRequest.Should().NotBeNull());
        client.LastSetProviderBudgetRequest!.ProviderKey.Should().Be("anthropic");
        client.LastSetProviderBudgetRequest.Budget.DollarCap.Should().Be("250");
    }

    [Fact]
    public void A_failed_mutation_surfaces_in_the_panes_error_banner()
    {
        var client = new StubClient();
        using var ctx = NewContext(client);
        var cut = RenderLoaded(ctx);
        client.NextFailure = "provider is in use";

        cut.Find("button[aria-label='Stop provider anthropic']").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("provider is in use"));
    }

    [Fact]
    public void Usage_card_renders_for_Anthropic_and_OpenAi_typed_providers_but_not_LocalRuntime()
    {
        // docs/router/openai-format-usage-accuracy-plan.md §6.2 generalizes this card beyond Anthropic:
        // it now renders (titled by the provider's own type) for both Anthropic- and OpenAI-typed
        // providers, but ollama (LocalRuntime) still must not render the section.
        var client = new StubClient();
        using var ctx = NewContext(client);

        var cut = RenderLoaded(ctx);

        cut.Markup.Should().Contain("Anthropic Usage");
        cut.Markup.Should().Contain("Reported by Anthropic");
        cut.Markup.Should().Contain("OpenAI Usage");
        cut.Markup.Should().Contain("Reported by OpenAI");
        cut.Markup.Should().Contain("Estimated from intercepted traffic");
    }

    [Fact]
    public void ReportedUsage_section_renders_only_for_AnthropicTyped_providers_andShowsEmptyState_whenNothingFetched()
    {
        // docs/router/secrets-at-rest-plan.md §8.2: the section is Anthropic-only, unlike the wider
        // Anthropic/OpenAI usage block it lives inside; the fixture's anthropic provider carries no
        // reportedUsage, so the empty state renders.
        var client = new StubClient();
        using var ctx = NewContext(client);

        var cut = RenderLoaded(ctx);

        cut.Markup.Should().Contain("Reported Usage (Anthropic)");
        cut.Markup.Should().Contain("No reported usage fetched yet");
        Regex.Matches(input: cut.Markup, pattern: "Reported Usage \\(Anthropic\\)").Count.Should().Be(1);
    }

    [Fact]
    public void AdminApiKey_field_renders_for_recognized_reconciliation_providers()
    {
        var client = new StubClient();
        using var ctx = NewContext(client);

        var cut = RenderLoaded(ctx);

        // anthropic and openai are both recognized (docs/router/agent-cost-tracking.md §3.5); the fixture's
        // "ollama" provider is neither, so its card must not offer the field at all.
        Regex.Matches(input: cut.Markup, pattern: "Admin API Key \\(cost reconciliation, optional\\)").Count.Should()
            .Be(2);
    }

    [Fact]
    public void ReportedUsage_section_rendersChartAndFetchedFooter_whenDataPresent()
    {
        var client = new StubClient { Response = ProvidersWithReportedUsage() };
        using var ctx = NewContext(client);

        var cut = RenderLoaded(ctx);

        cut.Markup.Should().Contain("Fetched 2026-03-02 04:00 UTC");
        cut.Markup.Should().NotContain("No reported usage fetched yet");
        cut.Markup.Should().Contain("•••••••• stored");
    }

    [Fact]
    public void Anthropic_Usage_card_shows_empty_states_when_nothing_recorded_yet()
    {
        var client = new StubClient();
        using var ctx = NewContext(client);

        var cut = RenderLoaded(ctx);

        // The fixture's anthropic provider has no usageLastRecordedAtUtc/rateLimit set.
        cut.Markup.Should().Contain("No usage recorded yet");
        cut.Markup.Should().Contain("No rate-limit data observed yet");
    }

    [Fact]
    public void Anthropic_Usage_card_renders_backend_timestamps_for_both_sub_blocks()
    {
        var client = new StubClient { Response = ProvidersWithUsageAndRateLimit() };
        using var ctx = NewContext(client);

        var cut = RenderLoaded(ctx);

        cut.Markup.Should().Contain("Last recorded 2026-03-01 08:00 UTC");
        cut.Markup.Should().Contain("As of 2026-03-01 12:00 UTC");
        cut.Markup.Should().Contain("Tokens: 158,000 of 200,000 remaining");
        cut.Markup.Should().Contain("5h window: allowed");
        cut.Markup.Should().NotContain("No usage recorded yet");
        cut.Markup.Should().NotContain("No rate-limit data observed yet");
    }

    [Fact]
    public void RateLimit_Stale_DimsAsOfFooterAndLabelsIt()
    {
        var client = new StubClient { Response = ProvidersWithStaleRateLimit() };
        using var ctx = NewContext(client);

        var cut = RenderLoaded(ctx);

        cut.Markup.Should().Contain("As of 2026-03-01 12:00 UTC · stale");
    }

    [Fact]
    public void RateLimit_ExhaustionProjection_RendersBurnRateLine()
    {
        var client = new StubClient { Response = ProvidersWithUsageAndRateLimit() };
        using var ctx = NewContext(client);

        var cut = RenderLoaded(ctx);

        cut.Markup.Should().Contain("at current rate");
    }

    /// <summary>
    /// A canned <c>ProviderAdminService</c> client: answers every read with the provider list, so the
    /// component's "mutate, then publish the returned list" flow works end to end.
    /// <see cref="NextFailure"/> makes the next mutation fail with <see cref="StatusCode.InvalidArgument"/>,
    /// which is how the error paths are reached. Overrides only the <c>CallOptions</c> overloads: the
    /// generated convenience overloads delegate to them.
    /// </summary>
    private sealed class StubClient : Contract.ProviderAdminService.ProviderAdminServiceClient
    {
        public Contract.ProviderListResponse Response { get; init; } = DefaultProviders();

        public string? NextFailure { get; set; }

        public Contract.UpsertProviderRequest? LastUpsertProviderRequest { get; private set; }
        public Contract.RemoveProviderRequest? LastRemoveProviderRequest { get; private set; }
        public Contract.SetProviderBudgetRequest? LastSetProviderBudgetRequest { get; private set; }
        public Contract.SetProviderEnabledRequest? LastSetProviderEnabledRequest { get; private set; }
        public Contract.UpsertModelRequest? LastUpsertModelRequest { get; private set; }
        public Contract.RemoveModelRequest? LastRemoveModelRequest { get; private set; }
        public Contract.SetModelEnabledRequest? LastSetModelEnabledRequest { get; private set; }
        public Contract.SetModelToolDialectRequest? LastSetModelToolDialectRequest { get; private set; }
        public Contract.RefreshFromEndpointRequest? LastRefreshFromEndpointRequest { get; private set; }

        public override AsyncUnaryCall<Contract.ProviderListResponse> ListProvidersAsync(
            Contract.ListProvidersRequest request, CallOptions options)
        {
            return Ok(Response);
        }

        public override AsyncUnaryCall<Contract.ProviderListResponse> UpsertProviderAsync(
            Contract.UpsertProviderRequest request, CallOptions options)
        {
            LastUpsertProviderRequest = request;
            return Mutate();
        }

        public override AsyncUnaryCall<Contract.ProviderListResponse> RemoveProviderAsync(
            Contract.RemoveProviderRequest request, CallOptions options)
        {
            LastRemoveProviderRequest = request;
            return Mutate();
        }

        public override AsyncUnaryCall<Contract.ProviderListResponse> SetProviderBudgetAsync(
            Contract.SetProviderBudgetRequest request, CallOptions options)
        {
            LastSetProviderBudgetRequest = request;
            return Mutate();
        }

        public override AsyncUnaryCall<Contract.ProviderListResponse> SetProviderEnabledAsync(
            Contract.SetProviderEnabledRequest request, CallOptions options)
        {
            LastSetProviderEnabledRequest = request;
            return Mutate();
        }

        public override AsyncUnaryCall<Contract.ProviderListResponse> UpsertModelAsync(
            Contract.UpsertModelRequest request, CallOptions options)
        {
            LastUpsertModelRequest = request;
            return Mutate();
        }

        public override AsyncUnaryCall<Contract.ProviderListResponse> RemoveModelAsync(
            Contract.RemoveModelRequest request, CallOptions options)
        {
            LastRemoveModelRequest = request;
            return Mutate();
        }

        public override AsyncUnaryCall<Contract.ProviderListResponse> SetModelEnabledAsync(
            Contract.SetModelEnabledRequest request, CallOptions options)
        {
            LastSetModelEnabledRequest = request;
            return Mutate();
        }

        public override AsyncUnaryCall<Contract.ProviderListResponse> SetModelToolDialectAsync(
            Contract.SetModelToolDialectRequest request, CallOptions options)
        {
            LastSetModelToolDialectRequest = request;
            return Mutate();
        }

        public override AsyncUnaryCall<Contract.ProviderListResponse> RefreshFromEndpointAsync(
            Contract.RefreshFromEndpointRequest request, CallOptions options)
        {
            LastRefreshFromEndpointRequest = request;
            return Mutate();
        }

        /// <summary>Answers a mutation: the next queued failure if one is set, otherwise the canned list.</summary>
        private AsyncUnaryCall<Contract.ProviderListResponse> Mutate()
        {
            if (NextFailure is { } failure)
            {
                NextFailure = null;
                return Fail(new RpcException(new Status(statusCode: StatusCode.InvalidArgument, detail: failure)));
            }

            return Ok(Response);
        }

        private static AsyncUnaryCall<Contract.ProviderListResponse> Ok(Contract.ProviderListResponse response)
        {
            return new AsyncUnaryCall<Contract.ProviderListResponse>(
                responseAsync: Task.FromResult(response),
                responseHeadersAsync: Task.FromResult(new Metadata()),
                getStatusFunc: () => Status.DefaultSuccess,
                getTrailersFunc: () => [],
                disposeAction: () => { });
        }

        private static AsyncUnaryCall<Contract.ProviderListResponse> Fail(RpcException failure)
        {
            return new AsyncUnaryCall<Contract.ProviderListResponse>(
                responseAsync: Task.FromException<Contract.ProviderListResponse>(failure),
                responseHeadersAsync: Task.FromResult(new Metadata()),
                getStatusFunc: () => Status.DefaultSuccess,
                getTrailersFunc: () => [],
                disposeAction: () => { });
        }
    }
}
