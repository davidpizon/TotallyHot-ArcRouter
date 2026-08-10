namespace TotallyHot.ArcRouter.Gui.Admin;

/// <summary>
/// A provider as returned by the proxy's <c>GET /admin/providers</c> endpoint. Authentication is expressed
/// purely via <see cref="Headers"/>, which masks credentials the same way every other header does (see
/// <see cref="ProviderHeaderView"/>).
/// </summary>
/// <param name="Key">The provider key (e.g. <c>openai</c>).</param>
/// <param name="Name">The user-friendly display name for this provider (e.g. <c>OpenAI API</c>); displayed in the Governance UI.</param>
/// <param name="BaseUrl">The provider's absolute base URL.</param>
/// <param name="AuthHeaderName">The header carrying the credential (e.g. <c>Authorization</c>).</param>
/// <param name="Models">The models configured to route to this provider.</param>
/// <param name="Headers">The provider's configured custom headers (literal values are returned; secrets live in env vars).</param>
/// <param name="IsFree">Whether this provider costs nothing (a local runtime, say), making its models' cost a known zero rather than unknown.</param>
/// <param name="DollarCap">The provider's monthly USD budget cap, or null for no dollar budget.</param>
/// <param name="TokenCap">The provider's monthly total-token budget cap, or null for no token budget.</param>
/// <param name="DollarSpent">USD spent against this provider in the current month.</param>
/// <param name="TokensUsed">
/// Total tokens used against this provider in the current month - prompt, completion, and cache
/// creation/read tokens combined.
/// </param>
/// <param name="Enabled">
/// Whether the operator has this provider switched on, driving the Stop/Play control in Governance &gt;
/// Providers. Enforced immediately on the next request by the proxy's routing path, no restart needed.
/// </param>
/// <param name="ProviderType">
/// The name of the <see cref="Admin.ProviderType"/> member the operator selected for this provider, or
/// <see langword="null"/> for one configured before the field existed (the editor then shows
/// <see cref="Admin.ProviderType.Other"/>). Round-tripping this is what lets the editor reopen a provider
/// with its own type - and therefore its own credential defaults - already selected.
/// </param>
/// <param name="EndpointCapabilities">
/// Which API flavors this provider's endpoint last answered to, as recorded by the proxy's own capability
/// scan (see <see cref="ProviderEndpointCapabilitiesView"/>), or <see langword="null"/> when the endpoint
/// has never been scanned. Drives the API badges shown next to the provider name in Governance &gt;
/// Providers.
/// </param>
/// <param name="UsageLastRecordedAtUtc">
/// The UTC instant the most recent request's usage was recorded against this provider, or
/// <see langword="null"/> if no usage has been recorded this month yet. Backs the Anthropic Usage card's
/// "estimated from intercepted traffic" footer.
/// </param>
/// <param name="RateLimit">
/// This provider's most recently captured rate-limit response headers - Anthropic's
/// <c>anthropic-ratelimit-*</c> or OpenAI's <c>x-ratelimit-*</c>, whichever family this provider's
/// responses carry - or <see langword="null"/> when none have been captured yet. Backs the Anthropic Usage
/// card's "reported by Anthropic" section.
/// </param>
/// <param name="WindowKind">The window the caps above reset on: <c>"Monthly"</c>, <c>"Weekly"</c>, or <c>"RollingHours"</c> (Phase 4, §5.10).</param>
/// <param name="NextResetUtc">The UTC instant the current period ends and spend resets to zero - backs a budget bar's "resets in" text.</param>
public sealed record ProviderAdminView(
    string Key,
    string? Name,
    string BaseUrl,
    string AuthHeaderName,
    IReadOnlyList<ModelAdminView> Models,
    IReadOnlyList<ProviderHeaderView> Headers,
    bool IsFree = false,
    decimal? DollarCap = null,
    long? TokenCap = null,
    decimal DollarSpent = 0m,
    long TokensUsed = 0L,
    bool Enabled = true,
    string? ProviderType = null,
    ProviderEndpointCapabilitiesView? EndpointCapabilities = null,
    DateTimeOffset? UsageLastRecordedAtUtc = null,
    ProviderRateLimitAdminView? RateLimit = null,
    string WindowKind = "Monthly",
    DateTimeOffset? NextResetUtc = null);

