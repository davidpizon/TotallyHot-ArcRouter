using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Quality.Grading;

/// <summary>
/// Background worker that drains the grading queue with a bounded degree of parallelism, grades each
/// request, and submits the result to the aggregator. All failures are swallowed so one malformed snippet
/// can never bring the host down.
/// </summary>
public sealed class QualityGradingService : BackgroundService
{
    private readonly IQualityQueue _queue;
    private readonly IQualityGrader _grader;
    private readonly IQualityScoreAggregator _aggregator;
    private readonly QualityOptions _options;
    private readonly ILogger<QualityGradingService> _logger;

    /// <summary>Initializes a new instance of the <see cref="QualityGradingService"/> class.</summary>
    /// <param name="queue">The work queue to drain.</param>
    /// <param name="grader">The grader that statically evaluates each request.</param>
    /// <param name="aggregator">The aggregator that joins the judge grade and writes exactly one score.</param>
    /// <param name="options">The quality options (enabled flag, worker concurrency).</param>
    /// <param name="logger">The logger.</param>
    public QualityGradingService(
        IQualityQueue queue,
        IQualityGrader grader,
        IQualityScoreAggregator aggregator,
        IOptions<QualityOptions> options,
        ILogger<QualityGradingService> logger)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(grader);
        ArgumentNullException.ThrowIfNull(aggregator);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _queue = queue;
        _grader = grader;
        _aggregator = aggregator;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Quality verifier disabled; background worker will not process the queue.");
            return;
        }

        var workerCount = Math.Max(1, _options.WorkerConcurrency);
        _logger.LogInformation("Starting {WorkerCount} quality grading worker(s).", workerCount);

        var workers = new Task[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            workers[i] = RunWorkerAsync(stoppingToken);
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    /// <summary>Continuously dequeues grading requests and processes them until cancellation stops the queue.</summary>
    private async Task RunWorkerAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var request in _queue.DequeueAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await ProcessAsync(request, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>Grades a single request and submits its result, swallowing any failure so one bad request cannot stop the worker.</summary>
    private async Task ProcessAsync(QualityRequest request, CancellationToken stoppingToken)
    {
        try
        {
            var result = await _grader.GradeAsync(request, stoppingToken).ConfigureAwait(false);
            await _aggregator.SubmitAsync(result, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown in progress; drop the in-flight item.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Quality grading failed for {Language} (correlation {CorrelationId}); dropping.",
                request.Language,
                request.CorrelationId);
        }
    }
}

