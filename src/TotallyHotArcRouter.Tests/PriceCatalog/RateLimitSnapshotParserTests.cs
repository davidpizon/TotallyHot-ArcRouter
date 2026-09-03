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
            new RateLimitHeaderRow(HeaderName: "anthropic-ratelimit-requests-limit", HeaderValue: "50"),
            new RateLimitHeaderRow(HeaderName: "anthropic-ratelimit-requests-remaining", HeaderValue: "49"),
            new RateLimitHeaderRow(HeaderName: "anthropic-ratelimit-requests-reset",
                HeaderValue: "2026-03-01T12:00:00Z"),
            new RateLimitHeaderRow(HeaderName: "anthropic-ratelimit-input-tokens-limit", HeaderValue: "200000"),
            new RateLimitHeaderRow(HeaderName: "anthropic-ratelimit-input-tokens-remaining", HeaderValue: "150000"),
            new RateLimitHeaderRow(HeaderName: "anthropic-ratelimit-output-tokens-remaining", HeaderValue: "8000"),
            new RateLimitHeaderRow(HeaderName: "anthropic-ratelimit-tokens-remaining", HeaderValue: "158000")
        };

        var view = RateLimitSnapshotParser.Parse(rows);

        Assert.Equal(4, actual: view.StandardDimensions.Count);
        var requests = view.StandardDimensions["requests"];
        Assert.Equal(50, actual: requests.Limit);
        Assert.Equal(49, actual: requests.Remaining);
        Assert.Equal(expected: DateTimeOffset.Parse("2026-03-01T12:00:00Z"), actual: requests.ResetAt);

        var inputTokens = view.StandardDimensions["input-tokens"];
        Assert.Equal(200000, actual: inputTokens.Limit);
        Assert.Equal(150000, actual: inputTokens.Remaining);
        Assert.Null(inputTokens.ResetAt);

        Assert.Equal(8000, actual: view.StandardDimensions["output-tokens"].Remaining);
        Assert.Equal(158000, actual: view.StandardDimensions["tokens"].Remaining);
    }

    [Fact]
    public void Parse_UnifiedFamily_PopulatesTopLevelAndWindows()
    {
        var rows = new[]
        {
            new RateLimitHeaderRow(HeaderName: "anthropic-ratelimit-unified-status", HeaderValue: "allowed"),
            new RateLimitHeaderRow(HeaderName: "anthropic-ratelimit-unified-reset",
                HeaderValue: "2026-03-01T17:00:00Z"),
            new RateLimitHeaderRow(HeaderName: "anthropic-ratelimit-unified-5h-status", HeaderValue: "allowed"),
            new RateLimitHeaderRow(HeaderName: "anthropic-ratelimit-unified-5h-remaining", HeaderValue: "42"),
            new RateLimitHeaderRow(HeaderName: "anthropic-ratelimit-unified-5h-reset",
                HeaderValue: "2026-03-01T13:00:00Z"),
            new RateLimitHeaderRow(HeaderName: "anthropic-ratelimit-unified-7d-status", HeaderValue: "allowed"),
            new RateLimitHeaderRow(HeaderName: "anthropic-ratelimit-unified-representative-claim",
                HeaderValue: "org-123")
        };

        var view = RateLimitSnapshotParser.Parse(rows);

        Assert.Equal(expected: "allowed", actual: view.UnifiedStatus);
        Assert.Equal(expected: DateTimeOffset.Parse("2026-03-01T17:00:00Z"), actual: view.UnifiedResetAt);
        Assert.Equal(expected: "org-123", actual: view.RepresentativeClaim);

        Assert.Equal(2, actual: view.UnifiedWindows.Count);
        var fiveHour = view.UnifiedWindows["5h"];
        Assert.Equal(expected: "allowed", actual: fiveHour.Status);
        Assert.Equal(42, actual: fiveHour.Remaining);
        Assert.Equal(expected: DateTimeOffset.Parse("2026-03-01T13:00:00Z"), actual: fiveHour.ResetAt);

        Assert.Equal(expected: "allowed", actual: view.UnifiedWindows["7d"].Status);
    }

    [Fact]
    public void Parse_MixedStandardAndUnified_PopulatesBothFamiliesIndependently()
    {
        var rows = new[]
        {
            new RateLimitHeaderRow(HeaderName: "anthropic-ratelimit-tokens-remaining", HeaderValue: "1000"),
            new RateLimitHeaderRow(HeaderName: "anthropic-ratelimit-unified-status", HeaderValue: "allowed")
        };

        var view = RateLimitSnapshotParser.Parse(rows);

        Assert.True(view.StandardDimensions.ContainsKey("tokens"));
        Assert.Equal(expected: "allowed", actual: view.UnifiedStatus);
    }

    [Fact]
    public void Parse_MalformedNumericAndDateValues_SurfaceAsNullButRawHeaderIsKept()
    {
        var rows = new[]
        {
            new RateLimitHeaderRow(HeaderName: "anthropic-ratelimit-tokens-remaining", HeaderValue: "not-a-number"),
            new RateLimitHeaderRow(HeaderName: "anthropic-ratelimit-requests-reset", HeaderValue: "not-a-date")
        };

        var view = RateLimitSnapshotParser.Parse(rows);

        Assert.Null(view.StandardDimensions["tokens"].Remaining);
        Assert.Null(view.StandardDimensions["requests"].ResetAt);
        Assert.Equal(expected: "not-a-number", actual: view.RawHeaders["anthropic-ratelimit-tokens-remaining"]);
        Assert.Equal(expected: "not-a-date", actual: view.RawHeaders["anthropic-ratelimit-requests-reset"]);
    }

    [Fact]
    public void Parse_UnrecognizedHeader_IsKeptInRawHeadersOnly()
    {
        var rows = new[]
            { new RateLimitHeaderRow(HeaderName: "anthropic-ratelimit-some-future-thing", HeaderValue: "value") };

        var view = RateLimitSnapshotParser.Parse(rows);

        Assert.Empty(view.StandardDimensions);
        Assert.Empty(view.UnifiedWindows);
        Assert.Equal(expected: "value", actual: view.RawHeaders["anthropic-ratelimit-some-future-thing"]);
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

    [Fact]
    public void Parse_OpenAiFamily_PopulatesRequestsAndTokensDimensions()
    {
        var rows = new[]
        {
            new RateLimitHeaderRow(HeaderName: "x-ratelimit-limit-requests", HeaderValue: "5000"),
            new RateLimitHeaderRow(HeaderName: "x-ratelimit-remaining-requests", HeaderValue: "4999"),
            new RateLimitHeaderRow(HeaderName: "x-ratelimit-reset-requests", HeaderValue: "6ms"),
            new RateLimitHeaderRow(HeaderName: "x-ratelimit-limit-tokens", HeaderValue: "160000"),
            new RateLimitHeaderRow(HeaderName: "x-ratelimit-remaining-tokens", HeaderValue: "159975"),
            new RateLimitHeaderRow(HeaderName: "x-ratelimit-reset-tokens", HeaderValue: "6m0s")
        };
        var observedAt = DateTimeOffset.Parse("2026-03-01T12:00:00Z");

        var view = RateLimitSnapshotParser.Parse(rows: rows, observedAtUtc: observedAt);

        Assert.Equal(2, actual: view.StandardDimensions.Count);
        var requests = view.StandardDimensions["requests"];
        Assert.Equal(5000, actual: requests.Limit);
        Assert.Equal(4999, actual: requests.Remaining);
        Assert.Equal(expected: observedAt + TimeSpan.FromMilliseconds(6), actual: requests.ResetAt);

        var tokens = view.StandardDimensions["tokens"];
        Assert.Equal(160000, actual: tokens.Limit);
        Assert.Equal(159975, actual: tokens.Remaining);
        Assert.Equal(expected: observedAt + TimeSpan.FromMinutes(6), actual: tokens.ResetAt);
    }

    [Theory]
    [InlineData("1s", 1)]
    [InlineData("23h", 23 * 3600)]
    [InlineData("6m0s", 6 * 60)]
    [InlineData("1h2m3s", 3600 + 120 + 3)]
    public void Parse_OpenAiResetHeader_ParsesCompoundGoDurations(string durationText, double expectedSeconds)
    {
        var observedAt = DateTimeOffset.Parse("2026-03-01T00:00:00Z");
        var rows = new[] { new RateLimitHeaderRow(HeaderName: "x-ratelimit-reset-tokens", HeaderValue: durationText) };

        var view = RateLimitSnapshotParser.Parse(rows: rows, observedAtUtc: observedAt);

        Assert.Equal(expected: observedAt + TimeSpan.FromSeconds(expectedSeconds),
            actual: view.StandardDimensions["tokens"].ResetAt);
    }

    [Fact]
    public void Parse_OpenAiResetHeader_MalformedDuration_SurfacesAsNullResetAt()
    {
        var rows = new[]
            { new RateLimitHeaderRow(HeaderName: "x-ratelimit-reset-tokens", HeaderValue: "not-a-duration") };

        var view = RateLimitSnapshotParser.Parse(rows: rows, observedAtUtc: DateTimeOffset.UtcNow);

        Assert.Null(view.StandardDimensions["tokens"].ResetAt);
        Assert.Equal(expected: "not-a-duration", actual: view.RawHeaders["x-ratelimit-reset-tokens"]);
    }

    [Fact]
    public void Parse_OpenAiResetHeader_NoObservedAtSupplied_ResetAtStaysNull()
    {
        var rows = new[] { new RateLimitHeaderRow(HeaderName: "x-ratelimit-reset-tokens", HeaderValue: "6m0s") };

        var view = RateLimitSnapshotParser.Parse(rows);

        Assert.Null(view.StandardDimensions["tokens"].ResetAt);
    }

    [Fact]
    public void Parse_MixedAnthropicAndOpenAiHeaders_BothFamiliesCoexistInStandardDimensions()
    {
        var rows = new[]
        {
            new RateLimitHeaderRow(HeaderName: "anthropic-ratelimit-input-tokens-remaining", HeaderValue: "150000"),
            new RateLimitHeaderRow(HeaderName: "x-ratelimit-remaining-tokens", HeaderValue: "159975")
        };

        var view = RateLimitSnapshotParser.Parse(rows);

        Assert.Equal(150000, actual: view.StandardDimensions["input-tokens"].Remaining);
        Assert.Equal(159975, actual: view.StandardDimensions["tokens"].Remaining);
    }
}