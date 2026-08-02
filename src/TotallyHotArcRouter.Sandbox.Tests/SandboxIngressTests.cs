using TotallyHot.ArcRouter.Sandbox;
using TotallyHot.ArcRouter.Sandbox.Execution;
using TotallyHot.ArcRouter.Sandbox.Ingress;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace TotallyHot.ArcRouter.Sandbox.Tests;

/// <summary>Covers the proxy-facing ingress: sampling, disabled mode, and non-blocking enqueue.</summary>
public class SandboxIngressTests
{
    private static SandboxIngestContext Context() =>
        new("```python\nprint(1)\n```", "generate code", "gpt-5.4", "corr", "sess");

    private static SandboxRequest Request() =>
        new("print(1)", SandboxLanguage.Python, "code_generation", "gpt-5.4", "corr", "sess");

    private static SandboxIngress CreateIngress(
        SandboxOptions options,
        Mock<ISignalExtractor> extractor,
        Mock<ISandboxQueue> queue) =>
        new(extractor.Object, queue.Object, Options.Create(options), NullLogger<SandboxIngress>.Instance);

    [Fact]
    public void TryIngest_Disabled_DoesNotEnqueue()
    {
        var extractor = new Mock<ISignalExtractor>();
        var queue = new Mock<ISandboxQueue>();
        var ingress = CreateIngress(new SandboxOptions { Enabled = false }, extractor, queue);

        ingress.TryIngest(Context());

        queue.Verify(q => q.TryEnqueue(It.IsAny<SandboxRequest>()), Times.Never);
        extractor.Verify(e => e.Extract(It.IsAny<SignalExtractionContext>()), Times.Never);
    }

    [Fact]
    public void TryIngest_ZeroSampling_DoesNotEnqueue()
    {
        var extractor = new Mock<ISignalExtractor>();
        var queue = new Mock<ISandboxQueue>();
        var ingress = CreateIngress(new SandboxOptions { SamplingRate = 0.0 }, extractor, queue);

        ingress.TryIngest(Context());

        queue.Verify(q => q.TryEnqueue(It.IsAny<SandboxRequest>()), Times.Never);
    }

    [Fact]
    public void TryIngest_RunnableBlock_Enqueues()
    {
        var extractor = new Mock<ISignalExtractor>();
        extractor.Setup(e => e.Extract(It.IsAny<SignalExtractionContext>())).Returns(Request());
        var queue = new Mock<ISandboxQueue>();
        queue.Setup(q => q.TryEnqueue(It.IsAny<SandboxRequest>())).Returns(true);
        var ingress = CreateIngress(new SandboxOptions(), extractor, queue);

        ingress.TryIngest(Context());

        queue.Verify(q => q.TryEnqueue(It.IsAny<SandboxRequest>()), Times.Once);
    }

    [Fact]
    public void TryIngest_NoRunnableBlock_DoesNotEnqueue()
    {
        var extractor = new Mock<ISignalExtractor>();
        extractor.Setup(e => e.Extract(It.IsAny<SignalExtractionContext>())).Returns((SandboxRequest?)null);
        var queue = new Mock<ISandboxQueue>();
        var ingress = CreateIngress(new SandboxOptions(), extractor, queue);

        ingress.TryIngest(Context());

        queue.Verify(q => q.TryEnqueue(It.IsAny<SandboxRequest>()), Times.Never);
    }

    [Fact]
    public void TryIngest_QueueFull_LogsAndDoesNotThrow()
    {
        var extractor = new Mock<ISignalExtractor>();
        extractor.Setup(e => e.Extract(It.IsAny<SignalExtractionContext>())).Returns(Request());
        var queue = new Mock<ISandboxQueue>();
        queue.Setup(q => q.TryEnqueue(It.IsAny<SandboxRequest>())).Returns(false);
        var ingress = CreateIngress(new SandboxOptions(), extractor, queue);

        // Must not throw even though the enqueue is rejected - a full queue is a drop, not a failure.
        ingress.TryIngest(Context());

        queue.Verify(q => q.TryEnqueue(It.IsAny<SandboxRequest>()), Times.Once);
    }

    [Fact]
    public void TryIngest_ExtractorThrows_IsSwallowed()
    {
        var extractor = new Mock<ISignalExtractor>();
        extractor.Setup(e => e.Extract(It.IsAny<SignalExtractionContext>())).Throws(new InvalidOperationException("boom"));
        var queue = new Mock<ISandboxQueue>();
        var ingress = CreateIngress(new SandboxOptions(), extractor, queue);

        // Must not throw — best-effort contract.
        ingress.TryIngest(Context());

        queue.Verify(q => q.TryEnqueue(It.IsAny<SandboxRequest>()), Times.Never);
    }
}

