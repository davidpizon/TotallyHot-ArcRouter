using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TotallyHot.ArcRouter.Quality.Grading;
using TotallyHot.ArcRouter.Quality.Ingress;

namespace TotallyHot.ArcRouter.Quality.Tests;

/// <summary>Covers the proxy-facing ingress: sampling, disabled mode, and non-blocking enqueue.</summary>
public class QualityIngressTests
{
    private static QualityIngestContext Context() =>
        new("```python\nprint(1)\n```", "generate code", "gpt-5.4", "corr", "sess");

    private static QualityRequest Request() =>
        new("print(1)", CodeLanguage.Python, "generate code", "code_generation", "gpt-5.4", "corr", "sess");

    private static QualityIngress CreateIngress(
        QualityOptions options,
        Mock<ISignalExtractor> extractor,
        Mock<IQualityQueue> queue) =>
        new(extractor.Object, queue.Object, Options.Create(options), NullLogger<QualityIngress>.Instance);

    [Fact]
    public void TryIngest_Disabled_DoesNotEnqueue()
    {
        var extractor = new Mock<ISignalExtractor>();
        var queue = new Mock<IQualityQueue>();
        var ingress = CreateIngress(new QualityOptions { Enabled = false }, extractor, queue);

        ingress.TryIngest(Context());

        queue.Verify(q => q.TryEnqueue(It.IsAny<QualityRequest>()), Times.Never);
        extractor.Verify(e => e.Extract(It.IsAny<SignalExtractionContext>()), Times.Never);
    }

    [Fact]
    public void TryIngest_ZeroSampling_DoesNotEnqueue()
    {
        var extractor = new Mock<ISignalExtractor>();
        var queue = new Mock<IQualityQueue>();
        var ingress = CreateIngress(new QualityOptions { SamplingRate = 0.0 }, extractor, queue);

        ingress.TryIngest(Context());

        queue.Verify(q => q.TryEnqueue(It.IsAny<QualityRequest>()), Times.Never);
    }

    [Fact]
    public void TryIngest_RunnableBlock_Enqueues()
    {
        var extractor = new Mock<ISignalExtractor>();
        extractor.Setup(e => e.Extract(It.IsAny<SignalExtractionContext>())).Returns(Request());
        var queue = new Mock<IQualityQueue>();
        queue.Setup(q => q.TryEnqueue(It.IsAny<QualityRequest>())).Returns(true);
        var ingress = CreateIngress(new QualityOptions(), extractor, queue);

        ingress.TryIngest(Context());

        queue.Verify(q => q.TryEnqueue(It.IsAny<QualityRequest>()), Times.Once);
    }

    [Fact]
    public void TryIngest_NoRunnableBlock_DoesNotEnqueue()
    {
        var extractor = new Mock<ISignalExtractor>();
        extractor.Setup(e => e.Extract(It.IsAny<SignalExtractionContext>())).Returns((QualityRequest?)null);
        var queue = new Mock<IQualityQueue>();
        var ingress = CreateIngress(new QualityOptions(), extractor, queue);

        ingress.TryIngest(Context());

        queue.Verify(q => q.TryEnqueue(It.IsAny<QualityRequest>()), Times.Never);
    }

    [Fact]
    public void TryIngest_QueueFull_LogsAndDoesNotThrow()
    {
        var extractor = new Mock<ISignalExtractor>();
        extractor.Setup(e => e.Extract(It.IsAny<SignalExtractionContext>())).Returns(Request());
        var queue = new Mock<IQualityQueue>();
        queue.Setup(q => q.TryEnqueue(It.IsAny<QualityRequest>())).Returns(false);
        var ingress = CreateIngress(new QualityOptions(), extractor, queue);

        // Must not throw even though the enqueue is rejected - a full queue is a drop, not a failure.
        ingress.TryIngest(Context());

        queue.Verify(q => q.TryEnqueue(It.IsAny<QualityRequest>()), Times.Once);
    }

    [Fact]
    public void TryIngest_ExtractorThrows_IsSwallowed()
    {
        var extractor = new Mock<ISignalExtractor>();
        extractor.Setup(e => e.Extract(It.IsAny<SignalExtractionContext>())).Throws(new InvalidOperationException("boom"));
        var queue = new Mock<IQualityQueue>();
        var ingress = CreateIngress(new QualityOptions(), extractor, queue);

        // Must not throw — best-effort contract.
        ingress.TryIngest(Context());

        queue.Verify(q => q.TryEnqueue(It.IsAny<QualityRequest>()), Times.Never);
    }
}