/// <summary>
/// A standard-family rate-limit dimension (<c>requests</c>, <c>input-tokens</c>, <c>output-tokens</c>, or
/// <c>tokens</c>), mirroring the proxy's <c>RateLimitDimensionView</c>.
/// </summary>
/// <param name="Limit">The dimension's configured cap, or <see langword="null"/> if unknown.</param>
/// <param name="Remaining">What's left before the cap, or <see langword="null"/> if unknown. Anthropic rounds this to the nearest thousand.</param>
/// <param name="ResetAt">When the dimension resets, or <see langword="null"/> if unknown.</param>
public sealed record RateLimitDimensionAdminView(long? Limit, long? Remaining, DateTimeOffset? ResetAt);

/// <summary>
/// One unified-family time window (e.g. <c>5h</c>), mirroring the proxy's <c>RateLimitWindowView</c>.
/// </summary>
/// <param name="Status">The window's raw status string, or <see langword="null"/> if unknown.</param>
/// <param name="Remaining">What's left in the window, or <see langword="null"/> if unknown.</param>
/// <param name="ResetAt">When the window resets, or <see langword="null"/> if unknown.</param>
public sealed record RateLimitWindowAdminView(string? Status, long? Remaining, DateTimeOffset? ResetAt);

/// <summary>
/// The typed projection of one provider's captured rate-limit headers - Anthropic's standard/unified
/// families or OpenAI's <c>x-ratelimit-*</c> family - mirroring the proxy's <c>RateLimitSnapshotView</c>.
/// </summary>
/// <param name="StandardDimensions">Standard-family dimensions keyed by name (<c>requests</c>, <c>input-tokens</c>, <c>output-tokens</c>, <c>tokens</c>).</param>
/// <param name="UnifiedStatus">The unified family's top-level status, or <see langword="null"/> if absent.</param>
/// <param name="UnifiedResetAt">The unified family's top-level reset time, or <see langword="null"/> if absent.</param>
/// <param name="UnifiedWindows">Unified-family time windows (e.g. <c>5h</c>) keyed by window name.</param>
/// <param name="RepresentativeClaim">The unified family's representative-claim value, or <see langword="null"/> if absent.</param>
/// <param name="RawHeaders">Every captured header, verbatim, keyed by lowercase name.</param>
public sealed record RateLimitSnapshotAdminView(
    IReadOnlyDictionary<string, RateLimitDimensionAdminView> StandardDimensions,
    string? UnifiedStatus,
    DateTimeOffset? UnifiedResetAt,
    IReadOnlyDictionary<string, RateLimitWindowAdminView> UnifiedWindows,
    string? RepresentativeClaim,
    IReadOnlyDictionary<string, string> RawHeaders);

/// <summary>A provider's most recently captured rate-limit header snapshot, mirroring the proxy's <c>ProviderRateLimitView</c>.</summary>
/// <param name="Snapshot">The typed projection of the captured headers.</param>
/// <param name="ObservedAtUtc">The UTC instant the most recent header in <see cref="Snapshot"/> was captured.</param>
/// <param name="IsStale">Whether <see cref="ObservedAtUtc"/> is older than the proxy's configured staleness threshold (§5.9) - the card dims and labels the "As of" footer when set.</param>
/// <param name="Projections">Per-dimension projected time-to-exhaustion, keyed by the same names as <see cref="RateLimitSnapshotAdminView.StandardDimensions"/>. A dimension absent here has no projection to show.</param>
public sealed record ProviderRateLimitAdminView(
    RateLimitSnapshotAdminView Snapshot,
    DateTimeOffset ObservedAtUtc,
    bool IsStale = false,
    IReadOnlyDictionary<string, RateLimitExhaustionAdminView>? Projections = null);

/// <summary>A projected time-to-exhaustion for one rate-limit dimension, mirroring the proxy's <c>RateLimitExhaustionProjection</c>.</summary>
/// <param name="TimeToExhaustion">How long until the dimension would hit zero at the observed burn rate.</param>
/// <param name="BurnRatePerMinute">The observed consumption rate, in the dimension's own unit, per minute.</param>
public sealed record RateLimitExhaustionAdminView(TimeSpan TimeToExhaustion, double BurnRatePerMinute);

