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
        Mock<IQualityQueue> queue)
    {
        return new QualityIngress(extractor: extractor.Object, queue: queue.Object, options: Options.Create(options),
            logger: NullLogger<QualityIngress>.Instance);
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
    public void TryIngest_NoRunnableBlock_DoesNotEnqueue()
    {
        var extractor = new Mock<ISignalExtractor>();
        extractor.Setup(e => e.Extract(It.IsAny<SignalExtractionContext>())).Returns((QualityRequest?)null);
        var queue = new Mock<IQualityQueue>();
        var ingress = CreateIngress(options: new QualityOptions(), extractor: extractor, queue: queue);

        ingress.TryIngest(Context());

        queue.Verify(expression: q => q.TryEnqueue(It.IsAny<QualityRequest>()), times: Times.Never);
    }

    [Fact]
    public void TryIngest_QueueFull_LogsAndDoesNotThrow()
    {
        var extractor = new Mock<ISignalExtractor>();
        extractor.Setup(e => e.Extract(It.IsAny<SignalExtractionContext>())).Returns(Request());
        var queue = new Mock<IQualityQueue>();
        queue.Setup(q => q.TryEnqueue(It.IsAny<QualityRequest>())).Returns(false);
        var ingress = CreateIngress(options: new QualityOptions(), extractor: extractor, queue: queue);

        // Must not throw even though the enqueue is rejected - a full queue is a drop, not a failure.
        ingress.TryIngest(Context());

        queue.Verify(expression: q => q.TryEnqueue(It.IsAny<QualityRequest>()), times: Times.Once);
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
}