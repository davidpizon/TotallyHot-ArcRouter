using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.Router.TextGeneration;

/// <summary>
/// The llm_router ONNX voter's currently active model: the Hugging Face folder its 5 files
/// (<see cref="LlmRouterModelFiles.All"/>) are downloaded from, and the slug naming its own cache
/// subdirectory under <see cref="LlmRouterOptions.ResolveModelsRootDirectory"/>.
/// </summary>
/// <param name="BaseUrl">
/// The folder URL every file is resolved against as <c>{BaseUrl}/{fileName}</c>. Never has a trailing
/// slash.
/// </param>
/// <param name="CacheDirectorySlug">
/// A stable, filesystem-safe hash of <paramref name="BaseUrl"/> (see
/// <see cref="LlmRouterModelOverrideStore.ComputeSlug"/>), opaque by design - not meant to be
/// human-readable, only to keep each distinct model's files in a directory of their own.
/// </param>
public sealed record LlmRouterModelOverride(string BaseUrl, string CacheDirectorySlug)
{
    /// <summary>
    /// Resolves the directory this model's 5 files are cached in: <see cref="CacheDirectorySlug"/> under
    /// the shared models root every llm_router model's cache directory lives under.
    /// </summary>
    public string ResolveCacheDirectory() =>
        Path.Combine(LlmRouterOptions.ResolveModelsRootDirectory(), CacheDirectorySlug);
}

/// <summary>
/// An immutable point-in-time view of the active <see cref="LlmRouterModelOverride"/>, paired with a
/// monotonically increasing <see cref="Version"/>, mirroring <see cref="Proxy.ProviderConfigSnapshot"/>'s
/// atomic-read shape.
/// </summary>
/// <param name="Override">The current override (never mutated in place).</param>
/// <param name="Version">The revision number of this snapshot; bumped on every successful edit.</param>
public sealed record LlmRouterModelSnapshot(LlmRouterModelOverride Override, int Version);

/// <summary>
/// Writable, thread-safe source of truth for the llm_router voter's active model. Lets the Governance →
/// Benchmark Data panel's "Local Voter Model" section switch to a different Hugging Face model folder at
/// runtime, without editing <c>appsettings.json</c> or restarting the proxy.
/// </summary>
/// <remarks>
/// On first run, when no persisted file exists yet, the store seeds itself from the
/// <c>appsettings.json</c>-bound <see cref="LlmRouterOptions"/> and holds it <em>in memory only</em>;
/// nothing is written to disk until the first edit, mirroring <see cref="Proxy.ProviderConfigStore"/>'s
/// seed-then-persist-on-edit behavior. Once an edit is made, the chosen model is persisted and that file
/// becomes the source of truth on subsequent startups.
/// </remarks>
public interface ILlmRouterModelOverrideStore
{
    /// <summary>Gets the current override and its version as one atomic reference.</summary>
    LlmRouterModelSnapshot Snapshot { get; }

    /// <summary>Raised after a successful model switch has been persisted and the snapshot swapped.</summary>
    event Action? Changed;

    /// <summary>
    /// Validates, persists, and atomically publishes a new active model base URL. Does not download -
    /// the caller runs <see cref="LlmRouterModelSyncService.SyncAsync"/> afterward to fetch the new
    /// model's files.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="baseUrl"/> is not an absolute URI.</exception>
    Task SetBaseUrlAsync(string baseUrl, CancellationToken cancellationToken = default);
}

/// <summary>
/// Configuration for <see cref="LlmRouterModelOverrideStore"/>, bound from the
/// <c>LlmRouterModelOverride</c> section.
/// </summary>
public sealed class LlmRouterModelOverrideStoreOptions
{
    /// <summary>Gets the configuration section name used for llm_router model override store settings.</summary>
    public const string SectionName = "LlmRouterModelOverride";

    /// <summary>
    /// Gets the path the active model override is persisted to. May contain <c>%LOCALAPPDATA%</c>; a
    /// relative path is resolved against the application base directory, the same convention
    /// <see cref="LlmRouterOptions.ModelCacheDirectory"/> uses.
    /// </summary>
    public string FilePath { get; init; } = @"%LOCALAPPDATA%\TotallyHot.ArcRouter\llm-router-model-override.json";
}