/// <summary>One dimension history point for the Providers card's rate-limit trend chart, mirroring the proxy's <c>RateLimitHistoryPointView</c>.</summary>
/// <param name="BucketUtc">The minute bucket's start instant, UTC.</param>
/// <param name="Remaining">What was left in this bucket, or <see langword="null"/> if unknown that minute.</param>
/// <param name="Limit">The dimension's configured cap in this bucket, or <see langword="null"/> if unknown.</param>
public sealed record RateLimitHistoryPointAdminView(DateTimeOffset BucketUtc, long? Remaining, long? Limit);

/// <summary>The <c>GET /admin/providers/{key}/rate-limit-history</c> response, mirroring the proxy's <c>RateLimitHistoryResponse</c>.</summary>
/// <param name="Dimensions">History points per standard-family dimension name, chronologically ordered.</param>
public sealed record RateLimitHistoryResponseAdminView(IReadOnlyDictionary<string, IReadOnlyList<RateLimitHistoryPointAdminView>> Dimensions);

/// <summary>A configured model as returned by the management API.</summary>
/// <param name="ModelName">The client-facing model name.</param>
/// <param name="ProviderModelId">The upstream model identifier.</param>
/// <param name="Dialect">
/// The tool-call dialect detected for this model (e.g. <c>"hermes"</c>, <c>"emulated"</c>,
/// <c>"openai-native"</c>), or <see langword="null"/> when it has never been classified.
/// </param>
/// <param name="Confidence">
/// How <see cref="Dialect"/> was learned (e.g. <c>"Heuristic"</c>, <c>"Template"</c>, <c>"Observed"</c>,
/// <c>"Operator"</c>), or <see langword="null"/> alongside a null <see cref="Dialect"/>.
/// </param>
/// <param name="Enabled">
/// The operator's own Start/Stop state for this model, driving the per-model Start/Stop control in
/// Governance &gt; Providers. Never changed by a "Refresh from endpoint" scan - only by an explicit
/// toggle via <see cref="ProviderAdminClient.SetModelEnabledAsync"/>.
/// </param>
/// <param name="PresentUpstream">
/// Whether the most recent "Refresh from endpoint" scan reported this model.
/// <see langword="false"/> means the provider's endpoint didn't list it last time - not that it was
/// removed from configuration - and is shown as a distinct "not detected" state from a manually stopped
/// model.
/// </param>
public sealed record ModelAdminView(
    string ModelName,
    string ProviderModelId,
    string? Dialect = null,
    string? Confidence = null,
    bool Enabled = true,
    bool PresentUpstream = true);

/// <summary>
/// Which API flavors one provider's endpoint answers, as returned by
/// <see cref="ProviderAdminClient.ScanCapabilitiesAsync"/> and mirrored on <see cref="ProviderAdminView"/>.
/// A GUI-side mirror of the proxy's own <c>ProviderEndpointCapabilities</c> - this project deliberately does
/// not reference the proxy, so the wire shape is duplicated here rather than shared.
/// </summary>
/// <param name="ProviderKey">The provider key this describes.</param>
/// <param name="OpenAiCompatible">Whether <c>GET {base}/v1/models</c> answered - the only flavor routing uses.</param>
/// <param name="LmStudioNative">Whether LM Studio's native <c>GET {base}/api/v0/models</c> answered.</param>
/// <param name="OllamaNative">Whether Ollama's native <c>GET {base}/api/tags</c> answered.</param>
/// <param name="AnthropicCompatible">Whether the endpoint accepted an Anthropic-shaped probe.</param>
/// <param name="ScannedAtUtc">When the scan ran.</param>
/// <param name="ScanError">Why the scan could not complete, or <see langword="null"/> when it did.</param>
public sealed record ProviderEndpointCapabilitiesView(
    string ProviderKey,
    bool OpenAiCompatible,
    bool LmStudioNative,
    bool OllamaNative,
    bool AnthropicCompatible,
    DateTimeOffset ScannedAtUtc,
    string? ScanError = null);

/// <summary>The credential source for a configured header.</summary>
public static class HeaderValueSource
{
    /// <summary>A literal value is stored. Returned by the management API only when the header is unlocked
    /// (see <see cref="ProviderHeaderView.Locked"/>); a locked header reports this marker alone.</summary>
    public const string Literal = "literal";

