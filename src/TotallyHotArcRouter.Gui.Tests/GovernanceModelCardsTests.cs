using System.Net;
using System.Text;
using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Services;
using Bunit;
using FluentAssertions;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="GovernanceModelCards"/>: one informational card per configured model, with real
/// spend from <see cref="UsageStore"/> and "Price unavailable" until a price-catalog channel exists (see
/// <c>docs/gui/governance-model-cards.md</c>).
/// </summary>
public sealed class GovernanceModelCardsTests
{
    private const string ProvidersJson = """
        {
          "providers": [
            {
              "key": "openai",
              "name": "OpenAI",
              "baseUrl": "https://api.openai.com/v1",
              "authHeaderName": "Authorization",
              "models": [
                { "modelName": "gpt-5.4", "providerModelId": "gpt-5-4-provider-id", "enabled": true, "presentUpstream": true }
              ],
              "headers": []
            }
          ]
        }
        """;

    private const string RollupJson = """
        [
          {
            "bucketStartUtc": "2026-08-01T00:00:00Z",
            "bucketWidth": "P1D",
            "groupKey": "gpt-5-4-provider-id",
            "requests": 3,
            "unpricedRequests": 0,
            "promptTokens": 1000,
            "completionTokens": 500,
            "cacheCreationTokens": 0,
            "cacheReadTokens": 0,
            "costUsd": 4.82
          }
        ]
        """;

    private sealed class StaticJsonTransport(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }

    private static BunitContext NewContext(bool withData)
    {
        var ctx = new BunitContext();
        if (withData)
        {
            ctx.Services.AddSingleton(new ProviderAdminStore(
                managementAddress: "http://127.0.0.1:59988", transport: new StaticJsonTransport(ProvidersJson)));
            ctx.Services.AddSingleton(new UsageStore(
                managementAddress: "http://127.0.0.1:59988", transport: new StaticJsonTransport(RollupJson)));
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

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("gpt-5.4");
            cut.Markup.Should().Contain("openai");
            cut.Markup.Should().Contain("$4.82");
            cut.Markup.Should().Contain("Price unavailable");
        }, TimeSpan.FromSeconds(4));
    }

    [Fact]
    public void Renders_unreachable_state_when_stores_cannot_reach_the_router()
    {
        using var ctx = NewContext(withData: false);

        var cut = ctx.Render<GovernanceModelCards>();

        // Distinct from "No models configured." - an unreachable proxy must never render as if there
        // were genuinely zero configured models (see GovernanceModelCards' ProviderStore.IsReachable /
        // UsageStore.IsReachable branch).
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Router unreachable");
            cut.Markup.Should().NotContain("No models configured.");
        }, TimeSpan.FromSeconds(6));
    }
}
