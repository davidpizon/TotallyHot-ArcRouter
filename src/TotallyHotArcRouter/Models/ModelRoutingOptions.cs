using System.Text;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Models;

/// <summary>
/// Represents the known-model allowlist and provider configuration bound from the <c>ModelRouting</c> section.
/// </summary>
public sealed class ModelRoutingOptions
{
    /// <summary>
    /// Gets the configuration section name used for model routing settings.
    /// </summary>
    public const string SectionName = "ModelRouting";

    /// <summary>
    /// Gets the configured upstream providers, keyed by provider name.
    /// </summary>
    public Dictionary<string, ProviderOptions> Providers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the allowlist of known models the proxy is permitted to route to.
    /// </summary>
    public List<ModelRouteEntry> ModelList { get; init; } = [];

    /// <summary>
    /// Performs domain-level validation that is not fully expressible through data annotations.
    /// </summary>
    /// <exception cref="OptionsValidationException">Thrown when the model routing configuration is inconsistent.</exception>
    public void EnsureValid()
    {
        var errors = new List<string>();
        var seenModelNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in ModelList)
        {
            if (string.IsNullOrWhiteSpace(entry.ModelName))
            {
                errors.Add("ModelList entries must have a non-empty ModelName.");
                continue;
            }

            if (!seenModelNames.Add(entry.ModelName))
            {
                errors.Add($"Duplicate ModelName '{entry.ModelName}' in ModelList.");
            }

            if (string.IsNullOrWhiteSpace(entry.Provider) || !Providers.ContainsKey(entry.Provider))
            {
                errors.Add($"ModelList entry '{entry.ModelName}' references unknown provider '{entry.Provider}'.");
            }

            if (string.IsNullOrWhiteSpace(entry.ProviderModelId))
            {
                errors.Add($"ModelList entry '{entry.ModelName}' must have a non-empty ProviderModelId.");
            }
        }

        foreach (var (name, provider) in Providers)
        {
            if (!Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out _))
            {
                errors.Add($"Provider '{name}' has an invalid BaseUrl '{provider.BaseUrl}'.");
            }

            if (string.IsNullOrWhiteSpace(provider.AuthHeaderName))
            {
                errors.Add($"Provider '{name}' must have a non-empty AuthHeaderName.");
            }

            foreach (var header in provider.Headers)
            {
                if (string.IsNullOrWhiteSpace(header.Name))
                {
                    errors.Add($"Provider '{name}' has a custom header with an empty Name.");
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new OptionsValidationException(nameof(ModelRoutingOptions), typeof(ModelRoutingOptions), errors);
        }
    }
}

/// <summary>
/// Represents a single upstream provider's connection details.
/// </summary>
/// <remarks>
/// A <see langword="record"/> rather than a plain class specifically so <c>with</c> expressions work.
/// <c>ManagementFacade</c>'s two write paths used to rebuild this type by listing its properties by hand,
/// and both lists had fallen behind the type - silently resetting <see cref="EnableToolCallGuard"/> on
/// every provider edit and every Stop/Play toggle, and all four <c>Aws*</c> fields on an edit (see
/// <c>docs/router/backlog.md</c> item 1). With <c>with</c>, those methods state only the fields they
/// intend to <em>change</em>, so a newly added property carries across by construction rather than by
/// someone remembering to update two call sites.
/// <para>
/// The value equality a record brings is unused - this type is only ever a dictionary value, never a key,
/// and nothing compares two instances - so it is a free side effect rather than a behavior change.
/// </para>
/// </remarks>
public sealed record ProviderOptions
{
    /// <summary>
    /// Gets the user-friendly display name for this provider (e.g. <c>OpenAI API</c>), shown in the
    /// Governance UI's provider card title. Defaults to null; when null or empty, the provider key is shown instead.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets the absolute base URL of the provider's API - scheme + host, optionally with a path
    /// prefix the provider's API is actually mounted under (e.g. <c>https://api.openai.com</c>, or
    /// <c>http://localhost:11434/v1</c> for Ollama's OpenAI-compatible routes). Any path segment here
    /// is preserved, not replaced, when combined with the forwarded request's own path - see
    /// <see cref="TotallyHot.ArcRouter.Proxy.ProxyMiddleware"/>'s <c>InvokeAsync</c> for how the two are joined.
    /// </summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>
    /// Gets the name of the environment variable holding the API key used to authenticate with this provider.
    /// Used only when <see cref="ApiKey"/> is not set. Prefer this over <see cref="ApiKey"/> so secrets are not
    /// checked into configuration files.
    /// </summary>
    public string? ApiKeyEnvVar { get; init; }

