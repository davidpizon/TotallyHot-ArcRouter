using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntimeGenAI;
using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.Router.TextGeneration;

/// <summary>
/// A local, in-process <see cref="ITextGenerationClient"/> backed by ONNX Runtime GenAI
/// (<c>Microsoft.ML.OnnxRuntimeGenAI</c>), for the <c>llm_router</c> voter (PLAN.md Phase L). The
/// model artifacts are downloaded once into <see cref="LlmRouterOptions.ModelCacheDirectory"/> on
/// first use and reused on every subsequent run; there is no network call on the routing hot path
/// once cached. See <see cref="LlmRouterOptions"/>'s remarks for why the default model is a
/// community-sourced, off-the-shelf instruct model rather than the paper's own fine-tuned checkpoint.
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
    private readonly ILogger<OnnxTextGenerationClient> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);

    private Model? _model;
    private Tokenizer? _tokenizer;

    /// <summary>
    /// Initializes a new instance of the <see cref="OnnxTextGenerationClient"/> class.
    /// </summary>
    /// <param name="options">The llm_router model configuration.</param>
    /// <param name="httpClientFactory">Used to download model artifacts on first use.</param>
    /// <param name="logger">The logger.</param>
    public OnnxTextGenerationClient(
        IOptions<LlmRouterOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<OnnxTextGenerationClient> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _options.EnsureValid();
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
        if (_model is not null && _tokenizer is not null)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_model is not null && _tokenizer is not null)
            {
                return;
            }

            var cacheDirectory = _options.ResolveModelCacheDirectory();
            Directory.CreateDirectory(cacheDirectory);

            await EnsureArtifactCachedAsync(cacheDirectory, "genai_config.json", _options.GenAiConfigUrl, cancellationToken)
                .ConfigureAwait(false);
            await EnsureArtifactCachedAsync(cacheDirectory, "tokenizer.json", _options.TokenizerJsonUrl, cancellationToken)
                .ConfigureAwait(false);
            await EnsureArtifactCachedAsync(cacheDirectory, "tokenizer_config.json", _options.TokenizerConfigJsonUrl, cancellationToken)
                .ConfigureAwait(false);
            await EnsureArtifactCachedAsync(cacheDirectory, "model.onnx", _options.ModelOnnxUrl, cancellationToken)
                .ConfigureAwait(false);

            if (_options.ModelOnnxDataUrl is not null)
            {
                await EnsureArtifactCachedAsync(cacheDirectory, "model.onnx.data", _options.ModelOnnxDataUrl, cancellationToken)
                    .ConfigureAwait(false);
            }

            var model = new Model(cacheDirectory);
            _tokenizer = new Tokenizer(model);
            _model = model;

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
    /// been cached locally. Mirrors <c>OnnxEmbeddingClient.EnsureArtifactCachedAsync</c>.
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

        var temporaryPath = destinationPath + ".download";
        try
        {
            using var httpClient = _httpClientFactory.CreateClient(nameof(OnnxTextGenerationClient));
            using var response = await httpClient.GetAsync(sourceUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
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
            File.Delete(temporaryPath);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            File.Delete(temporaryPath);
            _logger.LogError(
                ex,
                "Failed to download llm_router artifact from {SourceUrl}. The llm_router voter abstains until this artifact is cached, either by network access or by manually placing the file at {DestinationPath}.",
                sourceUrl,
                destinationPath);
            throw;
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
