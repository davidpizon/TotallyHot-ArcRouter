using Microsoft.ML.Tokenizers;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Telemetry.Tokenization;

/// <summary>
/// Counts tokens with a local tiktoken encoding from <c>Microsoft.ML.Tokenizers</c>. This is the only
/// counter in the codebase that does real tokenization; everything else either decorates it
/// (<see cref="CalibratedTokenCounter"/>) or stands in for it when no encoding fits
/// (<see cref="HeuristicTokenCounter"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The encoding is exact only for the OpenAI families it belongs to.</b> For Claude, Gemini, Mistral,
/// and local models, a tiktoken encoding is a *proxy* - Anthropic documents that tiktoken under-counts
/// Claude by roughly 15-20% on prose and considerably more on code. That is why this type reports
/// <see cref="TokenCountSource.LocalUncalibrated"/> and never claims exactness: correcting the bias is
/// <see cref="CalibratedTokenCounter"/>'s job, and admitting to it when uncorrected is this one's.
/// Deliberately counting a non-OpenAI model with a known-biased encoder is the accepted trade-off
/// recorded in ADR-0009 for keeping the estimator offline-capable.
/// </para>
/// <para>
/// Encodings are expensive to construct (each parses a large embedded rank table) and thread-safe once
/// built, so one instance per encoding is cached in a <see cref="Lazy{T}"/> for the process lifetime -
/// the same build-once-share-many shape <c>OnnxEmbeddingClient</c> uses for its session. Construction
/// failure is captured as <see langword="null"/> rather than thrown, so a missing data package degrades
/// this counter to "cannot serve" instead of faulting a background analytics cycle.
/// </para>
/// </remarks>
public sealed class TiktokenTokenCounter : ITokenCounter
{
    /// <summary>The encoding used by the GPT-4o and later OpenAI reasoning families.</summary>
    internal const string O200KBaseEncoding = "o200k_base";

    /// <summary>The encoding used by GPT-4 and GPT-3.5-turbo, and the fallback proxy for other vendors.</summary>
    internal const string Cl100KBaseEncoding = "cl100k_base";

    // Substrings that, in a canonicalized model id, indicate an o200k_base family. Checked against the
    // canonical form so every spelling of a model ("gpt-4o-2024-08-06", "openai/gpt-4o") lands the same way.
    private static readonly string[] O200KMarkers = ["gpt-4o", "gpt-4.1", "gpt-5", "o1", "o3", "o4"];

    private static readonly Lazy<TiktokenTokenizer?> O200K =
        new(() => TryCreate(O200KBaseEncoding), LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<TiktokenTokenizer?> Cl100K =
        new(() => TryCreate(Cl100KBaseEncoding), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <inheritdoc/>
    public bool TryCountPromptTokens(string? text, ModelKey key, out int tokens, out TokenCountSource source)
    {
        tokens = 0;
        source = TokenCountSource.Unavailable;

        if (string.IsNullOrWhiteSpace(text)) return false;

        var tokenizer = ResolveTokenizer(key);
        if (tokenizer is null) return false;

        try
        {
            tokens = tokenizer.CountTokens(text);
            source = TokenCountSource.LocalUncalibrated;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // A malformed surrogate pair or an encoding-internal failure. Counting is best-effort by this
            // type's interface contract, so report "cannot serve" rather than faulting the caller's cycle.
            tokens = 0;
            source = TokenCountSource.Unavailable;
            return false;
        }
    }

    /// <summary>
    /// Chooses the tiktoken encoding name for <paramref name="key"/>: <see cref="O200KBaseEncoding"/> for
    /// the GPT-4o-and-later OpenAI families, <see cref="Cl100KBaseEncoding"/> for everything else.
    /// </summary>
    /// <param name="key">The model and provider being counted for.</param>
    /// <returns>The encoding name, or <see langword="null"/> when the model id is unusable.</returns>
    /// <remarks>
    /// Matching happens on <see cref="ModelNameCanonicalizer.Canonicalize(string, string?)"/>'s output so
    /// this does not become a second, drifting place where model-name spellings are normalized - the
    /// single-normalizer rule from <c>docs/router/model-identity-canonicalization.md</c>.
    /// </remarks>
    internal static string? ResolveEncodingName(ModelKey key)
    {
        if (string.IsNullOrWhiteSpace(key.ModelName)) return null;

        var canonical = ModelNameCanonicalizer
            .Canonicalize(modelId: key.ModelName, provider: string.IsNullOrWhiteSpace(key.Provider) ? null : key.Provider)
            .ToLowerInvariant();

        foreach (var marker in O200KMarkers)
            if (canonical.Contains(value: marker, comparisonType: StringComparison.Ordinal))
                return O200KBaseEncoding;

        return Cl100KBaseEncoding;
    }

    /// <summary>Resolves the cached tokenizer instance for a model, or <see langword="null"/> when none applies.</summary>
    /// <param name="key">The model and provider being counted for.</param>
    /// <returns>The shared tokenizer, or <see langword="null"/> when the model id is unusable or the encoding failed to load.</returns>
    private static TiktokenTokenizer? ResolveTokenizer(ModelKey key)
    {
        return ResolveEncodingName(key) switch
        {
            O200KBaseEncoding => O200K.Value,
            Cl100KBaseEncoding => Cl100K.Value,
            _ => null
        };
    }

    /// <summary>
    /// Builds one encoding, returning <see langword="null"/> instead of throwing when its data package is
    /// absent, so a packaging mistake degrades counting rather than crashing a background service.
    /// </summary>
    /// <param name="encodingName">The tiktoken encoding to create.</param>
    /// <returns>The tokenizer, or <see langword="null"/> when it could not be created.</returns>
    private static TiktokenTokenizer? TryCreate(string encodingName)
    {
        try
        {
            return TiktokenTokenizer.CreateForEncoding(encodingName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }
}
