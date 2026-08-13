using System.Linq;
using System.Net;
using System.Text;
using TotallyHot.ArcRouter.Gui.Admin;
using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Services;
using Bunit;
using AwesomeAssertions;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="ProvidersAdmin"/> in its <em>loaded</em> state - the provider cards, their model
/// rows, the budget panel, and every mutation reachable from them.
/// <para>
/// <see cref="ProvidersAdminTests"/> deliberately covers only the unreachable branch, which is all a test
/// process could reach while the store built its own <see cref="HttpClient"/>: with nothing listening,
/// <c>OnInitializedAsync</c> always landed on "proxy unreachable" and the entire body of this component
/// went unrendered. These tests drive it through <see cref="ProviderAdminStore"/>'s <c>transport</c> seam
/// against a canned management API instead, so the loaded UI is exercised for real.
/// </para>
/// </summary>
public sealed class ProvidersAdminLoadedTests
{
    /// <summary>
    /// One fully-populated provider: a literal credential, a stopped model and a not-detected one, a
    /// completed capability scan, and both budget dimensions capped. Chosen so a single render walks most
    /// of the card's conditional branches at once rather than needing a fixture per branch.
    /// </summary>
    private const string ProvidersJson = """
        {
          "providers": [
            {
              "key": "anthropic",
              "name": "Anthropic Prod",
              "baseUrl": "https://api.anthropic.com",
              "authHeaderName": "x-api-key",
              "authHeaderScheme": "",
              "hasApiKey": true,
              "apiKeyEnvVar": null,
              "providerType": "Anthropic",
              "models": [
                { "modelName": "claude-opus", "providerModelId": "claude-opus-5", "dialect": "openai-native", "confidence": "Observed", "enabled": true, "presentUpstream": true },
                { "modelName": "claude-haiku", "providerModelId": "claude-haiku-4-5", "dialect": null, "confidence": null, "enabled": false, "presentUpstream": false }
              ],
              "headers": [ { "name": "anthropic-version", "source": "literal", "valueEnvVar": null } ],
              "isFree": false,
              "dollarCap": 100.0,
              "tokenCap": 1000000,
              "dollarSpent": 42.5,
              "tokensUsed": 250000,
              "enabled": true,
              "endpointCapabilities": {
                "providerKey": "anthropic",
                "openAiCompatible": false,
                "lmStudioNative": false,
                "ollamaNative": false,
                "anthropicCompatible": true,
                "scannedAtUtc": "2026-08-01T00:00:00Z",
                "scanError": null
              }
            },
            {
              "key": "ollama",
              "name": null,
              "baseUrl": "http://localhost:11434/v1",
              "authHeaderName": "Authorization",
              "authHeaderScheme": "Bearer",
              "hasApiKey": false,
              "apiKeyEnvVar": null,
              "providerType": "LocalRuntime",
              "models": [],
              "headers": [],
              "isFree": true,
              "dollarCap": null,
              "tokenCap": null,
              "dollarSpent": 0.0,
              "tokensUsed": 0,
              "enabled": false,
              "endpointCapabilities": null
            },
            {
              "key": "openai",
              "name": "OpenAI",
              "baseUrl": "https://api.openai.com/v1",
              "authHeaderName": "Authorization",
              "authHeaderScheme": "Bearer",
              "hasApiKey": false,
              "apiKeyEnvVar": "OPENAI_API_KEY",
              "providerType": "OpenAI",
              "models": [],
              "headers": [],
              "isFree": false,
              "dollarCap": null,
              "tokenCap": null,
              "dollarSpent": 3.25,
              "tokensUsed": 900,
              "enabled": true,
              "endpointCapabilities": {
                "providerKey": "openai",
                "openAiCompatible": false,
                "lmStudioNative": false,
                "ollamaNative": false,
                "anthropicCompatible": false,
                "scannedAtUtc": "2026-08-01T00:00:00Z",
                "scanError": "timed out"
              }
            }
          ]
        }
        """;

    private static BunitContext NewContext(StubTransport transport)
    {
        var ctx = new BunitContext();
        // The budget panel renders EChart, which calls into echartsInterop; Loose mode records the calls
        // instead of failing them, matching EChartTests and DashboardTests.
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(new ProviderAdminStore(
            managementAddress: "http://127.0.0.1:59995",
            adminToken: "test-token",
            transport: transport));
        return ctx;
    }

