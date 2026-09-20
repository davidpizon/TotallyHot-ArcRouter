using AwesomeAssertions;
using Bunit;
using TotallyHot.ArcRouter.Gui.Components;
using TotallyHot.ArcRouter.Gui.Services;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="ClientDropInPanel"/>: the default loopback base URL and <c>auto</c> model are
/// visible and copy-pasteable, and each copy button writes the matching payload to the clipboard.
/// </summary>
public sealed class ClientDropInPanelTests
{
    private static BunitContext NewContext(out FakeClipboardService clipboard)
    {
        var ctx = new BunitContext();
        clipboard = new FakeClipboardService();
        ctx.Services.AddSingleton<IClipboardService>(clipboard);
        return ctx;
    }

    [Fact]
    public void Renders_the_canonical_base_url_model_and_environment_block()
    {
        using var ctx = NewContext(out _);

        var cut = ctx.Render<ClientDropInPanel>();

        cut.Find("[data-testid='client-drop-in-base-url']").GetAttribute("value")
            .Should().Be(OpenAiCompatibleDropIn.BaseUrl);
        cut.Find("[data-testid='client-drop-in-model']").GetAttribute("value")
            .Should().Be(OpenAiCompatibleDropIn.Model);

        var env = cut.Find("[data-testid='client-drop-in-env']");
        env.TagName.Should().Be("TEXTAREA");
        env.TextContent.Should().Be(OpenAiCompatibleDropIn.BuildEnvironmentExports());
        cut.Markup.Should().Contain("Point a client");
        cut.Markup.Should().Contain($"\"model\": \"{OpenAiCompatibleDropIn.Model}\"");
    }

    [Fact]
    public void Copy_base_url_writes_the_canonical_url_and_confirms()
    {
        using var ctx = NewContext(out var clipboard);

        var cut = ctx.Render<ClientDropInPanel>();
        cut.Find("[data-testid='client-drop-in-copy-base-url']").Click();

        clipboard.LastCopiedText.Should().Be(OpenAiCompatibleDropIn.BaseUrl);
        cut.Find("[data-testid='client-drop-in-copy-base-url']").TextContent.Trim().Should().Be("Copied");
        cut.Find("[data-testid='client-drop-in-copy-model']").TextContent.Trim().Should().Be("Copy");
    }

    [Fact]
    public void Copy_model_writes_auto()
    {
        using var ctx = NewContext(out var clipboard);

        var cut = ctx.Render<ClientDropInPanel>();
        cut.Find("[data-testid='client-drop-in-copy-model']").Click();

        clipboard.LastCopiedText.Should().Be(OpenAiCompatibleDropIn.Model);
        cut.Find("[data-testid='client-drop-in-copy-model']").TextContent.Trim().Should().Be("Copied");
    }

    [Fact]
    public void Copy_env_writes_the_two_line_export_block()
    {
        using var ctx = NewContext(out var clipboard);

        var cut = ctx.Render<ClientDropInPanel>();
        cut.Find("[data-testid='client-drop-in-copy-env']").Click();

        clipboard.LastCopiedText.Should().Be(OpenAiCompatibleDropIn.BuildEnvironmentExports());
        cut.Find("[data-testid='client-drop-in-copy-env']").TextContent.Trim().Should().Be("Copied env");
    }

    [Fact]
    public void Each_panel_instance_gets_its_own_element_ids_so_two_can_coexist()
    {
        // The settings overlay opens on top of the Sessions empty state, so both panels are in the DOM
        // together. Shared ids would point every label's for= at the first panel's control.
        using var ctx = NewContext(out _);

        var first = ctx.Render<ClientDropInPanel>();
        var second = ctx.Render<ClientDropInPanel>();

        foreach (var testId in new[] { "base-url", "model", "env" })
        {
            var firstId = first.Find($"[data-testid='client-drop-in-{testId}']").Id;
            var secondId = second.Find($"[data-testid='client-drop-in-{testId}']").Id;

            firstId.Should().NotBeNullOrEmpty();
            firstId.Should().NotBe(secondId);
        }
    }

    [Fact]
    public void Every_label_points_at_a_control_inside_its_own_panel()
    {
        using var ctx = NewContext(out _);

        var cut = ctx.Render<ClientDropInPanel>();

        var labels = cut.FindAll("label");
        labels.Should().HaveCount(3);

        foreach (var label in labels)
        {
            var target = label.GetAttribute("for");
            target.Should().NotBeNullOrEmpty();
            cut.FindAll($"#{target}").Should().ContainSingle();
        }
    }
}
