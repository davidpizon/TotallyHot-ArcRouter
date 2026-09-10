using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Telemetry.Tokenization;
using TotallyHot.ArcRouter.Tests.PriceCatalog;

namespace TotallyHot.ArcRouter.Tests.Telemetry.Tokenization;

/// <summary>
/// Covers <see cref="TokenCalibrationStore"/>: the exponentially-weighted fold, the sample threshold that
/// gates trust, spelling-insensitive lookup, and durability across instances.
/// </summary>
public class TokenCalibrationStoreTests
{
    private static readonly ModelKey Claude = new(ModelName: "claude-opus-5", Provider: "anthropic");

    [Fact]
    public void TryGetTrustedFactor_NoSamples_ReturnsFalse()
    {
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var store = Build(temp);

        var found = store.TryGetTrustedFactor(key: Claude, factor: out _);

        Assert.False(found);
    }

    [Fact]
    public void TryGetTrustedFactor_BelowSampleThreshold_IsRecordedButNotTrusted()
    {
        // The difference between "we have not measured this" and "we measured it once" - one unrepresentative
        // prompt must not move every counterfactual for the model.
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var store = Build(temp: temp, minSamplesForTrust: 3);

        store.RecordSample(key: Claude, localTokens: 100, providerTokens: 120);

        Assert.False(store.TryGetTrustedFactor(key: Claude, factor: out _));
    }

    [Fact]
    public void RecordSample_FirstSample_SeedsTheFactorOutright()
    {
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var store = Build(temp: temp, minSamplesForTrust: 1);

        store.RecordSample(key: Claude, localTokens: 100, providerTokens: 120);

        Assert.True(store.TryGetTrustedFactor(key: Claude, factor: out var factor));
        Assert.Equal(expected: 1.20d, actual: factor, tolerance: 0.0001d);
    }

    [Fact]
    public void RecordSample_SecondSample_MovesOnlyBySmoothingFractionOfTheGap()
    {
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var store = Build(temp: temp, minSamplesForTrust: 1, smoothing: 0.5d);

        store.RecordSample(key: Claude, localTokens: 100, providerTokens: 100); // seeds 1.0
        store.RecordSample(key: Claude, localTokens: 100, providerTokens: 200); // sample 2.0

        // 1.0 + 0.5 * (2.0 - 1.0) = 1.5, not 2.0: a single outlier moves the factor by half the gap.
        Assert.True(store.TryGetTrustedFactor(key: Claude, factor: out var factor));
        Assert.Equal(expected: 1.5d, actual: factor, tolerance: 0.0001d);
    }

    [Fact]
    public void RecordSample_ManyOutlierResistantSamples_ConvergesTowardTheTruth()
    {
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var store = Build(temp: temp, minSamplesForTrust: 1, smoothing: 0.2d);

        for (var i = 0; i < 50; i++) store.RecordSample(key: Claude, localTokens: 100, providerTokens: 118);

        Assert.True(store.TryGetTrustedFactor(key: Claude, factor: out var factor));
        Assert.Equal(expected: 1.18d, actual: factor, tolerance: 0.01d);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-5, 100)]
    public void RecordSample_NonPositiveCounts_AreIgnored(int local, int provider)
    {
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var store = Build(temp: temp, minSamplesForTrust: 1);

        store.RecordSample(key: Claude, localTokens: local, providerTokens: provider);

        Assert.False(store.TryGetTrustedFactor(key: Claude, factor: out _));
    }

    [Fact]
    public void TryGetTrustedFactor_DifferentSpellingOfTheSameModel_FindsTheFactor()
    {
        // A factor learned under one spelling must apply when the counterfactual names the model another
        // way - the same matching problem TaxonomyComparisonService.TryFindAverage already solves.
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        var store = Build(temp: temp, minSamplesForTrust: 1);

        store.RecordSample(key: Claude, localTokens: 100, providerTokens: 130);

        var found = store.TryGetTrustedFactor(
            key: new ModelKey(ModelName: "claude-opus-5", Provider: "Anthropic"),
            factor: out var factor);

        Assert.True(found);
        Assert.Equal(expected: 1.30d, actual: factor, tolerance: 0.0001d);
    }

    [Fact]
    public void RecordSample_SurvivesIntoAFreshStoreInstance()
    {
        using var temp = new TempDatabase();
        temp.Database.EnsureCreated();
        Build(temp: temp, minSamplesForTrust: 1).RecordSample(key: Claude, localTokens: 100, providerTokens: 115);

        var reopened = Build(temp: temp, minSamplesForTrust: 1);

        Assert.True(reopened.TryGetTrustedFactor(key: Claude, factor: out var factor));
        Assert.Equal(expected: 1.15d, actual: factor, tolerance: 0.0001d);
    }

    /// <summary>Builds a store over the temp database with the given trust threshold and smoothing weight.</summary>
    /// <param name="temp">The temp database fixture.</param>
    /// <param name="minSamplesForTrust">Samples required before a factor is applied.</param>
    /// <param name="smoothing">The weight given to each new sample.</param>
    /// <returns>The configured store.</returns>
    private static TokenCalibrationStore Build(TempDatabase temp, int minSamplesForTrust = 10,
        double smoothing = 0.2d)
    {
        var options = new TokenizationOptions
        {
            MinSamplesForTrust = minSamplesForTrust,
            SmoothingFactor = smoothing
        };

        return new TokenCalibrationStore(database: temp.Database, options: new StaticOptionsMonitor(options));
    }

    /// <summary>A minimal <see cref="IOptionsMonitor{TOptions}"/> returning one fixed value.</summary>
    private sealed class StaticOptionsMonitor : IOptionsMonitor<TokenizationOptions>
    {
        /// <summary>Initializes the monitor with the value it always returns.</summary>
        /// <param name="value">The options value.</param>
        public StaticOptionsMonitor(TokenizationOptions value)
        {
            CurrentValue = value;
        }

        /// <inheritdoc/>
        public TokenizationOptions CurrentValue { get; }

        /// <inheritdoc/>
        public TokenizationOptions Get(string? name)
        {
            return CurrentValue;
        }

        /// <inheritdoc/>
        public IDisposable? OnChange(Action<TokenizationOptions, string?> listener)
        {
            return null;
        }
    }
}
