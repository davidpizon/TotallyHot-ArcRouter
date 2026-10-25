using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TotallyHot.ArcRouter.Quality.Grading;
using TotallyHot.ArcRouter.Quality.Ingress;

namespace TotallyHot.ArcRouter.Quality.Tests;

/// <summary>Covers the proxy-facing ingress: sampling, disabled mode, and non-blocking enqueue.</summary>
public class QualityIngressTests
{
    private static QualityIngestContext Context()
    {
        return new QualityIngestContext(ResponseText: "```python\nprint(1)\n```", Prompt: "generate code",
            Model: "gpt-5.4",
            CorrelationId: "corr", SessionId: "sess");
    }

    private static QualityRequest Request()
    {
        return new QualityRequest(Code: "print(1)", Language: CodeLanguage.Python, Prompt: "generate code",
            Dimension: "code_generation",
            Model: "gpt-5.4", CorrelationId: "corr", SessionId: "sess");
    }

    private static QualityIngress CreateIngress(
        QualityOptions options,
        Mock<ISignalExtractor> extractor,
        Mock<IQualityQueue> queue,
        IQualityScoreAggregator? aggregator = null)
    {
        return new QualityIngress(extractor: extractor.Object, queue: queue.Object, options: Options.Create(options),
            logger: NullLogger<QualityIngress>.Instance, aggregator: aggregator);
    }

    [Fact]
    public void TryIngest_Disabled_DoesNotEnqueue()
    {
        var extractor = new Mock<ISignalExtractor>();
        var queue = new Mock<IQualityQueue>();
        var ingress = CreateIngress(options: new QualityOptions { Enabled = false }, extractor: extractor,
            queue: queue);

        ingress.TryIngest(Context());

        queue.Verify(expression: q => q.TryEnqueue(It.IsAny<QualityRequest>()), times: Times.Never);
        extractor.Verify(expression: e => e.Extract(It.IsAny<SignalExtractionContext>()), times: Times.Never);
    }

    [Fact]
    public void TryIngest_ZeroSampling_DoesNotEnqueue()
    {
        var extractor = new Mock<ISignalExtractor>();
        var queue = new Mock<IQualityQueue>();
        var ingress = CreateIngress(options: new QualityOptions { SamplingRate = 0.0 }, extractor: extractor,
            queue: queue);

        ingress.TryIngest(Context());

        queue.Verify(expression: q => q.TryEnqueue(It.IsAny<QualityRequest>()), times: Times.Never);
    }

    [Fact]
    public void TryIngest_RunnableBlock_Enqueues()
    {
        var extractor = new Mock<ISignalExtractor>();
        extractor.Setup(e => e.Extract(It.IsAny<SignalExtractionContext>())).Returns(Request());
        var queue = new Mock<IQualityQueue>();
        queue.Setup(q => q.TryEnqueue(It.IsAny<QualityRequest>())).Returns(true);
        var ingress = CreateIngress(options: new QualityOptions(), extractor: extractor, queue: queue);

        ingress.TryIngest(Context());

        queue.Verify(expression: q => q.TryEnqueue(It.IsAny<QualityRequest>()), times: Times.Once);
    }

    [Fact]
    public void TryIngest_NoRunnableBlock_EnqueuesBootstrapRecipe()
    {
        var extractor = new Mock<ISignalExtractor>();
        extractor.Setup(e => e.Extract(It.IsAny<SignalExtractionContext>())).Returns((QualityRequest?)null);
        var queue = new Mock<IQualityQueue>();
        queue.Setup(q => q.TryEnqueue(It.IsAny<QualityRequest>())).Returns(true);
        var ingress = CreateIngress(options: new QualityOptions(), extractor: extractor, queue: queue);

        ingress.TryIngest(new QualityIngestContext(
            ResponseText: "here is a prose answer with no fence",
            Prompt: "what is a mutex",
            Model: "gpt-5.4",
            CorrelationId: "corr",
            SessionId: "sess"));

        queue.Verify(
            expression: q => q.TryEnqueue(It.Is<QualityRequest>(r =>
                r.RecipeId == GradingRecipes.Bootstrap &&
                r.Code == string.Empty &&
                r.ResponseText == "here is a prose answer with no fence")),
            times: Times.Once);
    }