    /// <summary>The value is read at request time from an environment variable (see <see cref="ProviderHeaderView.ValueEnvVar"/>).</summary>
    public const string EnvVar = "envVar";

    /// <summary>
    /// The value is stored in the router's protected secret store. Never returned by the management API,
    /// exactly like a locked <see cref="Literal"/> - see <see cref="ProviderHeaderView.Locked"/>.
    /// </summary>
    public const string Protected = "protected";

    /// <summary>Neither a literal value, a protected-store reference, nor an environment variable is configured.</summary>
    public const string None = "none";
}

/// <summary>
/// A custom HTTP header as returned by <c>GET /admin/providers</c>. Because one provider's headers mix
/// public configuration with secrets, readability is decided per header: an unlocked literal comes back in
/// <see cref="Value"/> for the editor to show, while a locked one is write-only and reports only its
/// <see cref="Source"/>. See <c>docs/gui/secret-field.md</c>.
/// </summary>
/// <param name="Name">The header name (e.g. <c>anthropic-version</c>).</param>
/// <param name="Source">One of <see cref="HeaderValueSource"/>: where this header's value comes from.</param>
/// <param name="ValueEnvVar">The environment variable name holding the value, when <paramref name="Source"/> is <see cref="HeaderValueSource.EnvVar"/>.</param>
/// <param name="Value">The literal value of an unlocked literal header; null when <paramref name="Locked"/>
/// is set or the value comes from anywhere else.</param>
/// <param name="Locked">Whether this header's value is a secret the router withholds from this GUI.</param>
public sealed record ProviderHeaderView(string Name, string Source, string? ValueEnvVar, string? Value = null, bool Locked = false);

/// <summary>
/// A custom HTTP header to write for a provider (<c>PUT /admin/providers/{key}</c>). A blank
/// <see cref="Value"/> and blank <see cref="ValueEnvVar"/> together preserve whatever is already stored
/// under this header's <see cref="Name"/>, since a locked value is never returned for the caller to resend.
/// </summary>
/// <param name="Name">The header name.</param>
/// <param name="Value">A literal value; takes precedence over <paramref name="ValueEnvVar"/> when non-empty.</param>
/// <param name="ValueEnvVar">The name of an environment variable holding the value, used when <paramref name="Value"/> is blank.</param>
/// <param name="Locked">Whether the literal value is a secret to withhold from future reads. Travels with
/// the header whether or not <paramref name="Value"/> is resent, so a stored secret can be locked without
/// retyping it, and it qualifies the blank rule: blank under <see langword="true"/> preserves the stored
/// value, while blank under an explicit <see langword="false"/> clears it (the editor's unlock). Null is
/// the legacy shape - blank preserves, and a literal stores locked. Ignored for an env-var-backed
/// header.</param>
public sealed record ProviderHeaderWriteModel(string? Name, string? Value, string? ValueEnvVar, bool? Locked = null);

/// <summary>The <c>GET /admin/providers</c> response envelope.</summary>
/// <param name="Providers">All configured providers.</param>
public sealed record ProvidersSnapshot(IReadOnlyList<ProviderAdminView> Providers);

/// <summary>
/// The body sent to add or edit a provider. Fields fall back to the existing provider's value when null.
/// Authentication is expressed purely via <see cref="Headers"/>.
/// </summary>
/// <param name="BaseUrl">The provider's absolute base URL.</param>
/// <param name="AuthHeaderName">The header carrying the credential.</param>
/// <param name="Headers">The full set of custom headers to store (replaces the existing set, one header at
/// a time via <see cref="ProviderHeaderWriteModel"/>'s blank-preserves-existing rule); null keeps them.</param>
/// <param name="IsFree">Whether this provider costs nothing; null keeps the existing value.</param>
/// <param name="Enabled">Whether the provider is switched on; null keeps the existing value. Prefer the
/// dedicated <see cref="ProviderEnabledWriteRequest"/> route for a pure on/off toggle.</param>
/// <param name="ProviderName">The user-friendly display name for this provider. Null preserves the existing value.
/// Any other value (including empty/whitespace) is normalized: empty/whitespace becomes null (an explicit clear);
/// non-empty becomes the trimmed string.</param>
/// <param name="ProviderType">The name of the <see cref="Admin.ProviderType"/> member selected in the
/// editor. Normalized exactly like <paramref name="ProviderName"/>: null preserves the existing value, so a
/// partial write can't silently reset a provider's type; any other value is trimmed, and empty/whitespace
/// becomes null (an explicit clear).</param>
public sealed record ProviderWriteRequest(
    string? BaseUrl,
    string? AuthHeaderName,
    IReadOnlyList<ProviderHeaderWriteModel>? Headers = null,
    bool? IsFree = null,
    bool? Enabled = null,
    string? ProviderName = null,
    string? ProviderType = null);

