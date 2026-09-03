using AwesomeAssertions;
using Bunit;
using TotallyHot.ArcRouter.Gui.Components;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>Tests for the <see cref="Icon"/> glyph host: parameter defaults and a sample of glyphs.</summary>
public sealed class IconTests
{
    [Theory]
    [InlineData("activity")]
    [InlineData("bar-chart")]
    [InlineData("trending-up")]
    [InlineData("shield-check")]
    [InlineData("search")]
    [InlineData("alert-triangle")]
    [InlineData("check-circle")]
    [InlineData("copy")]
    [InlineData("x")]
    [InlineData("trash")]
    [InlineData("refresh")]
    [InlineData("terminal")]
    [InlineData("plus")]
    [InlineData("server")]
    [InlineData("lock")]
    [InlineData("unlock")]
    [InlineData("settings")]
    [InlineData("cpu-chip")]
    public void Renders_an_svg_for_every_known_glyph_name(string name)
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<Icon>(p => p.Add(x => x.Name, name));

        cut.Find("svg").Should().NotBeNull();
    }

    [Fact]
    public void Unknown_glyph_name_renders_an_empty_svg_without_throwing()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<Icon>(p => p.Add(x => x.Name, "does-not-exist"));

        cut.Find("svg").ChildElementCount.Should().Be(0);
    }

    [Fact]
    public void Size_defaults_to_13_pixels()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<Icon>(p => p.Add(x => x.Name, "x"));

        cut.Find("svg").GetAttribute("width").Should().Be("13");
        cut.Find("svg").GetAttribute("height").Should().Be("13");
    }

    [Fact]
    public void Size_class_and_style_parameters_are_forwarded()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<Icon>(p => p
            .Add(x => x.Name, "x")
            .Add(x => x.Size, 24)
            .Add(x => x.Class, "my-class")
            .Add(x => x.Style, "color:red"));

        var svg = cut.Find("svg");
        svg.GetAttribute("width").Should().Be("24");
        svg.GetAttribute("height").Should().Be("24");
        svg.GetAttribute("class").Should().Be("my-class");
        svg.GetAttribute("style").Should().Be("color:red");
    }
}

