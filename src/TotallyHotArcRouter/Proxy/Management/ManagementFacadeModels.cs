using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

namespace TotallyHot.ArcRouter.Proxy.Management;

/// <summary>The kind of failure a <see cref="ManagementFacade"/> call reports, so each surface can map it to its own error shape.</summary>
public enum ManagementErrorType
{
    /// <summary>No error; <see cref="ManagementResult{T}.Success"/> is <see langword="true"/>.</summary>
    None,

    /// <summary>The referenced provider/model does not exist.</summary>
    NotFound,

    /// <summary>The request itself is invalid (validation failure, negative budget cap, etc.).</summary>
    InvalidRequest,

    /// <summary>The operation requires a dependency that isn't configured (e.g. no budget store).</summary>
    Unavailable,

    /// <summary>An unexpected failure occurred (e.g. a persistence error); the message is deliberately generic.</summary>
    Internal
}

/// <summary>
/// The outcome of a <see cref="ManagementFacade"/> write: either the refreshed <typeparamref name="T"/> on
/// success, or an <see cref="ManagementErrorType"/> and message on failure. Both the REST endpoints and the
/// MCP tools translate this into their own transport's error shape (HTTP status codes / MCP tool errors).
/// </summary>
public readonly record struct ManagementResult<T>(bool Success, T? Value, ManagementErrorType ErrorType, string? ErrorMessage)
{
    /// <summary>Creates a successful result carrying <paramref name="value"/>.</summary>
    public static ManagementResult<T> Ok(T value) => new(true, value, ManagementErrorType.None, null);

    /// <summary>Creates a failed result with the given <paramref name="errorType"/> and <paramref name="message"/>.</summary>
    public static ManagementResult<T> Fail(ManagementErrorType errorType, string message) => new(false, default, errorType, message);
}

/// <summary>The credential source for a stored custom header, mirrored in <c>TotallyHot.ArcRouter.Gui.Admin.HeaderValueSource</c>'s naming.</summary>
public static class HeaderValueSource
{
    /// <summary>A literal value is stored (never returned by any surface; only this source marker is).</summary>
    public const string Literal = "literal";

    /// <summary>The value is read at request time from an environment variable (see <see cref="HeaderView.ValueEnvVar"/>).</summary>
    public const string EnvVar = "envVar";

    /// <summary>
    /// The value is stored in the protected secret store (<c>docs/router/secrets-at-rest-plan.md</c> §3),
    /// referenced by <see cref="TotallyHot.ArcRouter.Models.ProviderHeader.ValueSecretRef"/>. Never returned
    /// by any management surface, exactly like a locked <see cref="Literal"/> - see §4's write-only
    /// invariant.
    /// </summary>
    public const string Protected = "protected";

    /// <summary>Neither a literal value, a protected-store reference, nor an environment variable is configured.</summary>
    public const string None = "none";
}

