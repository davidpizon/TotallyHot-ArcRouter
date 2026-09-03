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
/// <b>Spelling, not identity.</b> <see cref="Canonicalize"/> normalizes only <em>cosmetic</em> spelling -
/// case, a leading <c>provider/</c> qualifier, and <c>4.6</c> vs <c>4-6</c> version punctuation. It does
/// <em>not</em> strip a dated snapshot or a version/tier suffix, because those change <em>which model</em>
/// is named: <c>claude-opus-4.6-20250929</c> pins one immutable release and <c>claude-opus-4.6</c> is a
/// rolling pointer, and treating them as one key would silently merge two models' scores into a single
/// cell. See <c>docs/router/model-identity-canonicalization.md</c> for the evidence behind this decision.
/// </para>
/// <para>
/// The stages remain exposed individually because <see cref="PriceCatalog.ConfigModelIdentityResolver"/>
/// applies them one at a time, and <em>there</em> snapshot stripping is legitimate: its
/// <see cref="PriceCatalog.ResolutionRung"/> ladder distinguishes an exact match from a snapshot-stripped
/// one precisely <em>by which stage was needed</em>, and that distinction is what marks a resolved price
/// approximate. That is the dividing line this type is built around - an approximation that is
/// <em>labeled</em> (a price falling back to its base model's rate) is acceptable, while one that is
/// <em>silent</em> (a benchmark score or a routing vote resolving to a different release than the one
/// named) is not. Only the labeled path may call <see cref="StripSnapshotSuffix"/> or
/// <see cref="StripVersionSuffix"/>.
/// </para>
/// <para>
/// There is no fuzzy stage and no alias table today: all eight CodeRouterBench dataset ids reach their
/// configured <c>ModelName</c> through spelling normalization alone. An unrecognized id still goes through
/// the same normalization (lowercasing, provider-prefix stripping, dot-to-dash version unification) as a
/// recognized one - only the alias/fuzzy mapping is withheld, per <c>docs/router/d3-alias-resolution.md</c>.
/// An id that genuinely names no configured model is therefore left unresolved rather than approximately
/// mapped, which is the visible failure the same document prescribes.
/// </para>
/// </remarks>
public static class ModelNameCanonicalizer
{
    // Strips a trailing 8-digit dated snapshot suffix, e.g. "-20250929" off "claude-sonnet-4-5-20250929".
    private static readonly Regex SnapshotSuffix = new(pattern: @"-\d{8}$", options: RegexOptions.Compiled);

    // A small, fixed set of version/tier suffixes aggregators commonly append that a router's own
    // ModelList entry never carries. Not exhaustive by design - an unrecognized suffix simply falls through
    // to the next rung rather than being guessed at.
    private static readonly Regex VersionSuffix = new(pattern: @"(-latest|-preview|-exp|-beta|:free)$",
        options: RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches only a dot sitting between two digits, so a version separator is unified without touching
    // a vendor-prefixed id such as "anthropic.claude-3-5-sonnet-20241022-v2:0".
    private static readonly Regex VersionSeparator = new(pattern: @"(?<=\d)\.(?=\d)", options: RegexOptions.Compiled);

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
            if (trimmed.StartsWith(value: prefix, comparisonType: StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed[prefix.Length..];
        }
        else
        {
            var separator = trimmed.IndexOf('/');
            if (separator >= 0) trimmed = trimmed[(separator + 1)..];
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
        return SnapshotSuffix.Replace(input: modelId, replacement: string.Empty);
    }

    /// <summary>
    /// Removes a trailing version/tier suffix (<c>-latest</c>, <c>-preview</c>, <c>-exp</c>,
    /// <c>-beta</c>, <c>:free</c>), or returns <paramref name="modelId"/> unchanged when it carries none.
    /// </summary>
    /// <param name="modelId">The model id to strip.</param>
    public static string StripVersionSuffix(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        return VersionSuffix.Replace(input: modelId, replacement: string.Empty);
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
        return VersionSeparator.Replace(input: modelId, replacement: "-");
    }

    /// <summary>
    /// Runs the spelling-normalization stages - <see cref="NormalizeBase"/> then
    /// <see cref="UnifyVersionSeparators"/> - producing the key that every <em>spelling</em> of one model
    /// shares. This is the identity key for benchmark score lookups, routing votes, and anywhere else two
    /// model ids are compared for sameness.
    /// </summary>
    /// <param name="modelId">Any spelling of a model id, from config, a provider, a dataset, or a voter.</param>
    /// <param name="provider">Forwarded to <see cref="NormalizeBase"/> to strip a matching provider prefix.</param>
    /// <remarks>
    /// <para>
    /// Deliberately does <em>not</em> run <see cref="StripSnapshotSuffix"/> or
    /// <see cref="StripVersionSuffix"/>. <c>claude-opus-4.6</c> and <c>claude-opus-4-6</c> are one model
    /// written two ways and do collapse here; <c>claude-opus-4.6-20250929</c> is a different, pinned
    /// release and does not. All eight CodeRouterBench dataset ids reach their configured
    /// <c>ModelName</c> under these two stages alone, so the stripping bought nothing on that path while
    /// risking a silent score merge - see <c>docs/router/model-identity-canonicalization.md</c>.
    /// </para>
    /// <para>
    /// Not appropriate where the difference between an exact and an approximate match carries meaning,
    /// because it discards which stage did the work; <see cref="PriceCatalog.ConfigModelIdentityResolver"/>
    /// applies the stages individually for that reason.
    /// </para>
    /// </remarks>
    public static string Canonicalize(string modelId, string? provider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        return UnifyVersionSeparators(NormalizeBase(modelId: modelId, provider: provider));
    }
}