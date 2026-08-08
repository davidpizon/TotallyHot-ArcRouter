using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.PriceCatalog.Sources;

namespace TotallyHot.ArcRouter.Tests.PriceCatalog;

/// <summary>Covers <see cref="PriceCatalogRepository"/>'s upsert and freshness queries.</summary>
public class PriceCatalogRepositoryTests
{
    [Fact]
    public void UpsertPrices_SameModelTwoProviders_CoexistAsSeparateRows()
    {
        // The D7 composite key, asserted directly: a regression that keyed on the model alone would
        // silently collapse these two into one arbitrary winner.
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var repository = new PriceCatalogRepository(temp.Database);

        var written = repository.UpsertPrices(
            "litellm",
            priorityScore: 0,
            new[]
            {
                Price("llama-3-70b", "groq", input: 0.59m, output: 0.79m),
                Price("llama-3-70b", "together", input: 0.90m, output: 0.90m),
            },
            DateTimeOffset.UtcNow);

        Assert.Equal(2, written);

        var groq = repository.GetFreshPrice(new ModelKey("llama-3-70b", "groq"), TimeSpan.FromHours(24));
        var together = repository.GetFreshPrice(new ModelKey("llama-3-70b", "together"), TimeSpan.FromHours(24));

        Assert.NotNull(groq);
        Assert.NotNull(together);
        Assert.Equal(0.59m, groq!.InputPerMillionTokens);
        Assert.Equal(0.90m, together!.InputPerMillionTokens);
    }

    [Fact]
    public void UpsertPrices_SameCell_RefreshesInPlace()
    {
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var repository = new PriceCatalogRepository(temp.Database);

        repository.UpsertPrices("litellm", 0, new[] { Price("gpt-4o", "openai", 2.50m, 10.00m) }, DateTimeOffset.UtcNow);
        repository.UpsertPrices("litellm", 0, new[] { Price("gpt-4o", "openai", 3.00m, 12.00m) }, DateTimeOffset.UtcNow);

        Assert.Equal(1, repository.CountFreshPrices(TimeSpan.FromHours(24)));
        var price = repository.GetFreshPrice(new ModelKey("gpt-4o", "openai"), TimeSpan.FromHours(24));
        Assert.Equal(3.00m, price!.InputPerMillionTokens);
    }

    [Fact]
    public void GetFreshPrice_ReturnsNull_WhenRowIsOlderThanMaxAge()
    {
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var repository = new PriceCatalogRepository(temp.Database);

        repository.UpsertPrices(
            "litellm",
            0,
            new[] { Price("gpt-4o", "openai", 2.50m, 10.00m) },
            DateTimeOffset.UtcNow - TimeSpan.FromHours(48));

        Assert.Null(repository.GetFreshPrice(new ModelKey("gpt-4o", "openai"), TimeSpan.FromHours(24)));
        Assert.Equal(0, repository.CountFreshPrices(TimeSpan.FromHours(24)));
    }

    [Fact]
    public void GetFreshPrice_ReturnsNull_WhenKeyAbsent()
    {
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var repository = new PriceCatalogRepository(temp.Database);

        Assert.Null(repository.GetFreshPrice(new ModelKey("does-not-exist", "nowhere"), TimeSpan.FromHours(24)));
    }

    [Fact]
    public void GetFreshPrice_ReturnsNull_WhenStandardRatesAreNull()
    {
        // A row can exist for lineage while carrying no standard rates (e.g. batch-only). It is unpriced
        // for routing, so the fresh-price query must return null rather than a fabricated zero.
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var repository = new PriceCatalogRepository(temp.Database);

        repository.UpsertPrices(
            "litellm",
            0,
            new[] { new NormalizedPrice("odd-model", "openai", null, null, null, 1.0m, 2.0m) },
            DateTimeOffset.UtcNow);

        Assert.Null(repository.GetFreshPrice(new ModelKey("odd-model", "openai"), TimeSpan.FromHours(24)));
        // The row still counts toward the fresh-price total (it exists and is recent).
        Assert.Equal(1, repository.CountFreshPrices(TimeSpan.FromHours(24)));
    }