/// <summary>A single provider as returned to a management caller, with credentials masked.</summary>
/// <param name="Key">The provider key.</param>
/// <param name="Name">The user-friendly display name for this provider; null when not set.</param>
/// <param name="BaseUrl">The provider's absolute base URL.</param>
/// <param name="AuthHeaderName">The header carrying the credential.</param>
/// <param name="Models">The models configured to route to this provider.</param>
/// <param name="Headers">The provider's configured custom headers, masked (see <see cref="HeaderView"/>).</param>
/// <param name="IsFree">Whether this provider costs nothing, making its models' cost a known zero rather than unknown.</param>
/// <param name="DollarCap">The provider's monthly USD budget cap, or null for no dollar budget.</param>
/// <param name="TokenCap">The provider's monthly total-token budget cap, or null for no token budget.</param>
/// <param name="DollarSpent">USD spent against this provider in the current month.</param>
/// <param name="TokensUsed">
/// Total tokens used against this provider in the current month - prompt, completion, and cache
/// creation/read tokens combined (see <see cref="ProviderBudgetState.TokensUsed"/>).
/// </param>
/// <param name="Enabled">
/// Whether the operator has this provider switched on. Enforced immediately on the next request via
/// <see cref="ProviderOptions.Enabled"/>'s own documented routing-path gate
/// (<see cref="TotallyHot.ArcRouter.Proxy.IModelRouteResolver.IsProviderEnabled"/>), no restart needed.
/// </param>
/// <param name="EndpointCapabilities">
/// Which API flavors this provider's endpoint answers, as last recorded by
/// <see cref="ManagementFacade.ScanCapabilitiesAsync"/> (<c>docs/router/tool-call-normalization.md</c>
/// §3.3), or <see langword="null"/> when no capability store is wired up or the endpoint has never been
/// scanned.
/// </param>
/// <param name="ProviderType">
/// The provider family the operator selected in the editor (see <see cref="ProviderOptions.ProviderType"/>),
/// or <see langword="null"/> for a provider configured before the field existed. Returned so that reopening
/// a provider restores the right type rather than defaulting to "Other".
/// </param>
/// <param name="UsageLastRecordedAtUtc">
/// The UTC instant the most recent request's usage was recorded against this provider (the same instant
/// backing <see cref="DollarSpent"/>/<see cref="TokensUsed"/>), or <see langword="null"/> if no usage has
/// been recorded this month yet. Backs the Governance card's "estimated from intercepted traffic" footer.
/// </param>
/// <param name="RateLimit">
/// This provider's most recently captured rate-limit response headers - <c>anthropic-ratelimit-*</c>
/// (<c>docs/router/anthropic-reported-usage-plan.md</c> §5) or OpenAI's <c>x-ratelimit-*</c>
/// (<c>docs/router/openai-format-usage-accuracy-plan.md</c> §6.2), whichever family this provider's
/// responses carry - or <see langword="null"/> when no repository is wired up or no such header has ever
/// been captured for this provider.
/// </param>
/// <param name="WindowKind">
/// The <see cref="BudgetWindow"/> kind the caps above reset on: <c>"Monthly"</c>, <c>"Weekly"</c>, or
/// <c>"RollingHours"</c> (Phase 4, §5.10). <c>"Monthly"</c> when no budget store is wired up.
/// </param>
/// <param name="NextResetUtc">
/// The UTC instant the current period ends and spend resets to zero, computed live from
/// <paramref name="WindowKind"/> - backs the budget bar's "resets in 2h 10m" text.
/// </param>
/// <param name="HasStoredAdminKey">
/// Whether a reconciliation Admin API key is stored for this provider (docs/router/secrets-at-rest-plan.md
/// §4/§7) - a boolean only, never the key itself, matching every other secret-existence flag this view
/// exposes. <see langword="false"/> both when no key was ever saved and on a platform where the protected
/// store is unavailable.
/// </param>
/// <param name="ReportedUsage">
/// This provider's own reported per-model daily token usage (docs/router/secrets-at-rest-plan.md §8.1),
/// currently populated for <c>anthropic</c> only, or <see langword="null"/> when no price catalog
/// repository is wired up or nothing has been fetched yet (no Admin API key configured, or the first cycle
/// hasn't run).
/// </param>
/// <param name="AdminAction">
/// The outcome of the most recent admin-initiated interaction with this provider (refresh from endpoint,
/// capability scan, discovery), or <see langword="null"/> when no interaction status store is wired up or
/// none has happened yet since the router started. Backs the Governance card's warning icon - see
/// <see cref="ProviderInteractionStatusStore"/>.
/// </param>
/// <param name="LiveTraffic">
/// The outcome of the most recent classified live-traffic event from the hot request path (e.g. an
/// out-of-credits response - docs/adr/0004-surface-out-of-credits-provider-failures-on-the-providers-
/// tab.md), or <see langword="null"/> when no interaction status store is wired up or nothing has been
/// classified yet since the router started. Independent of <paramref name="AdminAction"/> - a successful
/// admin action never clears a live-traffic warning and vice versa, so both can be shown at once.
/// </param>
public sealed record ProviderView(
    string Key,
    string? Name,
    string BaseUrl,
    string AuthHeaderName,
    IReadOnlyList<ModelView> Models,
    IReadOnlyList<HeaderView> Headers,
    bool IsFree = false,
    decimal? DollarCap = null,
    long? TokenCap = null,
    decimal DollarSpent = 0m,
    long TokensUsed = 0L,
    bool Enabled = true,
    ProviderEndpointCapabilities? EndpointCapabilities = null,
    string? ProviderType = null,
    DateTimeOffset? UsageLastRecordedAtUtc = null,
    ProviderRateLimitView? RateLimit = null,
    string WindowKind = "Monthly",
    DateTimeOffset? NextResetUtc = null,
    bool HasStoredAdminKey = false,
    ProviderReportedUsageView? ReportedUsage = null,
    ProviderInteractionStatus? AdminAction = null,
    ProviderInteractionStatus? LiveTraffic = null);

/// <summary>
/// A provider's own reported per-model daily token usage (docs/router/secrets-at-rest-plan.md §8.1), as
/// returned to a management caller.
/// </summary>
/// <param name="Rows">Every currently-stored row, ordered by day then model.</param>
/// <param name="FetchedAtUtc">The most recent instant any of <paramref name="Rows"/> was fetched - the GUI's "Fetched" footer.</param>
public sealed record ProviderReportedUsageView(IReadOnlyList<ReportedUsageRowView> Rows, DateTimeOffset FetchedAtUtc);

