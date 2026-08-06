namespace TotallyHot.ArcRouter.Gui.Admin;

/// <summary>
/// A provider as returned by the proxy's <c>GET /admin/providers</c> endpoint. Credentials are masked:
/// the literal API key is never sent to the client, only <see cref="HasApiKey"/> and, if configured,
/// the <see cref="ApiKeyEnvVar"/> name.
/// </summary>
/// <param name="Key">The provider key (e.g. <c>openai</c>).</param>
/// <param name="Name">The user-friendly display name for this provider (e.g. <c>OpenAI API</c>); displayed in the Governance UI.</param>
/// <param name="BaseUrl">The provider's absolute base URL.</param>
/// <param name="AuthHeaderName">The header carrying the credential (e.g. <c>Authorization</c>).</param>
/// <param name="AuthHeaderScheme">The scheme prefixed to the credential (e.g. <c>Bearer</c>; may be empty).</param>
/// <param name="HasApiKey">Whether a literal API key is stored for this provider (used by the edit dialog only).</param>
/// <param name="ApiKeyEnvVar">The environment variable name holding the key, if configured (used by the edit dialog only).</param>
/// <param name="Models">The models configured to route to this provider.</param>
/// <param name="Headers">The provider's configured custom headers (literal values are returned; secrets live in env vars).</param>
/// <param name="IsFree">Whether this provider costs nothing (a local runtime, say), making its models' cost a known zero rather than unknown.</param>
/// <param name="DollarCap">The provider's monthly USD budget cap, or null for no dollar budget.</param>
/// <param name="TokenCap">The provider's monthly total-token budget cap, or null for no token budget.</param>
/// <param name="DollarSpent">USD spent against this provider in the current month.</param>
/// <param name="TokensUsed">Total (prompt + completion) tokens used against this provider in the current month.</param>
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
public sealed record ProviderAdminView(
    string Key,
    string? Name,
    string BaseUrl,
    string AuthHeaderName,
    string AuthHeaderScheme,
    bool HasApiKey,
    string? ApiKeyEnvVar,
    IReadOnlyList<ModelAdminView> Models,
    IReadOnlyList<ProviderHeaderView> Headers,
    bool IsFree = false,
    decimal? DollarCap = null,
    long? TokenCap = null,
    decimal DollarSpent = 0m,
    long TokensUsed = 0L,
    bool Enabled = true,
    string? ProviderType = null,
    ProviderEndpointCapabilitiesView? EndpointCapabilities = null);

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

/// <summary>The credential source for a configured header, mirroring <see cref="ProviderCredentialModes"/>'s naming.</summary>
public static class HeaderValueSource
{
    /// <summary>A literal value is stored. Returned by the management API only when the header is unlocked
    /// (see <see cref="ProviderHeaderView.Locked"/>); a locked header reports this marker alone.</summary>
    public const string Literal = "literal";

    /// <summary>The value is read at request time from an environment variable (see <see cref="ProviderHeaderView.ValueEnvVar"/>).</summary>
    public const string EnvVar = "envVar";

    /// <summary>Neither a literal value nor an environment variable is configured.</summary>
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
/// under this header's <see cref="Name"/> (a locked value is never returned for the caller to resend, so
/// this mirrors <see cref="ProviderWriteRequest.ApiKey"/>'s literal-mode blank-preserves-existing rule).
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

/// <summary>The credential source a <see cref="ProviderWriteRequest"/> selects for a provider.</summary>
public static class ProviderCredentialModes
{
    /// <summary>Store a literal key. A blank <see cref="ProviderWriteRequest.ApiKey"/> keeps the existing one.</summary>
    public const string Literal = "literal";

    /// <summary>Reference an environment variable (<see cref="ProviderWriteRequest.ApiKeyEnvVar"/>) holding the key.</summary>
    public const string EnvVar = "envVar";

    /// <summary>No credential (e.g. a local, unauthenticated endpoint). Clears any stored key and env-var name.</summary>
    public const string None = "none";
}

/// <summary>
/// The body sent to add or edit a provider. Non-credential fields (<see cref="BaseUrl"/>,
/// <see cref="AuthHeaderName"/>, <see cref="AuthHeaderScheme"/>) fall back to the existing provider's value
/// when null. Credentials are driven by <see cref="CredentialMode"/> so the caller can switch between key
/// sources or clear them: only a blank literal <see cref="ApiKey"/> under <see cref="ProviderCredentialModes.Literal"/>
/// preserves the stored secret (which the GUI never receives and so cannot resend). When
/// <see cref="CredentialMode"/> is null, both credential fields independently fall back on blank (legacy behavior).
/// </summary>
/// <param name="BaseUrl">The provider's absolute base URL.</param>
/// <param name="AuthHeaderName">The header carrying the credential.</param>
/// <param name="AuthHeaderScheme">The scheme prefixed to the credential (empty for a raw key).</param>
/// <param name="ApiKey">A literal API key; blank under the literal mode preserves any existing stored key.</param>
/// <param name="ApiKeyEnvVar">The name of an environment variable holding the key.</param>
/// <param name="CredentialMode">
/// One of <see cref="ProviderCredentialModes"/> selecting the credential source, or null for legacy
/// fall-back-on-blank behavior.
/// </param>
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
    string? AuthHeaderScheme,
    string? ApiKey,
    string? ApiKeyEnvVar,
    string? CredentialMode = null,
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
/// <param name="DollarCap">The monthly USD cap, or null for no dollar budget.</param>
/// <param name="TokenCap">The monthly total-token cap, or null for no token budget.</param>
public sealed record ProviderBudgetWriteRequest(decimal? DollarCap, long? TokenCap);

/// <summary>
/// The result of <c>POST /admin/providers/{key}/discover-models</c>: the model ids the provider's own
/// endpoint reports, or an explanation when the provider doesn't support OpenAI-shaped discovery.
/// </summary>
/// <param name="Supported">Whether the provider answered an OpenAI-shaped model list.</param>
/// <param name="Models">The discovered model ids (empty when unsupported).</param>
/// <param name="Error">A human-readable reason when <paramref name="Supported"/> is false.</param>
public sealed record DiscoverModelsResult(bool Supported, IReadOnlyList<string> Models, string? Error);