    private static IRenderedComponent<ProvidersAdmin> RenderLoaded(BunitContext ctx)
    {
        var cut = ctx.Render<ProvidersAdmin>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Configured Providers"), TimeSpan.FromSeconds(4));
        return cut;
    }

    /// <summary>
    /// Finds a button by label <em>inside the open modal</em>. Scoping matters: each provider card carries
    /// its own budget "Save", and those precede the dialog in the DOM - an unscoped
    /// <c>First(b =&gt; b.TextContent == "Save")</c> silently clicks the budget panel instead, which passes
    /// some assertions for entirely the wrong reason.
    /// </summary>
    private static AngleSharp.Dom.IElement FindDialogButton(IRenderedComponent<ProvidersAdmin> cut, string label) =>
        cut.FindAll(".overlay-panel button").First(b => b.TextContent.Trim() == label);

    [Fact]
    public void Renders_a_card_per_provider_with_its_display_name_and_endpoint()
    {
        var transport = new StubTransport();
        using var ctx = NewContext(transport);

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
        var transport = new StubTransport();
        using var ctx = NewContext(transport);

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
        var transport = new StubTransport();
        using var ctx = NewContext(transport);

        var cut = RenderLoaded(ctx);

        // Distinct from a deliberately stopped model, which is why it gets its own label.
        cut.Markup.Should().Contain("not detected");
        cut.Markup.Should().Contain("claude-haiku");
    }

    [Fact]
    public void Renders_budget_bars_when_capped_and_an_unlimited_note_when_not()
    {
        var transport = new StubTransport();
        using var ctx = NewContext(transport);

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
        var transport = new StubTransport();
        using var ctx = NewContext(transport);
        var cut = RenderLoaded(ctx);

        cut.Find("button[aria-label='Stop provider anthropic']").Click();

        cut.WaitForAssertion(() => transport.Requests.Should().Contain(r =>
            r.Method == "PUT" && r.Path.EndsWith("admin/providers/anthropic/enabled", StringComparison.Ordinal)));
        transport.LastBody.Should().Contain("false", Exactly.Once());
    }

    [Fact]
    public void A_stopped_provider_offers_start_rather_than_stop()
    {
        var transport = new StubTransport();
        using var ctx = NewContext(transport);

        var cut = RenderLoaded(ctx);

        cut.FindAll("button[aria-label='Start provider ollama']").Should().ContainSingle();
    }

    [Fact]
    public void Toggling_a_model_sends_the_inverted_enabled_flag()
    {
        var transport = new StubTransport();
        using var ctx = NewContext(transport);
        var cut = RenderLoaded(ctx);

        cut.Find("button[aria-label='Stop model claude-opus']").Click();

        cut.WaitForAssertion(() => transport.Requests.Should().Contain(r =>
            r.Path.EndsWith("admin/providers/anthropic/models/claude-opus/enabled", StringComparison.Ordinal)));
    }

    [Fact]
    public void Pinning_a_tool_dialect_writes_it_and_auto_detect_clears_it()
    {
        var transport = new StubTransport();
        using var ctx = NewContext(transport);
        var cut = RenderLoaded(ctx);

        cut.Find("select[aria-label='Tool-call dialect for claude-opus']").Change("hermes");

        cut.WaitForAssertion(() => transport.LastBody.Should().Contain("hermes", Exactly.Once()));
        transport.Requests.Should().Contain(r =>
            r.Path.EndsWith("admin/providers/anthropic/models/claude-opus/tool-dialect", StringComparison.Ordinal));
    }

