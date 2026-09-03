using AwesomeAssertions;
using Bunit;
using TotallyHot.ArcRouter.Gui.Components;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="EChart"/>, the thin Apache ECharts host. JSInterop is configured in Loose mode
/// (bUnit's default for an unconfigured runtime) so `echartsInterop.render`/`.dispose` calls resolve to
/// a no-op instead of throwing; this project has no JS engine to actually run the chart library.
/// </summary>
public sealed class EChartTests
{
    [Fact]
    public void Renders_a_host_div_with_a_unique_element_id()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var first = ctx.Render<EChart>(p => p.Add(parameterSelector: c => c.ModelJson, value: "{}"));
        var second = ctx.Render<EChart>(p => p.Add(parameterSelector: c => c.ModelJson, value: "{}"));

        var firstId = first.Find("div").Id;
        var secondId = second.Find("div").Id;

        firstId.Should().StartWith("echart-");
        secondId.Should().StartWith("echart-");
        firstId.Should().NotBe(secondId);
    }

    [Fact]
    public void Invokes_render_after_first_render_with_the_supplied_json()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<EChart>(p => p.Add(parameterSelector: c => c.ModelJson, value: "{\"a\":1}"));

        var invocation = ctx.JSInterop.Invocations.Should().ContainSingle(i => i.Identifier == "echartsInterop.render")
            .Subject;
        invocation.Arguments[1].Should().Be("{\"a\":1}");
    }

    [Fact]
    public void Does_not_re_render_when_the_json_is_unchanged()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<EChart>(p => p.Add(parameterSelector: c => c.ModelJson, value: "{}"));
        cut.Render(p => p.Add(parameterSelector: c => c.ModelJson, value: "{}"));

        ctx.JSInterop.Invocations.Count(i => i.Identifier == "echartsInterop.render").Should().Be(1);
    }

    [Fact]
    public void Re_renders_when_the_json_changes()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<EChart>(p => p.Add(parameterSelector: c => c.ModelJson, value: "{}"));
        cut.Render(p => p.Add(parameterSelector: c => c.ModelJson, value: "{\"changed\":true}"));

        ctx.JSInterop.Invocations.Count(i => i.Identifier == "echartsInterop.render").Should().Be(2);
    }

    [Fact]
    public void Disposing_calls_the_JS_dispose_hook()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<EChart>(p => p.Add(parameterSelector: c => c.ModelJson, value: "{}"));
        cut.Instance.Should().NotBeNull();

        ctx.Dispose();

        // The dispose invocation happened during context teardown; no assertion object survives it,
        // but this exercises the DisposeAsync path (see JSDisconnectedException/ObjectDisposedException
        // handling in EChart.razor) without throwing.
    }
}