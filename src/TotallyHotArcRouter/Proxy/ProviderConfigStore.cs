using System.Text.Json;
using TotallyHot.ArcRouter.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// An immutable point-in-time view of the routing configuration, paired with a monotonically
/// increasing <see cref="Version"/>. Handed out as a single object so a consumer
/// (e.g. <see cref="ModelRouteResolver"/>) reads the options and the version they belong to in one
/// atomic reference read, with no risk of tearing across two separate property reads.
/// </summary>
/// <param name="Options">The current routing configuration snapshot (never mutated in place).</param>
/// <param name="Version">The revision number of this snapshot; bumped on every successful edit.</param>
public sealed record ProviderConfigSnapshot(ModelRoutingOptions Options, int Version);

/// <summary>
/// Writable, thread-safe source of truth for the proxy's <see cref="ModelRoutingOptions"/> (providers
/// and the routable-model allowlist). Replaces the one-time <see cref="IOptions{TOptions}"/> snapshot
/// that <see cref="ModelRouteResolver"/> used to read, so provider/credential/model edits made at
/// runtime (via the management API) take effect live, without restarting the proxy.
/// </summary>
/// <remarks>
/// Config layering (an <c>appsettings.json</c> overlay) can override a key but cannot <em>delete</em>
/// one, so removal of a provider/model requires a single document that owns the whole configuration -
/// which is this store. On first run, when no persisted file exists yet, the store seeds itself from
/// the <c>appsettings.json</c>-bound <see cref="ModelRoutingOptions"/> and holds it <em>in memory
/// only</em>; nothing is written to disk until the first edit, so a proxy that is never reconfigured
/// behaves exactly as it did before this store existed (and leaves no file behind). Once an edit is
/// made, the full configuration is persisted and that file becomes the source of truth on subsequent
/// startups.
/// </remarks>
public interface IProviderConfigStore
{
    /// <summary>Gets the current configuration snapshot and its version as one atomic reference.</summary>
    ProviderConfigSnapshot Snapshot { get; }

    /// <summary>Raised after a successful edit has been persisted and the snapshot swapped.</summary>
    event Action? Changed;

    /// <summary>
    /// Validates, persists, and atomically publishes a completely new configuration.
    /// </summary>
    /// <exception cref="OptionsValidationException">The supplied configuration is invalid (see <see cref="ModelRoutingOptions.EnsureValid"/>).</exception>
    Task ReplaceAsync(ModelRoutingOptions next, CancellationToken cancellationToken = default);

    /// <summary>Adds or replaces a single provider (by key, case-insensitive), keeping the model list unchanged.</summary>
    Task UpsertProviderAsync(string key, ProviderOptions provider, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a single provider (by key, case-insensitive), cascading to every model that routes to it
    /// so no orphaned model entry is left behind. Both drops land in one atomic edit. Historical metrics
    /// (spend, usage, telemetry) are keyed independently of this config and are not affected.
    /// </summary>
    Task RemoveProviderAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Adds or replaces a single model entry (by <see cref="ModelRouteEntry.ModelName"/>, case-insensitive), preserving position on edit.</summary>
    Task UpsertModelAsync(ModelRouteEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Removes a single model entry (by <see cref="ModelRouteEntry.ModelName"/>, case-insensitive).</summary>
    Task RemoveModelAsync(string modelName, CancellationToken cancellationToken = default);
}

/// <summary>
/// Configuration for <see cref="ProviderConfigStore"/>, bound from the <c>ProviderConfig</c> section.
/// </summary>
public sealed class ProviderConfigStoreOptions
{
    /// <summary>Gets the configuration section name used for provider-config-store settings.</summary>
    public const string SectionName = "ProviderConfig";

    /// <summary>
    /// Gets the path the writable configuration is persisted to. Relative paths are resolved against
    /// <see cref="AppContext.BaseDirectory"/> (same convention as the memory and spend-log files).
    /// </summary>
    public string FilePath { get; init; } = "model-routing.json";
}

/// <inheritdoc cref="IProviderConfigStore" />
public sealed class ProviderConfigStore : IProviderConfigStore, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly ILogger<ProviderConfigStore> _logger;
    private readonly string _filePath;

    // Serializes edits so two concurrent writes can't interleave their file writes / version bumps.
    private readonly SemaphoreSlim _writeMutex = new(1, 1);

    // The whole current state, published as one reference so readers never tear across Options/Version.
    private volatile ProviderConfigSnapshot _snapshot;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderConfigStore"/> class, loading the persisted
    /// configuration when a file exists, or otherwise seeding from <paramref name="seed"/> in memory
    /// (without writing anything to disk).
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="seed">The <c>appsettings.json</c>-bound configuration to seed from on first run.</param>
    /// <param name="options">Store settings (persistence path).</param>
    /// <exception cref="OptionsValidationException">The effective configuration (loaded or seeded) is invalid.</exception>
    public ProviderConfigStore(
        ILogger<ProviderConfigStore> logger,
        IOptions<ModelRoutingOptions> seed,
        IOptions<ProviderConfigStoreOptions> options)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(options);

        _logger = logger;

        var configuredPath = options.Value.FilePath;
        _filePath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(AppContext.BaseDirectory, configuredPath);

        ModelRoutingOptions initial;
        if (File.Exists(_filePath))
        {
            initial = LoadFromFile();
            _logger.LogInformation("Loaded provider configuration from {FilePath}.", _filePath);
        }
        else
        {
            initial = Normalize(seed.Value);
            initial.EnsureValid();
            _logger.LogInformation(
                "No provider configuration file at {FilePath}; seeded from appsettings (held in memory, not yet persisted).",
                _filePath);
        }

        _snapshot = new ProviderConfigSnapshot(initial, 0);
    }

