using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Telemetry.Tokenization;

/// <summary>
/// Decorates a local token counter with the per-model correction factor learned by the calibration
/// sampler, turning a systematically-biased tiktoken count into a calibrated estimate for models whose
/// real tokenizer differs (ADR-0009). Passes through unchanged when no trusted factor exists, so the
/// router stays fully functional before any calibration has run - or with calibration switched off, which
/// is the default.
/// </summary>
/// <remarks>
/// Calibration is applied <em>only</em> to <see cref="TokenCountSource.LocalProxy"/> counts. A
/// factor is learned by comparing the provider's count against <see cref="TiktokenTokenCounter"/>'s, so it
/// is meaningful only against that same tokenizer; multiplying a
/// <see cref="TokenCountSource.Heuristic"/> character-count guess by it would compose two unrelated
/// approximations and dress the result up as the better of the two. A
/// <see cref="TokenCountSource.LocalNative"/> count is left alone for the opposite reason: it is already
/// the model's own tokenizer and has no bias to correct.
/// </remarks>
public sealed class CalibratedTokenCounter : ITokenCounter
{
    private readonly ITokenCalibrationSource? _calibration;
    private readonly ITokenCounter _inner;

    /// <summary>Initializes a new instance of the <see cref="CalibratedTokenCounter"/> class.</summary>
    /// <param name="inner">The local counter whose output is corrected - normally a <see cref="TiktokenTokenCounter"/>.</param>
    /// <param name="calibration">
    /// The learned factors. <see langword="null"/> - the default while calibration is disabled - makes this
    /// a transparent pass-through rather than an error, so the decorator can be wired unconditionally.
    /// </param>
    public CalibratedTokenCounter(ITokenCounter inner, ITokenCalibrationSource? calibration = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _calibration = calibration;
    }

    /// <inheritdoc/>
    public bool TryCountPromptTokens(string? text, ModelKey key, out int tokens, out TokenCountSource source)
    {
        if (!_inner.TryCountPromptTokens(text: text, key: key, tokens: out tokens, source: out source))
            return false;

        if (source != TokenCountSource.LocalProxy) return true;
        if (_calibration is null || !_calibration.TryGetTrustedFactor(key: key, factor: out var factor)) return true;
        if (!double.IsFinite(factor) || factor <= 0d) return true;

        // Round to nearest rather than truncating: a factor above 1 exists precisely because the local
        // count is too low, and truncating would give part of that correction straight back. Floor at 1 so
        // a very small factor on a very short prompt cannot produce a zero count for text that does exist.
        var scaled = Math.Round(tokens * factor, MidpointRounding.AwayFromZero);
        tokens = scaled >= int.MaxValue ? int.MaxValue : Math.Max(1, (int)scaled);
        source = TokenCountSource.LocalCalibrated;
        return true;
    }
}
