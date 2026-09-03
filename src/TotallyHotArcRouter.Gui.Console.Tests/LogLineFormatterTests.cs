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
        var timestamp = new DateTimeOffset(2026, 7, 9, 21, 10, 1, TimeSpan.Zero);
        var line = new LogLineDto(timestamp, "WARN", "API latency spike detected.");

        var formatted = LogLineFormatter.Format(line);

        Assert.Equal(
            $"[{timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}] [WARN ]  API latency spike detected.",
            formatted);
    }

    [Fact]
    public void FormatAll_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => LogLineFormatter.FormatAll(null!));
    }

    [Fact]
    public void FormatAll_Empty_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, LogLineFormatter.FormatAll([]));
    }

    [Fact]
    public void FormatAll_MultipleLines_JoinsWithNewlineInOrder()
    {
        var timestamp = new DateTimeOffset(2026, 7, 9, 21, 10, 1, TimeSpan.Zero);
        var lines = new[]
        {
            new LogLineDto(timestamp, "INFO", "first"),
            new LogLineDto(timestamp.AddSeconds(1), "ERROR", "second"),
        };

        var formatted = LogLineFormatter.FormatAll(lines);

        Assert.Equal(
            LogLineFormatter.Format(lines[0]) + Environment.NewLine + LogLineFormatter.Format(lines[1]),
            formatted);
    }
}