    [Fact]
    public void GetFreshPrice_ExcludesRowsOwnedByADisabledSource()
    {
        // D6's "neither polled nor served" half: a model priced only by a disabled source becomes unpriced
        // the moment it is switched off, rather than steering routing until its rows age out 24h later.
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        repository.UpsertPrices("litellm", 0, new[] { Price("gpt-4o", "openai", 2.50m, 10.00m) }, DateTimeOffset.UtcNow);

        Assert.NotNull(repository.GetFreshPrice(new ModelKey("gpt-4o", "openai"), TimeSpan.FromHours(24)));

        repository.SetSourceEnabled("litellm", enabled: false);

        Assert.Null(repository.GetFreshPrice(new ModelKey("gpt-4o", "openai"), TimeSpan.FromHours(24)));
    }

    [Fact]
    public void CountFreshPrices_ExcludesRowsOwnedByADisabledSource()
    {
        // Without this filter a disabled source's rows would suppress the zero-fresh-prices Error (D4),
        // reporting a healthy feed while nothing usable is actually being served.
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        repository.UpsertPrices("litellm", 0, new[] { Price("gpt-4o", "openai", 2.50m, 10.00m) }, DateTimeOffset.UtcNow);

        Assert.Equal(1, repository.CountFreshPrices(TimeSpan.FromHours(24)));

        repository.SetSourceEnabled("litellm", enabled: false);

        Assert.Equal(0, repository.CountFreshPrices(TimeSpan.FromHours(24)));
    }

    [Fact]
    public void ReEnablingASource_RestoresItsRowsImmediately()
    {
        // Re-enabling isn't special-cased: the rows never left, so they become visible again at once rather
        // than waiting for the next poll to rewrite them.
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        repository.UpsertPrices("litellm", 0, new[] { Price("gpt-4o", "openai", 2.50m, 10.00m) }, DateTimeOffset.UtcNow);

        repository.SetSourceEnabled("litellm", enabled: false);
        repository.SetSourceEnabled("litellm", enabled: true);

        Assert.NotNull(repository.GetFreshPrice(new ModelKey("gpt-4o", "openai"), TimeSpan.FromHours(24)));
    }

    [Fact]
    public void GetSourceStates_ListsDisabledSourcesToo()
    {
        // The opposite of GetFreshPrice: this describes the sources themselves, so a disabled one must still
        // be listed - otherwise the panel could never switch it back on.
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        repository.UpsertPrices("litellm", 0, new[] { Price("gpt-4o", "openai", 2.50m, 10.00m) }, DateTimeOffset.UtcNow);
        repository.SetSourceEnabled("litellm", enabled: false);

        var source = repository.GetSourceStates().Single(s => s.Name == "litellm");

        Assert.False(source.Enabled);
        Assert.Equal(1, source.PriceCount);
    }

    [Fact]
    public void SetSourceEnabled_UnknownSource_ReturnsFalse()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();

