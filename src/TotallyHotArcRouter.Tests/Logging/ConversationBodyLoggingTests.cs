using Microsoft.Extensions.Logging;
using Moq;
using TotallyHot.ArcRouter.Logging;
using TotallyHot.ArcRouter.Tests.TestSupport;

namespace TotallyHot.ArcRouter.Tests.Logging;

/// <summary>Covers the opt-in gate and secret obscuring on F9 emit helpers (#184 phase 3).</summary>
public sealed class ConversationBodyLoggingTests
{
    /// <summary>When the switch is off, nothing is logged.</summary>
    [Fact]
    public void LogExcerpt_WhenDisabled_DoesNotLog()
    {
        var logger = new Mock<ILogger>();
        var options = new StaticOptionsMonitor<BodyExcerptOptions>(new BodyExcerptOptions { Enabled = false });

        ConversationBodyLogging.LogExcerpt(logger.Object, options,
            ConversationBodyLogging.NewestUserMessage, "hello sk-abcdefghijklmnopqrstuvwxyz012345");

        logger.Verify(
            l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    /// <summary>When the switch is on, the line is Information and the key is obscured.</summary>
    [Fact]
    public void LogExcerpt_WhenEnabled_LogsInformationWithObscuredSecret()
    {
        var logger = new Mock<ILogger>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        object? capturedState = null;
        logger
            .Setup(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback((LogLevel _, EventId _, object state, Exception? _, Delegate _) => capturedState = state);

        var options = new StaticOptionsMonitor<BodyExcerptOptions>(new BodyExcerptOptions { Enabled = true });
        const string key = "sk-abcdefghijklmnopqrstuvwxyz012345";

        ConversationBodyLogging.LogExcerpt(logger.Object, options,
            ConversationBodyLogging.NewestUserMessage, $"hello {key}");

        logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
        Assert.NotNull(capturedState);
        var rendered = capturedState!.ToString() ?? string.Empty;
        Assert.DoesNotContain(key, rendered, StringComparison.Ordinal);
        Assert.Contains(SecretObscurer.RedactedToken, rendered, StringComparison.Ordinal);
    }
}