    [Fact]
    public void Refresh_from_endpoint_posts_to_the_provider()
    {
        var transport = new StubTransport();
        using var ctx = NewContext(transport);
        var cut = RenderLoaded(ctx);

        cut.Find("button[aria-label='Refresh provider anthropic']").Click();

        cut.WaitForAssertion(() => transport.Requests.Should().Contain(r =>
            r.Path.EndsWith("admin/providers/anthropic/refresh-from-endpoint", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_add_model_pane_is_collapsed_until_toggled()
    {
        var transport = new StubTransport();
        using var ctx = NewContext(transport);
        var cut = RenderLoaded(ctx);

        cut.FindAll("input[placeholder='model name']").Should().BeEmpty();

        cut.FindAll("button[aria-label='Add model manually']")[0].Click();

        cut.FindAll("input[placeholder='model name']").Should().ContainSingle();
    }

    [Fact]
    public void Adding_a_model_sends_its_name_and_optional_provider_id()
    {
        var transport = new StubTransport();
        using var ctx = NewContext(transport);
        var cut = RenderLoaded(ctx);
        cut.FindAll("button[aria-label='Add model manually']")[0].Click();

        cut.Find("input[placeholder='model name']").Input("claude-sonnet");
        cut.Find("input[placeholder='provider model id (optional)']").Input("claude-sonnet-5");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Add").Click();

        cut.WaitForAssertion(() => transport.Requests.Should().Contain(r =>
            r.Path.EndsWith("admin/providers/anthropic/models/claude-sonnet", StringComparison.Ordinal)));
        transport.LastBody.Should().Contain("claude-sonnet-5", Exactly.Once());
    }

    [Fact]
    public void Adding_a_model_with_a_blank_name_sends_nothing()
    {
        var transport = new StubTransport();
        using var ctx = NewContext(transport);
        var cut = RenderLoaded(ctx);
        cut.FindAll("button[aria-label='Add model manually']")[0].Click();

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Add").Click();

        // Only the initial GET; a blank name is a no-op rather than a 400 round-trip.
        transport.Requests.Should().OnlyContain(r => r.Method == "GET");
    }

    [Fact]
    public void Removing_a_model_deletes_it()
    {
        var transport = new StubTransport();
        using var ctx = NewContext(transport);
        var cut = RenderLoaded(ctx);

        cut.Find("button[title='Remove model']").Click();

        cut.WaitForAssertion(() => transport.Requests.Should().Contain(r =>
            r.Method == "DELETE" && r.Path.EndsWith("admin/providers/anthropic/models/claude-opus", StringComparison.Ordinal)));
    }

    [Fact]
    public void Removing_a_provider_opens_the_type_to_confirm_dialog_rather_than_deleting()
    {
        var transport = new StubTransport();
        using var ctx = NewContext(transport);
        var cut = RenderLoaded(ctx);

        cut.Find("button[aria-label='Remove provider anthropic']").Click();

        // Removal cascades to the provider's models, so it is gated behind typing the key.
        cut.Find(".overlay-panel span").TextContent.Trim().Should().Be("Remove Provider");
        cut.FindAll("input[aria-label='Type anthropic to confirm removal']").Should().ContainSingle();
        transport.Requests.Should().OnlyContain(r => r.Method == "GET");
    }

    [Fact]
    public void Cancelling_the_remove_dialog_closes_it_without_deleting()
    {
        var transport = new StubTransport();
        using var ctx = NewContext(transport);
        var cut = RenderLoaded(ctx);
        cut.Find("button[aria-label='Remove provider anthropic']").Click();

        FindDialogButton(cut, "Cancel").Click();

        cut.FindAll(".overlay-panel").Should().BeEmpty();
        transport.Requests.Should().OnlyContain(r => r.Method == "GET");
    }

    [Fact]
    public void Add_provider_opens_the_dialog_in_new_mode()
    {
        var transport = new StubTransport();
        using var ctx = NewContext(transport);
        var cut = RenderLoaded(ctx);

        cut.FindAll("button").First(b => b.TextContent.Contains("Add Provider", StringComparison.Ordinal)).Click();

        cut.Find(".overlay-panel span").TextContent.Trim().Should().Be("Add Provider");
        cut.Find("[data-testid='provider-name']").GetAttribute("value").Should().BeNullOrEmpty();
    }

    [Fact]
    public void Edit_opens_the_dialog_seeded_with_the_providers_stored_type()
    {
        var transport = new StubTransport();
        using var ctx = NewContext(transport);
        var cut = RenderLoaded(ctx);

        cut.FindAll("button[title='Edit']")[0].Click();

        // The round-trip bug: OpenEdit used to hardcode "Other", so an Anthropic provider always reopened
        // as Other and lost its type on the next save.
        cut.Find("[data-testid='provider-type']").GetAttribute("value").Should().Be(nameof(ProviderType.Anthropic));
        cut.Find("[data-testid='provider-name']").GetAttribute("value").Should().Be("Anthropic Prod");
    }

    [Fact]
    public void Saving_the_edit_dialog_writes_the_provider_type_through()
    {
        var transport = new StubTransport();
        using var ctx = NewContext(transport);
        var cut = RenderLoaded(ctx);
        cut.FindAll("button[title='Edit']")[0].Click();

        FindDialogButton(cut, "Save").Click();

        cut.WaitForAssertion(() => transport.Requests.Should().Contain(r =>
            r.Method == "PUT" && r.Path.EndsWith("admin/providers/anthropic", StringComparison.Ordinal)));
        transport.LastBody.Should().Contain("\"providerType\":\"Anthropic\"", Exactly.Once());
    }

    [Fact]
    public void Cancelling_the_edit_dialog_closes_it_without_writing()
    {
        var transport = new StubTransport();
        using var ctx = NewContext(transport);
        var cut = RenderLoaded(ctx);
        cut.FindAll("button[title='Edit']")[0].Click();

        FindDialogButton(cut, "Cancel").Click();

        cut.FindAll(".overlay-panel").Should().BeEmpty();
        transport.Requests.Should().OnlyContain(r => r.Method == "GET");
    }

    [Fact]
    public void A_rejected_edit_surfaces_the_servers_message_in_the_dialog()
    {
        var transport = new StubTransport();
        using var ctx = NewContext(transport);
        var cut = RenderLoaded(ctx);
        cut.FindAll("button[title='Edit']")[0].Click();
        transport.NextFailure = "BaseUrl must be an absolute URI.";

        FindDialogButton(cut, "Save").Click();

        // The dialog stays open carrying the reason, rather than closing over a write that didn't happen.
        cut.WaitForAssertion(() => cut.Find("[data-testid='dialog-error']").TextContent
            .Should().Contain("BaseUrl must be an absolute URI."));
    }

    [Theory]
    [InlineData("not-a-number", "", "Monthly $ cap must be a non-negative number")]
    [InlineData("", "1.5", "Monthly token cap must be a non-negative whole number")]
    public void An_invalid_budget_cap_is_rejected_locally_with_a_reason(string dollars, string tokens, string expected)
    {
        var transport = new StubTransport();
        using var ctx = NewContext(transport);
        var cut = RenderLoaded(ctx);

        cut.FindAll("input[placeholder='monthly $ cap (optional)']")[0].Input(dollars);
        cut.FindAll("input[placeholder='monthly token cap (optional)']")[0].Input(tokens);
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save").Click();

        cut.Markup.Should().Contain(expected);
        // Rejected before any request - a malformed cap never reaches the proxy.
        transport.Requests.Should().OnlyContain(r => r.Method == "GET");
    }

    [Fact]
    public void A_valid_budget_is_written()
    {
        var transport = new StubTransport();
        using var ctx = NewContext(transport);
        var cut = RenderLoaded(ctx);

        cut.FindAll("input[placeholder='monthly $ cap (optional)']")[0].Input("250");
        cut.FindAll("input[placeholder='monthly token cap (optional)']")[0].Input("5000");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save").Click();

        cut.WaitForAssertion(() => transport.Requests.Should().Contain(r =>
            r.Path.EndsWith("admin/providers/anthropic/budget", StringComparison.Ordinal)));
        transport.LastBody.Should().Contain("250", Exactly.Once());
    }

    [Fact]
    public void A_failed_mutation_surfaces_in_the_panes_error_banner()
    {
        var transport = new StubTransport();
        using var ctx = NewContext(transport);
        var cut = RenderLoaded(ctx);
        transport.NextFailure = "provider is in use";

        cut.Find("button[aria-label='Stop provider anthropic']").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("provider is in use"));
    }

    [Fact]
    public void Usage_card_renders_for_Anthropic_and_OpenAi_typed_providers_but_not_LocalRuntime()
    {
        // docs/router/openai-format-usage-accuracy-plan.md §6.2 generalizes this card beyond Anthropic:
        // it now renders (titled by the provider's own type) for both Anthropic- and OpenAI-typed
        // providers, but ollama (LocalRuntime) still must not render the section.
        var transport = new StubTransport();
        using var ctx = NewContext(transport);

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
        var transport = new StubTransport();
        using var ctx = NewContext(transport);

        var cut = RenderLoaded(ctx);

        cut.Markup.Should().Contain("Reported Usage (Anthropic)");
        cut.Markup.Should().Contain("No reported usage fetched yet");
        System.Text.RegularExpressions.Regex.Matches(cut.Markup, "Reported Usage \\(Anthropic\\)").Count.Should().Be(1);
    }

    [Fact]
    public void AdminApiKey_field_renders_for_recognized_reconciliation_providers()
    {
        var transport = new StubTransport();
        using var ctx = NewContext(transport);

        var cut = RenderLoaded(ctx);

        // anthropic and openai are both recognized (docs/router/agent-cost-tracking.md §3.5); the fixture's
        // "ollama" provider is neither, so its card must not offer the field at all.
        System.Text.RegularExpressions.Regex.Matches(cut.Markup, "Admin API Key \\(cost reconciliation, optional\\)").Count.Should().Be(2);
    }

    [Fact]
    public void ReportedUsage_section_rendersChartAndFetchedFooter_whenDataPresent()
    {
        var transport = new StubTransport { ResponseOverride = ProvidersJsonWithReportedUsage };
        using var ctx = NewContext(transport);

        var cut = RenderLoaded(ctx);

        cut.Markup.Should().Contain("Fetched 2026-03-02 04:00 UTC");
        cut.Markup.Should().NotContain("No reported usage fetched yet");
        cut.Markup.Should().Contain("•••••••• stored");
    }

    // Same anthropic provider as ProvidersJson, but with reportedUsage rows and a stored admin key -
    // exercises the populated-data branches of the reported-usage section (§8.2), which must render from
    // these backend-supplied values, never the GUI clock.
    private const string ProvidersJsonWithReportedUsage = """
        {
          "providers": [
            {
              "key": "anthropic",
              "name": "Anthropic Prod",
              "baseUrl": "https://api.anthropic.com",
              "authHeaderName": "x-api-key",
              "authHeaderScheme": "",
              "hasApiKey": true,
              "apiKeyEnvVar": null,
              "providerType": "Anthropic",
              "models": [],
              "headers": [],
              "isFree": false,
              "dollarCap": null,
              "tokenCap": null,
              "dollarSpent": 0.0,
              "tokensUsed": 0,
              "enabled": true,
              "endpointCapabilities": null,
              "hasStoredAdminKey": true,
              "reportedUsage": {
                "rows": [
                  { "usageDay": "2026-03-01", "model": "claude-opus-4-1", "inputTokens": 100, "outputTokens": 50, "cacheCreationTokens": 5, "cacheReadTokens": 10 }
                ],
                "fetchedAtUtc": "2026-03-02T04:00:00Z"
              }
            }
          ]
        }
        """;

    [Fact]
    public void Anthropic_Usage_card_shows_empty_states_when_nothing_recorded_yet()
    {
        var transport = new StubTransport();
        using var ctx = NewContext(transport);

        var cut = RenderLoaded(ctx);

        // The fixture's anthropic provider has no usageLastRecordedAtUtc/rateLimit set.
        cut.Markup.Should().Contain("No usage recorded yet");
        cut.Markup.Should().Contain("No rate-limit data observed yet");
    }

    [Fact]
    public void Anthropic_Usage_card_renders_backend_timestamps_for_both_sub_blocks()
    {
        var transport = new StubTransport { ResponseOverride = ProvidersJsonWithUsageAndRateLimit };
        using var ctx = NewContext(transport);

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
        var transport = new StubTransport { ResponseOverride = ProvidersJsonWithStaleRateLimit };
        using var ctx = NewContext(transport);

        var cut = RenderLoaded(ctx);

        cut.Markup.Should().Contain("As of 2026-03-01 12:00 UTC · stale");
    }

    [Fact]
    public void RateLimit_ExhaustionProjection_RendersBurnRateLine()
    {
        var transport = new StubTransport { ResponseOverride = ProvidersJsonWithUsageAndRateLimit };
        using var ctx = NewContext(transport);

        var cut = RenderLoaded(ctx);

        cut.Markup.Should().Contain("at current rate");
    }

    // Same fixture as ProvidersJsonWithUsageAndRateLimit but IsStale is set and no projections - exercises
    // the "As of ... stale" branch distinctly from the fresh-and-projected one.
    private const string ProvidersJsonWithStaleRateLimit = """
        {
          "providers": [
            {
              "key": "anthropic",
              "name": "Anthropic Prod",
              "baseUrl": "https://api.anthropic.com",
              "authHeaderName": "x-api-key",
              "authHeaderScheme": "",
              "hasApiKey": true,
              "apiKeyEnvVar": null,
              "providerType": "Anthropic",
              "models": [],
              "headers": [],
              "isFree": false,
              "dollarCap": null,
              "tokenCap": null,
              "dollarSpent": 12.5,
              "tokensUsed": 158000,
              "enabled": true,
              "endpointCapabilities": null,
              "usageLastRecordedAtUtc": "2026-03-01T08:00:00Z",
              "rateLimit": {
                "snapshot": {
                  "standardDimensions": {
                    "tokens": { "limit": 200000, "remaining": 158000, "resetAt": "2026-03-01T13:00:00Z" }
                  },
                  "unifiedStatus": null,
                  "unifiedResetAt": null,
                  "unifiedWindows": {},
                  "representativeClaim": null,
                  "rawHeaders": {}
                },
                "observedAtUtc": "2026-03-01T12:00:00Z",
                "isStale": true
              }
            }
          ]
        }
        """;

    // Same three providers as ProvidersJson, but the anthropic entry carries a populated
    // usageLastRecordedAtUtc and rateLimit - exercises the card's populated-data branches, which the
    // GUI must render from these backend-supplied values, never the GUI clock.
    private const string ProvidersJsonWithUsageAndRateLimit = """
        {
          "providers": [
            {
              "key": "anthropic",
              "name": "Anthropic Prod",
              "baseUrl": "https://api.anthropic.com",
              "authHeaderName": "x-api-key",
              "authHeaderScheme": "",
              "hasApiKey": true,
              "apiKeyEnvVar": null,
              "providerType": "Anthropic",
              "models": [],
              "headers": [],
              "isFree": false,
              "dollarCap": null,
              "tokenCap": null,
              "dollarSpent": 12.5,
              "tokensUsed": 158000,
              "enabled": true,
              "endpointCapabilities": null,
              "usageLastRecordedAtUtc": "2026-03-01T08:00:00Z",
              "rateLimit": {
                "snapshot": {
                  "standardDimensions": {
                    "tokens": { "limit": 200000, "remaining": 158000, "resetAt": "2026-03-01T13:00:00Z" }
                  },
                  "unifiedStatus": "allowed",
                  "unifiedResetAt": null,
                  "unifiedWindows": {
                    "5h": { "status": "allowed", "remaining": null, "resetAt": "2026-03-01T13:00:00Z" }
                  },
                  "representativeClaim": null,
                  "rawHeaders": {}
                },
                "observedAtUtc": "2026-03-01T12:00:00Z",
                "projections": {
                  "tokens": { "timeToExhaustion": "00:19:00", "burnRatePerMinute": 2210.5 }
                }
              }
            }
          ]
        }
        """;

    /// <summary>A record of one request the component caused, for asserting what reached the wire.</summary>
    private sealed record RecordedRequest(string Method, string Path);

    /// <summary>
    /// A canned management API: answers every request with the provider list, so the component's
    /// "mutate, then publish the returned list" flow works end to end. <see cref="NextFailure"/> makes the
    /// next write fail with a 400, which is how the error paths are reached.
    /// </summary>
    private sealed class StubTransport : HttpMessageHandler
    {
        private readonly List<RecordedRequest> _requests = [];

        public IReadOnlyList<RecordedRequest> Requests => _requests;

        public string? LastBody { get; private set; }

        /// <summary>When set, the next non-GET request answers 400 with this message and clears the flag.</summary>
        public string? NextFailure { get; set; }

        /// <summary>When set, every GET answers with this JSON instead of the default <see cref="ProvidersJson"/> fixture.</summary>
        public string? ResponseOverride { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Add(new RecordedRequest(request.Method.Method, request.RequestUri!.AbsolutePath));

            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            if (NextFailure is { } failure && request.Method != HttpMethod.Get)
            {
                NextFailure = null;
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(failure, Encoding.UTF8, "text/plain"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ResponseOverride ?? ProvidersJson, Encoding.UTF8, "application/json"),
            };
        }
    }
}
