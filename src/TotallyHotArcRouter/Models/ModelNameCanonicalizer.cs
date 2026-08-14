using System.Text.RegularExpressions;

namespace TotallyHot.ArcRouter.Models;

/// <summary>
/// The repository's single implementation of "these two strings name the same model". Model ids reach
/// this router under several spellings at once - the operator's own
/// <c>ModelRouting:ModelList[].ModelName</c>, a provider's public model id, a price aggregator's key,
/// and a benchmark dataset's column - and none of them agree on case, on whether a version is written
/// <c>4.6</c> or <c>4-6</c>, or on whether a dated snapshot suffix is carried.
/// </summary>
/// <remarks>
/// <para>
/// This type deliberately produces a <em>comparison key</em>, never a name. The configured
/// <c>ModelName</c> vocabulary is operator-authored and internally inconsistent - <c>claude-opus-4.6</c>
/// is dotted while <c>claude-opus-4-8</c> is dashed and <c>claude-haiku-4-5-20251001</c> carries a
/// snapshot - so no rule can <em>generate</em> those names. What a rule can do is collapse every
/// spelling of one model onto one key, letting both sides of a lookup meet without either side
/// inventing a name that is not in the configuration.
/// </para>
/// <para>
/// The stages are exposed individually because <see cref="PriceCatalog.ConfigModelIdentityResolver"/>
/// applies them one at a time: its <see cref="PriceCatalog.ResolutionRung"/> ladder distinguishes an
/// exact match from a snapshot-stripped one precisely <em>by which stage was needed</em>, and that
/// distinction is what marks a resolved price approximate. Only callers that want the whole pipeline
/// - such as the offline CodeRouterBench loader, which has no rung ladder and no provider column -
/// should call <see cref="Canonicalize"/>. Callers for whom a dated snapshot is a genuinely different,
/// non-interchangeable model - not an approximate stand-in for its base - should call
/// <see cref="CanonicalizeSpelling"/> instead, which normalizes only cosmetic spelling (case, provider
/// prefix, version separator) and leaves snapshot/version-tier suffixes untouched.
/// </para>
/// <para>
/// There is no fuzzy stage and no alias table today: all eight CodeRouterBench dataset ids reach their
/// configured <c>ModelName</c> by rule alone. An unrecognized id still goes through the same
/// normalization (lowercasing, snapshot/version-suffix stripping, dot-to-dash version unification) as a
/// recognized one - only the alias/fuzzy mapping is withheld, per <c>docs/router/d3-alias-resolution.md</c>.
/// </para>
/// </remarks>
public static class ModelNameCanonicalizer
{
    // Strips a trailing 8-digit dated snapshot suffix, e.g. "-20250929" off "claude-sonnet-4-5-20250929".
    private static readonly Regex SnapshotSuffix = new(@"-\d{8}$", RegexOptions.Compiled);

