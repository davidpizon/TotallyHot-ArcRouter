using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>
/// Covers <see cref="ITelemetryPublisher.PublishQualitySignalAsync"/>'s default implementation: existing
/// implementers may ignore the quality-signal event entirely, so the interface supplies a completed-task
/// no-op rather than forcing every implementer to add an empty override.
/// </summary>
public class ITelemetryPublisherDefaultMemberTests
{
    [Fact]
    public async Task PublishQualitySignalAsync_NotOverridden_CompletesAsANoOp()
    {
        ITelemetryPublisher publisher = new PublisherWithoutQualitySignalOverride();

        await publisher.PublishQualitySignalAsync(
            new QualitySignalEvent(
                CorrelationId: "corr-1",
                SessionId: "session-1",
                Dimension: "code_generation",
                Model: "test-model",
                Language: "csharp",
                SyntaxValid: true,
                SyntaxAuthoritative: true,
                AnalysisScore: 0.8,
                JudgeScore: null,
                UnifiedScore: 0.8,
                DegradedReason: null,
                TimestampUtc: DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);
    }

    private sealed class PublisherWithoutQualitySignalOverride : ITelemetryPublisher
    {
        public Task PublishAsync(RoutingTelemetryEvent telemetryEvent, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task PublishLogLineAsync(LogLineEvent logLine, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
