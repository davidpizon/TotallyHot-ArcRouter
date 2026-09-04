using Microsoft.Data.Sqlite;
using System.Globalization;
using TotallyHot.ArcRouter.PriceCatalog.Sources;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.PriceCatalog;

/// <summary>
/// Thin ADO.NET wrapper over the price upsert/read tables (<c>model_price_observations</c> and
/// <c>model_prices</c>). Reads and writes are keyed <c>(model, provider)</c> per D7. Prices are stored as
/// USD per 1,000,000 tokens (D2); timestamps as round-trip UTC ISO 8601, so the 24h freshness floor (D1) is
/// an ordinary lexicographic comparison. Split out of the former monolithic <c>PriceCatalogRepository</c>
/// (docs/router/code-smell-refactoring-plan.md M3) - the other five concerns that repository once mixed
/// together each now have their own type in this namespace.
/// </summary>
public sealed class PriceRepository : PriceCatalogRepositoryBase
{
    private readonly IModelIdentityResolver? _identityResolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="PriceRepository"/> class.
    /// </summary>
    /// <param name="database">The catalog database.</param>
    /// <param name="identityResolver">
    /// Optional D3 alias resolver mapping each source's own model/provider naming onto the configured router
    /// identity at ingest (see <c>docs/router/d3-alias-resolution.md</c>). When <see langword="null"/>, prices
    /// are stored under each source's own keys verbatim - the pre-D3 behavior - so existing callers and tests
    /// are unaffected.
    /// </param>
    public PriceRepository(PriceCatalogDatabase database, IModelIdentityResolver? identityResolver = null)
        : base(database)
    {
        _identityResolver = identityResolver;
    }

    /// <summary>
    /// Upserts a batch of normalized prices from one source into that source's own rows in
    /// <c>model_price_observations</c>, in a single transaction, then calls <see cref="RecomputeWinners"/> so
    /// <c>model_prices</c> - the served winner per cell - reflects this batch immediately. The observation
    /// write itself is unconditional; there is no priority gate here, since arbitrating which source's price
    /// is actually served for a contested <c>(model, provider)</c> cell is <see cref="RecomputeWinners"/>'s
    /// job, over every source's stored observation, not just this batch's. Missing providers, models, and the
    /// source itself are created on demand.
    /// </summary>
    /// <returns>The number of observation rows written, i.e. <c>prices.Count</c> on success.</returns>
    public int UpsertPrices(
        string sourceName,
        int priorityScore,
        IReadOnlyList<NormalizedPrice> prices,
        DateTimeOffset asOfUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentNullException.ThrowIfNull(prices);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        var sourceId = GetOrCreateSourceId(connection: connection, transaction: transaction, sourceName: sourceName,
            priorityScore: priorityScore);
        var timestamp = asOfUtc.UtcDateTime.ToString(format: TimestampFormat, provider: CultureInfo.InvariantCulture);

        var written = 0;
        foreach (var price in prices)
        {
            // D3 alias resolution: map the source's own (model, provider) naming onto the configured router
            // identity, so two sources naming one real model differently (LiteLLM "gpt-4o", OpenRouter
            // "openai/gpt-4o") collide into a single (ModelName, provider) cell - which is what exercises the
            // priority gate below and lets the runtime cost lookup resolve on ModelName. A miss falls back to
            // the source's own keys verbatim: an unmatched model is left unresolved, never mis-mapped. See
            // docs/router/d3-alias-resolution.md.
            var resolution = _identityResolver?.Resolve(sourceName: sourceName,
                aggregatorModelId: price.ModelIdentifier, aggregatorProvider: price.Provider);
            var providerName = resolution?.Identity.Provider ?? price.Provider;
            var modelIdentifier = resolution?.Identity.ModelName ?? price.ModelIdentifier;
            var isApproximate = resolution?.IsApproximate ?? false;

            var providerId = GetOrCreateProviderId(connection: connection, transaction: transaction,
                providerName: providerName);
            var modelId = GetOrCreateModelId(connection: connection, transaction: transaction,
                modelIdentifier: modelIdentifier);

            // The alias records the source's own name against whatever internal model id it resolved to (the
            // configured ModelName on a hit, the raw key on a miss). Written unconditionally, same as the
            // observation row itself now - neither has a priority concept of its own.
            UpsertAlias(connection: connection, transaction: transaction, sourceId: sourceId, modelId: modelId,
                aggregatorName: price.ModelIdentifier);
            UpsertPriceRow(connection: connection, transaction: transaction, modelId: modelId, providerId: providerId,
                sourceId: sourceId, price: price, timestamp: timestamp, isApproximate: isApproximate);
            written++;
        }

        transaction.Commit();

        // Recomputed on its own connection, after commit: RecomputeWinners re-reads model_price_observations,
        // so it must see this batch's writes as committed rows, not as an in-flight transaction it can't see.
        if (written > 0) RecomputeWinners();

        return written;
    }

