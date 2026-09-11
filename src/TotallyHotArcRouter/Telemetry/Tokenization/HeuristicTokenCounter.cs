using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Telemetry.Tokenization;

/// <summary>
/// The floor of last resort: estimates tokens from character length alone, for a model no real encoding
/// serves. Always reports <see cref="TokenCountSource.Heuristic"/>, so a figure this crude can never be
/// mistaken downstream for a measurement.
/// </summary>
/// <remarks>
/// <para>
/// The <c>chars / 4</c> ratio is the long-standing rough English-prose average across BPE vocabularies.
/// It is materially wrong for code (denser), for non-Latin scripts (far denser), and for highly repetitive
/// text (sparser) - which is exactly why this counter labels itself rather than competing with
/// <see cref="TiktokenTokenCounter"/>. It exists so a never-counted model produces a *disclosed* estimate
/// instead of nothing at all, which ADR-0009 judged better than leaving the cold-start counterfactual blank.
/// </para>
/// <para>
/// Rounding is upward: a partial token is still billed as a token, so truncating would bias every estimate
/// low, and a counterfactual biased low systematically overstates routing savings.
/// </para>
/// </remarks>
public sealed class HeuristicTokenCounter : ITokenCounter
{
    // Average characters per token for English prose under a BPE vocabulary. Not tuned per model: a
    // per-model number here would imply a precision this counter does not have, and any model worth
    // tuning for is better served by an encoding plus a calibration factor.
    private const int CharactersPerToken = 4;

    /// <inheritdoc/>
    /// <remarks>
    /// Ignores <paramref name="key"/> entirely - the whole point of this counter is that it applies when
    /// nothing model-specific is known. The parameter stays in the signature to satisfy
    /// <see cref="ITokenCounter"/>, which every other implementation does use it.
    /// </remarks>
    public bool TryCountPromptTokens(string? text, ModelKey key, out int tokens, out TokenCountSource source)
    {
        tokens = 0;
        source = TokenCountSource.Unavailable;

        if (string.IsNullOrWhiteSpace(text)) return false;

        tokens = (text.Length + CharactersPerToken - 1) / CharactersPerToken;
        source = TokenCountSource.Heuristic;
        return true;
    }
}
