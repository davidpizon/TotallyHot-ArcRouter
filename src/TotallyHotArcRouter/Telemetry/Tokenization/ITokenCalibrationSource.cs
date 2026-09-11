using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Telemetry.Tokenization;

/// <summary>
/// Supplies the per-model multiplier that corrects a local tiktoken count toward what the provider would
/// actually bill. Separated from the counter that applies it so the estimate path
/// (<see cref="CalibratedTokenCounter"/>) has no dependency on storage, HTTP, or the calibration sampler's
/// schedule - the offline-first constraint ADR-0009 turns on.
/// </summary>
/// <remarks>
/// Implementations answer from whatever has already been learned and persisted; they never fetch. A model
/// nobody has sampled, or one sampled too few times to trust, is simply absent - the same
/// "absent, not defaulted" convention <see cref="IModelPriceLookup"/> and
/// <see cref="Transcripts.ITranscriptStore.LoadObservedTokenAveragesAsync"/> already follow, and for the
/// same reason: a fabricated factor of <c>1.0</c> would be indistinguishable from a measured one.
/// </remarks>
public interface ITokenCalibrationSource
{
    /// <summary>
    /// Attempts to read a calibration factor for <paramref name="key"/> that has accumulated enough
    /// samples to be trusted.
    /// </summary>
    /// <param name="key">The model and provider whose factor is wanted.</param>
    /// <param name="factor">
    /// The multiplier to apply to a local count when this method returns <see langword="true"/>; otherwise
    /// unspecified and not to be read. Always strictly positive when returned.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when a trusted factor exists; <see langword="false"/> when none has been
    /// learned yet or the one on record is still below its sample threshold.
    /// </returns>
    bool TryGetTrustedFactor(ModelKey key, out double factor);
}
