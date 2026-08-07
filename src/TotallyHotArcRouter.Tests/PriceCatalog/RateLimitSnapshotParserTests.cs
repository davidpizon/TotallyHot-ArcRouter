using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.PriceCatalog;

/// <summary>Covers <see cref="RateLimitSnapshotParser.Parse"/>.</summary>
public class RateLimitSnapshotParserTests
{
    [Fact]
    public void Parse_StandardFamily_PopulatesDimensionsByName()
    {
        var rows = new[]
        {
            new RateLimitHeaderRow("anthropic-ratelimit-requests-limit", "50"),
            new RateLimitHeaderRow("anthropic-ratelimit-requests-remaining", "49"),
            new RateLimitHeaderRow("anthropic-ratelimit-requests-reset", "2026-03-01T12:00:00Z"),
            new RateLimitHeaderRow("anthropic-ratelimit-input-tokens-limit", "200000"),
            new RateLimitHeaderRow("anthropic-ratelimit-input-tokens-remaining", "150000"),
            new RateLimitHeaderRow("anthropic-ratelimit-output-tokens-remaining", "8000"),
            new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "158000"),
        };

        var view = RateLimitSnapshotParser.Parse(rows);

        Assert.Equal(4, view.StandardDimensions.Count);
        var requests = view.StandardDimensions["requests"];
        Assert.Equal(50, requests.Limit);
        Assert.Equal(49, requests.Remaining);
        Assert.Equal(DateTimeOffset.Parse("2026-03-01T12:00:00Z"), requests.ResetAt);

        var inputTokens = view.StandardDimensions["input-tokens"];
        Assert.Equal(200000, inputTokens.Limit);
        Assert.Equal(150000, inputTokens.Remaining);
        Assert.Null(inputTokens.ResetAt);

        Assert.Equal(8000, view.StandardDimensions["output-tokens"].Remaining);
        Assert.Equal(158000, view.StandardDimensions["tokens"].Remaining);
    }

    [Fact]
    public void Parse_UnifiedFamily_PopulatesTopLevelAndWindows()
    {
        var rows = new[]
        {
            new RateLimitHeaderRow("anthropic-ratelimit-unified-status", "allowed"),
            new RateLimitHeaderRow("anthropic-ratelimit-unified-reset", "2026-03-01T17:00:00Z"),
            new RateLimitHeaderRow("anthropic-ratelimit-unified-5h-status", "allowed"),
            new RateLimitHeaderRow("anthropic-ratelimit-unified-5h-remaining", "42"),
            new RateLimitHeaderRow("anthropic-ratelimit-unified-5h-reset", "2026-03-01T13:00:00Z"),
            new RateLimitHeaderRow("anthropic-ratelimit-unified-7d-status", "allowed"),
            new RateLimitHeaderRow("anthropic-ratelimit-unified-representative-claim", "org-123"),
        };

        var view = RateLimitSnapshotParser.Parse(rows);

        Assert.Equal("allowed", view.UnifiedStatus);
        Assert.Equal(DateTimeOffset.Parse("2026-03-01T17:00:00Z"), view.UnifiedResetAt);
        Assert.Equal("org-123", view.RepresentativeClaim);

        Assert.Equal(2, view.UnifiedWindows.Count);
        var fiveHour = view.UnifiedWindows["5h"];
        Assert.Equal("allowed", fiveHour.Status);
        Assert.Equal(42, fiveHour.Remaining);
        Assert.Equal(DateTimeOffset.Parse("2026-03-01T13:00:00Z"), fiveHour.ResetAt);

        Assert.Equal("allowed", view.UnifiedWindows["7d"].Status);
    }

    [Fact]
    public void Parse_MixedStandardAndUnified_PopulatesBothFamiliesIndependently()
    {
        var rows = new[]
        {
            new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "1000"),
            new RateLimitHeaderRow("anthropic-ratelimit-unified-status", "allowed"),
        };

        var view = RateLimitSnapshotParser.Parse(rows);

        Assert.True(view.StandardDimensions.ContainsKey("tokens"));
        Assert.Equal("allowed", view.UnifiedStatus);
    }

    [Fact]
    public void Parse_MalformedNumericAndDateValues_SurfaceAsNullButRawHeaderIsKept()
    {
        var rows = new[]
        {
            new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "not-a-number"),
            new RateLimitHeaderRow("anthropic-ratelimit-requests-reset", "not-a-date"),
        };

        var view = RateLimitSnapshotParser.Parse(rows);

        Assert.Null(view.StandardDimensions["tokens"].Remaining);
        Assert.Null(view.StandardDimensions["requests"].ResetAt);
        Assert.Equal("not-a-number", view.RawHeaders["anthropic-ratelimit-tokens-remaining"]);
        Assert.Equal("not-a-date", view.RawHeaders["anthropic-ratelimit-requests-reset"]);
    }

    [Fact]
    public void Parse_UnrecognizedHeader_IsKeptInRawHeadersOnly()
    {
        var rows = new[] { new RateLimitHeaderRow("anthropic-ratelimit-some-future-thing", "value") };

        var view = RateLimitSnapshotParser.Parse(rows);

        Assert.Empty(view.StandardDimensions);
        Assert.Empty(view.UnifiedWindows);
        Assert.Equal("value", view.RawHeaders["anthropic-ratelimit-some-future-thing"]);
    }

    [Fact]
    public void Parse_NoRows_ReturnsEmptyView()
    {
        var view = RateLimitSnapshotParser.Parse([]);

        Assert.Empty(view.StandardDimensions);
        Assert.Empty(view.UnifiedWindows);
        Assert.Null(view.UnifiedStatus);
        Assert.Null(view.RepresentativeClaim);
        Assert.Empty(view.RawHeaders);
    }
}
