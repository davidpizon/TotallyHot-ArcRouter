using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Globalization;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Telemetry.Tokenization;

/// <summary>
/// Persists and serves the per-<c>(model, provider)</c> calibration factors learned by
/// <see cref="TokenCalibrationService"/>, backed by the <c>model_token_calibration</c> table (ADR-0009).
/// Implements <see cref="ITokenCalibrationSource"/>, so <see cref="CalibratedTokenCounter"/> reads factors
/// through the narrow interface and never learns that a database is involved.
/// </summary>
/// <remarks>
/// <para>
/// Rows are cached in memory after the first read and updated in place on write. This process is the only
/// writer, so the cache cannot go stale behind our back, and the alternative - a SQLite round trip per
/// counterfactual - would put file I/O in the middle of a batch analytics loop for data that changes a few
/// times an hour at most.
/// </para>
/// <para>
/// Keys are canonicalized through <see cref="ModelNameCanonicalizer"/> on both write and read, so a factor
/// learned under one spelling of a model still applies when the counterfactual names it another way - the
/// same matching problem <c>TaxonomyComparisonService.TryFindAverage</c> already solves for token averages.
/// </para>
/// </remarks>
public sealed class TokenCalibrationStore : PriceCatalogRepositoryBase, ITokenCalibrationSource
{
    private readonly ConcurrentDictionary<string, CalibrationEntry> _cache = new(StringComparer.Ordinal);
    private readonly ILogger<TokenCalibrationStore>? _logger;
    private readonly IOptionsMonitor<TokenizationOptions> _options;
    private readonly Lock _loadGate = new();
    private bool _loaded;

    /// <summary>Initializes a new instance of the <see cref="TokenCalibrationStore"/> class.</summary>
    /// <param name="database">The shared catalog database holding <c>model_token_calibration</c>.</param>
    /// <param name="options">Supplies the trust threshold and the smoothing weight.</param>
    /// <param name="logger">Optional logger.</param>
    public TokenCalibrationStore(
        PriceCatalogDatabase database,
        IOptionsMonitor<TokenizationOptions> options,
        ILogger<TokenCalibrationStore>? logger = null)
        : base(database)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Returns <see langword="false"/> for a model whose factor has fewer than
    /// <see cref="TokenizationOptions.MinSamplesForTrust"/> samples behind it. The row still exists and
    /// keeps accumulating; it is simply not applied yet, which is the difference between "we have not
    /// measured this" and "we measured it once".
    /// </remarks>
    public bool TryGetTrustedFactor(ModelKey key, out double factor)
    {
        factor = 0d;

        var cacheKey = TryBuildKey(key);
        if (cacheKey is null) return false;

        EnsureLoaded();

        if (!_cache.TryGetValue(key: cacheKey, value: out var entry)) return false;
        if (entry.SampleCount < _options.CurrentValue.MinSamplesForTrust) return false;
        if (!double.IsFinite(entry.Factor) || entry.Factor <= 0d) return false;

        factor = entry.Factor;
        return true;
    }

    /// <summary>
    /// Folds one observation - the provider's own count of a text against a local count of the same text -
    /// into this model's running factor.
    /// </summary>
    /// <param name="key">The model and provider observed.</param>
    /// <param name="localTokens">The local tokenizer's count. Must be positive.</param>
    /// <param name="providerTokens">The provider's own count of the same text. Must be positive.</param>
    /// <remarks>
    /// The factor moves by <see cref="TokenizationOptions.SmoothingFactor"/> of the gap between its
    /// current value and this sample - an exponentially-weighted mean, so no sample history has to be kept
    /// on disk and a genuine tokenizer change is still tracked, while any single outlier can move the
    /// factor by at most that fraction. The first sample for a model seeds the factor outright, since
    /// there is nothing yet to average against.
    /// </remarks>
    public void RecordSample(ModelKey key, int localTokens, int providerTokens)
    {
        if (localTokens <= 0 || providerTokens <= 0) return;

        var cacheKey = TryBuildKey(key);
        if (cacheKey is null) return;

        EnsureLoaded();

        var sample = (double)providerTokens / localTokens;
        var smoothing = _options.CurrentValue.SmoothingFactor;

        var updated = _cache.TryGetValue(key: cacheKey, value: out var existing) && existing.SampleCount > 0
            ? new CalibrationEntry(
                Factor: existing.Factor + smoothing * (sample - existing.Factor),
                SampleCount: existing.SampleCount + 1)
            : new CalibrationEntry(Factor: sample, SampleCount: 1);

        _cache[cacheKey] = updated;
        Persist(key: key, cacheKey: cacheKey, entry: updated);
    }

