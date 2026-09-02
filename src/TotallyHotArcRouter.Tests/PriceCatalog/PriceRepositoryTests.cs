using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.PriceCatalog.Sources;

namespace TotallyHot.ArcRouter.Tests.PriceCatalog;

/// <summary>Covers <see cref="PriceRepository"/>'s upsert and freshness queries.</summary>
public class PriceRepositoryTests
{
    [Fact]
    public void UpsertPrices_SameModelTwoProviders_CoexistAsSeparateRows()
    {
        // The D7 composite key, asserted directly: a regression that keyed on the model alone would
        // silently collapse these two into one arbitrary winner.
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var repository = new PriceRepository(temp.Database);

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
        var repository = new PriceRepository(temp.Database);
        var sourceRepository = new PriceSourceRepository(temp.Database);

        repository.UpsertPrices("litellm", 0, new[] { Price("gpt-4o", "openai", 2.50m, 10.00m) }, DateTimeOffset.UtcNow);
        repository.UpsertPrices("litellm", 0, new[] { Price("gpt-4o", "openai", 3.00m, 12.00m) }, DateTimeOffset.UtcNow);

        Assert.Equal(1, sourceRepository.CountFreshPrices(TimeSpan.FromHours(24)));
        var price = repository.GetFreshPrice(new ModelKey("gpt-4o", "openai"), TimeSpan.FromHours(24));
        Assert.Equal(3.00m, price!.InputPerMillionTokens);
    }

    [Fact]
    public void GetFreshPrice_ReturnsNull_WhenRowIsOlderThanMaxAge()
    {
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var repository = new PriceRepository(temp.Database);
        var sourceRepository = new PriceSourceRepository(temp.Database);

        repository.UpsertPrices(
            "litellm",
            0,
            new[] { Price("gpt-4o", "openai", 2.50m, 10.00m) },
            DateTimeOffset.UtcNow - TimeSpan.FromHours(48));

        Assert.Null(repository.GetFreshPrice(new ModelKey("gpt-4o", "openai"), TimeSpan.FromHours(24)));
        Assert.Equal(0, sourceRepository.CountFreshPrices(TimeSpan.FromHours(24)));
    }

    [Fact]
    public void GetFreshPrice_ReturnsNull_WhenKeyAbsent()
    {
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var repository = new PriceRepository(temp.Database);

        Assert.Null(repository.GetFreshPrice(new ModelKey("does-not-exist", "nowhere"), TimeSpan.FromHours(24)));
    }

    [Fact]
    public void GetFreshPrice_ReturnsNull_WhenStandardRatesAreNull()
    {
        // A row can exist for lineage while carrying no standard rates (e.g. batch-only). It is unpriced
        // for routing, so the fresh-price query must return null rather than a fabricated zero.
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var repository = new PriceRepository(temp.Database);
        var sourceRepository = new PriceSourceRepository(temp.Database);

        repository.UpsertPrices(
            "litellm",
            0,
            new[] { new NormalizedPrice("odd-model", "openai", null, null, null, 1.0m, 2.0m) },
            DateTimeOffset.UtcNow);

        Assert.Null(repository.GetFreshPrice(new ModelKey("odd-model", "openai"), TimeSpan.FromHours(24)));
        // The row still counts toward the fresh-price total (it exists and is recent).
        Assert.Equal(1, sourceRepository.CountFreshPrices(TimeSpan.FromHours(24)));
    }

    [Fact]
    public void GetFreshPrice_ExcludesRowsOwnedByADisabledSource()
    {
        // D6's "neither polled nor served" half: a model priced only by a disabled source becomes unpriced
        // the moment it is switched off, rather than steering routing until its rows age out 24h later.
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var sourceRepository = temp.CreateSourceRepository();
        repository.UpsertPrices("litellm", 0, new[] { Price("gpt-4o", "openai", 2.50m, 10.00m) }, DateTimeOffset.UtcNow);

        Assert.NotNull(repository.GetFreshPrice(new ModelKey("gpt-4o", "openai"), TimeSpan.FromHours(24)));

        sourceRepository.SetSourceEnabled("litellm", enabled: false);

        Assert.Null(repository.GetFreshPrice(new ModelKey("gpt-4o", "openai"), TimeSpan.FromHours(24)));
    }

    [Fact]
    public void ReEnablingASource_RestoresItsRowsImmediately()
    {
        // Re-enabling isn't special-cased: the rows never left, so they become visible again at once rather
        // than waiting for the next poll to rewrite them.
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var sourceRepository = temp.CreateSourceRepository();
        repository.UpsertPrices("litellm", 0, new[] { Price("gpt-4o", "openai", 2.50m, 10.00m) }, DateTimeOffset.UtcNow);

        sourceRepository.SetSourceEnabled("litellm", enabled: false);
        sourceRepository.SetSourceEnabled("litellm", enabled: true);

        Assert.NotNull(repository.GetFreshPrice(new ModelKey("gpt-4o", "openai"), TimeSpan.FromHours(24)));
    }