    /// <summary>
    /// Returns every published rate tier for a <c>(model, provider)</c> key, but only when the row was
    /// fetched within <paramref name="maxAge"/> - the query D1's 24h routing floor reads. Returns
    /// <see langword="null"/> when the row is stale, absent, or has no standard rates; all three mean
    /// <em>unpriced</em> and a caller must treat them identically.
    /// </summary>
    /// <remarks>
    /// A free provider's zero does not come from here (see <c>ProviderOptions.IsFree</c>); this returns
    /// <see langword="null"/> for such a model, and the caller owns that carve-out.
    /// <para>
    /// A row owned by a <em>disabled</em> source is excluded outright, which is D6's "neither polled nor
    /// served" half: a model priced only by a disabled source becomes unpriced the moment the operator
    /// switches it off, rather than continuing to steer routing until its rows age out 24h later. Note this
    /// is not the same as a source that merely <em>failed</em> - stale rows from a failing source are still
    /// trusted for display, because "we couldn't refresh this" is a different claim from "stop using this".
    /// </para>
    /// <para>
    /// Every tier the row publishes is returned, unfiltered; picking <em>which</em> of them applies to a
    /// given request is <see cref="ModelPriceCatalog"/>'s job via <see cref="PriceContext"/>, not this
    /// method's. Keeping selection out of the repository is what lets the display and routing queries share
    /// one row shape while answering different questions about it.
    /// </para>
    /// </remarks>
    public ModelPrice? GetFreshPrice(ModelKey key, TimeSpan maxAge)
    {
        return ReadPrice(key: key, cutoff: FormatCutoff(maxAge))?.Price;
    }

    /// <summary>
    /// Returns every published rate tier for a <c>(model, provider)</c> key at <em>any</em> age, together
    /// with when the row was last refreshed. This is the read <see cref="ModelPriceCatalog"/> caches: it
    /// deliberately applies no freshness bound, because carrying the timestamp lets the caller evaluate D1's
    /// floor in memory at read time rather than baking an age bound into what was cached.
    /// </summary>
    /// <remarks>
    /// A disabled source's rows are excluded here exactly as in <see cref="GetFreshPrice"/> - D6's "not
    /// served" is about operator intent, which does not soften just because the question is a display one.
    /// Returns <see langword="null"/> only when there is no row at all, or it publishes no standard rates.
    /// </remarks>
    public CatalogPriceEntry? GetPriceEntry(ModelKey key)
    {
        return ReadPrice(key: key, null);
    }