/// <summary>One (day, model) row of <see cref="ProviderReportedUsageView.Rows"/>. Raw reported counts - no derived totals.</summary>
/// <param name="UsageDay">The UTC calendar day this usage was reported for.</param>
/// <param name="Model">The provider's own model identifier for this row.</param>
/// <param name="InputTokens">Uncached input tokens for this day/model.</param>
/// <param name="OutputTokens">Output tokens for this day/model.</param>
/// <param name="CacheCreationTokens">Cache-creation (cache-write) input tokens for this day/model.</param>
/// <param name="CacheReadTokens">Cache-read input tokens for this day/model.</param>
public sealed record ReportedUsageRowView(
    DateOnly UsageDay,
    string Model,
    long InputTokens,
    long OutputTokens,
    long CacheCreationTokens,
    long CacheReadTokens);

/// <summary>
/// A provider's most recently captured rate-limit header snapshot, as returned to a management caller.
/// </summary>
/// <param name="Snapshot">The typed projection of the captured headers.</param>
/// <param name="ObservedAtUtc">
/// The UTC instant the most recent header in <see cref="Snapshot"/> was captured. Server-reported numbers
/// are trustworthy only as of this instant - the GUI's "As of" footer.
/// </param>
/// <param name="IsStale">
/// Whether <see cref="ObservedAtUtc"/> is older than the configured staleness threshold (§5.9) - the GUI
/// dims the "As of" footer and labels it stale when set, rather than presenting an old snapshot as current.
/// The snapshot itself is unaffected: a stale/absent capture never clears the last-known values.
/// </param>
/// <param name="Projections">
/// Per-dimension projected time-to-exhaustion (§5.9), keyed by the same dimension names as
/// <see cref="RateLimitSnapshotView.StandardDimensions"/>. A dimension is absent here - not present with a
/// null value - when no projection could be made (flat, refilled, reset-before-empty, or too little
/// history); the GUI shows nothing for that dimension rather than fabricating urgency.
/// </param>
public sealed record ProviderRateLimitView(
    RateLimitSnapshotView Snapshot,
    DateTimeOffset ObservedAtUtc,
    bool IsStale,
    IReadOnlyDictionary<string, RateLimitExhaustionProjection> Projections);

/// <summary>One dimension's history points for the Providers card's rate-limit trend chart (§5.9).</summary>
/// <param name="BucketUtc">The minute bucket's start instant, UTC.</param>
/// <param name="Remaining">What was left in this bucket, or <see langword="null"/> if the header was absent/unparsable that minute.</param>
/// <param name="Limit">The dimension's configured cap in this bucket, or <see langword="null"/> if unknown.</param>
public sealed record RateLimitHistoryPointView(DateTimeOffset BucketUtc, long? Remaining, long? Limit);

/// <summary>The <c>GET /admin/providers/{key}/rate-limit-history</c> response: per-dimension history series.</summary>
/// <param name="Dimensions">History points per standard-family dimension name, chronologically ordered.</param>
public sealed record RateLimitHistoryResponse(IReadOnlyDictionary<string, IReadOnlyList<RateLimitHistoryPointView>> Dimensions);

/// <summary>A single configured model as returned to a management caller.</summary>
/// <param name="ModelName">The client-facing model name.</param>
/// <param name="ProviderModelId">The upstream model identifier.</param>
/// <param name="Dialect">
/// The tool-call dialect detected for this model (<c>docs/router/tool-call-normalization.md</c> §3.2), or
/// <see langword="null"/> when it has never been classified - no capability store is wired up, or no scan
/// or live observation has run yet for this (provider, model) pair.
/// </param>
/// <param name="Confidence">
/// How <see cref="Dialect"/> was learned (e.g. <c>"Heuristic"</c>, <c>"Template"</c>, <c>"Observed"</c>,
/// <c>"Operator"</c>), or <see langword="null"/> alongside a null <see cref="Dialect"/>.
/// </param>
/// <param name="Enabled">
/// The operator's own Start/Stop state for this model (<see cref="ModelRouteEntry.Enabled"/>) - never
/// changed by a scan, only by an explicit toggle.
/// </param>
/// <param name="PresentUpstream">
/// Whether the most recent "Refresh from endpoint" scan reported this model
/// (<see cref="ModelRouteEntry.PresentUpstream"/>). <see langword="false"/> means the provider's endpoint
/// didn't list it last time, not that it was removed from configuration.
/// </param>
public sealed record ModelView(
    string ModelName,
    string ProviderModelId,
    string? Dialect = null,
    string? Confidence = null,
    bool Enabled = true,
    bool PresentUpstream = true);