/// <inheritdoc cref="ILlmRouterModelOverrideStore" />
public sealed class LlmRouterModelOverrideStore : ILlmRouterModelOverrideStore, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly ILogger<LlmRouterModelOverrideStore> _logger;
    private readonly string _filePath;

    // Serializes edits so two concurrent writes can't interleave their file writes / version bumps.
    private readonly SemaphoreSlim _writeMutex = new(1, 1);

    // The whole current state, published as one reference so readers never tear across Override/Version.
    private volatile LlmRouterModelSnapshot _snapshot;

    /// <summary>
    /// Initializes a new instance of the <see cref="LlmRouterModelOverrideStore"/> class, loading the
    /// persisted override when a file exists, or otherwise seeding from <paramref name="seed"/> in memory
    /// (without writing anything to disk).
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="seed">The <c>appsettings.json</c>-bound configuration to seed the default model from on first run.</param>
    /// <param name="options">Store settings (persistence path).</param>
    /// <exception cref="InvalidOperationException">
    /// No override file exists yet, and <paramref name="seed"/>'s five artifact URLs do not all share one
    /// folder prefix - the store has no single base URL it could seed from.
    /// </exception>
    public LlmRouterModelOverrideStore(
        ILogger<LlmRouterModelOverrideStore> logger,
        IOptions<LlmRouterOptions> seed,
        IOptions<LlmRouterModelOverrideStoreOptions> options)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(options);

        _logger = logger;

        var configuredPath = options.Value.FilePath;
        _filePath = ExpandPath(configuredPath);

        LlmRouterModelOverride initial;
        if (File.Exists(_filePath))
        {
            initial = LoadFromFile();
            _logger.LogInformation(
                "Loaded llm_router model override from {FilePath}: {BaseUrl}.", _filePath, initial.BaseUrl);
        }
        else
        {
            initial = SeedFrom(seed.Value);
            _logger.LogInformation(
                "No llm_router model override file at {FilePath}; seeded from appsettings (held in memory, not yet persisted): {BaseUrl}.",
                _filePath,
                initial.BaseUrl);
        }

        _snapshot = new LlmRouterModelSnapshot(initial, 0);
    }

    /// <inheritdoc />
    public LlmRouterModelSnapshot Snapshot => _snapshot;

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public async Task SetBaseUrlAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        var normalized = NormalizeBaseUrl(baseUrl);
        var next = new LlmRouterModelOverride(normalized, ComputeSlug(normalized));

        await _writeMutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Persist first, then publish: if the write fails the in-memory snapshot stays consistent
            // with what's on disk (the edit is rejected, not half-applied).
            await PersistAsync(next, cancellationToken).ConfigureAwait(false);
            _snapshot = new LlmRouterModelSnapshot(next, _snapshot.Version + 1);
        }
        finally
        {
            _writeMutex.Release();
        }

        _logger.LogInformation("Switched llm_router model base URL to {BaseUrl}.", normalized);
        Changed?.Invoke();
    }

    /// <inheritdoc />
    public void Dispose() => _writeMutex.Dispose();

    /// <summary>
    /// Derives the seed base URL from <see cref="LlmRouterOptions.GenAiConfigUrl"/>'s folder and validates
    /// that every other configured artifact URL resolves to the same folder plus its own expected file
    /// name - the invariant <see cref="LlmRouterOptions"/>'s remarks describe (one execution-provider
    /// build, one shared folder) but that nothing previously enforced.
    /// </summary>
    private static LlmRouterModelOverride SeedFrom(LlmRouterOptions options)
    {
        var baseUrl = StripFileName(options.GenAiConfigUrl, "genai_config.json", nameof(options.GenAiConfigUrl));

        EnsureMatchesBaseUrl(baseUrl, options.TokenizerJsonUrl, "tokenizer.json", nameof(options.TokenizerJsonUrl));
        EnsureMatchesBaseUrl(baseUrl, options.TokenizerConfigJsonUrl, "tokenizer_config.json", nameof(options.TokenizerConfigJsonUrl));
        EnsureMatchesBaseUrl(baseUrl, options.ModelOnnxUrl, "model.onnx", nameof(options.ModelOnnxUrl));

        if (options.ModelOnnxDataUrl is not null)
        {
            EnsureMatchesBaseUrl(baseUrl, options.ModelOnnxDataUrl, "model.onnx.data", nameof(options.ModelOnnxDataUrl));
        }

        return new LlmRouterModelOverride(baseUrl, ComputeSlug(baseUrl));
    }

    private static string StripFileName(string url, string expectedFileName, string propertyName)
    {
        if (!url.EndsWith($"/{expectedFileName}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"'{propertyName}' ('{url}') does not end with the expected file name '{expectedFileName}'.");
        }

        return url[..^(expectedFileName.Length + 1)];
    }

    private static void EnsureMatchesBaseUrl(string baseUrl, string url, string expectedFileName, string propertyName)
    {
        var expected = $"{baseUrl}/{expectedFileName}";
        if (!string.Equals(url, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"'{propertyName}' ('{url}') does not share the same folder as 'GenAiConfigUrl' ('{baseUrl}'). " +
                $"Expected '{expected}'. Every LlmRouter artifact URL must point at the same model folder.");
        }
    }

    /// <summary>Validates and normalizes a candidate base URL: absolute, no trailing slash.</summary>
    /// <exception cref="ArgumentException"><paramref name="baseUrl"/> is not an absolute URI.</exception>
    private static string NormalizeBaseUrl(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
        {
            throw new ArgumentException($"'{baseUrl}' must be an absolute URI.", nameof(baseUrl));
        }

        return baseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Computes a stable, filesystem-safe cache-directory name for a base URL: the first 16 hex
    /// characters of the SHA-256 hash of its lowercased form. Deliberately opaque rather than a sanitized
    /// version of the URL itself, since an arbitrary Hugging Face URL can contain characters that are not
    /// safe path segments on every platform.
    /// </summary>
    private static string ComputeSlug(string baseUrl)
    {
        var bytes = Encoding.UTF8.GetBytes(baseUrl.ToLowerInvariant());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash)[..16];
    }

    private LlmRouterModelOverride LoadFromFile()
    {
        var json = File.ReadAllText(_filePath);
        return JsonSerializer.Deserialize<LlmRouterModelOverride>(json)
            ?? throw new InvalidOperationException($"llm_router model override file '{_filePath}' deserialized to null.");
    }

    private async Task PersistAsync(LlmRouterModelOverride value, CancellationToken cancellationToken)
    {
        var directoryPath = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        // Write-then-rename: a crash or interruption mid-write leaves only the ".tmp" file incomplete,
        // never the file _snapshot claims is on disk, matching this store's "persist first, then
        // publish" consistency intent above.
        var temporaryPath = _filePath + ".tmp";
        var json = JsonSerializer.Serialize(value, SerializerOptions);
        await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, _filePath, overwrite: true);
    }

    // Mirrors LlmRouterOptions' own %LOCALAPPDATA% expansion (that type has no public path-only helper
    // for an arbitrary configured path, only for its own ModelCacheDirectory/models-root properties).
    private static string ExpandPath(string path)
    {
        const string localAppDataToken = "%LOCALAPPDATA%";

        var expanded = Environment.ExpandEnvironmentVariables(path);

        if (expanded.Contains(localAppDataToken, StringComparison.OrdinalIgnoreCase))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(localAppData))
            {
                localAppData = AppContext.BaseDirectory;
            }

            localAppData = localAppData.TrimEnd('/', '\\');
            expanded = expanded.Replace(localAppDataToken, localAppData, StringComparison.OrdinalIgnoreCase);
        }

        expanded = expanded.Replace('\\', Path.DirectorySeparatorChar);

        return Path.IsPathRooted(expanded)
            ? expanded
            : Path.Combine(AppContext.BaseDirectory, expanded);
    }
}
