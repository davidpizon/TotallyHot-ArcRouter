using Microsoft.Extensions.Logging.Abstractions;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.PriceCatalog.Sources;

namespace TotallyHot.ArcRouter.Tests.PriceCatalog;

/// <summary>
/// Covers <see cref="ModelPriceCatalog"/>'s rate-tier selection, the freshness split between its display
/// and routing queries, and its cache-invalidation contract. The tier permutations here are the point of
/// the type: every one of them has a "provider doesn't publish this" variant that must fall back to the
/// standard rate rather than to zero.
/// </summary>
public class ModelPriceCatalogTests
{
    private static readonly TimeSpan Floor = TimeSpan.FromHours(24);

    private static ModelPriceCatalog CreateCatalog(PriceCatalogRepository repository) =>
        new(repository, NullLogger<ModelPriceCatalog>.Instance);

    [Fact]
    public void GetBestPriceForModel_StandardContext_ReportsStandardRatesUntouched()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        WriteFullyTieredRow(repository, DateTimeOffset.UtcNow);
        var catalog = CreateCatalog(repository);

        var price = catalog.GetBestPriceForModel(Key, PriceContext.Standard);

        Assert.NotNull(price);
        Assert.Equal(15.00m, price!.InputPerMillionTokens);
        Assert.Equal(75.00m, price.OutputPerMillionTokens);
    }

    [Fact]
    public void GetBestPriceForModel_BatchContext_ReportsBatchRatesAsTheHeadlineRates()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        WriteFullyTieredRow(repository, DateTimeOffset.UtcNow);
        var catalog = CreateCatalog(repository);

        var price = catalog.GetBestPriceForModel(Key, new PriceContext(IsBatchRequest: true, RepeatsCachedContext: false));

        Assert.NotNull(price);
        Assert.Equal(7.50m, price!.InputPerMillionTokens);
        Assert.Equal(37.50m, price.OutputPerMillionTokens);
    }

    [Fact]
    public void GetBestPriceForModel_BatchContextWhenProviderPublishesNoBatchRates_FallsBackToStandard()
    {
        // The load-bearing null case (D7): absent batch rates mean "this provider does not offer batch
        // pricing", so a batch-eligible request is billed at full price. Reading the null as zero - or as
        // any discount at all - would under-report spend, the one direction budget enforcement must not err.
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        WriteRow(repository, DateTimeOffset.UtcNow, cachedInput: null, batchInput: null, batchOutput: null);
        var catalog = CreateCatalog(repository);

        var price = catalog.GetBestPriceForModel(Key, new PriceContext(IsBatchRequest: true, RepeatsCachedContext: false));

        Assert.NotNull(price);
        Assert.Equal(15.00m, price!.InputPerMillionTokens);
        Assert.Equal(75.00m, price.OutputPerMillionTokens);
    }

    [Fact]
    public void GetBestPriceForModel_CachedContext_ReportsCachedRateAsTheHeadlineInputRate()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        WriteFullyTieredRow(repository, DateTimeOffset.UtcNow);
        var catalog = CreateCatalog(repository);

        var price = catalog.GetBestPriceForModel(Key, new PriceContext(IsBatchRequest: false, RepeatsCachedContext: true));

        Assert.NotNull(price);
        Assert.Equal(1.50m, price!.InputPerMillionTokens);

        // Output is unaffected: caching discounts input tokens only, and a regression that discounted
        // generation too would silently halve every projected cost.
        Assert.Equal(75.00m, price.OutputPerMillionTokens);
    }

    [Fact]
    public void GetBestPriceForModel_CachedContextWhenProviderPublishesNoCacheRate_FallsBackToStandard()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        WriteRow(repository, DateTimeOffset.UtcNow, cachedInput: null, batchInput: null, batchOutput: null);
        var catalog = CreateCatalog(repository);

        var price = catalog.GetBestPriceForModel(Key, new PriceContext(IsBatchRequest: false, RepeatsCachedContext: true));

        Assert.NotNull(price);
        Assert.Equal(15.00m, price!.InputPerMillionTokens);
    }

    [Fact]
    public void GetBestPriceForModel_BatchAndCachedTogether_CacheRateWinsOnInputAndBatchStillDiscountsOutput()
    {
        // The two discounts are not additive. Input tokens that are cache reads are billed at the cache
        // rate, so that rate describes them better than the batch rate does; output still gets the batch
        // discount because caching says nothing about generation.
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        WriteFullyTieredRow(repository, DateTimeOffset.UtcNow);
        var catalog = CreateCatalog(repository);

        var price = catalog.GetBestPriceForModel(Key, new PriceContext(IsBatchRequest: true, RepeatsCachedContext: true));

        Assert.NotNull(price);
        Assert.Equal(1.50m, price!.InputPerMillionTokens);
        Assert.Equal(37.50m, price.OutputPerMillionTokens);
    }

    [Fact]
    public void GetBestPriceForModel_PreservesTheRowsOwnCacheRatesRegardlessOfContext()
    {
        // Tier selection rewrites only the headline rates. The row's own cache columns must survive intact,
        // because EstimateCost(UsageInfo) prices actual reported cache tokens from them - if selection
        // clobbered them, a batch request's real cache reads would be repriced at the batch input rate.
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        WriteFullyTieredRow(repository, DateTimeOffset.UtcNow);
        var catalog = CreateCatalog(repository);

        var price = catalog.GetBestPriceForModel(Key, new PriceContext(IsBatchRequest: true, RepeatsCachedContext: true));

        Assert.NotNull(price);
        Assert.Equal(1.50m, price!.CacheReadPerMillionTokens);
        Assert.Equal(18.75m, price.CacheWritePerMillionTokens);
        Assert.Equal(7.50m, price.BatchInputPerMillionTokens);
        Assert.Equal(37.50m, price.BatchOutputPerMillionTokens);
    }

    [Fact]
    public void GetBestPriceForModel_ServesAStaleRow_WhileRoutingDoesNot()
    {
        // The whole reason these are two methods: display would rather show a month-old number than
        // nothing, and routing would rather decline to rank than steer on a price that may have moved.
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        WriteFullyTieredRow(repository, DateTimeOffset.UtcNow.AddDays(-30));
        var catalog = CreateCatalog(repository);

        Assert.NotNull(catalog.GetBestPriceForModel(Key, PriceContext.Standard));
        Assert.Null(catalog.GetFreshPriceForRouting(Key, PriceContext.Standard, Floor));
    }

    [Fact]
    public void GetFreshPriceForRouting_WithinTheFloor_ReturnsTheTierSelectedPrice()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        WriteFullyTieredRow(repository, DateTimeOffset.UtcNow.AddHours(-1));
        var catalog = CreateCatalog(repository);

        var price = catalog.GetFreshPriceForRouting(Key, new PriceContext(IsBatchRequest: true, RepeatsCachedContext: false), Floor);

        Assert.NotNull(price);
        Assert.Equal(7.50m, price!.InputPerMillionTokens);
    }

    [Fact]
    public void GetBestPriceForModel_RepositoryReadThrows_ReturnsNullInsteadOfPropagating()
    {
        // IModelPriceCatalog promises a live request path that a read never throws (see its own XML doc) -
        // a transient storage fault (SQLite locked/missing) must degrade to "unpriced" for this call, not
        // take the caller down.
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        WriteFullyTieredRow(repository, DateTimeOffset.UtcNow);
        var catalog = CreateCatalog(repository);

        // Pooled connections keep the file handle open even after the row above is committed, so the
        // directory delete below would otherwise fail with "file in use" rather than exercising the fault
        // this test is after. ClearPool (scoped to this test's own connection string), not the
        // process-global ClearAllPools, which under xUnit's parallel execution can tear down a pooled
        // native sqlite3 handle out from under a completely different test's in-flight query.
        using (var connection = temp.Database.OpenConnection())
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
        }

        Directory.Delete(Path.GetDirectoryName(temp.Path_)!, recursive: true);

        Assert.Null(catalog.GetBestPriceForModel(Key, PriceContext.Standard));
    }

    [Fact]
    public void BothQueries_ReturnNull_ForAModelTheCatalogHasNoRowFor()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var catalog = CreateCatalog(repository);

        var missing = new ModelKey("never-heard-of-it", "nowhere");

        Assert.Null(catalog.GetBestPriceForModel(missing, PriceContext.Standard));
        Assert.Null(catalog.GetFreshPriceForRouting(missing, PriceContext.Standard, Floor));
    }

    [Fact]
    public void GetBestPriceForModel_RepeatedReadsAreServedFromCache_AndDoNotSeeAWriteUntilInvalidated()
    {
        // The cache is the reason a routing decision can price candidates inline with a live request, so
        // "a later write is not visible until invalidation" is the intended contract, not a bug - and
        // Invalidate is what the ingestion service calls once a cycle has actually committed rows.
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        WriteFullyTieredRow(repository, DateTimeOffset.UtcNow);
        var catalog = CreateCatalog(repository);

        Assert.Equal(15.00m, catalog.GetBestPriceForModel(Key, PriceContext.Standard)!.InputPerMillionTokens);

        WriteRow(repository, DateTimeOffset.UtcNow, cachedInput: 1.50m, batchInput: 7.50m, batchOutput: 37.50m, input: 99.00m);

        Assert.Equal(15.00m, catalog.GetBestPriceForModel(Key, PriceContext.Standard)!.InputPerMillionTokens);

        catalog.Invalidate();

        Assert.Equal(99.00m, catalog.GetBestPriceForModel(Key, PriceContext.Standard)!.InputPerMillionTokens);
    }

    [Fact]
    public void Invalidate_AlsoClearsACachedMiss()
    {
        // Misses are cached too, so a model that gains its first price row would stay permanently unpriced
        // if invalidation only dropped the hits.
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var catalog = CreateCatalog(repository);

        Assert.Null(catalog.GetBestPriceForModel(Key, PriceContext.Standard));

        WriteFullyTieredRow(repository, DateTimeOffset.UtcNow);
        Assert.Null(catalog.GetBestPriceForModel(Key, PriceContext.Standard));

        catalog.Invalidate();
        Assert.NotNull(catalog.GetBestPriceForModel(Key, PriceContext.Standard));
    }

    private static ModelKey Key => new("claude-opus", "anthropic");

    private static void WriteFullyTieredRow(PriceCatalogRepository repository, DateTimeOffset fetchedAt) =>
        WriteRow(repository, fetchedAt, cachedInput: 1.50m, batchInput: 7.50m, batchOutput: 37.50m);

    private static void WriteRow(
        PriceCatalogRepository repository,
        DateTimeOffset fetchedAt,
        decimal? cachedInput,
        decimal? batchInput,
        decimal? batchOutput,
        decimal input = 15.00m) =>
        repository.UpsertPrices(
            "litellm",
            priorityScore: 0,
            new[]
            {
                new NormalizedPrice(
                    "claude-opus",
                    "anthropic",
                    StandardInputPrice: input,
                    StandardOutputPrice: 75.00m,
                    CachedInputPrice: cachedInput,
                    BatchInputPrice: batchInput,
                    BatchOutputPrice: batchOutput,
                    CacheWriteInputPrice: 18.75m),
            },
            fetchedAt);
}
