using FastBertTokenizer;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.Router.Embeddings;

/// <summary>
/// A local, in-process <see cref="IEmbeddingClient"/> backed by an ONNX Runtime <see cref="InferenceSession"/>
/// running BGE-large-en-v1.5, per PLAN.md Phase J. The model and tokenizer artifacts are downloaded once
/// into <see cref="EmbeddingOptions.ModelCacheDirectory"/> on first use and reused on every subsequent
/// run; there is no network call on the routing hot path once cached.
/// </summary>
/// <remarks>
/// Sentence embedding follows BGE's documented convention: the <c>[CLS]</c> token's final hidden state,
/// L2-normalized to a unit vector so cosine similarity between two results reduces to a plain dot
/// product (research-doc §3.3's kNN retrieval).
/// </remarks>
public sealed class OnnxEmbeddingClient : IEmbeddingClient, IAsyncDisposable
{
    /// <summary>The cached ONNX model file's name within <see cref="EmbeddingOptions.ModelCacheDirectory"/>.</summary>
    private const string ModelFileName = "model.onnx";

    /// <summary>The cached tokenizer JSON file's name within <see cref="EmbeddingOptions.ModelCacheDirectory"/>.</summary>
    private const string TokenizerFileName = "tokenizer.json";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly ILogger<OnnxEmbeddingClient> _logger;

    private readonly EmbeddingOptions _options;

    private InferenceSession? _session;
    private BertTokenizer? _tokenizer;

    /// <summary>
    /// Initializes a new instance of the <see cref="OnnxEmbeddingClient"/> class.
    /// </summary>
    /// <param name="options">The embedding configuration.</param>
    /// <param name="httpClientFactory">Used to download model artifacts on first use.</param>
    /// <param name="logger">The logger.</param>
    public OnnxEmbeddingClient(
        IOptions<EmbeddingOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<OnnxEmbeddingClient> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _options.EnsureValid();
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _initLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _session?.Dispose();
            _session = null;
        }
        finally
        {
            _initLock.Release();
        }

        _initLock.Dispose();
        _inferenceLock.Dispose();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The configured <see cref="EmbeddingOptions.ModelUrl"/>, which is what actually determines the
    /// weights this session loads. Deliberately not combined with
    /// <see cref="EmbeddingOptions.EmbeddingDimension"/>: that is a separate invariant already enforced by
    /// its own length checks, and folding it in here would report a <em>model</em> change when only the
    /// declared dimension was edited.
    /// </remarks>
    public string ModelIdentity => _options.ModelUrl;

    /// <inheritdoc/>
    public async Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        // BertTokenizer reuses its internal buffers across calls (see its Encode xmldoc), and
        // InferenceSession.Run is not documented safe for concurrent calls on the same session -
        // serialize inference rather than risk a torn read under concurrent routing requests.
        await _inferenceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return RunInference(text);
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    /// <summary>
    /// Runs one forward pass, returning the pooled vector plus the tokenized sequence length - the
    /// router's own token consumption for this request (see <see cref="EmbeddingResult.TokenCount"/>).
    /// The count is taken after <c>Encode</c> has applied <see cref="EmbeddingOptions.MaxTokens"/>
    /// truncation, so it is what the model actually processed rather than what the caller submitted.
    /// </summary>
    private EmbeddingResult RunInference(string text)
    {
        var (inputIds, attentionMask, tokenTypeIds) =
            _tokenizer!.Encode(input: text, maximumTokens: _options.MaxTokens);
        var sequenceLength = inputIds.Length;

        var inputIdsTensor = new DenseTensor<long>([1, sequenceLength]);
        var attentionMaskTensor = new DenseTensor<long>([1, sequenceLength]);
        var tokenTypeIdsTensor = new DenseTensor<long>([1, sequenceLength]);

        for (var i = 0; i < sequenceLength; i++)
        {
            inputIdsTensor[0, i] = inputIds.Span[i];
            attentionMaskTensor[0, i] = attentionMask.Span[i];
            tokenTypeIdsTensor[0, i] = tokenTypeIds.Span[i];
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(name: "input_ids", value: inputIdsTensor),
            NamedOnnxValue.CreateFromTensor(name: "attention_mask", value: attentionMaskTensor),
            NamedOnnxValue.CreateFromTensor(name: "token_type_ids", value: tokenTypeIdsTensor)
        };

        using var results = _session!.Run(inputs);
        var hiddenState = results.First().AsTensor<float>();

        // [CLS] pooling: the first token position of the last hidden state, per BGE's model card.
        var hiddenSize = hiddenState.Dimensions[^1];
        if (hiddenSize != _options.EmbeddingDimension)
            throw new InvalidOperationException(
                $"BGE ONNX model produced a {hiddenSize}-dimensional embedding, but {_options.EmbeddingDimension} was configured.");

        var embedding = new float[hiddenSize];
        for (var i = 0; i < hiddenSize; i++) embedding[i] = hiddenState[0, 0, i];

        Normalize(embedding);
        return new EmbeddingResult(Vector: embedding, TokenCount: sequenceLength);
    }