    /// <summary>
    /// Gets the literal API key used to authenticate with this provider, when supplied directly in configuration.
    /// Takes precedence over <see cref="ApiKeyEnvVar"/> when non-empty.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Gets the name of the HTTP header used to carry the API key (e.g. <c>Authorization</c> or <c>x-api-key</c>).
    /// </summary>
    public string AuthHeaderName { get; init; } = "Authorization";

    /// <summary>
    /// Gets the scheme prefixed to the API key value in the auth header (e.g. <c>Bearer</c>). Empty means the raw key is used.
    /// </summary>
    public string AuthHeaderScheme { get; init; } = "Bearer";

    /// <summary>
    /// Gets additional static HTTP headers to send on every upstream request to this provider (both
    /// proxied traffic and model discovery), beyond the auth header. Provider-agnostic: e.g. Anthropic's
    /// required <c>anthropic-version: 2023-06-01</c> is just an entry here rather than special-cased in code.
    /// </summary>
    public List<ProviderHeader> Headers { get; init; } = [];

    /// <summary>
    /// Gets whether requests to this provider cost nothing - a local runtime (Ollama, llama.cpp) or a
    /// genuinely free endpoint. This is the difference between a *known* price of zero and an *unknown*
    /// price: a model the price catalog has no data for reports no cost estimate at all, whereas a free
    /// provider's models report 0. Defaults to <see langword="false"/>, so a provider is paid unless the
    /// operator says otherwise. Not tied to credentials - a free endpoint may still require a token.
    /// </summary>
    public bool IsFree { get; init; }

    /// <summary>
    /// Gets whether the operator has this provider switched on. Defaults to <see langword="true"/>, and the
    /// default is load-bearing: a <c>model-routing.json</c> written before this field existed has no
    /// <c>Enabled</c> key, so every already-configured provider must come back on rather than silently
    /// stopped.
    /// <para>
    /// Enforced in the routing path via <see cref="TotallyHot.ArcRouter.Proxy.IModelRouteResolver.IsProviderEnabled"/>:
    /// a provider flagged <see langword="false"/> is treated as unavailable exactly like an open circuit
    /// breaker - <c>RequestInterceptor</c> never selects it as a primary or fallback candidate, and
    /// <c>ProxyMiddleware</c>'s per-attempt gate bypasses it with no network call if one slips through
    /// (e.g. stopped mid-request). Governance &gt; Providers' Stop/Play control flips this flag.
    /// </para>
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Gets whether responses from this provider should be scanned for a model expressing a tool call as
    /// literal text rather than as an OpenAI <c>tool_calls</c> delta - e.g. LM Studio serving a small GGUF
    /// that emits <c>&lt;tool_call&gt;{"name": ..., "arguments": ...}&lt;/tool_call&gt;</c> as plain
    /// assistant content (<c>docs/router/unified-api-translation.md</c> §4.5).
    ///
    /// <para>
    /// <b>Obsolete.</b> Superseded by per-(provider, model) capability detection in
    /// <c>docs/router/tool-call-normalization.md</c> Phase 4, and removed in Phase 6. This setting is
    /// scoped to the wrong axis: a model's tool-call syntax comes from its chat template, not from the
    /// server hosting it, so one LM Studio process serves both a model that needs rewriting and a natively
    /// tool-calling model that must never be scanned. Arming provider-wide therefore both missed models
    /// needing a different dialect and exposed capable ones to a false positive - a strong model emitting
    /// a <c>&lt;tool_call&gt;</c> block as a prose *example* had it rewritten into a real invocation.
    /// </para>
    ///
    /// <para>
    /// Retained for one release as a forced-on override:
    /// <c>TotallyHot.ArcRouter.Proxy.Translation.ToolCalling.ToolCallNormalizerFactory</c> arms a route that sets
    /// it even when the request offers no tools and even when the model is classified as
    /// <c>openai-native</c>, so an operator who set it because they saw a route misbehave keeps that fix
    /// through the transition. Leaving it <see langword="false"/> - the default - is now the right choice
    /// for every provider: normalization arms itself per request from the capability store.
    /// </para>
    /// </summary>
    public bool EnableToolCallGuard { get; init; }