    // A small, fixed set of version/tier suffixes aggregators commonly append that a router's own
    // ModelList entry never carries. Not exhaustive by design - an unrecognized suffix simply falls through
    // to the next rung rather than being guessed at.
    private static readonly Regex VersionSuffix = new(@"(-latest|-preview|-exp|-beta|:free)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches only a dot sitting between two digits, so a version separator is unified without touching
    // a vendor-prefixed id such as "anthropic.claude-3-5-sonnet-20241022-v2:0".
    private static readonly Regex VersionSeparator = new(@"(?<=\d)\.(?=\d)", RegexOptions.Compiled);

    /// <summary>
    /// Normalizes a model id into the form every other stage assumes: trimmed, stripped of a leading
    /// <c>provider/</c> qualifier, and lowercased.
    /// </summary>
    /// <param name="modelId">The model id to normalize, e.g. <c>openai/GPT-4o</c>.</param>
    /// <param name="provider">
    /// The provider whose <c>provider/</c> prefix should be stripped when present. When
    /// <see langword="null"/>, any leading <c>segment/</c> is stripped instead - the behavior a caller
    /// with no provider column, such as a benchmark CSV, needs.
    /// </param>
    public static string NormalizeBase(string modelId, string? provider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var trimmed = modelId.Trim();

        if (provider is not null)
        {
            var prefix = provider.Trim() + "/";
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[prefix.Length..];
            }
        }
        else
        {
            var separator = trimmed.IndexOf('/');
            if (separator >= 0)
            {
                trimmed = trimmed[(separator + 1)..];
            }
        }

        return trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// Normalizes a provider name for use as a dictionary key by trimming whitespace and lowercasing.
    /// </summary>
    /// <param name="provider">The provider key to normalize.</param>
    public static string NormalizeProvider(string provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        return provider.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Removes a trailing dated snapshot suffix (e.g. <c>-20250929</c>), or returns
    /// <paramref name="modelId"/> unchanged when it carries none.
    /// </summary>
    /// <param name="modelId">The model id to strip.</param>
    public static string StripSnapshotSuffix(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        return SnapshotSuffix.Replace(modelId, string.Empty);
    }

    /// <summary>
    /// Removes a trailing version/tier suffix (<c>-latest</c>, <c>-preview</c>, <c>-exp</c>,
    /// <c>-beta</c>, <c>:free</c>), or returns <paramref name="modelId"/> unchanged when it carries none.
    /// </summary>
    /// <param name="modelId">The model id to strip.</param>
    public static string StripVersionSuffix(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        return VersionSuffix.Replace(modelId, string.Empty);
    }

    /// <summary>
    /// Rewrites a dot between two digits as a dash, so <c>claude-opus-4.6</c> and <c>claude-opus-4-6</c>
    /// converge. Deliberately narrow: a dot not flanked by digits (a vendor prefix such as
    /// <c>anthropic.claude-…</c>) is left alone.
    /// </summary>
    /// <param name="modelId">The model id whose version separators should be unified.</param>
    public static string UnifyVersionSeparators(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        return VersionSeparator.Replace(modelId, "-");
    }

    /// <summary>
    /// Runs every stage in order - <see cref="NormalizeBase"/>, <see cref="StripSnapshotSuffix"/>,
    /// <see cref="StripVersionSuffix"/>, <see cref="UnifyVersionSeparators"/> - producing the key that
    /// all spellings of one model share.
    /// </summary>
    /// <param name="modelId">Any spelling of a model id, from config, a provider, or a dataset.</param>
    /// <remarks>
    /// Intended for callers that compare ids and nothing else. It is <em>not</em> appropriate where the
    /// difference between an exact and an approximate match carries meaning, because it discards which
    /// stage did the work; see the remarks on <see cref="ModelNameCanonicalizer"/>. It is also
    /// <em>not</em> appropriate where a dated snapshot must stay distinct from its rolling base model -
    /// use <see cref="CanonicalizeSpelling"/> for that instead, since this method deliberately conflates
    /// the two so a benchmark's snapshotted dataset id can still resolve to its unsnapshotted configured
    /// <c>ModelName</c>.
    /// </remarks>
    public static string Canonicalize(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        return UnifyVersionSeparators(StripVersionSuffix(StripSnapshotSuffix(NormalizeBase(modelId))));
    }

    /// <summary>
    /// Runs only the spelling-normalization stages - <see cref="NormalizeBase"/> and
    /// <see cref="UnifyVersionSeparators"/> - producing a key for callers that must keep a dated snapshot
    /// or version/tier suffix distinct from its base model.
    /// </summary>
    /// <param name="modelId">Any spelling of a model id, from config, a provider, or a voter's own pick.</param>
    /// <param name="provider">Forwarded to <see cref="NormalizeBase"/> to strip a matching provider prefix.</param>
    /// <remarks>
    /// Unlike <see cref="Canonicalize"/>, this never strips <see cref="StripSnapshotSuffix"/> or
    /// <see cref="StripVersionSuffix"/> - <c>claude-opus-4.6-20250929</c> and <c>claude-opus-4.6</c> pin
    /// different, non-interchangeable releases and must not collapse onto one key here, even though
    /// <c>claude-opus-4.6</c> and <c>claude-opus-4-6</c> (same release, different punctuation) still should.
    /// </remarks>
    public static string CanonicalizeSpelling(string modelId, string? provider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        return UnifyVersionSeparators(NormalizeBase(modelId, provider));
    }
}