/// <summary>
/// The body sent to switch a provider on or off (<c>PUT /admin/providers/{key}/enabled</c>). The dedicated
/// route preserves every other configured field, including the AWS ones a generic provider write drops.
/// </summary>
/// <param name="Enabled">The provider's new on/off state.</param>
public sealed record ProviderEnabledWriteRequest(bool Enabled);

/// <summary>The body sent to add or edit a model under a provider.</summary>
/// <param name="ProviderModelId">The upstream model identifier; the server defaults it to the model name when blank.</param>
public sealed record ModelWriteRequest(string? ProviderModelId);

/// <summary>
/// The body sent to switch a model on or off (<c>PUT /admin/providers/{key}/models/{modelName}/enabled</c>).
/// The per-model twin of <see cref="ProviderEnabledWriteRequest"/>.
/// </summary>
/// <param name="Enabled">The model's new on/off state.</param>
public sealed record ModelEnabledWriteRequest(bool Enabled);

/// <summary>
/// The body sent to pin how a model expresses tool calls
/// (<c>PUT /admin/providers/{key}/models/{modelName}/tool-dialect</c>), overriding automatic detection.
/// </summary>
/// <param name="Dialect">
/// The dialect name to pin at operator confidence, which no automatic scan may overwrite, or
/// <see langword="null"/>/empty to clear the pin and return the model to automatic detection.
/// </param>
public sealed record ModelToolDialectWriteRequest(string? Dialect);

/// <summary>
/// One operator-authored price override, as returned by <c>GET /admin/price-overrides</c> - the §5.7
/// resolution ladder's top rung. Backs the Governance price-overrides pane.
/// </summary>
/// <param name="SourceName">The aggregator source this override applies to (e.g. <c>LiteLLM</c>).</param>
/// <param name="AggregatorModelKey">The source's own model key this override matches, verbatim.</param>
/// <param name="ModelName">The client-facing <c>ModelName</c> the override resolves to.</param>
public sealed record PriceOverrideView(string SourceName, string AggregatorModelKey, string ModelName);

/// <summary>The body for adding or replacing a price override (<c>PUT /admin/price-overrides</c>).</summary>
/// <param name="SourceName">The aggregator source this override applies to.</param>
/// <param name="AggregatorModelKey">The source's own model key this override matches, verbatim.</param>
/// <param name="ModelName">The client-facing <c>ModelName</c> to resolve to; must already be configured.</param>
public sealed record PriceOverrideWriteRequest(string SourceName, string AggregatorModelKey, string ModelName);

/// <summary>
/// One configured model's current price-resolution state, as returned by <c>GET /admin/price-resolution</c>.
/// Backs the Governance price-overrides pane's read-only diagnosis view.
/// </summary>
/// <param name="ModelName">The client-facing <c>ModelName</c>.</param>
/// <param name="Provider">The configured provider serving this model.</param>
/// <param name="Resolved">Whether the catalog currently holds a fresh price for this model.</param>
/// <param name="IsApproximate">Whether the resolved price was matched via a ladder rung below Exact/OperatorOverride.</param>
public sealed record PriceResolutionDiagnosisView(string ModelName, string Provider, bool Resolved, bool IsApproximate);

/// <summary>
/// The tool-call dialect names the Governance UI offers for an operator override.
/// </summary>
/// <remarks>
/// Duplicated here rather than referenced from <c>ToolCallDialectRegistry</c> because that type is internal
/// to the router assembly and this one is the GUI's own contract - the same reason every other write-request
/// record in this file restates its shape. The server validates the value regardless, so a list that drifts
/// produces a rejected write rather than a silently wrong pin.
/// </remarks>
public static class ToolCallDialectNames
{
    /// <summary>Every dialect an operator may pin, in the order the UI lists them.</summary>
    public static IReadOnlyList<string> All { get; } =
        ["openai-native", "constrained", "emulated", "hermes", "mistral", "llama3-json", "function-call"];
}