    /// <summary>
    /// Gets the AWS region for an Amazon Bedrock provider (e.g. <c>us-east-1</c>), passed to
    /// <c>RegionEndpoint.GetBySystemName</c> when constructing the Bedrock Runtime SDK client (PLAN.md
    /// TODO 4's "Unified API Translation" pillar, Bedrock slice - see
    /// <c>docs/router/unified-api-translation.md</c> §4.2). Unused for every non-Bedrock provider, which
    /// leave it <see langword="null"/>. Unlike every other provider's single static
    /// <see cref="AuthHeaderName"/>-carried credential, Bedrock is invoked through the AWS SDK rather
    /// than a forwarded <c>HttpRequestMessage</c> - the SDK computes the actual endpoint and
    /// signs each request itself, so <see cref="BaseUrl"/> is present only to satisfy the existing
    /// provider-wide "must have a valid BaseUrl" validation and is otherwise informational.
    /// </summary>
    public string? AwsRegion { get; init; }

    /// <summary>
    /// Gets the name of the environment variable holding the AWS access key id used to authenticate an
    /// Amazon Bedrock provider. When unset (or unresolved), the Bedrock Runtime SDK client falls back to
    /// its own default AWS credential chain (environment variables, <c>~/.aws/credentials</c> profiles,
    /// instance/container role, etc.) - so this field is an optional override, not a requirement, mirroring
    /// how the SDK is normally used outside this proxy.
    /// </summary>
    public string? AwsAccessKeyIdEnvVar { get; init; }

    /// <summary>
    /// Gets the name of the environment variable holding the AWS secret access key. Used only alongside
    /// <see cref="AwsAccessKeyIdEnvVar"/>; see its remarks for the default-credential-chain fallback.
    /// </summary>
    public string? AwsSecretAccessKeyEnvVar { get; init; }

    /// <summary>
    /// Gets the name of the environment variable holding an AWS session token, for temporary/STS
    /// credentials. Optional even when <see cref="AwsAccessKeyIdEnvVar"/>/<see cref="AwsSecretAccessKeyEnvVar"/>
    /// are set - long-lived IAM user credentials have no session token.
    /// </summary>
    public string? AwsSessionTokenEnvVar { get; init; }

    /// <summary>
    /// Overrides the compiler-generated member printer (the pattern the record feature recognizes: a method
    /// named exactly <c>PrintMembers</c> with this signature replaces the default one, which prints every
    /// property verbatim), following <see cref="TotallyHot.ArcRouter.Proxy.ResolvedModelRoute"/>'s precedent.
    /// <para>
    /// Load-bearing specifically because this type became a <c>record</c>. As a class its
    /// <c>ToString()</c> was the type name and carried nothing; the synthesized one would print
    /// <see cref="ApiKey"/> in plain text into any log line, exception message, or debugger view that
    /// happens to include a <see cref="ProviderOptions"/>.
    /// </para>
    /// <para>
    /// The <c>*EnvVar</c> properties stay visible: they name where a secret lives rather than carrying one,
    /// and they are what an operator actually needs to see when diagnosing a credential problem - the same
    /// judgment call that leaves <c>ResolvedModelRoute.AwsAccessKeyId</c> visible.
    /// </para>
    /// </summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("BaseUrl = ").Append(BaseUrl);
        builder.Append(", ApiKeyEnvVar = ").Append(ApiKeyEnvVar);
        builder.Append(", ApiKey = ").Append(Redacted(ApiKey));
        builder.Append(", AuthHeaderName = ").Append(AuthHeaderName);
        builder.Append(", AuthHeaderScheme = ").Append(AuthHeaderScheme);
        // Header names are shown and values are not, matching ResolvedModelRoute.ExtraHeaders. A
        // List<ProviderHeader> would print as its type name today rather than its contents, so this is
        // defensive rather than a live leak - but it stops one the moment ProviderHeader becomes a record
        // or anything enumerates these into a message.
        builder.Append(", Headers = [")
            .Append(string.Join(", ", Headers.Select(h => $"{h.Name}=<redacted>")))
            .Append(']');
        builder.Append(", IsFree = ").Append(IsFree);
        builder.Append(", Enabled = ").Append(Enabled);
        builder.Append(", EnableToolCallGuard = ").Append(EnableToolCallGuard);
        builder.Append(", AwsRegion = ").Append(AwsRegion);
        builder.Append(", AwsAccessKeyIdEnvVar = ").Append(AwsAccessKeyIdEnvVar);
        builder.Append(", AwsSecretAccessKeyEnvVar = ").Append(AwsSecretAccessKeyEnvVar);
        builder.Append(", AwsSessionTokenEnvVar = ").Append(AwsSessionTokenEnvVar);
        return true;
    }

    /// <summary>Returns a safe placeholder for a secret value: "&lt;null&gt;" if absent, otherwise "&lt;redacted&gt;".</summary>
    private static string Redacted(string? secret) => secret is null ? "<null>" : "<redacted>";
}

