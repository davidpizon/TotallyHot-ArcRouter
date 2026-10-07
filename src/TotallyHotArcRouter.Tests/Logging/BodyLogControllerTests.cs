using Serilog.Events;
using Serilog.Parsing;
using TotallyHot.ArcRouter.Logging;

namespace TotallyHot.ArcRouter.Tests.Logging;

/// <summary>
/// Covers <see cref="BodyLogController"/>: opt-in body files, Clear under an open sink, and the
/// ConversationBody gate (#184 phase 3).
/// </summary>
public sealed class BodyLogControllerTests : IDisposable
{
    private readonly string _directory = Path.Combine(TestScratchDirectory.RunRoot,
        "bodies-" + Guid.NewGuid().ToString("N")[..8]);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    /// <summary>A marked event is written when body excerpts are enabled.</summary>
    [Fact]
    public void Emit_WhenEnabled_WritesMarkedEventToBodiesFile()
    {
        var controller = new BodyLogController(_directory, isEnabled: () => true);
        controller.Emit(MarkedEvent("prompt text with sk-abcdefghijklmnopqrstuvwxyz012345"));
        controller.Dispose();

        var files = Directory.GetFiles(_directory, "bodies*.log");
        Assert.Single(files);
        var contents = File.ReadAllText(files[0]);
        Assert.Contains("prompt text", contents, StringComparison.Ordinal);
    }

    /// <summary>Unmarked diagnostic events never create a body file.</summary>
    [Fact]
    public void Emit_UnmarkedEvent_IsIgnored()
    {
        using var controller = new BodyLogController(_directory, isEnabled: () => true);
        controller.Emit(UnmarkedEvent("ordinary diagnostic"));

        Assert.False(Directory.Exists(_directory) && Directory.EnumerateFiles(_directory, "bodies*.log").Any());
    }

    /// <summary>Clear closes the open sink, deletes body files, and lets a later emit reopen.</summary>
    [Fact]
    public void ClearBodyFiles_DeletesOpenBodiesFileAndAllowsReopen()
    {
        var controller = new BodyLogController(_directory, isEnabled: () => true);
        controller.Emit(MarkedEvent("first body"));
        Assert.NotEmpty(Directory.GetFiles(_directory, "bodies*.log"));

        Assert.True(controller.ClearBodyFiles());
        Assert.Empty(Directory.GetFiles(_directory, "bodies*.log"));

        controller.Emit(MarkedEvent("second body"));
        controller.Dispose();

        var files = Directory.GetFiles(_directory, "bodies*.log");
        Assert.Single(files);
        var contents = File.ReadAllText(files[0]);
        Assert.Contains("second body", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("first body", contents, StringComparison.Ordinal);
    }

    private static LogEvent MarkedEvent(string message)
    {
        return new LogEvent(
            timestamp: DateTimeOffset.UtcNow,
            level: LogEventLevel.Information,
            exception: null,
            messageTemplate: new MessageTemplateParser().Parse(message),
            properties:
            [
                new LogEventProperty(ConversationBodyLogging.PropertyName, new ScalarValue(true))
            ]);
    }

    private static LogEvent UnmarkedEvent(string message)
    {
        return new LogEvent(
            timestamp: DateTimeOffset.UtcNow,
            level: LogEventLevel.Information,
            exception: null,
            messageTemplate: new MessageTemplateParser().Parse(message),
            properties: []);
    }
}
