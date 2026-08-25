using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Quality.Grading;
using TotallyHot.ArcRouter.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// Adapts verifier-derived scores into <see cref="RouterMemory"/>. Live heuristic scores are written under
/// a configurable dimension prefix (default <c>live:</c>) so they occupy a separate namespace from the
/// checked-in benchmark matrices the offline evaluation relies on.
/// </summary>
public sealed class RouterMemoryScoreObserver : IQualityScoreObserver
{
    /// <summary>The router memory this observer writes live scores into.</summary>
    private readonly RouterMemory _memory;

    /// <summary>The quality options, carrying the live-memory dimension prefix.</summary>
    private readonly QualityOptions _options;

    /// <summary>The logger.</summary>
    private readonly ILogger<RouterMemoryScoreObserver> _logger;

    /// <summary>The optional dashboard telemetry publisher; <see langword="null"/> when telemetry publishing is not configured.</summary>
    private readonly ITelemetryPublisher? _telemetryPublisher;

    /// <summary>Initializes a new instance of the <see cref="RouterMemoryScoreObserver"/> class.</summary>
    /// <param name="memory">The router memory to observe into.</param>
    /// <param name="options">The quality options carrying the live-memory prefix.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="telemetryPublisher">Optional publisher for the dashboard's live quality-signal tile.</param>
    public RouterMemoryScoreObserver(
        RouterMemory memory,
        IOptions<QualityOptions> options,
        ILogger<RouterMemoryScoreObserver> logger,
        ITelemetryPublisher? telemetryPublisher = null)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _memory = memory;
        _options = options.Value;
        _logger = logger;
        _telemetryPublisher = telemetryPublisher;
    }

    /// <inheritdoc />
    public async Task ObserveAsync(QualityResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (string.IsNullOrEmpty(result.Model))
        {
            _logger.LogDebug("Quality result has no model attribution; skipping observation.");
            return;
        }

        var score = Math.Clamp(result.UnifiedScore, 0.0, 1.0);
        var dimension = RouterDimension.ToLiveKey(_options.LiveMemoryPrefix, result.Dimension);

        await _memory.AddScoreAsync(dimension, result.Model, score).ConfigureAwait(false);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Quality observed {Language} dim {Dimension} model {Model} -> u={Score:F3} (correlation {CorrelationId}).",
                result.Language,
                dimension,
                result.Model,
                score,
                result.RequestCorrelationId);
        }

        if (_telemetryPublisher is not null && !string.IsNullOrEmpty(result.RequestCorrelationId))
        {
            var signal = new QualitySignalEvent(
                CorrelationId: result.RequestCorrelationId,
                SessionId: result.SessionId,
                Dimension: dimension,
                Model: result.Model,
                Language: result.Language,
                SyntaxValid: result.SyntaxValid,
                SyntaxAuthoritative: result.SyntaxAuthoritative,
                AnalysisScore: result.AnalysisScore,
                JudgeScore: result.JudgeScore,
                UnifiedScore: score,
                DegradedReason: result.DegradedReason,
                TimestampUtc: DateTimeOffset.UtcNow);

            await _telemetryPublisher.PublishQualitySignalAsync(signal, cancellationToken).ConfigureAwait(false);
        }
    }
}

