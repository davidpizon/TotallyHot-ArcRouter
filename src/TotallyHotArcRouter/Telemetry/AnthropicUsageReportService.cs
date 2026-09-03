using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Refreshes Anthropic's own reported per-model daily token usage on the existing hourly reconciliation
/// cycle (<c>docs/router/secrets-at-rest-plan.md</c> §8.1) - not on card load, per Anthropic's guidance to
/// cache dashboard data rather than poll on every render. Entirely optional: an account with no Console/
/// Enterprise Admin API key (a Claude Pro/Max subscription, say) simply has nothing to resolve here, so
/// every cycle is a silent no-op and Phase 6's card renders its empty state - the rest of the application
/// works fully without one.
/// </summary>
public sealed class AnthropicUsageReportService
{
    /// <summary>How many trailing days of usage are re-fetched (and upserted) on every cycle.</summary>
    private const int TrailingWindowDays = 30;

    /// <summary>The provider key rows are upserted under, matching the key used elsewhere for Anthropic cost tracking.</summary>
    private const string Provider = "anthropic";

    private readonly HttpClient _httpClient;
    private readonly ReportedUsageRepository _repository;
    private readonly Func<string?> _resolveAdminApiKey;
    private readonly ILogger<AnthropicUsageReportService> _logger;

    /// <summary>Initializes a new instance of the <see cref="AnthropicUsageReportService"/> class.</summary>
    /// <param name="httpClient">The client used to call Anthropic's usage report endpoint.</param>
    /// <param name="repository">Where fetched rows are upserted and later read back for the management API.</param>
    /// <param name="resolveAdminApiKey">
    /// Resolves the current Admin API key fresh on every call - stored secret first, then the configured
    /// environment variable (the same order <c>PriceCatalogServiceCollectionExtensions.TryResolveAdminApiKey</c> uses
    /// for cost reconciliation) - or <see langword="null"/> when neither is configured. Called once per
    /// <see cref="RunCycleAsync"/> rather than resolved at construction, so a key saved from the GUI between
    /// cycles takes effect without a restart.
    /// </param>
    /// <param name="logger">Logs a failed fetch (swallowed - retried next cycle).</param>
    public AnthropicUsageReportService(
        HttpClient httpClient,
        ReportedUsageRepository repository,
        Func<string?> resolveAdminApiKey,
        ILogger<AnthropicUsageReportService> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(resolveAdminApiKey);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _repository = repository;
        _resolveAdminApiKey = resolveAdminApiKey;
        _logger = logger;
    }

    /// <summary>
    /// Fetches the trailing <see cref="TrailingWindowDays"/>-day window (ending yesterday, the most recent
    /// fully-closed UTC day) and upserts it, or does nothing when no Admin API key currently resolves.
    /// Failures are logged and swallowed - this runs on a timer, so the next cycle retries.
    /// </summary>
    public async Task RunCycleAsync(CancellationToken cancellationToken = default)
    {
        var adminApiKey = _resolveAdminApiKey();
        if (string.IsNullOrWhiteSpace(adminApiKey))
        {
            return;
        }

        try
        {
            var client = new AnthropicUsageReportClient(_httpClient, adminApiKey);
            var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1));
            var startingDay = yesterday.AddDays(-(TrailingWindowDays - 1));

            var rows = await client.GetUsageReportAsync(startingDay, yesterday.AddDays(1), cancellationToken).ConfigureAwait(false);
            _repository.UpsertReportedUsage(Provider, rows, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to fetch Anthropic's reported usage; will retry next cycle.");
        }
    }
}
