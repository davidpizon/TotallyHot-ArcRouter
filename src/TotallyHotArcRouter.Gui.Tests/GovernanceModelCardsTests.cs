using AwesomeAssertions;
using Bunit;
using Google.Protobuf.WellKnownTypes;
using TotallyHot.ArcRouter.Gui.Admin;
using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Services;
using Contract = TotallyHot.ArcRouter.Admin.Contract;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="GovernanceModelCards"/>: one informational card per configured model, with real
/// spend from <see cref="UsageStore"/> and "Price unavailable" until a price-catalog channel exists (see
/// <c>docs/gui/governance-model-cards.md</c>).
/// </summary>
public sealed class GovernanceModelCardsTests
{
    private static Contract.ProviderListResponse ProvidersResponse()
    {
        var response = new Contract.ProviderListResponse();
        response.Providers.Add(new Contract.ProviderState
        {
            Key = "openai", Name = "OpenAI", BaseUrl = "https://api.openai.com/v1", AuthHeaderName = "Authorization",
            DollarSpent = "0", WindowKind = "Monthly", Enabled = true,
            Models = { new Contract.ModelState { ModelName = "gpt-5.4", ProviderModelId = "gpt-5-4-provider-id", Enabled = true, PresentUpstream = true } }
        });
        return response;
    }

    private static Contract.UsageRollupResponse RollupResponse()
    {
        var response = new Contract.UsageRollupResponse();
        response.Buckets.Add(new Contract.UsageRollupBucketRow
        {
            BucketStartUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-01T00:00:00Z")),
            BucketWidth = "P1D",
            GroupKey = "gpt-5-4-provider-id",
            Requests = 3,
            UnpricedRequests = 0,
            PromptTokens = 1000,
            CompletionTokens = 500,
            CacheCreationTokens = 0,
            CacheReadTokens = 0,
            CostUsd = "4.82"
        });
        return response;
    }

    private static BunitContext NewContext(bool withData)
    {
        var ctx = new BunitContext();
        if (withData)
        {
            ctx.Services.AddSingleton(new ProviderAdminStore(client: new ProviderAdminClient(
                new StubProviderAdminServiceClient { ListProvidersResponse = ProvidersResponse() })));
            ctx.Services.AddSingleton(new UsageStore(client: new UsageQueryClient(
                new StubUsageAdminServiceClient { RollupResponse = RollupResponse() })));
        }
        else
        {
            // Unreachable addresses - the store's own LoadAsync/LoadRollupAsync resolve to their
            // "unreachable" branch, exercising the empty-state markup.
            ctx.Services.AddSingleton(new ProviderAdminStore(managementAddress: "http://127.0.0.1:59987"));
            ctx.Services.AddSingleton(new UsageStore(managementAddress: "http://127.0.0.1:59987"));
        }

        return ctx;
    }

    [Fact]
    public void Renders_a_card_with_real_spend_and_unavailable_price()
    {
        using var ctx = NewContext(withData: true);

        var cut = ctx.Render<GovernanceModelCards>();

        cut.WaitForAssertion(assertion: () =>
        {
            cut.Markup.Should().Contain("gpt-5.4");
            cut.Markup.Should().Contain("openai");
            cut.Markup.Should().Contain("$4.82");
            cut.Markup.Should().Contain("Price unavailable");
        }, timeout: TimeSpan.FromSeconds(4));
    }

    [Fact]
    public void Renders_unreachable_state_when_stores_cannot_reach_the_router()
    {
        using var ctx = NewContext(withData: false);

        var cut = ctx.Render<GovernanceModelCards>();

        // Distinct from "No models configured." - an unreachable proxy must never render as if there
        // were genuinely zero configured models (see GovernanceModelCards' ProviderStore.IsReachable /
        // UsageStore.IsReachable branch).
        cut.WaitForAssertion(assertion: () =>
        {
            cut.Markup.Should().Contain("Router unreachable");
            cut.Markup.Should().NotContain("No models configured.");
        }, timeout: TimeSpan.FromSeconds(6));
    }

}