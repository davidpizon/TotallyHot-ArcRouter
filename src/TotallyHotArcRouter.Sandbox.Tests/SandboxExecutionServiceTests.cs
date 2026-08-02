using TotallyHot.ArcRouter.Sandbox;
using TotallyHot.ArcRouter.Sandbox.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace TotallyHot.ArcRouter.Sandbox.Tests;

/// <summary>Covers the background worker draining and observing, and the disabled short-circuit.</summary>
public class SandboxExecutionServiceTests
{
    private sealed class CountingObserver(int target) : IRouterScoreObserver
    {
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public Task Completed => _completed.Task;

        public Task ObserveAsync(SandboxResult result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _count) >= target)
            {
                _completed.TrySetResult();
            }

            return Task.CompletedTask;
        }
    }

    private static SandboxRequest Request() =>
        new("print(1)", SandboxLanguage.Python, "code_generation", "gpt-5.4", "corr", "sess");

    private static ISandboxExecutor StubExecutor()
    {
        var mock = new Mock<ISandboxExecutor>();
        mock.Setup(e => e.ExecuteAsync(It.IsAny<SandboxRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SandboxResult { Model = "gpt-5.4", UnifiedScore = 1.0 });
        return mock.Object;
    }

    [Fact]
    public async Task ExecuteAsync_DrainsQueueAndObserves()
    {
        var queue = new SandboxWorkQueue(Options.Create(new SandboxOptions { QueueCapacity = 8 }));
        queue.TryEnqueue(Request());
        queue.TryEnqueue(Request());
        var observer = new CountingObserver(2);
        var service = new SandboxExecutionService(
            queue,
            StubExecutor(),
            observer,
            Options.Create(new SandboxOptions { Enabled = true, WorkerConcurrency = 1 }),
            NullLogger<SandboxExecutionService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await observer.Completed.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, observer.Count);
    }

    [Fact]
    public async Task ExecuteAsync_ExecutorThrows_LogsAndDropsItem_KeepsDrainingSubsequentItems()
    {
        var queue = new SandboxWorkQueue(Options.Create(new SandboxOptions { QueueCapacity = 8 }));
        queue.TryEnqueue(Request());
        queue.TryEnqueue(Request());
        var observer = new CountingObserver(1);

        var mock = new Mock<ISandboxExecutor>();
        var callCount = 0;
        mock.Setup(e => e.ExecuteAsync(It.IsAny<SandboxRequest>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 1
                    ? throw new InvalidOperationException("boom")
                    : Task.FromResult(new SandboxResult { Model = "gpt-5.4", UnifiedScore = 1.0 });
            });

        var service = new SandboxExecutionService(
            queue,
            mock.Object,
            observer,
            Options.Create(new SandboxOptions { Enabled = true, WorkerConcurrency = 1 }),
            NullLogger<SandboxExecutionService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await observer.Completed.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        // First item's failure was swallowed (dropped, not observed); the second still made it through.
        Assert.Equal(1, observer.Count);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task ExecuteAsync_ExecutorThrowsOperationCanceled_DropsItemWithoutFaultingWorker()
    {
        var queue = new SandboxWorkQueue(Options.Create(new SandboxOptions { QueueCapacity = 8 }));
        queue.TryEnqueue(Request());
        queue.TryEnqueue(Request());
        var observer = new CountingObserver(1);

        var mock = new Mock<ISandboxExecutor>();
        var callCount = 0;
        mock.Setup(e => e.ExecuteAsync(It.IsAny<SandboxRequest>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 1
                    ? throw new OperationCanceledException("run cancelled")
                    : Task.FromResult(new SandboxResult { Model = "gpt-5.4", UnifiedScore = 1.0 });
            });

        var service = new SandboxExecutionService(
            queue,
            mock.Object,
            observer,
            Options.Create(new SandboxOptions { Enabled = true, WorkerConcurrency = 1 }),
            NullLogger<SandboxExecutionService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await observer.Completed.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        // A single request's own cancellation must not tear down the worker loop - the second item still
        // gets processed and observed.
        Assert.Equal(1, observer.Count);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task ExecuteAsync_Disabled_DoesNotObserve()
    {
        var queue = new SandboxWorkQueue(Options.Create(new SandboxOptions { QueueCapacity = 8 }));
        queue.TryEnqueue(Request());
        var observer = new CountingObserver(1);
        var service = new SandboxExecutionService(
            queue,
            StubExecutor(),
            observer,
            Options.Create(new SandboxOptions { Enabled = false }),
            NullLogger<SandboxExecutionService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, observer.Count);
    }
}

