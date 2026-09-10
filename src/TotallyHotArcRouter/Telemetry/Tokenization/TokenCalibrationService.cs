using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Telemetry.Tokenization;

/// <summary>
/// Learns how far the local tiktoken proxy count sits from what a provider actually bills, by counting a
/// bounded sample of already-captured prompts both ways and folding the ratio into
/// <see cref="TokenCalibrationStore"/> (ADR-0009, plan Phase 2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Inert unless enabled.</b> With <see cref="TokenizationOptions.CalibrationEnabled"/> off - the
/// default - or with no counting client configured, this service returns immediately and never opens a
/// socket. That is the security posture ADR-0009 committed to: a second egress destination in a process
/// that proxies other people's prompts does not switch itself on.
/// </para>
/// <para>
/// <b>Never contends with served traffic.</b> A cycle is skipped entirely while any request is in flight,
/// reusing the hard-pause discipline <c>TaxonomyComparisonService</c> established - under sustained load
/// calibration simply lags, which costs accuracy that was optional anyway.
/// </para>
/// <para>
/// Sampling reuses <see cref="ITranscriptStore.ListSessionsAsync"/> rather than adding a query: it already
/// returns recent rows carrying both the captured prompt and the model that served it, which is exactly
/// the pair a sample needs.
/// </para>
/// </remarks>
public sealed class TokenCalibrationService : BackgroundService
{
    private readonly AnthropicTokenCountClient? _countClient;
    private readonly InFlightRequestGauge? _inFlightGauge;
    private readonly TiktokenTokenCounter _localCounter = new();
    private readonly ILogger<TokenCalibrationService> _logger;
    private readonly IOptionsMonitor<TokenizationOptions> _options;
    private readonly IModelRouteResolver _routeResolver;
    private readonly TokenCalibrationStore _store;
    private readonly ITranscriptStore _transcriptStore;

    /// <summary>Initializes a new instance of the <see cref="TokenCalibrationService"/> class.</summary>
    /// <param name="logger">Structured logger.</param>
    /// <param name="transcriptStore">Supplies the already-captured prompts sampled each cycle.</param>
    /// <param name="store">Receives each observation.</param>
    /// <param name="routeResolver">Maps a routed model name onto the provider that served it.</param>
    /// <param name="options">Supplies the enable flag, cadence, and per-cycle sample bound.</param>
    /// <param name="countClient">
    /// The provider's token-counting client, or <see langword="null"/> when none is configured - in which
    /// case this service is inert. Supplied by the composition root only when calibration is enabled
    /// <em>and</em> a key resolved, so the credential decision stays in one place.
    /// </param>
    /// <param name="inFlightGauge">
    /// The proxy's in-flight counter. <see langword="null"/> disables the hard pause, which is intended
    /// only for tests - production always supplies it.
    /// </param>
    public TokenCalibrationService(
        ILogger<TokenCalibrationService> logger,
        ITranscriptStore transcriptStore,
        TokenCalibrationStore store,
        IModelRouteResolver routeResolver,
        IOptionsMonitor<TokenizationOptions> options,
        AnthropicTokenCountClient? countClient = null,
        InFlightRequestGauge? inFlightGauge = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(transcriptStore);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(routeResolver);
        ArgumentNullException.ThrowIfNull(options);

        // Fail at startup on a nonsensical cadence or smoothing weight rather than when PeriodicTimer
        // rejects the period mid-run - the same eager-validation placement CostReconciliationHostedService
        // uses for its own options.
        options.CurrentValue.EnsureValid();

        _logger = logger;
        _transcriptStore = transcriptStore;
        _store = store;
        _routeResolver = routeResolver;
        _options = options;
        _countClient = countClient;
        _inFlightGauge = inFlightGauge;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.CurrentValue.CalibrationEnabled || _countClient is null)
        {
            _logger.LogInformation(
                "[TOKEN-CALIBRATION] Disabled; counterfactual token counts will be reported uncalibrated.");
            return;
        }

        _logger.LogInformation(
            "[TOKEN-CALIBRATION] Enabled; sampling up to {MaxSamples} prompts every {IntervalMinutes} minutes.",
            _options.CurrentValue.MaxSamplesPerCycle, _options.CurrentValue.CycleIntervalMinutes);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_options.CurrentValue.CycleIntervalMinutes));

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            try
            {
                await RunCycleAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A background improvement must never take the host down.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogWarning(exception: ex, message: "[TOKEN-CALIBRATION] Cycle failed; will retry next tick.");
            }
    }

    /// <summary>
    /// Runs one sampling cycle: reads recent captured prompts, counts a bounded number of them both
    /// locally and at the provider, and folds each ratio into the store.
    /// </summary>
    /// <param name="cancellationToken">Cancels the cycle.</param>
    /// <returns>The number of samples successfully recorded.</returns>
    /// <remarks>
    /// Internal so tests can drive a single cycle deterministically instead of waiting on
    /// <see cref="PeriodicTimer"/>, keeping every case far inside the repo's 5-second test ceiling.
    /// </remarks>
    internal async Task<int> RunCycleAsync(CancellationToken cancellationToken)
    {
        if (_countClient is null) return 0;

        if (_inFlightGauge is { Count: > 0 })
        {
            _logger.LogDebug("[TOKEN-CALIBRATION] Requests in flight; skipping this cycle.");
            return 0;
        }

        var options = _options.CurrentValue;

        // Over-fetch: most recent rows may lack a captured prompt (capture disabled for that request) or
        // route to a provider with no counting endpoint, and both are filtered out below.
        var candidates = await _transcriptStore
            .ListSessionsAsync(limit: options.MaxSamplesPerCycle * 4, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var recorded = 0;

        foreach (var row in candidates)
        {
            if (recorded >= options.MaxSamplesPerCycle) break;
            if (cancellationToken.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(row.PromptText)) continue;
            if (!TryResolveCountableModel(row.RoutedModel, out var key)) continue;

            if (!_localCounter.TryCountPromptTokens(text: row.PromptText, key: key, tokens: out var localTokens,
                    source: out _))
                continue;

            var providerTokens = await _countClient
                .TryCountTokensAsync(model: key.ModelName, text: row.PromptText, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (providerTokens is not { } counted) continue;

            _store.RecordSample(key: key, localTokens: localTokens, providerTokens: counted);
            recorded++;
        }

        if (recorded > 0)
            _logger.LogInformation("[TOKEN-CALIBRATION] Recorded {SampleCount} calibration samples this cycle.",
                recorded);

        return recorded;
    }

    /// <summary>
    /// Resolves a routed model name to a <see cref="ModelKey"/> when that model is served by a provider
    /// this service can obtain an exact count from.
    /// </summary>
    /// <param name="routedModel">The model name as captured on the transcript row.</param>
    /// <param name="key">The resolved key when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the model is countable at its provider.</returns>
    /// <remarks>
    /// Restricted to Anthropic because <see cref="AnthropicTokenCountClient"/> is the only counting client
    /// implemented. That is not a limitation of the design: a model on any other provider simply keeps its
    /// uncalibrated local count, which is already correct for the OpenAI families whose encoding it is.
    /// </remarks>
    private bool TryResolveCountableModel(string routedModel, out ModelKey key)
    {
        key = default;

        if (string.IsNullOrWhiteSpace(routedModel)) return false;
        if (!_routeResolver.TryResolve(modelName: routedModel, route: out var route)) return false;
        if (!string.Equals(a: route.Provider, b: "anthropic", comparisonType: StringComparison.OrdinalIgnoreCase))
            return false;

        key = new ModelKey(ModelName: route.ModelName, Provider: route.Provider);
        return true;
    }
}
