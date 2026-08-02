using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Sandbox.Execution;

/// <summary>
/// Background worker that drains the sandbox queue with a bounded degree of parallelism, evaluates each
/// request, and observes the resulting score into router memory. All failures are swallowed so a bad
/// snippet or a slow runtime can never bring the host down.
/// </summary>
public sealed class SandboxExecutionService : BackgroundService
{
    private readonly ISandboxQueue _queue;
    private readonly ISandboxExecutor _executor;
    private readonly IRouterScoreObserver _observer;
    private readonly SandboxOptions _options;
    private readonly ILogger<SandboxExecutionService> _logger;

    /// <summary>Initializes a new instance of the <see cref="SandboxExecutionService"/> class.</summary>
    /// <param name="queue">The work queue to drain.</param>
    /// <param name="executor">The executor that evaluates each request.</param>
    /// <param name="observer">The observer that records scores into router memory.</param>
    /// <param name="options">The sandbox options (enabled flag, worker concurrency).</param>
    /// <param name="logger">The logger.</param>
    public SandboxExecutionService(
        ISandboxQueue queue,
        ISandboxExecutor executor,
        IRouterScoreObserver observer,
        IOptions<SandboxOptions> options,
        ILogger<SandboxExecutionService> logger)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(observer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _queue = queue;
        _executor = executor;
        _observer = observer;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Sandbox executor disabled; background worker will not process the queue.");
            return;
        }

        var workerCount = Math.Max(1, _options.WorkerConcurrency);
        _logger.LogInformation("Starting {WorkerCount} sandbox execution worker(s).", workerCount);

        var workers = new Task[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            workers[i] = RunWorkerAsync(stoppingToken);
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    /// <summary>Continuously dequeues sandbox requests and processes them until cancellation stops the queue.</summary>
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

    /// <summary>Executes a single sandbox request and records its result, swallowing any failure so one bad request cannot stop the worker.</summary>
    private async Task ProcessAsync(SandboxRequest request, CancellationToken stoppingToken)
    {
        try
        {
            var result = await _executor.ExecuteAsync(request, stoppingToken).ConfigureAwait(false);
            await _observer.ObserveAsync(result, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown in progress; drop the in-flight item.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Sandbox evaluation failed for {Language} (correlation {CorrelationId}); dropping.",
                request.Language,
                request.CorrelationId);
        }
    }
}

