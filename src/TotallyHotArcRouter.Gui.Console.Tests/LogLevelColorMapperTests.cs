using TotallyHot.ArcRouter.Gui.Console;

namespace TotallyHot.ArcRouter.Gui.Console.Tests;

/// <summary>Covers <see cref="LogLevelColorMapper"/>: level-to-color mapping for the log viewport.</summary>
public class LogLevelColorMapperTests
{
    [Theory]
    [InlineData("DEBUG", LogLevelColorMapper.DebugColor)]
    [InlineData("debug", LogLevelColorMapper.DebugColor)]
    [InlineData("VERBOSE", LogLevelColorMapper.DebugColor)]
    [InlineData("TRACE", LogLevelColorMapper.DebugColor)]
    [InlineData("INFO", LogLevelColorMapper.InfoColor)]
    [InlineData("INFORMATION", LogLevelColorMapper.InfoColor)]
    [InlineData("WARN", LogLevelColorMapper.WarnColor)]
    [InlineData("WARNING", LogLevelColorMapper.WarnColor)]
    [InlineData("ERROR", LogLevelColorMapper.ErrorColor)]
    [InlineData("FATAL", LogLevelColorMapper.FatalColor)]
    [InlineData("CRITICAL", LogLevelColorMapper.FatalColor)]
    public void GetColor_KnownLevel_ReturnsSpecColor(string level, string expectedColor)
    {
        Assert.Equal(expectedColor, LogLevelColorMapper.GetColor(level));
    }

    [Fact]
    public void GetColor_MixedCaseWithWhitespace_IsCaseAndTrimInsensitive()
    {
        Assert.Equal(LogLevelColorMapper.ErrorColor, LogLevelColorMapper.GetColor("  Error  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NOTALEVEL")]
    public void GetColor_UnrecognizedLevel_FallsBackWithoutThrowing(string? level)
    {
        Assert.Equal(LogLevelColorMapper.UnknownColor, LogLevelColorMapper.GetColor(level));
    }
}