    [Fact]
    public void UpsertPrices_DoesNotClobberOperatorOwnedColumns()
    {
        // The ingestion loop passes a default priorityScore every cycle. If the source upsert wrote it back,
        // every poll would silently reset a rank (and, by the same mistake, a toggle) the operator had set.
        using var temp = new TempDatabase();
        temp.SeedExtraSource("litellm", enabled: true, priorityScore: 7);
        var repository = temp.CreateRepository();
        var sourceRepository = temp.CreateSourceRepository();

        repository.UpsertPrices("litellm", priorityScore: 0, new[] { Price("gpt-4o", "openai", 2.50m, 10.00m) }, DateTimeOffset.UtcNow);

        Assert.Equal(7, sourceRepository.GetSourceStates().Single(s => s.Name == "litellm").PriorityScore);
    }

    [Fact]
    public void UpsertPrices_HigherPrioritySourceWins_RegardlessOfWriteOrder()
    {
        // The core correctness fix: whichever source polls LAST does not win a contested cell just by
        // writing last - RecomputeWinners re-derives the served winner from priority_score on every upsert,
        // not from write order. Unlike the old write-time priority gate, the losing write is still retained
        // (in model_price_observations) rather than discarded - it just isn't the one served.
        using var temp = new TempDatabase();
        temp.SeedExtraSource("high", enabled: true, priorityScore: 10);
        temp.SeedExtraSource("low", enabled: true, priorityScore: 0);
        var repository = temp.CreateRepository();

        // The low-priority source writes SECOND, after the high-priority source.
        repository.UpsertPrices("high", 10, new[] { Price("gpt-4o", "openai", 2.50m, 10.00m) }, DateTimeOffset.UtcNow);
        var written = repository.UpsertPrices("low", 0, new[] { Price("gpt-4o", "openai", 999m, 999m) }, DateTimeOffset.UtcNow);

        Assert.Equal(1, written); // the observation is retained even though it does not win
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
    public void ReorderSources_ThenRecomputeWinners_FlipsContestedCellImmediately()
    {
        // Reordering alone doesn't retouch model_prices - it only rewrites priority_score. It is
        // RecomputeWinners, run over what's already in model_price_observations, that makes the new order
        // take effect - with no intervening UpsertPrices call, i.e. no live pull.
        using var temp = new TempDatabase();
        temp.SeedExtraSource("high", enabled: true, priorityScore: 10);
        temp.SeedExtraSource("low", enabled: true, priorityScore: 0);
        var repository = temp.CreateRepository();
        var sourceRepository = temp.CreateSourceRepository();
        repository.UpsertPrices("high", 10, new[] { Price("gpt-4o", "openai", 2.50m, 10.00m) }, DateTimeOffset.UtcNow);
        repository.UpsertPrices("low", 0, new[] { Price("gpt-4o", "openai", 999m, 999m) }, DateTimeOffset.UtcNow);
        Assert.Equal(2.50m, repository.GetFreshPrice(new ModelKey("gpt-4o", "openai"), TimeSpan.FromHours(24))!.InputPerMillionTokens);

        Assert.True(sourceRepository.ReorderSources(["low", "high", PriceCatalogOptions.LiteLlmSourceName, PriceCatalogOptions.OpenRouterSourceName]));
        var changed = repository.RecomputeWinners();

        Assert.True(changed > 0);
        var price = repository.GetFreshPrice(new ModelKey("gpt-4o", "openai"), TimeSpan.FromHours(24));
        Assert.Equal(999m, price!.InputPerMillionTokens);
    }

    [Fact]
    public void UpsertPrices_TwoSourcesNamingOneModelDifferently_ResolveToOneCell_AndPriorityApplies()
    {
        // The D3 payoff: LiteLLM names the model "gpt-4o" and OpenRouter "openai/gpt-4o". Pre-D3 these landed
        // in two different `models` rows and never contested a cell (D7's "0 real collisions"); resolved onto
        // one configured identity, they now share a cell - which is the FIRST time priority actually
        // arbitrates two different sources rather than a source against itself.
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated(); // seeds litellm (0) and openrouter (-10)
        var resolver = new StubIdentityResolver(new ResolvedModelIdentity("gpt-5.4", "openai"));
        var repository = new PriceRepository(temp.Database, resolver);

        repository.UpsertPrices("litellm", 0, new[] { Price("gpt-4o", "openai", 2.50m, 10.00m) }, DateTimeOffset.UtcNow);
        var written = repository.UpsertPrices("openrouter", -10, new[] { Price("openai/gpt-4o", "openai", 999m, 999m) }, DateTimeOffset.UtcNow);

        // openrouter's observation is retained even though it does not win the now-shared cell.
        Assert.Equal(1, written);

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
        var repository = new PriceRepository(temp.Database, new StubIdentityResolver(null));

        repository.UpsertPrices("litellm", 0, new[] { Price("mystery-model", "openai", 1m, 2m) }, DateTimeOffset.UtcNow);

        Assert.NotNull(repository.GetFreshPrice(new ModelKey("mystery-model", "openai"), TimeSpan.FromHours(24)));
    }

    [Fact]
    public void UpsertPrices_ExactRungResolution_StoresPriceAsNotApproximate()
    {
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var resolver = new StubIdentityResolver(new ResolvedModelIdentity("gpt-5.4", "openai"), ResolutionRung.Exact);
        var repository = new PriceRepository(temp.Database, resolver);

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
        var repository = new PriceRepository(temp.Database, resolver);

        repository.UpsertPrices("litellm", 0, new[] { Price("gpt-4o-20250101", "openai", 2m, 6m) }, DateTimeOffset.UtcNow);

        var price = repository.GetFreshPrice(new ModelKey("gpt-5.4", "openai"), TimeSpan.FromHours(24));
        Assert.True(price!.IsApproximateMatch);
    }

    [Fact]
    public void UpsertPrices_NoResolver_StoresPriceAsNotApproximate()
    {
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var repository = new PriceRepository(temp.Database);

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
    public void GetFreshPrice_RoundTripsBatchRates()
    {
        // The batch columns were written by ingestion long before any read surfaced them; this pins the
        // read half so a stored batch discount can never again be silently invisible to cost estimation.
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();

        repository.UpsertPrices(
            "litellm",
            0,
            new[]
            {
                new NormalizedPrice(
                    "gpt-4o", "openai",
                    StandardInputPrice: 2.50m, StandardOutputPrice: 10.00m,
                    CachedInputPrice: null, BatchInputPrice: 1.25m, BatchOutputPrice: 5.00m),
            },
            DateTimeOffset.UtcNow);

        var price = repository.GetFreshPrice(new ModelKey("gpt-4o", "openai"), TimeSpan.FromHours(24));

        Assert.NotNull(price);
        Assert.Equal(1.25m, price!.BatchInputPerMillionTokens);
        Assert.Equal(5.00m, price.BatchOutputPerMillionTokens);
    }

    [Fact]
    public void GetFreshPrice_NoBatchRatesPublished_BatchFieldsAreNull()
    {
        // Null, never 0m: a provider that publishes no batch rate does not offer batch pricing, and
        // collapsing that into a zero rate would price batch work as free.
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();

        repository.UpsertPrices("litellm", 0, new[] { Price("gpt-4o", "openai", 2.50m, 10.00m) }, DateTimeOffset.UtcNow);

        var price = repository.GetFreshPrice(new ModelKey("gpt-4o", "openai"), TimeSpan.FromHours(24));

        Assert.NotNull(price);
        Assert.Null(price!.BatchInputPerMillionTokens);
        Assert.Null(price.BatchOutputPerMillionTokens);
    }

    [Fact]
    public void GetPriceEntry_ReturnsAStaleRowWithItsFetchTimestamp()
    {
        // GetFreshPrice's counterpart: no age bound, and it carries last_updated_utc so the read-side cache
        // can evaluate the freshness floor in memory instead of caching one entry per age bound.
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var fetchedAt = DateTimeOffset.UtcNow.AddDays(-30);

        repository.UpsertPrices("litellm", 0, new[] { Price("gpt-4o", "openai", 2.50m, 10.00m) }, fetchedAt);

        Assert.Null(repository.GetFreshPrice(new ModelKey("gpt-4o", "openai"), TimeSpan.FromHours(24)));

        var entry = repository.GetPriceEntry(new ModelKey("gpt-4o", "openai"));

        Assert.NotNull(entry);
        Assert.Equal(2.50m, entry!.Value.Price.InputPerMillionTokens);
        Assert.Equal(fetchedAt, entry.Value.LastUpdatedUtc, TimeSpan.FromSeconds(1));
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
    public void RecomputeWinners_ThreeSourcesContestOneCell_PicksHighestPriority()
    {
        using var temp = new TempDatabase();
        temp.SeedExtraSource("a", enabled: true, priorityScore: 1);
        temp.SeedExtraSource("b", enabled: true, priorityScore: 2);
        temp.SeedExtraSource("c", enabled: true, priorityScore: 3);
        var repository = temp.CreateRepository();
        var sourceRepository = temp.CreateSourceRepository();

        repository.UpsertPrices("a", 1, new[] { Price("gpt-4o", "openai", 1.00m, 1.00m) }, DateTimeOffset.UtcNow);
        repository.UpsertPrices("b", 2, new[] { Price("gpt-4o", "openai", 2.00m, 2.00m) }, DateTimeOffset.UtcNow);
        repository.UpsertPrices("c", 3, new[] { Price("gpt-4o", "openai", 3.00m, 3.00m) }, DateTimeOffset.UtcNow);

        Assert.Equal(3.00m, repository.GetFreshPrice(new ModelKey("gpt-4o", "openai"), TimeSpan.FromHours(24))!.InputPerMillionTokens);

        // Reorder so "a" now outranks everything, then recompute with no intervening UpsertPrices call - the
        // literal "no live pull" contract: the flip can only have come from a's already-stored observation.
        Assert.True(sourceRepository.ReorderSources(["a", "c", "b", PriceCatalogOptions.LiteLlmSourceName, PriceCatalogOptions.OpenRouterSourceName]));
        repository.RecomputeWinners();

        Assert.Equal(1.00m, repository.GetFreshPrice(new ModelKey("gpt-4o", "openai"), TimeSpan.FromHours(24))!.InputPerMillionTokens);
    }

    [Fact]
    public void RecomputeWinners_DisabledSourceNeverWins_EvenWithHighestPriorityAndMostRecentObservation()
    {
        using var temp = new TempDatabase();
        temp.SeedExtraSource("disabled-high", enabled: true, priorityScore: 100);
        temp.SeedExtraSource("enabled-low", enabled: true, priorityScore: 1);
        var repository = temp.CreateRepository();
        var sourceRepository = temp.CreateSourceRepository();

        repository.UpsertPrices("enabled-low", 1, new[] { Price("gpt-4o", "openai", 2.00m, 2.00m) }, DateTimeOffset.UtcNow);
        repository.UpsertPrices("disabled-high", 100, new[] { Price("gpt-4o", "openai", 999m, 999m) }, DateTimeOffset.UtcNow);
        sourceRepository.SetSourceEnabled("disabled-high", enabled: false);

        repository.RecomputeWinners();

        // disabled-high has both the highest priority and the most recent write, yet must never win (D6).
        Assert.Equal(2.00m, repository.GetFreshPrice(new ModelKey("gpt-4o", "openai"), TimeSpan.FromHours(24))!.InputPerMillionTokens);
    }

    [Fact]
    public void RecomputeWinners_SourceNeverPolled_ContributesNothing_AndDoesNotThrow()
    {
        // A source that is enabled but has never fetched anything has no row in model_price_observations for
        // any cell - RecomputeWinners must simply skip it, not treat that as an error.
        using var temp = new TempDatabase();
        temp.SeedExtraSource("never-polled", enabled: true, priorityScore: 100);
        var repository = temp.CreateRepository();

        repository.UpsertPrices("litellm", 0, new[] { Price("gpt-4o", "openai", 2.00m, 2.00m) }, DateTimeOffset.UtcNow);

        var changed = repository.RecomputeWinners();

        Assert.True(changed > 0);
        Assert.Equal(2.00m, repository.GetFreshPrice(new ModelKey("gpt-4o", "openai"), TimeSpan.FromHours(24))!.InputPerMillionTokens);
    }

    [Fact]
    public void UpsertPrices_LosingSourcesObservation_IsRetainedInModelPriceObservations()
    {
        // The core storage fix this feature depends on: unlike model_prices (winner-only), every enabled
        // source's own observation for a contested cell must survive, even the losing one's.
        using var temp = new TempDatabase();
        temp.SeedExtraSource("high", enabled: true, priorityScore: 10);
        temp.SeedExtraSource("low", enabled: true, priorityScore: 0);
        var repository = temp.CreateRepository();

        repository.UpsertPrices("high", 10, new[] { Price("gpt-4o", "openai", 2.50m, 10.00m) }, DateTimeOffset.UtcNow);
        repository.UpsertPrices("low", 0, new[] { Price("gpt-4o", "openai", 999m, 999m) }, DateTimeOffset.UtcNow);

        using var connection = temp.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.source_name, o.standard_input_price
            FROM model_price_observations o
            JOIN aggregator_sources s ON s.source_id = o.aggregator_source_id
            ORDER BY s.source_name;
            """;
        using var reader = command.ExecuteReader();
        var observed = new Dictionary<string, decimal>();
        while (reader.Read())
        {
            observed[reader.GetString(0)] = reader.GetDecimal(1);
        }

        Assert.Equal(2.50m, observed["high"]);
        Assert.Equal(999m, observed["low"]); // retained even though it lost the cell
    }

    private static NormalizedPrice Price(string model, string provider, decimal input, decimal output) =>
        new(model, provider, input, output, CachedInputPrice: null, BatchInputPrice: null, BatchOutputPrice: null);
}
