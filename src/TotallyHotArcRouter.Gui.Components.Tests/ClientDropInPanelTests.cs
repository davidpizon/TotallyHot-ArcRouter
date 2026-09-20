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
        cut.Find("[data-testid='client-drop-in-env']").TextContent
            .Should().Be(OpenAiCompatibleDropIn.BuildEnvironmentExports());
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
}