/// <summary>
/// A single custom header as returned to a management caller. A header carries a mix of public
/// configuration and secrets, so readability is per-header: an unlocked literal is returned in
/// <see cref="Value"/> so the operator can read and edit it, while a locked one is write-only and only
/// its <see cref="Source"/> is reported. <see cref="ManagementFacade"/>'s projection is the single place
/// that decides this - see <c>docs/gui/secret-field.md</c>.
/// </summary>
/// <param name="Name">The header name.</param>
/// <param name="Source">One of <see cref="HeaderValueSource"/>: where this header's value comes from.</param>
/// <param name="ValueEnvVar">The environment variable name holding the value, when <paramref name="Source"/> is <see cref="HeaderValueSource.EnvVar"/>.</param>
/// <param name="Value">The literal value, but only for an unlocked literal header; null whenever
/// <paramref name="Locked"/> is set or the value comes from elsewhere.</param>
/// <param name="Locked">Whether this header's value is a secret withheld from management callers.</param>
public sealed record HeaderView(string Name, string Source, string? ValueEnvVar, string? Value = null, bool Locked = false);

/// <summary>The <c>GET /admin/providers</c> (and MCP <c>list_providers</c>) response envelope.</summary>
/// <param name="Providers">All configured providers, ordered by key.</param>
public sealed record ProvidersResponse(IReadOnlyList<ProviderView> Providers);

/// <summary>The body for adding or editing a provider. Fields fall back to the existing value when
/// omitted. Authentication is expressed purely via <paramref name="Headers"/>.</summary>
/// <param name="BaseUrl">The provider's absolute base URL.</param>
/// <param name="AuthHeaderName">The header carrying the credential.</param>
/// <param name="Headers">The full set of custom headers to store (replaces the existing set, one header at
/// a time via the blank-preserves-existing rule); null keeps the existing headers (legacy callers).</param>
/// <param name="IsFree">Whether this provider costs nothing; null keeps the existing value, so a partial
/// write can't silently un-free a provider.</param>
/// <param name="Enabled">Whether the provider is switched on; null keeps the existing value, so a partial
/// write can't silently restart a stopped provider.</param>
/// <param name="ProviderName">The user-friendly display name for this provider. Null preserves the existing value.
/// Any other value (including empty/whitespace) is normalized: empty/whitespace becomes null (an explicit clear);
/// non-empty becomes the trimmed string.</param>
/// <param name="ProviderType">The provider family selected in the editor (see
/// <see cref="ProviderOptions.ProviderType"/>). Normalized exactly like <paramref name="ProviderName"/>:
/// null keeps the existing value, so a partial write can't silently reset a provider's type; any other
/// value is trimmed, and empty/whitespace becomes null (an explicit clear).</param>
public sealed record ProviderWriteRequest(
    string? BaseUrl,
    string? AuthHeaderName,
    IReadOnlyList<HeaderWriteRequest>? Headers = null,
    bool? IsFree = null,
    bool? Enabled = null,
    string? ProviderName = null,
    string? ProviderType = null);

/// <summary>
/// The body sent to switch a provider on or off (<c>PUT /admin/providers/{key}/enabled</c>). Unlike
/// <see cref="ProviderWriteRequest.Enabled"/> this is non-nullable: the dedicated route exists to state the
/// new value outright rather than leaving it optional among a full provider edit.
/// </summary>
/// <param name="Enabled">The provider's new on/off state.</param>
public sealed record ProviderEnabledWriteRequest(bool Enabled);

/// <summary>The body sent to store a secret (<c>PUT /admin/secrets/{name}</c>, docs/router/secrets-at-rest-plan.md §7). See <see cref="ManagementFacade.SetSecret"/> for which names are accepted.</summary>
/// <param name="Value">The secret value to store.</param>
public sealed record SecretWriteRequest(string Value);

