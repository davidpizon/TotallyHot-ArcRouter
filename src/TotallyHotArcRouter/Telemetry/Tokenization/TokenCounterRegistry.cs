using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Telemetry.Tokenization;

/// <summary>
/// The composed <see cref="ITokenCounter"/> the rest of the application depends on: a calibrated local
/// tokenizer, backed by the character-length heuristic for any model no encoding serves. Callers ask this
/// one type and read <see cref="TokenCountSource"/> to learn how good the answer they got is, rather than
/// choosing between counters themselves.
/// </summary>
/// <remarks>
/// The fallback is deliberately last and deliberately present. Preferring a real tokenizer keeps the
/// common case accurate; still answering when none fits is what closes ADR-0009's cold-start hole, where
/// the previous estimator returned nothing at all for a model that had never been routed to. Because the
/// heuristic labels itself, "worse answer" never silently becomes "wrong answer presented as good".
/// </remarks>
public sealed class TokenCounterRegistry : ITokenCounter
{
    private readonly ITokenCounter _fallback;
    private readonly ITokenCounter _primary;

    /// <summary>
    /// Initializes a new instance of the <see cref="TokenCounterRegistry"/> class over the standard
    /// composition: a <see cref="TiktokenTokenCounter"/> wrapped in <see cref="CalibratedTokenCounter"/>,
    /// falling back to <see cref="HeuristicTokenCounter"/>.
    /// </summary>
    /// <param name="calibration">
    /// The learned calibration factors, or <see langword="null"/> when calibration is disabled (the
    /// default) - in which case local counts are reported as
    /// <see cref="TokenCountSource.LocalUncalibrated"/>.
    /// </param>
    public TokenCounterRegistry(ITokenCalibrationSource? calibration = null)
        : this(
            primary: new CalibratedTokenCounter(inner: new TiktokenTokenCounter(), calibration: calibration),
            fallback: new HeuristicTokenCounter())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TokenCounterRegistry"/> class over explicit
    /// collaborators. Test-only seam; production code uses the public constructor so the composition stays
    /// defined in exactly one place.
    /// </summary>
    /// <param name="primary">The preferred counter, consulted first.</param>
    /// <param name="fallback">The counter consulted when <paramref name="primary"/> cannot serve the model.</param>
    internal TokenCounterRegistry(ITokenCounter primary, ITokenCounter fallback)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(fallback);
        _primary = primary;
        _fallback = fallback;
    }

    /// <inheritdoc/>
    public bool TryCountPromptTokens(string? text, ModelKey key, out int tokens, out TokenCountSource source)
    {
        if (_primary.TryCountPromptTokens(text: text, key: key, tokens: out tokens, source: out source))
            return true;

        return _fallback.TryCountPromptTokens(text: text, key: key, tokens: out tokens, source: out source);
    }
}
