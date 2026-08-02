using Microsoft.Extensions.Logging;

namespace TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

/// <summary>
/// Writable, thread-safe cache over the tool-call capability tables: which API flavors each provider
/// answers, and how each (provider, model) expresses a tool call
/// (<c>docs/router/tool-call-normalization.md</c> Phase 1).
/// </summary>
/// <remarks>
/// Shaped after <see cref="TotallyHot.ArcRouter.PriceCatalog.PriceSourceToggleStore"/>: immutable snapshots
/// swapped under a lock and published through <c>volatile</c> fields, so the read path takes no lock at
/// all. That matters here more than it does for price sources -
/// <see cref="GetModelCapability"/> sits on the request path, consulted for every request that carries
/// <c>tools</c>, so it must be a dictionary lookup rather than a query.
/// <para>
/// Like the toggle and budget stores, the constructor deliberately does <em>not</em> read the database.
/// This is a singleton built while the DI graph is assembled, before
/// <see cref="TotallyHot.ArcRouter.Hosting.StartupHealthCheckHostedService"/> has run
/// <c>EnsureCreated</c>, so querying tables that don't exist yet would throw during construction. That
/// check calls <see cref="Reload"/> immediately after creating the schema. Until then every model reads as
/// unclassified - the safe direction, since "unknown" means "forward natively and observe", which is
/// exactly what an unconfigured model should do.
/// </para>
/// </remarks>
public sealed class ToolCallCapabilityStore : IToolCallCapabilityStore
{
    // A matched tool call carries the user's own arguments, so evidence must never be raw model output.
    // ModelToolCapability.Evidence documents that contract for callers; this cap is the backstop that keeps
    // an accidental payload from being persisted whole.
    private const int MaxEvidenceLength = 200;

    private readonly ToolCallCapabilityRepository _repository;
    private readonly ILogger<ToolCallCapabilityStore> _logger;
    private readonly object _gate = new();

    // Assigned brand-new (never mutated) dictionaries under _gate. Volatile for the same reason
    // PriceSourceToggleStore's snapshot is: reference assignment being atomic only rules out a torn read,
    // it says nothing about visibility, so a lock-free reader could otherwise keep observing a stale
    // snapshot indefinitely - here that would mean scanning a model with a dialect an operator has since
    // corrected.
    private volatile IReadOnlyDictionary<ModelCapabilityKey, ModelToolCapability> _modelSnapshot =
        new Dictionary<ModelCapabilityKey, ModelToolCapability>();

    private volatile IReadOnlyDictionary<string, ProviderEndpointCapabilities> _providerSnapshot =
        new Dictionary<string, ProviderEndpointCapabilities>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new instance of the <see cref="ToolCallCapabilityStore"/> class with empty snapshots.</summary>
    public ToolCallCapabilityStore(ToolCallCapabilityRepository repository, ILogger<ToolCallCapabilityStore> logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(logger);

        _repository = repository;
        _logger = logger;
    }

    /// <summary>Raised after a capability has been persisted and the snapshot swapped.</summary>
    public event Action? Changed;

    /// <summary>
    /// Re-reads both capability tables and swaps the snapshots. Called once at startup, and after each
    /// write so the cache reflects what was actually persisted rather than what was requested - which
    /// matters here because a write can be silently rejected by the confidence gate.
    /// </summary>
    public void Reload()
    {
        var models = _repository.GetModelCapabilities()
            .ToDictionary(c => new ModelCapabilityKey(c.ProviderKey, c.ModelName), c => c);
        var providers = _repository.GetProviderCapabilities()
            .ToDictionary(c => c.ProviderKey, c => c, StringComparer.OrdinalIgnoreCase);

        lock (_gate)
        {
            _modelSnapshot = models;
            _providerSnapshot = providers;
        }
    }

    /// <inheritdoc />
    public ModelToolCapability? GetModelCapability(string providerKey, string modelName)
    {
        if (string.IsNullOrWhiteSpace(providerKey) || string.IsNullOrWhiteSpace(modelName))
        {
            return null;
        }

        return _modelSnapshot.TryGetValue(new ModelCapabilityKey(providerKey, modelName), out var capability)
            ? capability
            : null;
    }

    /// <inheritdoc />
    public ProviderEndpointCapabilities? GetProviderCapabilities(string providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            return null;
        }