/// <summary>
/// The body sent to set a provider's monthly budget caps (<c>PUT /admin/providers/{key}/budget</c>). A null
/// cap clears that dimension; both null removes the budget entirely.
/// </summary>
/// <param name="DollarCap">The cap for the window, or null for no dollar budget.</param>
/// <param name="TokenCap">The cap for the window, or null for no token budget.</param>
/// <param name="WindowKind">The window the caps reset on (<c>"Monthly"</c>, <c>"Weekly"</c>, or <c>"RollingHours"</c>); null keeps today's window.</param>
/// <param name="WindowHours">Required and must be positive when <paramref name="WindowKind"/> is <c>"RollingHours"</c>; otherwise ignored.</param>
public sealed record ProviderBudgetWriteRequest(decimal? DollarCap, long? TokenCap, string? WindowKind = null, int? WindowHours = null);

/// <summary>
/// The result of <c>POST /admin/providers/{key}/discover-models</c>: the model ids the provider's own
/// endpoint reports, or an explanation when the provider doesn't support OpenAI-shaped discovery.
/// </summary>
/// <param name="Supported">Whether the provider answered an OpenAI-shaped model list.</param>
/// <param name="Models">The discovered model ids (empty when unsupported).</param>
/// <param name="Error">A human-readable reason when <paramref name="Supported"/> is false.</param>
public sealed record DiscoverModelsResult(bool Supported, IReadOnlyList<string> Models, string? Error);

/// <summary>
/// Totals over a preset window, as returned by <c>GET /admin/usage/summary</c> (Phase 4, §5.15). Backs the
/// header ticker's System Tokens tile and other summary displays.
/// </summary>
/// <param name="Requests">Total requests in the window.</param>
/// <param name="UnpricedRequests">How many of <paramref name="Requests"/> had an unknown cost (§5.6) - never silently folded into a zero total.</param>
/// <param name="PromptTokens">Total prompt/input tokens.</param>
/// <param name="CompletionTokens">Total completion/output tokens.</param>
/// <param name="CacheCreationTokens">Total cache-creation input tokens.</param>
/// <param name="CacheReadTokens">Total cache-read input tokens.</param>
/// <param name="CostUsd">Total USD cost summed over priced requests only.</param>
public sealed record UsageSummaryView(
    long Requests,
    long UnpricedRequests,
    long PromptTokens,
    long CompletionTokens,
    long CacheCreationTokens,
    long CacheReadTokens,
    decimal CostUsd);

/// <summary>
/// One aggregated bucket, as returned by <c>GET /admin/usage/rollup</c> (Phase 4, §5.15) - the Model
/// Distribution / Cost Analytics chart feed.
/// </summary>
/// <param name="BucketStartUtc">Meaningful only when the query grouped by <c>"day"</c>; otherwise the query's <c>from</c> bound.</param>
/// <param name="BucketWidth">The ISO-8601 duration the query was made at (<c>"PT1H"</c> or <c>"P1D"</c>).</param>
/// <param name="GroupKey">The model name, provider key, or ISO day string this row aggregates, depending on the query's <c>groupBy</c>.</param>
/// <param name="Requests">Requests in this bucket.</param>
/// <param name="UnpricedRequests">How many of <paramref name="Requests"/> had an unknown cost.</param>
/// <param name="PromptTokens">Prompt/input tokens in this bucket.</param>
/// <param name="CompletionTokens">Completion/output tokens in this bucket.</param>
/// <param name="CacheCreationTokens">Cache-creation input tokens in this bucket.</param>
/// <param name="CacheReadTokens">Cache-read input tokens in this bucket.</param>
/// <param name="CostUsd">USD cost summed over priced requests in this bucket.</param>
public sealed record UsageRollupBucketView(
    DateTimeOffset BucketStartUtc,
    string BucketWidth,
    string GroupKey,
    long Requests,
    long UnpricedRequests,
    long PromptTokens,
    long CompletionTokens,
    long CacheCreationTokens,
    long CacheReadTokens,
    decimal CostUsd);