    [Fact]
    public async Task TryIngest_NoTextAndNoTools_SubmitsNoTextIncomplete()
    {
        var extractor = new Mock<ISignalExtractor>();
        var queue = new Mock<IQualityQueue>();
        var submitted = new TaskCompletionSource<QualityResult>();
        var aggregator = new Mock<IQualityScoreAggregator>();
        aggregator.Setup(a => a.SubmitAsync(It.IsAny<QualityResult>(), It.IsAny<CancellationToken>()))
            .Callback<QualityResult, CancellationToken>((result, _) => submitted.TrySetResult(result))
            .Returns(Task.CompletedTask);
        var ingress = CreateIngress(options: new QualityOptions(), extractor: extractor, queue: queue,
            aggregator: aggregator.Object);

        ingress.TryIngest(new QualityIngestContext(
            ResponseText: string.Empty,
            Prompt: "what is a mutex",
            Model: "gpt-5.4",
            CorrelationId: "corr",
            SessionId: "sess"));

        queue.Verify(expression: q => q.TryEnqueue(It.IsAny<QualityRequest>()), times: Times.Never);
        extractor.Verify(expression: e => e.Extract(It.IsAny<SignalExtractionContext>()), times: Times.Never);
        var observed = await submitted.Task.WaitAsync(TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        Assert.Equal(expected: IncompleteReasons.NoText, actual: observed.IncompleteReason);
    }

    [Fact]
    public async Task TryIngest_WhitespaceOnly_SubmitsEmptyIncomplete()
    {
        var extractor = new Mock<ISignalExtractor>();
        var queue = new Mock<IQualityQueue>();
        var submitted = new TaskCompletionSource<QualityResult>();
        var aggregator = new Mock<IQualityScoreAggregator>();
        aggregator.Setup(a => a.SubmitAsync(It.IsAny<QualityResult>(), It.IsAny<CancellationToken>()))
            .Callback<QualityResult, CancellationToken>((result, _) => submitted.TrySetResult(result))
            .Returns(Task.CompletedTask);
        var ingress = CreateIngress(options: new QualityOptions(), extractor: extractor, queue: queue,
            aggregator: aggregator.Object);

        ingress.TryIngest(new QualityIngestContext(
            ResponseText: "   \n",
            Prompt: "what is a mutex",
            Model: "gpt-5.4",
            CorrelationId: "corr",
            SessionId: "sess"));

        var observed = await submitted.Task.WaitAsync(TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        Assert.Equal(expected: IncompleteReasons.Empty, actual: observed.IncompleteReason);
    }

    [Fact]
    public async Task TryIngest_ToolOnlyBody_SubmitsDeferredToLoopAndLearnsNothing()
    {
        var (ingress, extractor, queue, submitted) = CreateTerminalIngress();

        ingress.TryIngest(new QualityIngestContext(
            ResponseText: string.Empty,
            Prompt: "call the tool",
            Model: "gpt-5.4",
            CorrelationId: "corr",
            SessionId: "sess",
            HasToolCalls: true));

        var observed = await submitted.Task.WaitAsync(TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        Assert.Equal(expected: IncompleteReasons.DeferredToLoop, actual: observed.IncompleteReason);
        AssertLearnsNothing(observed);
        queue.Verify(expression: q => q.TryEnqueue(It.IsAny<QualityRequest>()), times: Times.Never);
        extractor.Verify(expression: e => e.Extract(It.IsAny<SignalExtractionContext>()), times: Times.Never);
    }

    [Fact]
    public async Task TryIngest_WhitespaceAndToolCalls_SubmitsDeferredToLoopNotEmpty()
    {
        var (ingress, _, queue, submitted) = CreateTerminalIngress();

        ingress.TryIngest(new QualityIngestContext(
            ResponseText: "   \n",
            Prompt: "call the tool",
            Model: "gpt-5.4",
            CorrelationId: "corr",
            SessionId: "sess",
            HasToolCalls: true));

        var observed = await submitted.Task.WaitAsync(TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        Assert.Equal(expected: IncompleteReasons.DeferredToLoop, actual: observed.IncompleteReason);
        AssertLearnsNothing(observed);
        queue.Verify(expression: q => q.TryEnqueue(It.IsAny<QualityRequest>()), times: Times.Never);
    }

    [Fact]
    public async Task TryIngest_TextAfterToolResults_SubmitsLoopUnlinkedAndLearnsNothing()
    {
        var (ingress, extractor, queue, submitted) = CreateTerminalIngress();

        ingress.TryIngest(new QualityIngestContext(
            ResponseText: "It is 72F.",
            Prompt: "what is the weather",
            Model: "gpt-5.4",
            CorrelationId: "corr",
            SessionId: "sess",
            FollowsToolResults: true));

        var observed = await submitted.Task.WaitAsync(TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        Assert.Equal(expected: IncompleteReasons.LoopUnlinked, actual: observed.IncompleteReason);
        AssertLearnsNothing(observed);
        queue.Verify(expression: q => q.TryEnqueue(It.IsAny<QualityRequest>()), times: Times.Never);
        extractor.Verify(expression: e => e.Extract(It.IsAny<SignalExtractionContext>()), times: Times.Never);
    }

    [Fact]
    public async Task TryIngest_TextAndToolCallsAfterToolResults_SubmitsLoopUnlinked()
    {
        var (ingress, _, queue, submitted) = CreateTerminalIngress();

        ingress.TryIngest(new QualityIngestContext(
            ResponseText: "It is 72F.",
            Prompt: "what is the weather",
            Model: "gpt-5.4",
            CorrelationId: "corr",
            SessionId: "sess",
            HasToolCalls: true,
            FollowsToolResults: true));

        var observed = await submitted.Task.WaitAsync(TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        Assert.Equal(expected: IncompleteReasons.LoopUnlinked, actual: observed.IncompleteReason);
        AssertLearnsNothing(observed);
        queue.Verify(expression: q => q.TryEnqueue(It.IsAny<QualityRequest>()), times: Times.Never);
    }

    [Fact]
    public void TryIngest_PlainText_EnqueuesAndDoesNotSubmitLoopUnlinked()
    {
        var extractor = new Mock<ISignalExtractor>();
        extractor.Setup(e => e.Extract(It.IsAny<SignalExtractionContext>())).Returns((QualityRequest?)null);
        var queue = new Mock<IQualityQueue>();
        queue.Setup(q => q.TryEnqueue(It.IsAny<QualityRequest>())).Returns(true);
        var aggregator = new Mock<IQualityScoreAggregator>();
        var ingress = CreateIngress(options: new QualityOptions(), extractor: extractor, queue: queue,
            aggregator: aggregator.Object);

        ingress.TryIngest(new QualityIngestContext(
            ResponseText: "Rain falls on the harbour.",
            Prompt: "write a poem about rain",
            Model: "gpt-5.4",
            CorrelationId: "corr",
            SessionId: "sess"));

        queue.Verify(
            expression: q => q.TryEnqueue(It.Is<QualityRequest>(r =>
                r.RecipeId == GradingRecipes.Bootstrap && r.ResponseText == "Rain falls on the harbour.")),
            times: Times.Once);
        aggregator.Verify(
            expression: a => a.SubmitAsync(It.IsAny<QualityResult>(), It.IsAny<CancellationToken>()),
            times: Times.Never);
    }

    [Fact]
    public async Task TryIngest_QueueFull_LogsAndDoesNotThrow()
    {
        var extractor = new Mock<ISignalExtractor>();
        extractor.Setup(e => e.Extract(It.IsAny<SignalExtractionContext>())).Returns(Request());
        var queue = new Mock<IQualityQueue>();
        queue.Setup(q => q.TryEnqueue(It.IsAny<QualityRequest>())).Returns(false);
        var submitted = new TaskCompletionSource<QualityResult>();
        var aggregator = new Mock<IQualityScoreAggregator>();
        aggregator.Setup(a => a.SubmitAsync(It.IsAny<QualityResult>(), It.IsAny<CancellationToken>()))
            .Callback<QualityResult, CancellationToken>((result, _) => submitted.TrySetResult(result))
            .Returns(Task.CompletedTask);
        var ingress = CreateIngress(options: new QualityOptions(), extractor: extractor, queue: queue,
            aggregator: aggregator.Object);

        // Must not throw even though the enqueue is rejected - a full queue is a drop, not a failure.
        ingress.TryIngest(Context());

        queue.Verify(expression: q => q.TryEnqueue(It.IsAny<QualityRequest>()), times: Times.Once);
        var observed = await submitted.Task.WaitAsync(TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        Assert.Equal(expected: IncompleteReasons.QueueFull, actual: observed.IncompleteReason);
    }

    [Fact]
    public void TryIngest_ExtractorThrows_IsSwallowed()
    {
        var extractor = new Mock<ISignalExtractor>();
        extractor.Setup(e => e.Extract(It.IsAny<SignalExtractionContext>()))
            .Throws(new InvalidOperationException("boom"));
        var queue = new Mock<IQualityQueue>();
        var ingress = CreateIngress(options: new QualityOptions(), extractor: extractor, queue: queue);

        // Must not throw — best-effort contract.
        ingress.TryIngest(Context());

        queue.Verify(expression: q => q.TryEnqueue(It.IsAny<QualityRequest>()), times: Times.Never);
    }

    private static (QualityIngress Ingress, Mock<ISignalExtractor> Extractor, Mock<IQualityQueue> Queue,
        TaskCompletionSource<QualityResult> Submitted) CreateTerminalIngress()
    {
        var extractor = new Mock<ISignalExtractor>();
        var queue = new Mock<IQualityQueue>();
        var submitted = new TaskCompletionSource<QualityResult>();
        var aggregator = new Mock<IQualityScoreAggregator>();
        aggregator.Setup(a => a.SubmitAsync(It.IsAny<QualityResult>(), It.IsAny<CancellationToken>()))
            .Callback<QualityResult, CancellationToken>((result, _) => submitted.TrySetResult(result))
            .Returns(Task.CompletedTask);
        var ingress = CreateIngress(options: new QualityOptions(), extractor: extractor, queue: queue,
            aggregator: aggregator.Object);
        return (ingress, extractor, queue, submitted);
    }

    private static void AssertLearnsNothing(QualityResult observed)
    {
        Assert.Equal(expected: GradeStatus.Incomplete, actual: observed.GradeStatus);
        Assert.Equal(expected: GradingRecipes.Bootstrap, actual: observed.RecipeId);
        Assert.Null(observed.JudgeScore);
        Assert.Null(observed.LocalScore);
    }
}