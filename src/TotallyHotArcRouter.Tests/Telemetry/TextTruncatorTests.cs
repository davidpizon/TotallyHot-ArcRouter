using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers <see cref="TextTruncator"/>: bounding text broadcast over the telemetry hub.</summary>
public class TextTruncatorTests
{
    [Fact]
    public void Truncate_Null_ReturnsNull()
    {
        Assert.Null(TextTruncator.Truncate(null));
    }

    [Fact]
    public void Truncate_ShorterThanMax_ReturnsUnchanged()
    {
        Assert.Equal(expected: "hello", actual: TextTruncator.Truncate(text: "hello", 10));
    }

    [Fact]
    public void Truncate_ExactlyAtMax_ReturnsUnchanged()
    {
        var text = new string('a', 10);

        Assert.Equal(expected: text, actual: TextTruncator.Truncate(text: text, 10));
    }

    [Fact]
    public void Truncate_LongerThanMax_TruncatesWithMarker()
    {
        var text = new string('a', 15);

        var result = TextTruncator.Truncate(text: text, 10);

        Assert.Equal(expected: new string('a', 10) + "…", actual: result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Truncate_NonPositiveMaxLength_Throws(int maxLength)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TextTruncator.Truncate(text: "hello", maxLength: maxLength));
    }

    [Fact]
    public void Truncate_DefaultMaxLength_Is2000()
    {
        Assert.Equal(2000, actual: TextTruncator.DefaultMaxLength);
    }
}