        return _providerSnapshot.TryGetValue(providerKey, out var capabilities) ? capabilities : null;
    }

    /// <inheritdoc />
    public bool TryRecordModelCapability(ModelToolCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentException.ThrowIfNullOrWhiteSpace(capability.ProviderKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(capability.ModelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(capability.Dialect);

        // Rejected at the boundary rather than clamped, unlike the same field read back from the database
        // (see ToolCallCapabilityRepository.ReadObservationCount). The two are different trust boundaries: a
        // negative count arriving from a caller is a programming error worth surfacing loudly, while one
        // found on disk is corruption that must not take the proxy down mid-request. This mirrors
        // ProviderBudgetStore.SetBudget, which likewise throws ArgumentOutOfRangeException on a negative cap
        // rather than silently coercing it.
        ArgumentOutOfRangeException.ThrowIfNegative(capability.ObservationCount, nameof(capability));

        // A default(DateTimeOffset) reads back as "very old" and would make the row look indefinitely
        // stale, so stamp it here rather than making every caller remember to.
        var stamped = capability with
        {
            Evidence = Truncate(capability.Evidence),
            DetectedAtUtc = capability.DetectedAtUtc == default ? DateTimeOffset.UtcNow : capability.DetectedAtUtc,
        };

        var written = _repository.TryUpsertModelCapability(stamped);
        if (!written)
        {
            // Refresh on rejection too, not just on success. A rejection proves the database holds a
            // higher-confidence row, and the snapshot may not know about it - which is not a hypothetical
            // while an operator pin can only be created by editing the database directly (the GUI surface
            // is Phase 6). Without this the pin is invisible to the running process: GetModelCapability
            // keeps returning null, so the request path keeps treating the model as unclassified,
            // re-observing it and re-attempting a write that is rejected again every single request. The
            // operator's correction silently does nothing until a restart. Reloading here makes the first
            // rejection self-healing, and *reduces* database traffic rather than adding to it, since the
            // caller then sees the pin and stops re-recording.
            Reload();

            _logger.LogDebug(
                "Tool-call capability for {Provider}/{Model} was not recorded as {Dialect}: an existing higher-confidence classification takes precedence.",
                SanitizeForLog(stamped.ProviderKey),
                SanitizeForLog(stamped.ModelName),
                SanitizeForLog(stamped.Dialect));
            return false;
        }

        Reload();

        _logger.LogInformation(
            "Tool-call dialect for {Provider}/{Model} recorded as {Dialect} ({Confidence}).",
            SanitizeForLog(stamped.ProviderKey),
            SanitizeForLog(stamped.ModelName),
            SanitizeForLog(stamped.Dialect),
            stamped.Confidence);

        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Removes a model's classification entirely, handing it back to automatic detection.
    /// </summary>
    /// <remarks>
    /// Deliberately absent from <see cref="IToolCallCapabilityStore"/>: only the management surface clears a
    /// row, and the request path must not be able to. Unconditional by design - it is reachable only from an
    /// operator action, and a confidence gate here would make an operator unable to undo an operator pin,
    /// which is the one thing this method exists to allow.
    /// <para>
    /// Deleting rather than writing a low-confidence row matters. A row saying "heuristic: unknown" would
    /// still be a row, and <see cref="ToolCallNormalizerFactory"/> reads a missing row as "forward natively
    /// and classify from the first real response" - which is exactly the fresh start being asked for.
    /// </para>
    /// </remarks>
    /// <param name="providerKey">The provider serving the model.</param>
    /// <param name="modelName">The client-facing model name.</param>
    public void ClearModelCapability(string providerKey, string modelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        _repository.DeleteModelCapability(providerKey, modelName);
        Reload();

        _logger.LogInformation(
            "Tool-call dialect override for {Provider}/{Model} was cleared; the model returns to automatic detection.",
            SanitizeForLog(providerKey),
            SanitizeForLog(modelName));

        Changed?.Invoke();
    }

    /// <summary>
    /// Persists a provider's scanned endpoint capabilities and refreshes the cache. Unconditional, unlike
    /// the model path: endpoint flavors are a direct observation of what the server answered, so there is
    /// no weaker-source problem for a confidence gate to solve.
    /// </summary>
    public void SetProviderCapabilities(ProviderEndpointCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilities.ProviderKey);

        var stamped = capabilities.ScannedAtUtc == default
            ? capabilities with { ScannedAtUtc = DateTimeOffset.UtcNow }
            : capabilities;

        _repository.UpsertProviderCapabilities(stamped);
        Reload();

        _logger.LogInformation(
            "Endpoint capabilities for provider {Provider} scanned: openai={OpenAi}, lmstudio={LmStudio}, ollama={Ollama}, anthropic={Anthropic}.",
            SanitizeForLog(stamped.ProviderKey),
            stamped.OpenAiCompatible,
            stamped.LmStudioNative,
            stamped.OllamaNative,
            stamped.AnthropicCompatible);

        Changed?.Invoke();
    }

    /// <summary>Caps evidence length, so an accidentally-passed payload is not persisted whole.</summary>
    private static string? Truncate(string? evidence) =>
        evidence is { Length: > MaxEvidenceLength }
            ? evidence[..MaxEvidenceLength]
            : evidence;

    /// <summary>
    /// Strips CR/LF from a config-controlled key before it enters a log template, so a crafted provider or
    /// model name cannot forge additional log lines (CodeQL: log forging, CWE-117). Chained Replace on the
    /// tainted value directly is the sanitizer shape the analysis recognizes - mirrors
    /// <c>ProxyMiddleware</c>'s and <c>ProviderBudgetStore</c>'s own <c>SanitizeForLog</c>.
    /// </summary>
    private static string SanitizeForLog(string value) =>
        value.Replace("\r", " ").Replace("\n", " ");
}

/// <summary>
/// The (provider, model) pair a tool-call capability is keyed on, compared case-insensitively to match the
/// <see cref="StringComparer.OrdinalIgnoreCase"/> lookups <c>ModelRouteResolver</c> uses for both halves -
/// and the <c>COLLATE NOCASE</c> on the backing table's key columns.
/// </summary>
/// <remarks>
/// A dedicated key rather than <c>TotallyHot.ArcRouter.PriceCatalog.ModelKey</c>: that type is documented as
/// identifying "one priced thing" and orders its members the other way round (model, then provider), so
/// reusing it would invite a silent transposition at a call site.
/// </remarks>
public readonly record struct ModelCapabilityKey
{
    /// <summary>Initializes a new instance of the <see cref="ModelCapabilityKey"/> struct.</summary>
    /// <param name="providerKey">The <c>ModelRouting:Providers</c> key.</param>
    /// <param name="modelName">The client-facing model name.</param>
    public ModelCapabilityKey(string providerKey, string modelName)
    {
        ProviderKey = providerKey;
        ModelName = modelName;
    }

    /// <summary>Gets the <c>ModelRouting:Providers</c> key.</summary>
    public string ProviderKey { get; }

    /// <summary>Gets the client-facing model name.</summary>
    public string ModelName { get; }

    /// <summary>
    /// Compares both halves case-insensitively. Null-safe by construction:
    /// <see cref="StringComparer.Equals(string, string)"/> already treats two nulls as equal and a null
    /// against a value as unequal, so a <c>default</c> key compares correctly rather than throwing.
    /// </summary>
    /// <param name="other">The key to compare against.</param>
    public bool Equals(ModelCapabilityKey other) =>
        StringComparer.OrdinalIgnoreCase.Equals(ProviderKey, other.ProviderKey) &&
        StringComparer.OrdinalIgnoreCase.Equals(ModelName, other.ModelName);

    /// <summary>
    /// Hashes both halves case-insensitively, tolerating nulls.
    /// </summary>
    /// <remarks>
    /// The null checks are not redundant despite both properties being non-nullable reference types.
    /// Every struct has an unsuppressable <c>default</c> whose reference fields are null regardless of what
    /// the constructor guarantees, and nullable-reference analysis cannot close that hole - so
    /// <c>default(ModelCapabilityKey)</c> reaches here with nulls. Passing one to
    /// <see cref="StringComparer.GetHashCode(string)"/> throws <see cref="ArgumentNullException"/> (verified,
    /// not assumed), which would turn merely inserting a default key into a dictionary into a crash. A key
    /// type that is public - as this one is, to satisfy the public store's signature - has to be total here.
    /// </remarks>
    public override int GetHashCode() => HashCode.Combine(
        ProviderKey is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(ProviderKey),
        ModelName is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(ModelName));
}

