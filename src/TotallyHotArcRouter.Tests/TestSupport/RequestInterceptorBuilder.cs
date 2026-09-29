using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Classification;
using TotallyHot.ArcRouter.Router.Embeddings;

namespace TotallyHot.ArcRouter.Tests.TestSupport;

/// <summary>
/// Builds a <see cref="RequestInterceptor"/> with the same fake defaults its constructor supplies for
/// omitted collaborators, so a test names only the collaborators it cares about. Exists because the
/// interceptor's constructor is the hub other code keeps growing parameters onto: constructing it by
/// hand at every test site meant one new parameter touched dozens of files. Adding a collaborator now
/// means adding one <c>With*</c> method here.
/// </summary>
public sealed class RequestInterceptorBuilder
{
    private readonly IModelRouteResolver _resolver;
    private ILogger<RequestInterceptor> _logger = NullLogger<RequestInterceptor>.Instance;
    private SingleModelServingOptions? _singleModelServingOptions;
    private RouterMemory? _routerMemory;
    private ICircuitBreaker? _circuitBreaker;
    private IDimensionInferrer? _dimensionInferrer;
    private IOptions<QualityOptions>? _qualityOptions;
    private IRequestClassifier? _requestClassifier;
    private IRoutingPolicy? _routingPolicy;
    private IEmbeddingClient? _embeddingClient;
    private EmbeddingWarmupState? _embeddingWarmupState;
    private IOptions<RoutingOptions>? _routingOptions;
    private IProviderInteractionStatusStore? _interactionStatusStore;
    private UntrainedBaselineSelector? _untrainedBaselineSelector;

    /// <summary>Initializes a new instance of the <see cref="RequestInterceptorBuilder"/> class.</summary>
    /// <param name="resolver">The model route resolver, the one collaborator every interceptor needs.</param>
    public RequestInterceptorBuilder(IModelRouteResolver resolver)
    {
        _resolver = resolver;
    }

    /// <summary>Builds an interceptor over <paramref name="resolver"/> with every other collaborator at its default.</summary>
    /// <param name="resolver">The model route resolver.</param>
    /// <returns>The interceptor.</returns>
    public static RequestInterceptor For(IModelRouteResolver resolver)
    {
        return new RequestInterceptorBuilder(resolver).Build();
    }

    /// <summary>Sets the logger (e.g. a mock a test verifies).</summary>
    /// <param name="logger">The logger.</param>
    /// <returns>This builder.</returns>
    public RequestInterceptorBuilder WithLogger(ILogger<RequestInterceptor> logger)
    {
        _logger = logger;
        return this;
    }

    /// <summary>Sets the single-model serving options.</summary>
    /// <param name="options">The options.</param>
    /// <returns>This builder.</returns>
    public RequestInterceptorBuilder WithSingleModelServing(SingleModelServingOptions options)
    {
        _singleModelServingOptions = options;
        return this;
    }

    /// <summary>Sets the router memory.</summary>
    /// <param name="routerMemory">The memory.</param>
    /// <returns>This builder.</returns>
    public RequestInterceptorBuilder WithRouterMemory(RouterMemory routerMemory)
    {
        _routerMemory = routerMemory;
        return this;
    }

    /// <summary>Sets the circuit breaker.</summary>
    /// <param name="circuitBreaker">The breaker.</param>
    /// <returns>This builder.</returns>
    public RequestInterceptorBuilder WithCircuitBreaker(ICircuitBreaker circuitBreaker)
    {
        _circuitBreaker = circuitBreaker;
        return this;
    }

    /// <summary>Sets the dimension inferrer.</summary>
    /// <param name="dimensionInferrer">The inferrer.</param>
    /// <returns>This builder.</returns>
    public RequestInterceptorBuilder WithDimensionInferrer(IDimensionInferrer dimensionInferrer)
    {
        _dimensionInferrer = dimensionInferrer;
        return this;
    }

    /// <summary>Sets the quality options.</summary>
    /// <param name="qualityOptions">The options.</param>
    /// <returns>This builder.</returns>
    public RequestInterceptorBuilder WithQualityOptions(IOptions<QualityOptions> qualityOptions)
    {
        _qualityOptions = qualityOptions;
        return this;
    }

    /// <summary>Sets the request classifier.</summary>
    /// <param name="requestClassifier">The classifier.</param>
    /// <returns>This builder.</returns>
    public RequestInterceptorBuilder WithRequestClassifier(IRequestClassifier requestClassifier)
    {
        _requestClassifier = requestClassifier;
        return this;
    }

    /// <summary>Sets the routing policy.</summary>
    /// <param name="routingPolicy">The policy.</param>
    /// <returns>This builder.</returns>
    public RequestInterceptorBuilder WithRoutingPolicy(IRoutingPolicy routingPolicy)
    {
        _routingPolicy = routingPolicy;
        return this;
    }

    /// <summary>Sets the embedding client and, optionally, its warm-up state.</summary>
    /// <param name="embeddingClient">The client.</param>
    /// <param name="warmupState">The shared warm-up state, if the test exercises it.</param>
    /// <returns>This builder.</returns>
    public RequestInterceptorBuilder WithEmbedding(IEmbeddingClient embeddingClient,
        EmbeddingWarmupState? warmupState = null)
    {
        _embeddingClient = embeddingClient;
        _embeddingWarmupState = warmupState;
        return this;
    }

    /// <summary>Sets the routing options.</summary>
    /// <param name="routingOptions">The options.</param>
    /// <returns>This builder.</returns>
    public RequestInterceptorBuilder WithRoutingOptions(IOptions<RoutingOptions> routingOptions)
    {
        _routingOptions = routingOptions;
        return this;
    }

    /// <summary>Sets the provider interaction status store.</summary>
    /// <param name="store">The store.</param>
    /// <returns>This builder.</returns>
    public RequestInterceptorBuilder WithInteractionStatusStore(IProviderInteractionStatusStore store)
    {
        _interactionStatusStore = store;
        return this;
    }

    /// <summary>Sets the untrained-baseline selector.</summary>
    /// <param name="selector">The selector.</param>
    /// <returns>This builder.</returns>
    public RequestInterceptorBuilder WithUntrainedBaselineSelector(UntrainedBaselineSelector selector)
    {
        _untrainedBaselineSelector = selector;
        return this;
    }

    /// <summary>Builds the interceptor.</summary>
    /// <returns>The interceptor.</returns>
    public RequestInterceptor Build()
    {
        return new RequestInterceptor(
            logger: _logger,
            modelRouteResolver: _resolver,
            singleModelServingOptions: _singleModelServingOptions,
            routerMemory: _routerMemory,
            circuitBreaker: _circuitBreaker,
            dimensionInferrer: _dimensionInferrer,
            qualityOptions: _qualityOptions,
            requestClassifier: _requestClassifier,
            routingPolicy: _routingPolicy,
            embeddingClient: _embeddingClient,
            embeddingWarmupState: _embeddingWarmupState,
            routingOptions: _routingOptions,
            interactionStatusStore: _interactionStatusStore,
            untrainedBaselineSelector: _untrainedBaselineSelector);
    }
}
