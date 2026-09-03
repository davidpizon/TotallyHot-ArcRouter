using Microsoft.Data.Sqlite;
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

    private static ModelKey Key => new(ModelName: "claude-opus", Provider: "anthropic");

    private static ModelPriceCatalog CreateCatalog(PriceRepository repository)
    {
        return new ModelPriceCatalog(repository: repository, logger: NullLogger<ModelPriceCatalog>.Instance);
    }

    [Fact]
    public void GetBestPriceForModel_StandardContext_ReportsStandardRatesUntouched()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        WriteFullyTieredRow(repository: repository, fetchedAt: DateTimeOffset.UtcNow);
        var catalog = CreateCatalog(repository);

        var price = catalog.GetBestPriceForModel(key: Key, context: PriceContext.Standard);

        Assert.NotNull(price);
        Assert.Equal(15.00m, actual: price!.InputPerMillionTokens);
        Assert.Equal(75.00m, actual: price.OutputPerMillionTokens);
    }

    [Fact]
    public void GetBestPriceForModel_BatchContext_ReportsBatchRatesAsTheHeadlineRates()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        WriteFullyTieredRow(repository: repository, fetchedAt: DateTimeOffset.UtcNow);
        var catalog = CreateCatalog(repository);

        var price = catalog.GetBestPriceForModel(key: Key, context: new PriceContext(true, false));

        Assert.NotNull(price);
        Assert.Equal(7.50m, actual: price!.InputPerMillionTokens);
        Assert.Equal(37.50m, actual: price.OutputPerMillionTokens);
    }

    [Fact]
    public void GetBestPriceForModel_BatchContextWhenProviderPublishesNoBatchRates_FallsBackToStandard()
    {
        // The load-bearing null case (D7): absent batch rates mean "this provider does not offer batch
        // pricing", so a batch-eligible request is billed at full price. Reading the null as zero - or as
        // any discount at all - would under-report spend, the one direction budget enforcement must not err.
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        WriteRow(repository: repository, fetchedAt: DateTimeOffset.UtcNow, null, null, null);
        var catalog = CreateCatalog(repository);

        var price = catalog.GetBestPriceForModel(key: Key, context: new PriceContext(true, false));

        Assert.NotNull(price);
        Assert.Equal(15.00m, actual: price!.InputPerMillionTokens);
        Assert.Equal(75.00m, actual: price.OutputPerMillionTokens);
    }

    [Fact]
    public void GetBestPriceForModel_CachedContext_ReportsCachedRateAsTheHeadlineInputRate()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        WriteFullyTieredRow(repository: repository, fetchedAt: DateTimeOffset.UtcNow);
        var catalog = CreateCatalog(repository);

        var price = catalog.GetBestPriceForModel(key: Key, context: new PriceContext(false, true));

        Assert.NotNull(price);
        Assert.Equal(1.50m, actual: price!.InputPerMillionTokens);

        // Output is unaffected: caching discounts input tokens only, and a regression that discounted
        // generation too would silently halve every projected cost.
        Assert.Equal(75.00m, actual: price.OutputPerMillionTokens);
    }

    [Fact]
    public void GetBestPriceForModel_CachedContextWhenProviderPublishesNoCacheRate_FallsBackToStandard()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        WriteRow(repository: repository, fetchedAt: DateTimeOffset.UtcNow, null, null, null);
        var catalog = CreateCatalog(repository);

        var price = catalog.GetBestPriceForModel(key: Key, context: new PriceContext(false, true));

        Assert.NotNull(price);
        Assert.Equal(15.00m, actual: price!.InputPerMillionTokens);
    }

    [Fact]
    public void GetBestPriceForModel_BatchAndCachedTogether_CacheRateWinsOnInputAndBatchStillDiscountsOutput()
    {
        // The two discounts are not additive. Input tokens that are cache reads are billed at the cache
        // rate, so that rate describes them better than the batch rate does; output still gets the batch
        // discount because caching says nothing about generation.
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        WriteFullyTieredRow(repository: repository, fetchedAt: DateTimeOffset.UtcNow);
        var catalog = CreateCatalog(repository);

        var price = catalog.GetBestPriceForModel(key: Key, context: new PriceContext(true, true));

        Assert.NotNull(price);
        Assert.Equal(1.50m, actual: price!.InputPerMillionTokens);
        Assert.Equal(37.50m, actual: price.OutputPerMillionTokens);
    }

    [Fact]
    public void GetBestPriceForModel_PreservesTheRowsOwnCacheRatesRegardlessOfContext()
    {
        // Tier selection rewrites only the headline rates. The row's own cache columns must survive intact,
        // because EstimateCost(UsageInfo) prices actual reported cache tokens from them - if selection
        // clobbered them, a batch request's real cache reads would be repriced at the batch input rate.
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        WriteFullyTieredRow(repository: repository, fetchedAt: DateTimeOffset.UtcNow);
        var catalog = CreateCatalog(repository);

        var price = catalog.GetBestPriceForModel(key: Key, context: new PriceContext(true, true));

        Assert.NotNull(price);
        Assert.Equal(1.50m, actual: price!.CacheReadPerMillionTokens);
        Assert.Equal(18.75m, actual: price.CacheWritePerMillionTokens);
        Assert.Equal(7.50m, actual: price.BatchInputPerMillionTokens);
        Assert.Equal(37.50m, actual: price.BatchOutputPerMillionTokens);
    }

    [Fact]
    public void GetBestPriceForModel_ServesAStaleRow_WhileRoutingDoesNot()
    {
        // The whole reason these are two methods: display would rather show a month-old number than
        // nothing, and routing would rather decline to rank than steer on a price that may have moved.
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        WriteFullyTieredRow(repository: repository, fetchedAt: DateTimeOffset.UtcNow.AddDays(-30));
        var catalog = CreateCatalog(repository);

        Assert.NotNull(catalog.GetBestPriceForModel(key: Key, context: PriceContext.Standard));
        Assert.Null(catalog.GetFreshPriceForRouting(key: Key, context: PriceContext.Standard, maxAge: Floor));
    }

    [Fact]
    public void GetFreshPriceForRouting_WithinTheFloor_ReturnsTheTierSelectedPrice()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        WriteFullyTieredRow(repository: repository, fetchedAt: DateTimeOffset.UtcNow.AddHours(-1));
        var catalog = CreateCatalog(repository);

        var price = catalog.GetFreshPriceForRouting(key: Key, context: new PriceContext(true, false), maxAge: Floor);

        Assert.NotNull(price);
        Assert.Equal(7.50m, actual: price!.InputPerMillionTokens);
    }

    [Fact]
    public void GetBestPriceForModel_RepositoryReadThrows_ReturnsNullInsteadOfPropagating()
    {
        // IModelPriceCatalog promises a live request path that a read never throws (see its own XML doc) -
        // a transient storage fault (SQLite locked/missing) must degrade to "unpriced" for this call, not
        // take the caller down.
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        WriteFullyTieredRow(repository: repository, fetchedAt: DateTimeOffset.UtcNow);
        var catalog = CreateCatalog(repository);

        // Pooled connections keep the file handle open even after the row above is committed, so the
        // directory delete below would otherwise fail with "file in use" rather than exercising the fault
        // this test is after. ClearPool (scoped to this test's own connection string), not the
        // process-global ClearAllPools, which under xUnit's parallel execution can tear down a pooled
        // native sqlite3 handle out from under a completely different test's in-flight query.
        using (var connection = temp.Database.OpenConnection())
        {
            SqliteConnection.ClearPool(connection);
        }

        Directory.Delete(path: Path.GetDirectoryName(temp.Path_)!, true);

        Assert.Null(catalog.GetBestPriceForModel(key: Key, context: PriceContext.Standard));
    }

    [Fact]
    public void BothQueries_ReturnNull_ForAModelTheCatalogHasNoRowFor()
    {
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var catalog = CreateCatalog(repository);

        var missing = new ModelKey(ModelName: "never-heard-of-it", Provider: "nowhere");

        Assert.Null(catalog.GetBestPriceForModel(key: missing, context: PriceContext.Standard));
        Assert.Null(catalog.GetFreshPriceForRouting(key: missing, context: PriceContext.Standard, maxAge: Floor));
    }

    [Fact]
    public void GetBestPriceForModel_RepeatedReadsAreServedFromCache_AndDoNotSeeAWriteUntilInvalidated()
    {
        // The cache is the reason a routing decision can price candidates inline with a live request, so
        // "a later write is not visible until invalidation" is the intended contract, not a bug - and
        // Invalidate is what the ingestion service calls once a cycle has actually committed rows.
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        WriteFullyTieredRow(repository: repository, fetchedAt: DateTimeOffset.UtcNow);
        var catalog = CreateCatalog(repository);

        Assert.Equal(15.00m,
            actual: catalog.GetBestPriceForModel(key: Key, context: PriceContext.Standard)!.InputPerMillionTokens);

        WriteRow(repository: repository, fetchedAt: DateTimeOffset.UtcNow, 1.50m, 7.50m, 37.50m, 99.00m);

        Assert.Equal(15.00m,
            actual: catalog.GetBestPriceForModel(key: Key, context: PriceContext.Standard)!.InputPerMillionTokens);

        catalog.Invalidate();

        Assert.Equal(99.00m,
            actual: catalog.GetBestPriceForModel(key: Key, context: PriceContext.Standard)!.InputPerMillionTokens);
    }

    [Fact]
    public void Invalidate_AlsoClearsACachedMiss()
    {
        // Misses are cached too, so a model that gains its first price row would stay permanently unpriced
        // if invalidation only dropped the hits.
        using var temp = new TempDatabase();
        var repository = temp.CreateRepository();
        var catalog = CreateCatalog(repository);

        Assert.Null(catalog.GetBestPriceForModel(key: Key, context: PriceContext.Standard));

        WriteFullyTieredRow(repository: repository, fetchedAt: DateTimeOffset.UtcNow);
        Assert.Null(catalog.GetBestPriceForModel(key: Key, context: PriceContext.Standard));

        catalog.Invalidate();
        Assert.NotNull(catalog.GetBestPriceForModel(key: Key, context: PriceContext.Standard));
    }

    private static void WriteFullyTieredRow(PriceRepository repository, DateTimeOffset fetchedAt)
    {
        WriteRow(repository: repository, fetchedAt: fetchedAt, 1.50m, 7.50m,
            37.50m);
    }

    private static void WriteRow(
        PriceRepository repository,
        DateTimeOffset fetchedAt,
        decimal? cachedInput,
        decimal? batchInput,
        decimal? batchOutput,
        decimal input = 15.00m)
    {
        repository.UpsertPrices(
            sourceName: "litellm",
            0,
            prices: new[]
            {
                new NormalizedPrice(
                    ModelIdentifier: "claude-opus",
                    Provider: "anthropic",
                    StandardInputPrice: input,
                    75.00m,
                    CachedInputPrice: cachedInput,
                    BatchInputPrice: batchInput,
                    BatchOutputPrice: batchOutput,
                    18.75m)
            },
            asOfUtc: fetchedAt);
    }
}