    /// <summary>Writes one factor row, replacing any existing row for the same canonical cell.</summary>
    /// <param name="key">The model and provider being recorded.</param>
    /// <param name="cacheKey">The canonical cache key, whose halves are the persisted model/provider.</param>
    /// <param name="entry">The folded factor and sample count.</param>
    private void Persist(ModelKey key, string cacheKey, CalibrationEntry entry)
    {
        try
        {
            var separator = cacheKey.IndexOf('|', StringComparison.Ordinal);

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                                  INSERT INTO model_token_calibration (model, provider, factor, sample_count, updated_at_utc)
                                  VALUES ($model, $provider, $factor, $samples, $updated)
                                  ON CONFLICT(model, provider) DO UPDATE SET
                                      factor = excluded.factor,
                                      sample_count = excluded.sample_count,
                                      updated_at_utc = excluded.updated_at_utc;
                                  """;
            command.Parameters.AddWithValue(parameterName: "$model", value: cacheKey[..separator]);
            command.Parameters.AddWithValue(parameterName: "$provider", value: cacheKey[(separator + 1)..]);
            command.Parameters.AddWithValue(parameterName: "$factor",
                value: entry.Factor.ToString(format: "R", provider: CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue(parameterName: "$samples", value: entry.SampleCount);
            command.Parameters.AddWithValue(parameterName: "$updated",
                value: DateTimeOffset.UtcNow.UtcDateTime.ToString(format: TimestampFormat,
                    provider: CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            // Best-effort, exactly like every other telemetry side-effect: the in-memory factor is already
            // updated and still improves this process's estimates, so a storage failure costs durability
            // across a restart, not correctness now.
            _logger?.LogWarning(exception: ex,
                message: "[TOKEN-CALIBRATION] Could not persist the calibration factor for {Model} on {Provider}.",
                key.ModelName, key.Provider);
        }
    }

    /// <summary>Loads every persisted factor into the cache once, on first use.</summary>
    private void EnsureLoaded()
    {
        if (_loaded) return;

        lock (_loadGate)
        {
            if (_loaded) return;

            try
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT model, provider, factor, sample_count FROM model_token_calibration;";

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (!double.TryParse(s: reader.GetString(2), style: NumberStyles.Float,
                            provider: CultureInfo.InvariantCulture, result: out var factor))
                        continue;

                    _cache[$"{reader.GetString(0)}|{reader.GetString(1)}"] =
                        new CalibrationEntry(Factor: factor, SampleCount: reader.GetInt32(3));
                }
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
            {
                // An unreadable table degrades every model to LocalUncalibrated, which is exactly the
                // pre-calibration behavior - correct, just less accurate. Do not fault the caller.
                _logger?.LogWarning(exception: ex,
                    message: "[TOKEN-CALIBRATION] Could not read stored calibration factors; counting uncalibrated.");
            }

            _loaded = true;
        }
    }

    /// <summary>
    /// Builds the canonical <c>model|provider</c> cache key for a model, or <see langword="null"/> when the
    /// key carries no usable model name.
    /// </summary>
    /// <param name="key">The model and provider to canonicalize.</param>
    /// <returns>The cache key, or <see langword="null"/>.</returns>
    private static string? TryBuildKey(ModelKey key)
    {
        if (string.IsNullOrWhiteSpace(key.ModelName)) return null;

        var provider = string.IsNullOrWhiteSpace(key.Provider) ? string.Empty : key.Provider.ToLowerInvariant();
        var canonical = ModelNameCanonicalizer.Canonicalize(
            modelId: key.ModelName,
            provider: string.IsNullOrWhiteSpace(key.Provider) ? null : key.Provider);

        return $"{canonical}|{provider}";
    }

    /// <summary>One model's learned factor and how many observations stand behind it.</summary>
    /// <param name="Factor">The multiplier applied to a local count.</param>
    /// <param name="SampleCount">How many observations have been folded in.</param>
    private readonly record struct CalibrationEntry(double Factor, int SampleCount);
}
