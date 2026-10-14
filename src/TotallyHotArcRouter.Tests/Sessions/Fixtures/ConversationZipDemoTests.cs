using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;
using TotallyHot.ArcRouter.Telemetry;
using TotallyHot.ArcRouter.Tests.Proxy.Translation.ToolCalling;

namespace TotallyHot.ArcRouter.Tests.Sessions.Fixtures;

/// <summary>
/// Demonstrates the parent-plan phase 5 hand-off: a schema-v1 zip is opened by
/// <see cref="ConversationZipReader"/> and its client response bytes are fed into existing
/// parsers/translators. Each test builds a synthetic zip — no real conversation archive is checked in.
/// </summary>
public sealed class ConversationZipDemoTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "thar-demo-" + Guid.NewGuid().ToString("N"));

    /// <summary>Initializes a new instance of the <see cref="ConversationZipDemoTests"/> class.</summary>
    public ConversationZipDemoTests() => Directory.CreateDirectory(_work);

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of temp fixtures.
        }
    }

    /// <summary>Reader → <see cref="ResponseTextExtractor"/> recovers the planted reply text.</summary>
    [Fact]
    public void Reader_FeedsResponseTextExtractor()
    {
        const string expected = "phase-5 reply text";
        var response =
            """{"id":"chatcmpl-demo","choices":[{"message":{"role":"assistant","content":"phase-5 reply text"}}]}"""u8.ToArray();
        var zip = SyntheticConversationZip.Write(response, directory: Path.Combine(_work, "text"));

        var turn = Assert.Single(ConversationZipReader.ReadAll(zip));
        Assert.NotNull(turn.ClientResponse);

        var extractor = new ResponseTextExtractor();
        var ok = extractor.TryExtractText(
            provider: "openai", isStreaming: false, bufferedResponseBody: turn.ClientResponse, text: out var text,
            hasToolCalls: out _);

        Assert.True(ok);
        Assert.Equal(expected, text);
    }

    /// <summary>Reader → <see cref="UsageExtractor"/> recovers the planted token counts.</summary>
    [Fact]
    public void Reader_FeedsUsageExtractor()
    {
        var response =
            """{"usage":{"prompt_tokens":11,"completion_tokens":7},"choices":[{"message":{"content":"x"}}]}"""u8.ToArray();
        var zip = SyntheticConversationZip.Write(response, directory: Path.Combine(_work, "usage"));

        var turn = Assert.Single(ConversationZipReader.ReadAll(zip));
        Assert.NotNull(turn.ClientResponse);

        var extractor = new UsageExtractor();
        var ok = extractor.TryExtractUsage(
            provider: "openai", isStreaming: false, bufferedResponseBody: turn.ClientResponse, usage: out var usage);

        Assert.True(ok);
        Assert.Equal(11, usage.PromptTokens);
        Assert.Equal(7, usage.CompletionTokens);
    }

    /// <summary>
    /// Reader → <see cref="ToolCallNormalizingTranslator"/> normalizes one recorded Qwen envelope the same
    /// way <see cref="ToolCallEmulationReplayTests"/> does for the <c>single</c> scenario.
    /// </summary>
    [Fact]
    public void Reader_FeedsToolCallTranslation_FromRecordedModelTranscript()
    {
        var transcript = RecordedModelTranscripts.All.Single(t => t.ModelName == "qwen2.5.1-coder-7b-instruct");
        var scenario = ToolCallEmulationScenarios.All.Single(s => s.Name == "single");
        var recorded = Assert.Contains(expected: "single", collection: (IDictionary<string, string>)transcript.ResponseByScenario);

        var zip = SyntheticConversationZip.Write(
            clientResponse: Encoding.UTF8.GetBytes(recorded),
            directory: Path.Combine(_work, "tools"));

        var turn = Assert.Single(ConversationZipReader.ReadAll(zip));
        Assert.NotNull(turn.ClientResponse);

        var plan = new ToolCallNormalizationPlan(
            ProviderKey: "replay",
            ModelName: transcript.ModelName,
            Candidates: [ToolCallDialectRegistry.Emulated],
            false,
            true);
        var normalized = new ToolCallNormalizingTranslator(plan: plan, null, logger: Mock.Of<ILogger>())
            .TranslateResponse(turn.ClientResponse);

        using var parsed = JsonDocument.Parse(normalized);
        var message = parsed.RootElement.GetProperty("choices")[0].GetProperty("message");
        var actual = message.TryGetProperty(propertyName: "tool_calls", value: out var calls) &&
                     calls.ValueKind == JsonValueKind.Array
            ? calls.EnumerateArray().Select(call => call.GetProperty("function").GetProperty("name").GetString()!).ToArray()
            : [];

        Assert.Equal(scenario.ExpectedCalls, actual);
    }
}
