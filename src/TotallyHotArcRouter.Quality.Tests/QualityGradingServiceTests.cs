using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TotallyHot.ArcRouter.Quality.Grading;

namespace TotallyHot.ArcRouter.Quality.Tests;

/// <summary>Covers the background worker draining and submitting, and the disabled short-circuit.</summary>
public class QualityGradingServiceTests
{
    private static QualityRequest Request()
    {
        return new QualityRequest(Code: "print(1)", Language: CodeLanguage.Python, Prompt: "print one",
            Dimension: "code_generation", Model: "gpt-5.4", CorrelationId: "corr", SessionId: "sess");
    }

    private static IQualityGrader StubGrader()
    {
        var mock = new Mock<IQualityGrader>();
        mock.Setup(e => e.GradeAsync(It.IsAny<QualityRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QualityResult { Model = "gpt-5.4", UnifiedScore = 1.0 });
        return mock.Object;
    }

    [Fact]
    public async Task ExecuteAsync_DrainsQueueAndSubmits()
    {
        var queue = new QualityWorkQueue(Options.Create(new QualityOptions { QueueCapacity = 8 }));
        queue.TryEnqueue(Request());
        queue.TryEnqueue(Request());
        var aggregator = new CountingAggregator(2);
        var service = new QualityGradingService(
            queue: queue,
            grader: StubGrader(),
            aggregator: aggregator,
            options: Options.Create(new QualityOptions { Enabled = true, WorkerConcurrency = 1 }),
            logger: NullLogger<QualityGradingService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await aggregator.Completed.WaitAsync(timeout: TimeSpan.FromSeconds(2),
            cancellationToken: TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, actual: aggregator.Count);
    }

    [Fact]
    public async Task ExecuteAsync_GraderThrows_LogsAndDropsItem_KeepsDrainingSubsequentItems()
    {
        var queue = new QualityWorkQueue(Options.Create(new QualityOptions { QueueCapacity = 8 }));
        queue.TryEnqueue(Request());
        queue.TryEnqueue(Request());
        var aggregator = new CountingAggregator(1);

        var mock = new Mock<IQualityGrader>();
        var callCount = 0;
        mock.Setup(e => e.GradeAsync(It.IsAny<QualityRequest>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 1
                    ? throw new InvalidOperationException("boom")
                    : Task.FromResult(new QualityResult { Model = "gpt-5.4", UnifiedScore = 1.0 });
            });

        var service = new QualityGradingService(
            queue: queue,
            grader: mock.Object,
            aggregator: aggregator,
            options: Options.Create(new QualityOptions { Enabled = true, WorkerConcurrency = 1 }),
            logger: NullLogger<QualityGradingService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await aggregator.Completed.WaitAsync(timeout: TimeSpan.FromSeconds(2),
            cancellationToken: TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        // First item's failure was swallowed (dropped, not submitted); the second still made it through.
        Assert.Equal(1, actual: aggregator.Count);
        Assert.Equal(2, actual: callCount);
    }

    [Fact]
    public async Task ExecuteAsync_GraderThrowsOperationCanceled_DropsItemWithoutFaultingWorker()
    {
        var queue = new QualityWorkQueue(Options.Create(new QualityOptions { QueueCapacity = 8 }));
        queue.TryEnqueue(Request());
        queue.TryEnqueue(Request());
        var aggregator = new CountingAggregator(1);

        var mock = new Mock<IQualityGrader>();
        var callCount = 0;
        mock.Setup(e => e.GradeAsync(It.IsAny<QualityRequest>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 1
                    ? throw new OperationCanceledException("run cancelled")
                    : Task.FromResult(new QualityResult { Model = "gpt-5.4", UnifiedScore = 1.0 });
            });

        var service = new QualityGradingService(
            queue: queue,
            grader: mock.Object,
            aggregator: aggregator,
            options: Options.Create(new QualityOptions { Enabled = true, WorkerConcurrency = 1 }),
            logger: NullLogger<QualityGradingService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await aggregator.Completed.WaitAsync(timeout: TimeSpan.FromSeconds(2),
            cancellationToken: TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        // A single request's own cancellation must not tear down the worker loop - the second item still
        // gets processed and submitted.
        Assert.Equal(1, actual: aggregator.Count);
        Assert.Equal(2, actual: callCount);
    }

    [Fact]
    public async Task ExecuteAsync_Disabled_DoesNotSubmit()
    {
        var queue = new QualityWorkQueue(Options.Create(new QualityOptions { QueueCapacity = 8 }));
        queue.TryEnqueue(Request());
        var aggregator = new CountingAggregator(1);
        var service = new QualityGradingService(
            queue: queue,
            grader: StubGrader(),
            aggregator: aggregator,
            options: Options.Create(new QualityOptions { Enabled = false }),
            logger: NullLogger<QualityGradingService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(100, cancellationToken: TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, actual: aggregator.Count);
    }

    /// <summary>Counts submissions to the aggregator seam and signals once the expected number arrive.</summary>
    private sealed class CountingAggregator(int target) : IQualityScoreAggregator
    {
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public Task Completed => _completed.Task;

        public Task SubmitAsync(QualityResult result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _count) >= target) _completed.TrySetResult();

            return Task.CompletedTask;
        }

        public Task<bool> CompleteWithJudgeAsync(string correlationId, double judgeScore,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }

        public Task<bool> AbandonJudgeAsync(string correlationId, string reason,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }

        public Task<int> SweepExpiredAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }
    }
}