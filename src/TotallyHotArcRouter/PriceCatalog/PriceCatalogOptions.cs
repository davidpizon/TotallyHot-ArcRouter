using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.PriceCatalog;

/// <summary>
/// Configuration for the model price catalog, bound from the <c>PriceCatalog</c> section. Governs the
/// ingestion poll interval and per-source endpoint overrides.
/// </summary>
/// <remarks>
/// The per-source enable/disable toggle is deliberately <em>not</em> here: it lives in
/// <c>aggregator_sources.enabled</c> so the Governance panel can change it at runtime, and configuration
/// neither seeds nor overrides it (see <c>docs/router/model-price-catalog.md</c> D6). What remains in
/// config is the poll cadence and the endpoint override - facts an operator sets once, not policy they
/// flip while the app is running.
/// </remarks>
public sealed class PriceCatalogOptions
{
    /// <summary>Gets the configuration section name used for price catalog settings.</summary>
    public const string SectionName = "PriceCatalog";

    /// <summary>The original source; still the higher-ranked of the two by default (see <see cref="KnownSources"/>).</summary>
    public const string LiteLlmSourceName = "litellm";

    /// <summary>
    /// OpenRouter's registry name. Its endpoint was verified 2026-07-16 (see
    /// <c>docs/router/model-price-catalog.md</c>'s "Still open" section); naming it in configuration is no
    /// longer a hard error now that <see cref="Sources.OpenRouterPriceSourceClient"/> exists.
    /// </summary>
    public const string OpenRouterSourceName = "openrouter";

    private const int MinPollIntervalHours = 4;
    private const int MaxPollIntervalHours = 12;

    /// <summary>
    /// The sources that have a client and can therefore actually be polled. This is what
    /// <see cref="PriceCatalogDatabase.EnsureCreated"/> seeds into <c>aggregator_sources</c> and what
    /// <see cref="PriceSourceRegistry"/> builds clients for.
    /// </summary>
    /// <remarks>
    /// Deliberately a list of sources-with-clients rather than sources-in-the-design: a seeded row shows up
    /// in the Governance panel as something the user can switch on, and a source that is switched on but
    /// cannot poll is the precise failure D6's validation rule exists to prevent. Adding a source means
    /// adding its client and its entry here in the same commit, so the two can never drift apart.
    /// <para>
    /// OpenRouter's default rank is strictly below LiteLLM's, not equal to it. Seeding is
    /// <c>ON CONFLICT DO NOTHING</c> (see <see cref="PriceCatalogDatabase.EnsureCreated"/>), so an install
    /// that already has a <c>litellm</c> row keeps its existing priority - 0, from before ranking existed -
    /// untouched; there is no migration that revisits it. Giving OpenRouter a negative default is what makes
    /// "LiteLLM outranks OpenRouter by default" true on <em>every</em> install, upgraded or fresh, without
    /// a special case for either.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<KnownPriceSource> KnownSources =
    [
        new(Name: LiteLlmSourceName, 0, true),
        new(Name: OpenRouterSourceName, -10, true)
    ];

    /// <summary>
    /// Gets how often the background ingestion service refreshes prices. Must land in the documented
    /// 4-12h band (D4): prices change at most once per 24h, so this is near-realtime without provoking
    /// rate limits.
    /// </summary>
    public int PollIntervalHours { get; init; } = 6;

    /// <summary>
    /// Gets the per-source configuration, keyed by source name. Carries only the optional endpoint
    /// override; the enable/disable toggle is <em>not</em> here - it lives in <c>aggregator_sources.enabled</c>
    /// and is managed from Governance → Price Sources (D6). Recognized keys are <see cref="KnownSources"/>'
    /// names; naming any other is a hard error until its client exists (see <see cref="EnsureValid"/>).
    /// </summary>
    public Dictionary<string, PriceSourceOptions> Sources { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Performs domain-level validation that is not fully expressible through data annotations, mirroring
    /// <see cref="TotallyHot.ArcRouter.Models.ModelRoutingOptions.EnsureValid"/>'s eager-validation pattern -
    /// fail at startup, not on the first poll.
    /// </summary>
    /// <exception cref="OptionsValidationException">Thrown when the price catalog configuration is inconsistent.</exception>
    public void EnsureValid()
    {
        var errors = new List<string>();

        if (PollIntervalHours is < MinPollIntervalHours or > MaxPollIntervalHours)
            errors.Add(
                $"PollIntervalHours must be between {MinPollIntervalHours} and {MaxPollIntervalHours} (was {PollIntervalHours}).");

        foreach (var (name, source) in Sources)
        {
            // An entry naming a source with no registered client (a typo, or a source whose client
            // doesn't exist yet - e.g. "openpipe", rejected as a source entirely; see "Still open" in
            // model-price-catalog.md) is a hard error, not a silent no-op: an operator who configures a
            // source and gets no polling has been misled.
            if (!IsKnownSourceName(name))
                errors.Add(
                    $"Unknown price source '{name}'. Recognized sources are: " +
                    $"{string.Join(separator: ", ", values: KnownSources.Select(s => $"'{s.Name}'"))}.");

            if (source is null) errors.Add($"Price source '{name}' has no configuration object.");
        }

        if (errors.Count > 0)
            throw new OptionsValidationException(optionsName: nameof(PriceCatalogOptions),
                optionsType: typeof(PriceCatalogOptions), failureMessages: errors);
    }

    /// <summary>
    /// Gets the configured endpoint override for <paramref name="sourceName"/>, or <see langword="null"/>
    /// when the source should use its client's built-in canonical URL.
    /// </summary>
    public string? GetSourceUrl(string sourceName)
    {
        return Sources.TryGetValue(key: sourceName, value: out var source) && !string.IsNullOrWhiteSpace(source?.Url)
            ? source.Url
            : null;
    }

    /// <summary>
    /// Determines whether <paramref name="name"/> matches one of the known aggregator source names
    /// (case-insensitive).
    /// </summary>
    private static bool IsKnownSourceName(string name)
    {
        return KnownSources.Any(s =>
            string.Equals(a: s.Name, b: name, comparisonType: StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// A source that has a client and can therefore be polled. Its defaults apply only when the source is first
/// seeded into <c>aggregator_sources</c>; from then on the database owns both values and these are ignored.
/// </summary>
/// <param name="Name">
/// The registry name, matching
/// <see cref="TotallyHot.ArcRouter.PriceCatalog.Sources.IPriceSourceClient.Name"/>.
/// </param>
/// <param name="DefaultPriorityScore">
/// The rank assigned on first seed. Ranking now arbitrates contested cells (see
/// <c>PriceRepository.UpsertPrices</c>'s priority gate); this default only ever applies once, when a
/// row is first seeded - see <see cref="PriceCatalogOptions.KnownSources"/>'s remarks for why the two known
/// sources' defaults are unequal.
/// </param>
/// <param name="DefaultEnabled">Whether the source polls on a fresh database.</param>
public sealed record KnownPriceSource(string Name, int DefaultPriorityScore, bool DefaultEnabled);

/// <summary>
/// Per-source configuration. Carries only the optional endpoint override: operator policy (the enable/disable
/// toggle) and source identity/lineage both live in the <c>aggregator_sources</c> table (D6).
/// </summary>
public sealed class PriceSourceOptions
{
    /// <summary>
    /// Gets an optional endpoint override. When empty, the client uses its own built-in canonical URL.
    /// Present so an operator can point at a mirror or a commit-pinned copy without a code change.
    /// </summary>
    public string? Url { get; init; }
}