    /// <summary>
    /// Re-derives <c>model_prices</c> - the served winner per <c>(model, provider)</c> cell - from every
    /// enabled source's last-known observation in <c>model_price_observations</c> and the sources' current
    /// <c>priority_score</c> ranking. Ties (equal priority) favor the most recently observed row. Performs
    /// no network I/O: unlike the old incremental priority gate, which only ever compared an incoming row
    /// against the current incumbent, this is a full re-derivation over everything currently on hand - which
    /// is what a reorder needs, since the newly top-ranked source's price was not necessarily the incoming
    /// row in any recent upsert.
    /// </summary>
    /// <remarks>
    /// A disabled source is excluded via the same <c>aggregator_sources.enabled</c> join <see cref="ReadPrice"/>
    /// already applies (D6): its observations never win, no matter their rank or recency. A cell with no
    /// observation from any currently enabled source is left untouched in <c>model_prices</c> - it is already
    /// excluded from reads by that same join, so there is nothing to clear.
    /// </remarks>
    /// <returns>
    /// The number of <c>(model, provider)</c> cells recomputed, i.e. that have at least one observation from
    /// a currently enabled source.
    /// </returns>
    public int RecomputeWinners()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              WITH ranked AS (
                                  SELECT o.*, s.priority_score,
                                         ROW_NUMBER() OVER (
                                             PARTITION BY o.model_id, o.provider_id
                                             ORDER BY s.priority_score DESC, o.last_updated_utc DESC
                                         ) AS rn
                                  FROM model_price_observations o
                                  JOIN aggregator_sources s ON s.source_id = o.aggregator_source_id
                                  WHERE s.enabled = 1
                              )
                              INSERT INTO model_prices (
                                  model_id, provider_id, aggregator_source_id,
                                  standard_input_price, standard_output_price, cached_input_price, cache_write_input_price,
                                  batch_input_price, batch_output_price, last_updated_utc, is_approximate)
                              SELECT model_id, provider_id, aggregator_source_id,
                                     standard_input_price, standard_output_price, cached_input_price, cache_write_input_price,
                                     batch_input_price, batch_output_price, last_updated_utc, is_approximate
                              FROM ranked WHERE rn = 1
                              ON CONFLICT(model_id, provider_id) DO UPDATE SET
                                  aggregator_source_id    = excluded.aggregator_source_id,
                                  standard_input_price    = excluded.standard_input_price,
                                  standard_output_price   = excluded.standard_output_price,
                                  cached_input_price      = excluded.cached_input_price,
                                  cache_write_input_price = excluded.cache_write_input_price,
                                  batch_input_price       = excluded.batch_input_price,
                                  batch_output_price      = excluded.batch_output_price,
                                  last_updated_utc        = excluded.last_updated_utc,
                                  is_approximate          = excluded.is_approximate;
                              """;
        return command.ExecuteNonQuery();
    }

    /// <summary>
    /// Shared read behind <see cref="GetFreshPrice"/> and <see cref="GetPriceEntry"/>, applying the
    /// freshness predicate only when <paramref name="cutoff"/> is supplied. One method so the two queries
    /// can never drift in which columns or which source-enabled filter they apply - the only intended
    /// difference between them is the age bound.
    /// </summary>
    private CatalogPriceEntry? ReadPrice(ModelKey key, string? cutoff)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = cutoff is null
            ? """
              SELECT mp.standard_input_price, mp.standard_output_price,
                     mp.cached_input_price, mp.cache_write_input_price, mp.is_approximate,
                     mp.batch_input_price, mp.batch_output_price, mp.last_updated_utc
              FROM model_prices mp
              JOIN models            m ON m.model_id    = mp.model_id
              JOIN providers         p ON p.provider_id = mp.provider_id
              JOIN aggregator_sources s ON s.source_id  = mp.aggregator_source_id
              WHERE m.model_identifier = $model
                AND p.provider_name    = $provider
                AND s.enabled = 1
              ORDER BY mp.last_updated_utc DESC
              LIMIT 1;
              """
            : """
              SELECT mp.standard_input_price, mp.standard_output_price,
                     mp.cached_input_price, mp.cache_write_input_price, mp.is_approximate,
                     mp.batch_input_price, mp.batch_output_price, mp.last_updated_utc
              FROM model_prices mp
              JOIN models            m ON m.model_id    = mp.model_id
              JOIN providers         p ON p.provider_id = mp.provider_id
              JOIN aggregator_sources s ON s.source_id  = mp.aggregator_source_id
              WHERE m.model_identifier = $model
                AND p.provider_name    = $provider
                AND s.enabled = 1
                AND mp.last_updated_utc >= $cutoff
              ORDER BY mp.last_updated_utc DESC
              LIMIT 1;
              """;
        command.Parameters.AddWithValue(parameterName: "$model", value: key.ModelName);
        command.Parameters.AddWithValue(parameterName: "$provider", value: key.Provider);

        if (cutoff is not null) command.Parameters.AddWithValue(parameterName: "$cutoff", value: cutoff);

        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0) || reader.IsDBNull(1)) return null;

        var price = new ModelPrice(
            InputPerMillionTokens: reader.GetDecimal(0),
            OutputPerMillionTokens: reader.GetDecimal(1),
            CacheReadPerMillionTokens: reader.IsDBNull(2) ? null : reader.GetDecimal(2),
            CacheWritePerMillionTokens: reader.IsDBNull(3) ? null : reader.GetDecimal(3),
            IsApproximateMatch: reader.GetInt32(4) != 0,
            BatchInputPerMillionTokens: reader.IsDBNull(5) ? null : reader.GetDecimal(5),
            BatchOutputPerMillionTokens: reader.IsDBNull(6) ? null : reader.GetDecimal(6));

        return new CatalogPriceEntry(Price: price, LastUpdatedUtc: ParseTimestamp(reader.GetString(7)));
    }

    /// <summary>
    /// Returns the id of the aggregator source row for <paramref name="sourceName"/>, inserting it with
    /// <paramref name="priorityScore"/> if it does not already exist. An existing row's priority_score and
    /// enabled flag are never overwritten, since those are operator-owned.
    /// </summary>
    private static int GetOrCreateSourceId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceName,
        int priorityScore)
    {
        using var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        // The conflict branch is a deliberate no-op write (the same trick GetOrCreateProviderId uses to make
        // RETURNING fire on an existing row) rather than an update of priority_score. Both priority_score and
        // enabled are operator-owned: they are seeded once by PriceCatalogDatabase.EnsureCreated and changed
        // only through the Governance panel. Writing them back here would mean every poll silently reset the
        // operator's choice to whatever default the ingestion loop happened to pass in.
        upsert.CommandText = """
                             INSERT INTO aggregator_sources (source_name, priority_score)
                             VALUES ($name, $priority)
                             ON CONFLICT(source_name) DO UPDATE SET source_name = excluded.source_name
                             RETURNING source_id;
                             """;
        upsert.Parameters.AddWithValue(parameterName: "$name", value: sourceName);
        upsert.Parameters.AddWithValue(parameterName: "$priority", value: priorityScore);
        return Convert.ToInt32(value: upsert.ExecuteScalar(), provider: CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Returns the id of the provider row for <paramref name="providerName"/>, inserting it if it does not
    /// already exist.
    /// </summary>
    private static int GetOrCreateProviderId(SqliteConnection connection, SqliteTransaction transaction,
        string providerName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                              INSERT INTO providers (provider_name) VALUES ($name)
                              ON CONFLICT(provider_name) DO UPDATE SET provider_name = excluded.provider_name
                              RETURNING provider_id;
                              """;
        command.Parameters.AddWithValue(parameterName: "$name", value: providerName);
        return Convert.ToInt32(value: command.ExecuteScalar(), provider: CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Returns the id of the model row for <paramref name="modelIdentifier"/>, inserting it if it does not
    /// already exist.
    /// </summary>
    private static int GetOrCreateModelId(SqliteConnection connection, SqliteTransaction transaction,
        string modelIdentifier)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                              INSERT INTO models (model_identifier) VALUES ($id)
                              ON CONFLICT(model_identifier) DO UPDATE SET model_identifier = excluded.model_identifier
                              RETURNING model_id;
                              """;
        command.Parameters.AddWithValue(parameterName: "$id", value: modelIdentifier);
        return Convert.ToInt32(value: command.ExecuteScalar(), provider: CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Inserts or updates the aggregator alias mapping (source, aggregator name) to <paramref name="modelId"/>,
    /// so a later lookup by the aggregator's own name for that source resolves to this model.
    /// </summary>
    private static void UpsertAlias(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int sourceId,
        int modelId,
        string aggregatorName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                              INSERT INTO model_aliases (model_id, source_id, aggregator_name)
                              VALUES ($model, $source, $name)
                              ON CONFLICT(source_id, aggregator_name) DO UPDATE SET model_id = excluded.model_id;
                              """;
        command.Parameters.AddWithValue(parameterName: "$model", value: modelId);
        command.Parameters.AddWithValue(parameterName: "$source", value: sourceId);
        command.Parameters.AddWithValue(parameterName: "$name", value: aggregatorName);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Inserts or updates one source's own observation row for (model, provider) in
    /// <c>model_price_observations</c>, unconditionally - there is no priority gate here. Contested-cell
    /// arbitration happens later, in <see cref="RecomputeWinners"/>.
    /// </summary>
    private static void UpsertPriceRow(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int modelId,
        int providerId,
        int sourceId,
        NormalizedPrice price,
        string timestamp,
        bool isApproximate)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                              INSERT INTO model_price_observations (
                                  model_id, provider_id, aggregator_source_id,
                                  standard_input_price, standard_output_price, cached_input_price, cache_write_input_price,
                                  batch_input_price, batch_output_price, last_updated_utc, is_approximate)
                              VALUES (
                                  $model, $provider, $source,
                                  $stdIn, $stdOut, $cachedIn, $cacheWrite,
                                  $batchIn, $batchOut, $updated, $approximate)
                              ON CONFLICT(model_id, provider_id, aggregator_source_id) DO UPDATE SET
                                  standard_input_price    = excluded.standard_input_price,
                                  standard_output_price   = excluded.standard_output_price,
                                  cached_input_price      = excluded.cached_input_price,
                                  cache_write_input_price = excluded.cache_write_input_price,
                                  batch_input_price       = excluded.batch_input_price,
                                  batch_output_price      = excluded.batch_output_price,
                                  last_updated_utc        = excluded.last_updated_utc,
                                  is_approximate          = excluded.is_approximate;
                              """;
        command.Parameters.AddWithValue(parameterName: "$model", value: modelId);
        command.Parameters.AddWithValue(parameterName: "$provider", value: providerId);
        command.Parameters.AddWithValue(parameterName: "$source", value: sourceId);
        command.Parameters.AddWithValue(parameterName: "$stdIn",
            value: (object?)price.StandardInputPrice ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$stdOut",
            value: (object?)price.StandardOutputPrice ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$cachedIn",
            value: (object?)price.CachedInputPrice ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$cacheWrite",
            value: (object?)price.CacheWriteInputPrice ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$batchIn",
            value: (object?)price.BatchInputPrice ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$batchOut",
            value: (object?)price.BatchOutputPrice ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$updated", value: timestamp);
        command.Parameters.AddWithValue(parameterName: "$approximate", value: isApproximate ? 1 : 0);
        command.ExecuteNonQuery();
    }
}