using TotallyHot.ArcRouter.Sandbox;
using TotallyHot.ArcRouter.Sandbox.Execution;
using TotallyHot.ArcRouter.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// Adapts sandbox-derived scores into <see cref="RouterMemory"/>. Live heuristic scores are written under
/// a configurable dimension prefix (default <c>live:</c>) so they occupy a separate namespace from the
/// checked-in benchmark matrices the offline evaluation relies on (architecture doc §15).
/// </summary>
public sealed class RouterMemoryScoreObserver : IRouterScoreObserver
{
    private readonly RouterMemory _memory;
    private readonly SandboxOptions _options;
    private readonly ILogger<RouterMemoryScoreObserver> _logger;
    private readonly ITelemetryPublisher? _telemetryPublisher;

    /// <summary>Initializes a new instance of the <see cref="RouterMemoryScoreObserver"/> class.</summary>
    /// <param name="memory">The router memory to observe into.</param>
    /// <param name="options">The sandbox options carrying the live-memory prefix.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="telemetryPublisher">Optional publisher for the dashboard's live execution-signal tile.</param>
    public RouterMemoryScoreObserver(
        RouterMemory memory,
        IOptions<SandboxOptions> options,
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
    public async Task ObserveAsync(SandboxResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (string.IsNullOrEmpty(result.Model))
        {
            _logger.LogDebug("Sandbox result has no model attribution; skipping observation.");
            return;
        }

        var score = Math.Clamp(result.UnifiedScore, 0.0, 1.0);
        var dimension = _options.LiveMemoryPrefix + result.Dimension;

        await _memory.AddScoreAsync(dimension, result.Model, score).ConfigureAwait(false);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Sandbox observed {Language} dim {Dimension} tier {Tier} model {Model} -> u={Score:F3} (correlation {CorrelationId}).",
                result.Language,
                dimension,
                result.Tier,
                result.Model,
                score,
                result.RequestCorrelationId);
        }

        if (_telemetryPublisher is not null && !string.IsNullOrEmpty(result.RequestCorrelationId))
        {
            var signal = new SandboxSignalEvent(
                CorrelationId: result.RequestCorrelationId,
                SessionId: result.SessionId,
                Dimension: dimension,
                Model: result.Model,
                Language: result.Language,
                Tier: result.Tier,
                SyntaxValid: result.SyntaxValid,
                Executed: result.Executed,
                ExitCode: result.ExitCode,
                TimedOut: result.TimedOut,
                UnifiedScore: score,
                WallClockMs: result.WallClockMs,
                PeakMemoryBytes: result.PeakMemoryBytes,
                TimestampUtc: DateTimeOffset.UtcNow);

            await _telemetryPublisher.PublishSandboxSignalAsync(signal, cancellationToken).ConfigureAwait(false);
        }
    }
}