/// <summary>
/// A single custom HTTP header to send upstream for a provider. The value is either a literal
/// <see cref="Value"/> or looked up at request time from the environment variable named by
/// <see cref="ValueEnvVar"/> (so secrets need not be stored in configuration), mirroring how the API key
/// supports both a literal and an env-var source.
/// </summary>
public sealed class ProviderHeader
{
    /// <summary>Gets the header name (e.g. <c>anthropic-version</c>).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the literal header value. Takes precedence over <see cref="ValueEnvVar"/> when non-empty.</summary>
    public string? Value { get; init; }

    /// <summary>Gets the name of an environment variable holding the header value; used only when <see cref="Value"/> is empty.</summary>
    public string? ValueEnvVar { get; init; }
}

/// <summary>
/// Maps a client-facing model alias to a provider and the model identifier that provider expects.
/// </summary>
public sealed class ModelRouteEntry
{
    /// <summary>
    /// Gets the client-facing model name, as sent in the request body's <c>model</c> field.
    /// </summary>
    public string ModelName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the provider key this model routes to. Must exist in <see cref="ModelRoutingOptions.Providers"/>.
    /// </summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>
    /// Gets the model identifier to substitute into the forwarded request's <c>model</c> field.
    /// </summary>
    public string ProviderModelId { get; init; } = string.Empty;

    /// <summary>
    /// Gets whether the operator has this model switched on. Defaults to <see langword="true"/>, and the
    /// default is load-bearing for the same reason as <see cref="ProviderOptions.Enabled"/>: a
    /// <c>model-routing.json</c> written before this field existed has no <c>Enabled</c> key on any
    /// entry, so every already-configured model must come back on rather than silently stopped.
    /// <para>
    /// Independent of <see cref="PresentUpstream"/>: this is the operator's own intent and is never
    /// written by a "Refresh from endpoint" scan, only by an explicit Start/Stop toggle. A model auto-added
    /// by that scan starts with this <see langword="false"/> - newly discovered, but not yet approved to
    /// receive traffic - while a manually added model defaults <see langword="true"/> as today.
    /// </para>
    /// <para>
    /// Enforced in the routing path via <see cref="TotallyHot.ArcRouter.Proxy.IModelRouteResolver.IsModelEnabled"/>,
    /// the model-level twin of <see cref="ProviderOptions.Enabled"/>'s own enforcement: a model flagged
    /// <see langword="false"/> is never selected as a primary or fallback candidate, exactly as a stopped
    /// provider is not.
    /// </para>
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Gets whether the most recent "Refresh from endpoint" scan of <see cref="Provider"/> reported this
    /// model. Defaults to <see langword="true"/> for the same backward-compatibility reason as
    /// <see cref="Enabled"/> - an entry from before this field existed, or one nobody has refreshed yet,
    /// must not appear absent just because it has never been scanned.
    /// <para>
    /// Fully scan-managed, never operator-writable: a model that stops appearing in the provider's live
    /// model list (e.g. unloaded in LM Studio) is flagged <see langword="false"/> rather than removed from
    /// configuration, since "not listed right now" is not proof the model is gone for good. Rediscovering
    /// it flips this back to <see langword="true"/> without touching <see cref="Enabled"/>, so a model the
    /// operator had started resumes routable with no extra click.
    /// </para>
    /// <para>
    /// Enforced identically to <see cref="Enabled"/> via
    /// <see cref="TotallyHot.ArcRouter.Proxy.IModelRouteResolver.IsModelEnabled"/>, which is true only when both
    /// flags are.
    /// </para>
    /// </summary>
    public bool PresentUpstream { get; init; } = true;
}

