using Moq;
using Serilog.Events;
using Serilog.Parsing;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers <see cref="TelemetryLogEventSink"/>: the Serilog-to-Console-tab bridge.</summary>
public class TelemetryLogEventSinkTests
{
    private static LogEvent SampleEvent(LogEventLevel level, string message, DateTimeOffset? timestamp = null)
    {
        return new LogEvent(
            timestamp: timestamp ?? DateTimeOffset.UtcNow,
            level: level,
            null,
            messageTemplate: new MessageTemplateParser().Parse(message),
            properties: []);
    }

    [Fact]
    public void Constructor_NullPublisher_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new TelemetryLogEventSink(null!));
    }

    [Fact]
    public void Emit_NullLogEvent_Throws()
    {
        var sink = new TelemetryLogEventSink(Mock.Of<ITelemetryPublisher>());

        Assert.Throws<ArgumentNullException>(() => sink.Emit(null!));
    }

    [Fact]
    public void Emit_PublishesRenderedMessageAndTimestamp()
    {
        LogLineEvent? published = null;
        var publisherMock = new Mock<ITelemetryPublisher>();
        publisherMock
            .Setup(p => p.PublishLogLineAsync(It.IsAny<LogLineEvent>(), It.IsAny<CancellationToken>()))
            .Callback<LogLineEvent, CancellationToken>((line, _) => published = line)
            .Returns(Task.CompletedTask);

        var sink = new TelemetryLogEventSink(publisherMock.Object);
        sink.Emit(SampleEvent(level: LogEventLevel.Information, message: "Connected to database."));

        Assert.NotNull(published);
        Assert.Equal(expected: "INFO", actual: published!.Level);
        Assert.Equal(expected: "Connected to database.", actual: published.Message);
    }

    [Fact]
    public void Emit_UsesLogEventOwnTimestamp_NormalizedToUtc()
    {
        // A non-UTC offset, so a bug reverting to DateTimeOffset.UtcNow (wrong point in time
        // entirely, not just wrong offset) would fail this even though both are "3 PM local".
        var timestamp = new DateTimeOffset(2026, 7, 9, 15, 0, 0, offset: TimeSpan.FromHours(-5));
        LogLineEvent? published = null;
        var publisherMock = new Mock<ITelemetryPublisher>();
        publisherMock
            .Setup(p => p.PublishLogLineAsync(It.IsAny<LogLineEvent>(), It.IsAny<CancellationToken>()))
            .Callback<LogLineEvent, CancellationToken>((line, _) => published = line)
            .Returns(Task.CompletedTask);

        var sink = new TelemetryLogEventSink(publisherMock.Object);
        sink.Emit(SampleEvent(level: LogEventLevel.Information, message: "hi", timestamp: timestamp));

        Assert.NotNull(published);
        Assert.Equal(expected: timestamp.ToUniversalTime(), actual: published!.TimestampUtc);
        Assert.Equal(expected: TimeSpan.Zero, actual: published.TimestampUtc.Offset);
    }

    [Theory]
    [InlineData(LogEventLevel.Verbose, "DEBUG")]
    [InlineData(LogEventLevel.Debug, "DEBUG")]
    [InlineData(LogEventLevel.Information, "INFO")]
    [InlineData(LogEventLevel.Warning, "WARN")]
    [InlineData(LogEventLevel.Error, "ERROR")]
    [InlineData(LogEventLevel.Fatal, "FATAL")]
    public void NormalizeLevel_MapsToGuiExpectedShortForm(LogEventLevel level, string expected)
    {
        Assert.Equal(expected: expected, actual: TelemetryLogEventSink.NormalizeLevel(level));
    }

    [Fact]
    public void Emit_PublisherThrowsSynchronously_PropagatesRatherThanSwallowing()
    {
        // Emit itself has no try/catch - fault isolation is ITelemetryPublisher's contract
        // (PublishLogLineAsync must never throw). This documents that Emit relies entirely on that
        // contract rather than duplicating it.
        var publisherMock = new Mock<ITelemetryPublisher>();
        publisherMock
            .Setup(p => p.PublishLogLineAsync(It.IsAny<LogLineEvent>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("boom"));

        var sink = new TelemetryLogEventSink(publisherMock.Object);

        Assert.Throws<InvalidOperationException>(() =>
            sink.Emit(SampleEvent(level: LogEventLevel.Information, message: "hi")));
    }
}