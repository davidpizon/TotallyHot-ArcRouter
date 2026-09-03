namespace TotallyHot.ArcRouter.Gui.Console.Tests;

/// <summary>Covers <see cref="LogLineFormatter"/>: the shared viewport/copy text formatting.</summary>
public class LogLineFormatterTests
{
    [Fact]
    public void Format_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => LogLineFormatter.Format(null!));
    }

    [Fact]
    public void Format_PadsLevelAndJoinsFields()
    {
        var timestamp = new DateTimeOffset(2026, 7, 9, 21, 10, 1, offset: TimeSpan.Zero);
        var line = new LogLineDto(TimestampUtc: timestamp, Level: "WARN", Message: "API latency spike detected.");

        var formatted = LogLineFormatter.Format(line);

        Assert.Equal(
            expected: $"[{timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}] [WARN ]  API latency spike detected.",
            actual: formatted);
    }

    [Fact]
    public void FormatAll_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => LogLineFormatter.FormatAll(null!));
    }

    [Fact]
    public void FormatAll_Empty_ReturnsEmptyString()
    {
        Assert.Equal(expected: string.Empty, actual: LogLineFormatter.FormatAll([]));
    }

    [Fact]
    public void FormatAll_MultipleLines_JoinsWithNewlineInOrder()
    {
        var timestamp = new DateTimeOffset(2026, 7, 9, 21, 10, 1, offset: TimeSpan.Zero);
        var lines = new[]
        {
            new LogLineDto(TimestampUtc: timestamp, Level: "INFO", Message: "first"),
            new LogLineDto(TimestampUtc: timestamp.AddSeconds(1), Level: "ERROR", Message: "second")
        };

        var formatted = LogLineFormatter.FormatAll(lines);

        Assert.Equal(
            expected: LogLineFormatter.Format(lines[0]) + Environment.NewLine + LogLineFormatter.Format(lines[1]),
            actual: formatted);
    }
}