        // openpipe, not openrouter: openrouter is a real, seeded source now.
        Assert.False(repository.SetSourceEnabled("openpipe", enabled: true));
    }

    [Fact]
    public void UpsertPrices_DoesNotClobberOperatorOwnedColumns()
    {
        // The ingestion loop passes a default priorityScore every cycle. If the source upsert wrote it back,
        // every poll would silently reset a rank (and, by the same mistake, a toggle) the operator had set.
        using var temp = new TempDatabase();
        temp.SeedExtraSource("litellm", enabled: true, priorityScore: 7);
        var repository = temp.CreateRepository();

        repository.UpsertPrices("litellm", priorityScore: 0, new[] { Price("gpt-4o", "openai", 2.50m, 10.00m) }, DateTimeOffset.UtcNow);

        Assert.Equal(7, repository.GetSourceStates().Single(s => s.Name == "litellm").PriorityScore);
    }

    [Fact]
    public void UpsertPrices_HigherPrioritySourceWins_RegardlessOfWriteOrder()
    {
        // The core correctness fix: without the gate, whichever source polls LAST wins a contested cell,
        // which is exactly the "confidently wrong number" the whole priority design exists to prevent.
        using var temp = new TempDatabase();
        temp.SeedExtraSource("high", enabled: true, priorityScore: 10);
        temp.SeedExtraSource("low", enabled: true, priorityScore: 0);
        var repository = temp.CreateRepository();

        // The low-priority source writes SECOND, after the high-priority source - if the gate didn't exist,
        // last-writer-wins would let it clobber the better number.
        repository.UpsertPrices("high", 10, new[] { Price("gpt-4o", "openai", 2.50m, 10.00m) }, DateTimeOffset.UtcNow);
        var written = repository.UpsertPrices("low", 0, new[] { Price("gpt-4o", "openai", 999m, 999m) }, DateTimeOffset.UtcNow);

        Assert.Equal(0, written); // the gate rejected it - nothing was actually written
        var price = repository.GetFreshPrice(new ModelKey("gpt-4o", "openai"), TimeSpan.FromHours(24));
        Assert.Equal(2.50m, price!.InputPerMillionTokens);
    }

    [Fact]
    public void UpsertPrices_LowerPrioritySourceCannotClobberAHigherOnesCell_EvenWhenItArrivesLater()
    {
        using var temp = new TempDatabase();
        temp.SeedExtraSource("high", enabled: true, priorityScore: 10);
        temp.SeedExtraSource("low", enabled: true, priorityScore: 0);
        var repository = temp.CreateRepository();

        repository.UpsertPrices("low", 0, new[] { Price("gpt-4o", "openai", 999m, 999m) }, DateTimeOffset.UtcNow);
        var written = repository.UpsertPrices("high", 10, new[] { Price("gpt-4o", "openai", 2.50m, 10.00m) }, DateTimeOffset.UtcNow);

        // The higher-priority source arriving second must still win - "wins" cannot mean "wins only if it
        // happens to poll first".
        Assert.Equal(1, written);
        var price = repository.GetFreshPrice(new ModelKey("gpt-4o", "openai"), TimeSpan.FromHours(24));
        Assert.Equal(2.50m, price!.InputPerMillionTokens);
    }

    [Fact]
    public void UpsertPrices_EqualPriority_LetsASourceRefreshItsOwnRow()
    {
        // The ">=" in the gate, not ">": a source polling again must still be able to update its own previous
        // row. This is also what keeps single-source behavior unchanged - a source always ties with itself.
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();

        repository.UpsertPrices("litellm", 0, new[] { Price("gpt-4o", "openai", 2.50m, 10.00m) }, DateTimeOffset.UtcNow);
        var written = repository.UpsertPrices("litellm", 0, new[] { Price("gpt-4o", "openai", 3.00m, 12.00m) }, DateTimeOffset.UtcNow);

        Assert.Equal(1, written);
        var price = repository.GetFreshPrice(new ModelKey("gpt-4o", "openai"), TimeSpan.FromHours(24));
        Assert.Equal(3.00m, price!.InputPerMillionTokens);
    }

    [Fact]
    public void UpsertPrices_FirstWriteToACell_AlwaysSucceeds_RegardlessOfRank()
    {
        // There is no incumbent to compare against on a fresh INSERT, so even the lowest-ranked source must
        // be able to price a model nobody else has priced yet.
        using var temp = new TempDatabase();
        temp.SeedExtraSource("low", enabled: true, priorityScore: -100);
        var repository = temp.CreateRepository();

        var written = repository.UpsertPrices("low", -100, new[] { Price("only-low-prices-this", "openai", 1m, 2m) }, DateTimeOffset.UtcNow);

        Assert.Equal(1, written);
        Assert.NotNull(repository.GetFreshPrice(new ModelKey("only-low-prices-this", "openai"), TimeSpan.FromHours(24)));
    }

    [Fact]
    public void ReorderSources_RewritesContiguousScoresFromListPosition()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository(); // seeds litellm (0) and openrouter (-10)

        var reordered = repository.ReorderSources(
            [PriceCatalogOptions.OpenRouterSourceName, PriceCatalogOptions.LiteLlmSourceName]);

        Assert.True(reordered);
        var states = repository.GetSourceStates();
        var openRouter = states.Single(s => s.Name == PriceCatalogOptions.OpenRouterSourceName);
        var liteLlm = states.Single(s => s.Name == PriceCatalogOptions.LiteLlmSourceName);
        Assert.Equal(1, openRouter.PriorityScore);
        Assert.Equal(0, liteLlm.PriorityScore);
    }

    [Fact]
    public void ReorderSources_MissingASource_RejectsAndChangesNothing()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var before = repository.GetSourceStates().ToDictionary(s => s.Name, s => s.PriorityScore);

        var reordered = repository.ReorderSources([PriceCatalogOptions.LiteLlmSourceName]);

        // A partial list would leave the unlisted source's rank stale relative to the ones that moved -
        // rejected outright rather than applied best-effort.
        Assert.False(reordered);
        var after = repository.GetSourceStates().ToDictionary(s => s.Name, s => s.PriorityScore);
        Assert.Equal(before, after);
    }

    [Fact]
    public void ReorderSources_UnknownSourceName_Rejects()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();

        var reordered = repository.ReorderSources(
            [PriceCatalogOptions.LiteLlmSourceName, PriceCatalogOptions.OpenRouterSourceName, "openpipe"]);

        Assert.False(reordered);
    }

    [Fact]
    public void ReorderSources_DuplicateName_Rejects()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();

        var reordered = repository.ReorderSources(
            [PriceCatalogOptions.LiteLlmSourceName, PriceCatalogOptions.LiteLlmSourceName]);

        Assert.False(reordered);
    }

    [Fact]
    public void ReorderSources_TakesEffectOnTheNextUpsert()
    {
        // Reordering doesn't retroactively rewrite existing rows by itself - it changes the rank the NEXT
        // ingestion cycle's gate reads. The panel's auto-triggered refresh is what makes that immediate.
        using var temp = new TempDatabase();
        temp.SeedExtraSource("high", enabled: true, priorityScore: 10);
        temp.SeedExtraSource("low", enabled: true, priorityScore: 0);
        var repository = temp.CreateRepository();
        repository.UpsertPrices("high", 10, new[] { Price("gpt-4o", "openai", 2.50m, 10.00m) }, DateTimeOffset.UtcNow);

        Assert.True(repository.ReorderSources(["low", "high", PriceCatalogOptions.LiteLlmSourceName, PriceCatalogOptions.OpenRouterSourceName]));
        var written = repository.UpsertPrices("low", 0, new[] { Price("gpt-4o", "openai", 999m, 999m) }, DateTimeOffset.UtcNow);

        Assert.Equal(1, written);
        var price = repository.GetFreshPrice(new ModelKey("gpt-4o", "openai"), TimeSpan.FromHours(24));
        Assert.Equal(999m, price!.InputPerMillionTokens);
    }

    [Fact]
    public void UpsertPrices_TwoSourcesNamingOneModelDifferently_ResolveToOneCell_AndPriorityGateApplies()
    {
        // The D3 payoff: LiteLLM names the model "gpt-4o" and OpenRouter "openai/gpt-4o". Pre-D3 these landed
        // in two different `models` rows and never contested a cell (D7's "0 real collisions"); resolved onto
        // one configured identity, they now share a cell - which is the FIRST time the priority gate actually
        // arbitrates two different sources rather than a source against itself.
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated(); // seeds litellm (0) and openrouter (-10)
        var resolver = new StubIdentityResolver(new ResolvedModelIdentity("gpt-5.4", "openai"));
        var repository = new PriceCatalogRepository(temp.Database, resolver);

        repository.UpsertPrices("litellm", 0, new[] { Price("gpt-4o", "openai", 2.50m, 10.00m) }, DateTimeOffset.UtcNow);
        var written = repository.UpsertPrices("openrouter", -10, new[] { Price("openai/gpt-4o", "openai", 999m, 999m) }, DateTimeOffset.UtcNow);

        // openrouter (rank -10) cannot clobber litellm's (rank 0) number in the now-shared cell.
        Assert.Equal(0, written);

        // Resolves on the client-facing ModelName and carries litellm's price, not openrouter's.
        var price = repository.GetFreshPrice(new ModelKey("gpt-5.4", "openai"), TimeSpan.FromHours(24));
        Assert.NotNull(price);
        Assert.Equal(2.50m, price!.InputPerMillionTokens);

        // Nothing is stored under either raw source key anymore - both were resolved onto the one identity.
        Assert.Null(repository.GetFreshPrice(new ModelKey("gpt-4o", "openai"), TimeSpan.FromHours(24)));
        Assert.Null(repository.GetFreshPrice(new ModelKey("openai/gpt-4o", "openai"), TimeSpan.FromHours(24)));
    }

    [Fact]
    public void UpsertPrices_UnresolvedModel_FallsBackToRawSourceKeys()
    {
        // A resolver miss must not drop or mis-map the price: it is stored under the source's own keys exactly
        // as it was before D3, so an unmatched model stays unresolved-by-routing-key rather than disappearing.
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var repository = new PriceCatalogRepository(temp.Database, new StubIdentityResolver(null));

        repository.UpsertPrices("litellm", 0, new[] { Price("mystery-model", "openai", 1m, 2m) }, DateTimeOffset.UtcNow);

        Assert.NotNull(repository.GetFreshPrice(new ModelKey("mystery-model", "openai"), TimeSpan.FromHours(24)));
    }

    [Fact]
    public void UpsertPrices_ExactRungResolution_StoresPriceAsNotApproximate()
    {
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var resolver = new StubIdentityResolver(new ResolvedModelIdentity("gpt-5.4", "openai"), ResolutionRung.Exact);
        var repository = new PriceCatalogRepository(temp.Database, resolver);

        repository.UpsertPrices("litellm", 0, new[] { Price("gpt-4o", "openai", 2m, 6m) }, DateTimeOffset.UtcNow);

        var price = repository.GetFreshPrice(new ModelKey("gpt-5.4", "openai"), TimeSpan.FromHours(24));
        Assert.False(price!.IsApproximateMatch);
    }

    [Fact]
    public void UpsertPrices_ApproximateRungResolution_FlagsStoredPriceApproximate()
    {
        // §5.7: every rung below Exact/OperatorOverride marks the stored price approximate, so a later
        // lookup can report CostConfidence.CatalogApproximate rather than an unqualified Catalog.
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var resolver = new StubIdentityResolver(new ResolvedModelIdentity("gpt-5.4", "openai"), ResolutionRung.SnapshotSuffixStripped);
        var repository = new PriceCatalogRepository(temp.Database, resolver);

        repository.UpsertPrices("litellm", 0, new[] { Price("gpt-4o-20250101", "openai", 2m, 6m) }, DateTimeOffset.UtcNow);

        var price = repository.GetFreshPrice(new ModelKey("gpt-5.4", "openai"), TimeSpan.FromHours(24));
        Assert.True(price!.IsApproximateMatch);
    }

    [Fact]
    public void UpsertPrices_NoResolver_StoresPriceAsNotApproximate()
    {
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var repository = new PriceCatalogRepository(temp.Database);

        repository.UpsertPrices("litellm", 0, new[] { Price("gpt-4o", "openai", 2m, 6m) }, DateTimeOffset.UtcNow);

        var price = repository.GetFreshPrice(new ModelKey("gpt-4o", "openai"), TimeSpan.FromHours(24));
        Assert.False(price!.IsApproximateMatch);
    }

    // Returns a fixed identity (or null) regardless of input: this exercises the repository's *use* of a
    // resolver's output. The resolver's own matching logic is covered by ConfigModelIdentityResolverTests.
    private sealed class StubIdentityResolver(ResolvedModelIdentity? identity, ResolutionRung rung = ResolutionRung.Exact) : IModelIdentityResolver
    {
        public IdentityResolution? Resolve(string sourceName, string aggregatorModelId, string aggregatorProvider) =>
            identity is null ? null : new IdentityResolution(identity.Value, rung);
    }

    [Fact]
    public void GetFreshPrice_RoundTripsCacheReadAndCacheWriteRates()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();

        repository.UpsertPrices(
            "litellm",
            0,
            new[]
            {
                new NormalizedPrice(
                    "claude-opus", "anthropic",
                    StandardInputPrice: 15.00m, StandardOutputPrice: 75.00m,
                    CachedInputPrice: 1.50m, BatchInputPrice: null, BatchOutputPrice: null,
                    CacheWriteInputPrice: 18.75m),
            },
            DateTimeOffset.UtcNow);

        var price = repository.GetFreshPrice(new ModelKey("claude-opus", "anthropic"), TimeSpan.FromHours(24));

        Assert.NotNull(price);
        Assert.Equal(1.50m, price!.CacheReadPerMillionTokens);
        Assert.Equal(18.75m, price.CacheWritePerMillionTokens);
    }

    [Fact]
    public void GetFreshPrice_NoCacheRatesPublished_CacheFieldsAreNull()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();

        repository.UpsertPrices("litellm", 0, new[] { Price("gpt-4o", "openai", 2.50m, 10.00m) }, DateTimeOffset.UtcNow);

        var price = repository.GetFreshPrice(new ModelKey("gpt-4o", "openai"), TimeSpan.FromHours(24));

        Assert.NotNull(price);
        Assert.Null(price!.CacheReadPerMillionTokens);
        Assert.Null(price.CacheWritePerMillionTokens);
    }

    [Fact]
    public void AddProviderSpend_RepeatedCalls_AccumulatesCacheTokensAndAdvancesLastUsageAt()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var firstUsageAt = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var secondUsageAt = firstUsageAt.AddHours(1);

        repository.AddProviderSpend("anthropic", "2026-03", 1m, 10, 5, cacheCreationTokens: 100, cacheReadTokens: 200, usageAtUtc: firstUsageAt);
        repository.AddProviderSpend("anthropic", "2026-03", 2m, 20, 10, cacheCreationTokens: 50, cacheReadTokens: 25, usageAtUtc: secondUsageAt);

        var row = Assert.Single(repository.GetProviderSpend("2026-03"));
        Assert.Equal(150L, row.CacheCreationTokens);
        Assert.Equal(225L, row.CacheReadTokens);
        Assert.Equal(secondUsageAt, row.LastUsageAtUtc);
    }

    [Fact]
    public void UpsertRateLimitHeaders_EmptyList_IsNoOp()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();

        repository.UpsertRateLimitHeaders("anthropic", [], DateTimeOffset.UtcNow);

        var (headers, observedAt) = repository.GetRateLimitSnapshot("anthropic");
        Assert.Empty(headers);
        Assert.Null(observedAt);
    }

    [Fact]
    public void UpsertRateLimitHeaders_UpsertsLatestValuePerHeader()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var first = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        var second = first.AddMinutes(1);

        repository.UpsertRateLimitHeaders(
            "anthropic",
            [new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "1000")],
            first);
        repository.UpsertRateLimitHeaders(
            "anthropic",
            [new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "500")],
            second);

        var (headers, observedAt) = repository.GetRateLimitSnapshot("anthropic");
        var row = Assert.Single(headers);
        Assert.Equal("500", row.HeaderValue);
        Assert.Equal(second, observedAt);
    }

    [Fact]
    public void UpsertRateLimitHeaders_HeaderNameIsLowercased()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();

        repository.UpsertRateLimitHeaders(
            "anthropic",
            [new RateLimitHeaderRow("Anthropic-Ratelimit-Requests-Limit", "50")],
            DateTimeOffset.UtcNow);

        var (headers, _) = repository.GetRateLimitSnapshot("anthropic");
        Assert.Equal("anthropic-ratelimit-requests-limit", Assert.Single(headers).HeaderName);
    }

    [Fact]
    public void GetRateLimitSnapshot_UnknownProvider_ReturnsEmptyAndNullObservedAt()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();

        var (headers, observedAt) = repository.GetRateLimitSnapshot("does-not-exist");

        Assert.Empty(headers);
        Assert.Null(observedAt);
    }

    [Fact]
    public void UpsertRateLimitHeaders_History_DedupesWithinTheSameMinuteBucket()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var database = temp.Database;
        var timestamp = new DateTimeOffset(2026, 3, 1, 12, 0, 30, TimeSpan.Zero);
        var laterSameMinute = timestamp.AddSeconds(20);

        repository.UpsertRateLimitHeaders(
            "anthropic",
            [new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "1000")],
            timestamp);
        repository.UpsertRateLimitHeaders(
            "anthropic",
            [new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "999")],
            laterSameMinute);

        Assert.Equal(1, CountHistoryRows(database, "anthropic"));
    }

    [Fact]
    public void UpsertRateLimitHeaders_History_AddsARowForANewMinuteBucket()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var database = temp.Database;
        var first = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        var nextMinute = first.AddMinutes(1);

        repository.UpsertRateLimitHeaders(
            "anthropic",
            [new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "1000")],
            first);
        repository.UpsertRateLimitHeaders(
            "anthropic",
            [new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "999")],
            nextMinute);

        Assert.Equal(2, CountHistoryRows(database, "anthropic"));
    }

    [Fact]
    public void UpsertRateLimitHeaders_History_PrunesRowsOlderThan30Days()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var database = temp.Database;
        var old = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var recent = old.AddDays(31);

        repository.UpsertRateLimitHeaders(
            "anthropic",
            [new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "1000")],
            old);
        Assert.Equal(1, CountHistoryRows(database, "anthropic"));

        // The write itself carries the pruning: a capture more than 30 days after the old row is what
        // triggers its removal, not a background job.
        repository.UpsertRateLimitHeaders(
            "anthropic",
            [new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "999")],
            recent);

        Assert.Equal(1, CountHistoryRows(database, "anthropic"));
    }

    [Fact]
    public void GetRateLimitHistory_ReturnsBucketsChronologicallyWithOnlyCapturedHeaders()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var first = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        var second = first.AddMinutes(1);

        repository.UpsertRateLimitHeaders(
            "anthropic",
            [new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "1000")],
            first);
        repository.UpsertRateLimitHeaders(
            "anthropic",
            [new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "900")],
            second);

        var buckets = repository.GetRateLimitHistory("anthropic", first.AddMinutes(-5));

        Assert.Equal(2, buckets.Count);
        Assert.Equal(first, buckets[0].BucketUtc);
        Assert.Single(buckets[0].Headers);
        Assert.Equal("1000", buckets[0].Headers[0].HeaderValue);
        Assert.Equal(second, buckets[1].BucketUtc);
        Assert.Equal("900", buckets[1].Headers[0].HeaderValue);
    }

    [Fact]
    public void GetRateLimitHistory_ExcludesBucketsBeforeSince()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var old = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        var recent = old.AddMinutes(10);

        repository.UpsertRateLimitHeaders(
            "anthropic",
            [new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "1000")],
            old);
        repository.UpsertRateLimitHeaders(
            "anthropic",
            [new RateLimitHeaderRow("anthropic-ratelimit-tokens-remaining", "900")],
            recent);

        var buckets = repository.GetRateLimitHistory("anthropic", old.AddMinutes(5));

        Assert.Single(buckets);
        Assert.Equal(recent, buckets[0].BucketUtc);
    }

    [Fact]
    public void GetRateLimitHistory_UnknownProvider_ReturnsEmpty()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();

        var buckets = repository.GetRateLimitHistory("does-not-exist", DateTimeOffset.UtcNow.AddDays(-1));

        Assert.Empty(buckets);
    }

    private static int CountHistoryRows(PriceCatalogDatabase database, string providerKey)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM provider_rate_limit_history WHERE provider_key = $key;";
        command.Parameters.AddWithValue("$key", providerKey);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static NormalizedPrice Price(string model, string provider, decimal input, decimal output) =>
        new(model, provider, input, output, CachedInputPrice: null, BatchInputPrice: null, BatchOutputPrice: null);
}

