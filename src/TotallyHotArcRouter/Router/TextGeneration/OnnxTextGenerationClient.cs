using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntimeGenAI;
using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.Router.TextGeneration;

/// <summary>
/// A local, in-process <see cref="ITextGenerationClient"/> backed by ONNX Runtime GenAI
/// (<c>Microsoft.ML.OnnxRuntimeGenAI</c>), for the <c>llm_router</c> voter (PLAN.md Phase L). The
/// model artifacts are downloaded once into the active model's cache directory (see
/// <see cref="ILlmRouterModelOverrideStore"/>) on first use and reused on every subsequent run; there
/// is no network call on the routing hot path once cached. See <see cref="LlmRouterOptions"/>'s remarks
/// for why the default model is a community-sourced, off-the-shelf instruct model rather than the
/// paper's own fine-tuned checkpoint.
/// </summary>
/// <remarks>
/// Greedy decoding only (matching research-doc §B.2's <c>T=0</c> configuration) - ONNX Runtime GenAI
/// defaults to greedy search unless sampling is explicitly enabled via <c>GeneratorParams</c>, so no
/// sampling options are set here. Generation runs entirely on ONNX Runtime GenAI's own synchronous,
/// CPU-bound loop (it owns KV-cache management internally); this class polls
/// <see cref="CancellationToken"/> and a wall-clock deadline between tokens so a stuck or slow
/// generation degrades to a timeout rather than blocking the routing hot path indefinitely.
/// </remarks>
public sealed class OnnxTextGenerationClient : ITextGenerationClient, IAsyncDisposable
{
    private readonly LlmRouterOptions _options;
    private readonly ILlmRouterModelOverrideStore _overrideStore;
    private readonly ILogger<OnnxTextGenerationClient> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);

    private Model? _model;
    private Tokenizer? _tokenizer;
    private int _loadedOverrideVersion = -1;

    /// <summary>
    /// Initializes a new instance of the <see cref="OnnxTextGenerationClient"/> class.
    /// </summary>
    /// <param name="options">
    /// The llm_router model's non-URL configuration (<see cref="LlmRouterOptions.MaxNewTokens"/>,
    /// <see cref="LlmRouterOptions.GenerationTimeoutMs"/>). The model's artifact URLs and cache directory
    /// are read from <paramref name="overrideStore"/> instead - see that parameter's remarks.
    /// </param>
    /// <param name="overrideStore">
    /// The llm_router voter's active model. Always has a value: seeded from <paramref name="options"/> on
    /// first run if the Governance panel's "Local Voter Model" section has never switched models, so this
    /// client's behavior is unchanged for an installation that never uses that panel.
    /// </param>
    /// <param name="httpClientFactory">Used to download model artifacts on first use.</param>
    /// <param name="logger">The logger.</param>
    public OnnxTextGenerationClient(
        IOptions<LlmRouterOptions> options,
        ILlmRouterModelOverrideStore overrideStore,
        IHttpClientFactory httpClientFactory,
        ILogger<OnnxTextGenerationClient> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(overrideStore);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _options.EnsureValid();
        _overrideStore = overrideStore;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        // Generator.GenerateNextToken is synchronous and not documented safe for concurrent calls on
        // the same Model - serialize inference rather than risk a torn read under concurrent routing
        // requests, matching OnnxEmbeddingClient's _inferenceLock precedent.
        await _inferenceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(_options.GenerationTimeoutMs));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            return RunGeneration(prompt, linked.Token);
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    private string RunGeneration(string prompt, CancellationToken cancellationToken)
    {
        using var sequences = _tokenizer!.Encode(prompt);

        using var generatorParams = new GeneratorParams(_model!);
        generatorParams.SetSearchOption("max_length", sequences[0].Length + _options.MaxNewTokens);

        using var tokenizerStream = _tokenizer.CreateStream();
        using var generator = new Generator(_model!, generatorParams);
        generator.AppendTokenSequences(sequences);

        var output = new StringBuilder();
        while (!generator.IsDone())
        {
            cancellationToken.ThrowIfCancellationRequested();

            generator.GenerateNextToken();

            var newToken = generator.GetSequence(0)[^1];
            output.Append(tokenizerStream.Decode(newToken));
        }

        return output.ToString();
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        var snapshot = _overrideStore.Snapshot;
        if (_model is not null && _tokenizer is not null && _loadedOverrideVersion == snapshot.Version)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            snapshot = _overrideStore.Snapshot;
            if (_model is not null && _tokenizer is not null && _loadedOverrideVersion == snapshot.Version)
            {
                return;
            }

            if (_model is not null || _tokenizer is not null)
            {
                // A model switch landed after this instance already loaded one: swap it out under the
                // inference lock too, so a generation in flight (which only holds _inferenceLock, not
                // _initLock) never reads a disposed Model/Tokenizer.
                await _inferenceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    _tokenizer?.Dispose();
                    _tokenizer = null;
                    _model?.Dispose();
                    _model = null;
                }
                finally
                {
                    _inferenceLock.Release();
                }
            }

            var activeOverride = snapshot.Override;
            var cacheDirectory = activeOverride.ResolveCacheDirectory();
            Directory.CreateDirectory(cacheDirectory);

            foreach (var fileName in LlmRouterModelFiles.All)
            {
                await EnsureArtifactCachedAsync(cacheDirectory, fileName, $"{activeOverride.BaseUrl}/{fileName}", cancellationToken)
                    .ConfigureAwait(false);
            }

            var model = new Model(cacheDirectory);
            _tokenizer = new Tokenizer(model);
            _model = model;
            _loadedOverrideVersion = snapshot.Version;

            _logger.LogInformation("Loaded llm_router ONNX GenAI model from {CacheDirectory}.", cacheDirectory);
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Downloads an artifact to <paramref name="fileName"/> inside <paramref name="cacheDirectory"/> if
    /// it is not already present - the documented cold-start path for a first run before the model has
    /// been cached locally. Mirrors <c>OnnxEmbeddingClient.EnsureArtifactCachedAsync</c>. A no-op when
    /// <paramref name="fileName"/> is <see cref="LlmRouterModelFiles.IsOptional"/> and 404s: some exports
    /// inline all weights in <c>model.onnx</c> and never publish a separate external-data file.
    /// </summary>
    private async Task EnsureArtifactCachedAsync(string cacheDirectory, string fileName, string sourceUrl, CancellationToken cancellationToken)
    {
        var destinationPath = Path.Combine(cacheDirectory, fileName);
        if (File.Exists(destinationPath))
        {
            return;
        }

        _logger.LogInformation(
            "llm_router artifact {DestinationPath} not found in cache; downloading from {SourceUrl}.",
            destinationPath,
            sourceUrl);

        // A GUID-suffixed temp name, not a fixed "<destination>.download" - this lazy downloader and a
        // concurrent LlmRouterModelSyncService sync for the same file must not race on the same temp path.
        var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.download";
        try
        {
            using var httpClient = _httpClientFactory.CreateClient(nameof(OnnxTextGenerationClient));
            using var response = await httpClient.GetAsync(sourceUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound && LlmRouterModelFiles.IsOptional(fileName))
            {
                _logger.LogInformation(
                    "llm_router optional artifact {FileName} not published at {SourceUrl}; the model's weights are presumably inlined in model.onnx.",
                    fileName,
                    sourceUrl);
                return;
            }

            response.EnsureSuccessStatusCode();

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var destination = File.Create(temporaryPath))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            SafeDelete(temporaryPath);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            SafeDelete(temporaryPath);
            _logger.LogError(
                ex,
                "Failed to download llm_router artifact from {SourceUrl}. The llm_router voter abstains until this artifact is cached, either by network access or by manually placing the file at {DestinationPath}.",
                sourceUrl,
                destinationPath);
            throw;
        }
    }

    /// <summary>
    /// Best-effort deletion of a partial download's temp file. Mirrors
    /// <c>LlmRouterModelSyncService.SafeDelete</c>: any failure here (e.g. <see cref="UnauthorizedAccessException"/>
    /// on a read-only file, or an <see cref="IOException"/> because something still has it open) must not mask the
    /// original network/cancellation failure that triggered the cleanup.
    /// </summary>
    private static void SafeDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Best-effort cleanup of a partial download; a failure here doesn't change the caller's outcome.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _initLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _tokenizer?.Dispose();
            _tokenizer = null;
            _model?.Dispose();
            _model = null;
        }
        finally
        {
            _initLock.Release();
        }

        _initLock.Dispose();
        _inferenceLock.Dispose();
    }
}
