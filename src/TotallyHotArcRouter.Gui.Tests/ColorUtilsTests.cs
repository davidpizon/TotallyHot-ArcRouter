using AwesomeAssertions;
using TotallyHot.ArcRouter.Gui.Utils;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>Tests for <see cref="ColorUtils"/>: it is a thin delegation to <c>ChartPalette</c>, so these
/// tests only guard the delegation itself (determinism and null-safety), not the palette's hashing.</summary>
public sealed class ColorUtilsTests
{
    [Fact]
    public void GetColorForAgent_is_deterministic_for_the_same_name()
    {
        var first = ColorUtils.GetColorForAgent("Code Review Bot");
        var second = ColorUtils.GetColorForAgent("Code Review Bot");

        first.Should().Be(second);
        first.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GetColorForAgent_handles_a_null_name()
    {
        var act = () => ColorUtils.GetColorForAgent(null);
        act.Should().NotThrow();
    }

    [Fact]
    public void GetColorForAgent_returns_different_colors_for_different_names_in_practice()
    {
        var a = ColorUtils.GetColorForAgent("Data Analyst Wrapper");
        var b = ColorUtils.GetColorForAgent("Customer Support NLP");

        a.Should().NotBe(b);
    }
}