    /// <inheritdoc />
    public ProviderConfigSnapshot Snapshot => _snapshot;

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public async Task ReplaceAsync(ModelRoutingOptions next, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(next);

        var normalized = Normalize(next);
        // Validate before touching disk, so an invalid edit is rejected without persisting anything.
        normalized.EnsureValid();

        await _writeMutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Persist first, then publish: if the write fails the in-memory snapshot stays consistent
            // with what's on disk (the edit is rejected, not half-applied).
            await PersistAsync(normalized, cancellationToken).ConfigureAwait(false);
            _snapshot = new ProviderConfigSnapshot(normalized, _snapshot.Version + 1);
        }
        finally
        {
            _writeMutex.Release();
        }

        Changed?.Invoke();
    }

    /// <inheritdoc />
    public Task UpsertProviderAsync(string key, ProviderOptions provider, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(provider);

        var current = _snapshot.Options;
        var providers = new Dictionary<string, ProviderOptions>(current.Providers, StringComparer.OrdinalIgnoreCase)
        {
            [key] = provider
        };

        return ReplaceAsync(
            new ModelRoutingOptions { Providers = providers, ModelList = [.. current.ModelList] },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task RemoveProviderAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var current = _snapshot.Options;
        var providers = new Dictionary<string, ProviderOptions>(current.Providers, StringComparer.OrdinalIgnoreCase);
        providers.Remove(key);

        // Cascade to this provider's models in the same edit. Leaving them behind would strand every one
        // of them pointing at a provider that no longer exists, which EnsureValid rejects outright - so
        // without this the removal could never succeed for a provider that had any models at all. One
        // ReplaceAsync means the drop is atomic: config is never persisted in the orphaned intermediate
        // state. Historical metrics (spend, usage, telemetry) are keyed separately and are deliberately
        // left untouched - removing a provider from the routing config is not a reason to lose its past.
        var models = current.ModelList
            .Where(m => !string.Equals(m.Provider, key, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return ReplaceAsync(
            new ModelRoutingOptions { Providers = providers, ModelList = models },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task UpsertModelAsync(ModelRouteEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var current = _snapshot.Options;
        var replaced = false;
        var models = current.ModelList
            .Select(m =>
            {
                if (string.Equals(m.ModelName, entry.ModelName, StringComparison.OrdinalIgnoreCase))
                {
                    replaced = true;
                    return entry;
                }

                return m;
            })
            .ToList();

        if (!replaced)
        {
            models.Add(entry);
        }

        return ReplaceAsync(
            new ModelRoutingOptions
            {
                Providers = new Dictionary<string, ProviderOptions>(current.Providers, StringComparer.OrdinalIgnoreCase),
                ModelList = models
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task RemoveModelAsync(string modelName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var current = _snapshot.Options;
        var models = current.ModelList
            .Where(m => !string.Equals(m.ModelName, modelName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return ReplaceAsync(
            new ModelRoutingOptions
            {
                Providers = new Dictionary<string, ProviderOptions>(current.Providers, StringComparer.OrdinalIgnoreCase),
                ModelList = models
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose() => _writeMutex.Dispose();

    /// <summary>
    /// Reads and deserializes the configuration file from disk, normalizing the result and validating it
    /// before returning.
    /// </summary>
    private ModelRoutingOptions LoadFromFile()
    {
        var json = File.ReadAllText(_filePath);
        var loaded = JsonSerializer.Deserialize<ModelRoutingOptions>(json)
            ?? throw new InvalidOperationException($"Provider configuration file '{_filePath}' deserialized to null.");

        var normalized = Normalize(loaded);
        normalized.EnsureValid();
        return normalized;
    }

    /// <summary>Serializes the given options and writes them to the configuration file, creating its directory if needed.</summary>
    private async Task PersistAsync(ModelRoutingOptions options, CancellationToken cancellationToken)
    {
        var directoryPath = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        var json = JsonSerializer.Serialize(options, SerializerOptions);
        await File.WriteAllTextAsync(_filePath, json, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns a copy whose <see cref="ModelRoutingOptions.Providers"/> dictionary uses a
    /// case-insensitive comparer regardless of how it was produced (a fresh initializer preserves the
    /// comparer, but <see cref="System.Text.Json"/> deserialization does not), so provider lookups and
    /// validation stay case-insensitive after a load. Also defends against null collections from a
    /// hand-edited or partial file.
    /// </summary>
    private static ModelRoutingOptions Normalize(ModelRoutingOptions options) => new()
    {
        Providers = options.Providers is null
            ? new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, ProviderOptions>(options.Providers, StringComparer.OrdinalIgnoreCase),
        ModelList = options.ModelList is null ? [] : [.. options.ModelList]
    };
}

