using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.PriceCatalog.Sources;

namespace TotallyHot.ArcRouter.PriceCatalog;

/// <summary>
/// The set of price-source clients the ingestion loop should poll. An interface so the loop can be
/// unit-tested with fake sources without a real HTTP-backed registry.
/// </summary>
public interface IPriceSourceRegistry
{
    /// <summary>
    /// Gets the enabled source clients. A disabled source is absent here, not a skipped rung (D6).
    /// </summary>
    IReadOnlyList<IPriceSourceClient> EnabledClients { get; }
}

/// <summary>
/// Builds and holds the price-source clients, exposing the currently enabled subset. It is the seam that
/// makes adding a second source a new class rather than a redesign. Every client it builds fetches through
/// the <see cref="HttpClientName"/> named client, whose registration in <c>AddPriceCatalog</c> is the one
/// place the shared attribution headers are set - so a new client inherits them for free rather than having
/// to remember (Phase 2). Holds LiteLLM and OpenRouter today; it was written for exactly this growth.
/// </summary>
/// <remarks>
/// Validating the options in the constructor is what makes a bad source name fail at startup: the
/// ingestion service and the startup health check both depend on this registry, so the whole graph is
/// constructed (and validated) before Kestrel binds - mirroring how <c>ProviderConfigStore</c> validates
/// <c>ModelRoutingOptions</c> in its own constructor.
/// <para>
/// Every known client is built up front and <see cref="EnabledClients"/> filters them per call, rather than
/// the constructor building only the enabled ones. That is what lets the Governance panel flip a source on
/// without a restart: the toggle is read at each cycle, not once at startup.
/// </para>
/// </remarks>
public sealed class PriceSourceRegistry : IPriceSourceRegistry
{
    /// <summary>
    /// The named <see cref="HttpClient"/> shared by every price-source fetch, already carrying the
    /// <c>X-Title</c> and <c>HTTP-Referer</c> attribution headers.
    /// </summary>
    public const string HttpClientName = nameof(PriceSourceRegistry);

    private readonly List<IPriceSourceClient> _allClients = [];
    private readonly PriceSourceToggleStore _toggleStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="PriceSourceRegistry"/> class, validating the
    /// configuration and constructing a client for each known source.
    /// </summary>
    /// <param name="options">The price catalog configuration, validated here so a bad source name fails at startup.</param>
    /// <param name="toggleStore">The live enabled/disabled toggle read on every <see cref="EnabledClients"/> access.</param>
    /// <param name="loggerFactory">Creates per-source loggers.</param>
    /// <param name="httpClientFactory">
    /// Creates a fresh <see cref="HttpClientName"/> client per fetch so this singleton does not capture a
    /// handler past <c>IHttpClientFactory</c>'s rotation.
    /// </param>
    /// <exception cref="OptionsValidationException">The price catalog configuration is invalid.</exception>
    public PriceSourceRegistry(
        IOptions<PriceCatalogOptions> options,
        PriceSourceToggleStore toggleStore,
        ILoggerFactory loggerFactory,
        IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(toggleStore);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(httpClientFactory);

        _toggleStore = toggleStore;

        var catalogOptions = options.Value;
        catalogOptions.EnsureValid();

        // Build a client for every known source, enabled or not - EnabledClients does the filtering. This
        // grows by adding another case below plus an entry in PriceCatalogOptions.KnownSources, not by a
        // rewrite - which is exactly what adding OpenRouter here was.
        _allClients.Add(new LiteLlmPriceSourceClient(
            httpClient: null,
            url: catalogOptions.GetSourceUrl(PriceCatalogOptions.LiteLlmSourceName) ??
                 LiteLlmPriceSourceClient.DefaultUrl,
            logger: loggerFactory.CreateLogger<LiteLlmPriceSourceClient>(),
            httpClientFactory: httpClientFactory));
        _allClients.Add(new OpenRouterPriceSourceClient(
            httpClient: null,
            url: catalogOptions.GetSourceUrl(PriceCatalogOptions.OpenRouterSourceName) ??
                 OpenRouterPriceSourceClient.DefaultUrl,
            logger: loggerFactory.CreateLogger<OpenRouterPriceSourceClient>(),
            httpClientFactory: httpClientFactory));
    }

    /// <summary>
    /// Gets the enabled source clients, evaluated fresh on every read against the current toggle state. The
    /// ingestion loop never learns which sources exist but are switched off (D6) - a disabled source is
    /// absent here, not a skipped rung.
    /// </summary>
    public IReadOnlyList<IPriceSourceClient> EnabledClients =>
        [.. _allClients.Where(client => _toggleStore.IsEnabled(client.Name))];
}
