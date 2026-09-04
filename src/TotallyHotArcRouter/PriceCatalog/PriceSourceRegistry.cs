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
/// makes adding a second source a new class rather than a redesign, and the one place the shared attribution
/// headers are set - so a new client inherits them for free rather than having to remember (Phase 2). Holds
/// LiteLLM and OpenRouter today; it was written for exactly this growth.
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
public sealed class PriceSourceRegistry : IPriceSourceRegistry, IDisposable
{
    private readonly List<IPriceSourceClient> _allClients = [];
    private readonly HttpClient _httpClient;
    private readonly PriceSourceToggleStore _toggleStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="PriceSourceRegistry"/> class, validating the
    /// configuration and constructing a client for each known source.
    /// </summary>
    /// <exception cref="OptionsValidationException">The price catalog configuration is invalid.</exception>
    public PriceSourceRegistry(
        IOptions<PriceCatalogOptions> options,
        PriceSourceToggleStore toggleStore,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(toggleStore);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _toggleStore = toggleStore;

        var catalogOptions = options.Value;
        catalogOptions.EnsureValid();

        // Set the attribution headers once on the shared handler, so a user's local polling reads as
        // legitimate application traffic rather than anonymous scraping (Phase 2). Every source client
        // built below shares this client and therefore these headers.
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add(name: "X-Title", value: "TotallyHot Arc Router");
        _httpClient.DefaultRequestHeaders.Add(name: "HTTP-Referer",
            value: "https://github.com/davidpizon/TotallyHot-ArcRouter");

        // Build a client for every known source, enabled or not - EnabledClients does the filtering. This
        // grows by adding another case below plus an entry in PriceCatalogOptions.KnownSources, not by a
        // rewrite - which is exactly what adding OpenRouter here was.
        _allClients.Add(new LiteLlmPriceSourceClient(
            httpClient: _httpClient,
            url: catalogOptions.GetSourceUrl(PriceCatalogOptions.LiteLlmSourceName) ??
                 LiteLlmPriceSourceClient.DefaultUrl,
            logger: loggerFactory.CreateLogger<LiteLlmPriceSourceClient>()));
        _allClients.Add(new OpenRouterPriceSourceClient(
            httpClient: _httpClient,
            url: catalogOptions.GetSourceUrl(PriceCatalogOptions.OpenRouterSourceName) ??
                 OpenRouterPriceSourceClient.DefaultUrl,
            logger: loggerFactory.CreateLogger<OpenRouterPriceSourceClient>()));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _httpClient.Dispose();
    }

    /// <summary>
    /// Gets the enabled source clients, evaluated fresh on every read against the current toggle state. The
    /// ingestion loop never learns which sources exist but are switched off (D6) - a disabled source is
    /// absent here, not a skipped rung.
    /// </summary>
    public IReadOnlyList<IPriceSourceClient> EnabledClients =>
        [.. _allClients.Where(client => _toggleStore.IsEnabled(client.Name))];
}