    /// <summary>
    /// L2-normalizes <paramref name="vector"/> in place so its magnitude is 1, leaving an all-zero vector unchanged
    /// rather than dividing by zero.
    /// </summary>
    /// <param name="vector">The vector to normalize.</param>
    private static void Normalize(float[] vector)
    {
        var sumOfSquares = 0.0;
        foreach (var component in vector) sumOfSquares += (double)component * component;

        var norm = Math.Sqrt(sumOfSquares);
        if (norm <= 0) return;

        for (var i = 0; i < vector.Length; i++) vector[i] = (float)(vector[i] / norm);
    }

    /// <summary>
    /// Loads the ONNX session and tokenizer on first use, downloading their artifacts if not already
    /// cached. Uses double-checked locking under <see cref="_initLock"/> so concurrent callers do not
    /// race to download or load the model twice.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_session is not null && _tokenizer is not null) return;

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is not null && _tokenizer is not null) return;

            var cacheDirectory = _options.ResolveModelCacheDirectory();
            Directory.CreateDirectory(cacheDirectory);

            var modelPath = Path.Combine(path1: cacheDirectory, path2: ModelFileName);
            var tokenizerPath = Path.Combine(path1: cacheDirectory, path2: TokenizerFileName);

            await EnsureArtifactCachedAsync(destinationPath: modelPath, sourceUrl: _options.ModelUrl,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await EnsureArtifactCachedAsync(destinationPath: tokenizerPath, sourceUrl: _options.TokenizerJsonUrl,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var tokenizer = new BertTokenizer();
            await tokenizer.LoadTokenizerJsonAsync(tokenizerPath).ConfigureAwait(false);

            _tokenizer = tokenizer;
            _session = new InferenceSession(modelPath);

            _logger.LogInformation(
                message: "Loaded BGE embedding model from {CacheDirectory}.", cacheDirectory);
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Downloads an artifact to <paramref name="destinationPath"/> if it is not already present -
    /// the documented cold-start path for a first run before the model has been cached locally.
    /// </summary>
    private async Task EnsureArtifactCachedAsync(string destinationPath, string sourceUrl,
        CancellationToken cancellationToken)
    {
        if (File.Exists(destinationPath)) return;

        _logger.LogInformation(
            message: "Embedding artifact {DestinationPath} not found in cache; downloading from {SourceUrl}.",
            destinationPath,
            sourceUrl);

        var temporaryPath = destinationPath + ".download";
        try
        {
            using var httpClient = _httpClientFactory.CreateClient(nameof(OnnxEmbeddingClient));
            using var response = await httpClient.GetAsync(requestUri: sourceUrl,
                    completionOption: HttpCompletionOption.ResponseHeadersRead, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var destination = File.Create(temporaryPath))
            {
                await source.CopyToAsync(destination: destination, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(sourceFileName: temporaryPath, destFileName: destinationPath, true);
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
                exception: ex,
                message:
                "Failed to download embedding artifact from {SourceUrl}. Routing memory retrieval is unavailable until this artifact is cached, either by network access or by manually placing the file at {DestinationPath}.",
                sourceUrl,
                destinationPath);
            throw;
        }
    }
}