/// <summary>A single custom header to store for a provider.</summary>
/// <param name="Name">The header name.</param>
/// <param name="Value">A literal value; takes precedence over <paramref name="ValueEnvVar"/> when non-empty.</param>
/// <param name="ValueEnvVar">The name of an environment variable holding the value, used when <paramref name="Value"/> is blank.</param>
/// <param name="Locked">Whether the literal value is a secret to withhold from future reads. Applies to the
/// header whether or not <paramref name="Value"/> is resent, so a value can be locked without retyping it,
/// and it also decides what a blank write means: <see langword="true"/> preserves the stored value (the
/// caller was never shown it), while an explicit <see langword="false"/> clears it (the caller could see
/// the field and left it empty - this is how the editor's unlock clears a secret). Null is the legacy
/// shape, kept for callers that predate the flag: blank preserves, and a literal stores locked. Ignored
/// for an env-var-backed header, which always stores unlocked.</param>
public sealed record HeaderWriteRequest(string? Name, string? Value, string? ValueEnvVar, bool? Locked = null);

/// <summary>The body for adding or editing a model under a provider.</summary>
/// <param name="ProviderModelId">The upstream model identifier; defaults to the model name when blank.</param>
public sealed record ModelWriteRequest(string? ProviderModelId);

/// <summary>The body sent to switch a model on or off (<c>PUT /admin/providers/{key}/models/{modelName}/enabled</c>).</summary>
/// <param name="Enabled">The model's new on/off state.</param>
public sealed record ModelEnabledWriteRequest(bool Enabled);

/// <summary>
/// The body sent to pin how a model expresses tool calls
/// (<c>PUT /admin/providers/{key}/models/{modelName}/tool-dialect</c>).
/// </summary>
/// <param name="Dialect">
/// A <see cref="TotallyHot.ArcRouter.Proxy.Translation.ToolCalling.ToolCallDialect.Name"/> to pin at
/// <see cref="TotallyHot.ArcRouter.Proxy.Translation.ToolCalling.DetectionConfidence.Operator"/>, or
/// <see langword="null"/>/empty to clear the pin and hand the model back to automatic detection.
/// </param>
public sealed record ModelToolDialectWriteRequest(string? Dialect);

/// <summary>
/// The body for setting a provider's budget caps and reset window. A null cap clears that dimension; both
/// null removes the budget. Caps must be non-negative.
/// </summary>
/// <param name="DollarCap">The cap for the window, or null for no dollar budget.</param>
/// <param name="TokenCap">The cap for the window, or null for no token budget.</param>
/// <param name="WindowKind">
/// The window the caps reset on: <c>"Monthly"</c>, <c>"Weekly"</c>, or <c>"RollingHours"</c>. Null (the
/// default) keeps today's behavior - <see cref="ProviderBudgetStore.SetBudget"/> defaults to Monthly.
/// </param>
/// <param name="WindowHours">Required and must be positive when <paramref name="WindowKind"/> is <c>"RollingHours"</c>; otherwise ignored.</param>
public sealed record ProviderBudgetWriteRequest(decimal? DollarCap, long? TokenCap, string? WindowKind = null, int? WindowHours = null);

/// <summary>
/// The body for adding or replacing an operator price override (§5.7's <see cref="ResolutionRung.OperatorOverride"/> rung).
/// </summary>
/// <param name="SourceName">The aggregator source this override applies to (e.g. <c>LiteLLM</c>).</param>
/// <param name="AggregatorModelKey">The source's own model key this override matches, verbatim.</param>
/// <param name="ModelName">The client-facing <c>ModelRouting:ModelList[].ModelName</c> to resolve to; must already be configured.</param>
public sealed record PriceOverrideWriteRequest(string SourceName, string AggregatorModelKey, string ModelName);

/// <summary>
/// One configured model's current price-resolution state, as returned by
/// <see cref="ManagementFacade.GetPriceResolutionDiagnosis"/>. Backs the Governance price-overrides pane's
/// read-only diagnosis view.
/// </summary>
/// <param name="ModelName">The client-facing <c>ModelRouting:ModelList[].ModelName</c>.</param>
/// <param name="Provider">The configured provider serving this model.</param>
/// <param name="Resolved">Whether the catalog currently holds a fresh price for this (model, provider) cell.</param>
/// <param name="IsApproximate">
/// Whether the resolved price (when <paramref name="Resolved"/>) was matched via a resolution-ladder rung
/// below <c>Exact</c>/<c>OperatorOverride</c> - a disclosed estimate rather than an exact match.
/// </param>
public sealed record PriceResolutionDiagnosisView(string ModelName, string Provider, bool Resolved, bool IsApproximate);

/// <summary>The result of a model-discovery call.</summary>
/// <param name="Supported">Whether the provider answered an OpenAI-shaped model list.</param>
/// <param name="Models">The discovered model ids (empty when unsupported).</param>
/// <param name="Error">A human-readable reason when <paramref name="Supported"/> is false.</param>
public sealed record DiscoverModelsResponse(bool Supported, IReadOnlyList<string> Models, string? Error);
