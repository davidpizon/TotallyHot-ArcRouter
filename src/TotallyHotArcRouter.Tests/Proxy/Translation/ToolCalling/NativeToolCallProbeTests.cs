using System.Text.Json;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

namespace TotallyHot.ArcRouter.Tests.Proxy.Translation.ToolCalling;

/// <summary>
/// What a server does with a <c>tools</c> array it cannot render, measured rather than assumed
/// (<see cref="RecordedNativeToolCallProbes"/>).
/// <para>
/// These assert a <b>negative</b>, which is the awkward kind of test to write and the reason to write it
/// carefully. "No tool call came back" is true of a model that has no tool support, and equally true of a
/// model that has tool support and saw no reason to use it - so asserting only that would pass for the
/// wrong reason and keep passing if the situation reversed. The token-count assertions below are what
/// make the claim specific: the tools were not weighed and declined, they were never in the prompt.
/// </para>
/// </summary>
public class NativeToolCallProbeTests
{
    private static JsonElement Message(string body)
    {
        return JsonDocument.Parse(body).RootElement.GetProperty("choices")[0].GetProperty("message");
    }

    private static int PromptTokens(string body)
    {
        return JsonDocument.Parse(body).RootElement.GetProperty("usage").GetProperty("prompt_tokens").GetInt32();
    }

    public static TheoryData<string> Scenarios()
    {
        var data = new TheoryData<string>();
        foreach (var scenario in ToolCallEmulationScenarios.All) data.Add(scenario.Name);

        return data;
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void SendingToolsNatively_NeverReachedTheModel(string scenarioName)
    {
        // The measurement behind the "silent drop" claim. Emulation renders the same tools into the system
        // prompt and pays 138-245 prompt tokens for them; sending them natively costs 10/13/14/17/16 -
        // token for token what the same five questions cost with no tools key at all. An order of
        // magnitude is not a rendering-efficiency difference; the tools are simply absent.
        //
        // Ratio rather than absolute numbers, so re-recording against a different tokenizer or a reworded
        // scenario does not require re-deriving a threshold. Anything remotely close to 1 fails.
        var native =
            PromptTokens(RecordedNativeToolCallProbes.DeepSeekR1DistillQwen7B.ResponseByScenario[scenarioName]);
        var emulated = PromptTokens(RecordedModelTranscripts.DeepSeekR1DistillQwen7B.ResponseByScenario[scenarioName]);

        Assert.True(
            condition: native * 5 < emulated,
            userMessage:
            $"{scenarioName}: native prompt_tokens {native} vs emulated {emulated}. The native request was "
            + "expected to carry no rendered tools at all; a comparable count means the server started "
            + "rendering them, which changes what these probes prove.");
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void ADroppedToolsArray_StillReturnsAWellFormedSuccessfulReply(string scenarioName)
    {
        // Nothing in the response distinguishes it from a normal answer, which is precisely why this
        // failure mode survived: there is no error to catch, no status code to check, and no field to
        // inspect. A proxy sitting in front of this sees a healthy 200 and forwards it.
        var body = RecordedNativeToolCallProbes.DeepSeekR1DistillQwen7B.ResponseByScenario[scenarioName];
        var root = JsonDocument.Parse(body).RootElement;

        Assert.Equal(expected: "stop", actual: root.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        Assert.False(root.TryGetProperty(propertyName: "error", value: out _));
        Assert.NotEqual(0, actual: Message(body).GetProperty("content").GetString()!.Length);
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void NoNativeReply_CarriesAToolCallInAnyRegisteredDialect(string scenarioName)
    {
        // Scanned against every scannable dialect, not just the emulated one. This is the check that would
        // have caught a real DeepSeek framing had one appeared - if this model emitted the delimiter tokens
        // docs/router/tracked-todos.md #3 is about, the scan would either match an existing dialect or the
        // content would visibly contain them. Neither happens, and the probe above explains why: the model
        // was never told any tool existed.
        var body = RecordedNativeToolCallProbes.DeepSeekR1DistillQwen7B.ResponseByScenario[scenarioName];
        var content = Message(body).GetProperty("content").GetString()!;

        Assert.Null(DialectMatcher.MatchAny(content: content, candidates: ToolCallDialectRegistry.ScannableDialects));
        Assert.Empty(Message(body).GetProperty("tool_calls").EnumerateArray());
    }

    [Fact]
    public void NoNativeReply_ContainsDeepSeeksDelimiterTokens()
    {
        // The specific negative result that keeps `deepseek` out of ToolCallDialectRegistry. DeepSeek's
        // published tool-call template frames calls in tokens built from U+FF5C FULLWIDTH VERTICAL LINE and
        // U+2581 LOWER ONE EIGHTH BLOCK. Searching for the distinctive codepoints rather than a full token
        // spelling is deliberate: the spelling is exactly what is unverified, so asserting a guessed
        // spelling is absent would prove nothing. If a future recording of a tool-capable DeepSeek does
        // contain these characters, this test fails and points at the transcript that finally shows the
        // real framing.
        foreach (var body in RecordedNativeToolCallProbes.DeepSeekR1DistillQwen7B.ResponseByScenario.Values)
        {
            var content = Message(body).GetProperty("content").GetString()!;

            Assert.DoesNotContain('｜', collection: content);
            Assert.DoesNotContain('▁', collection: content);
        }
    }

    [Fact]
    public void EveryNativeProbe_CoversEveryScenario()
    {
        // Same reason the replay suite checks this: an omitted scenario reads as "not tested" rather than
        // "not recorded".
        foreach (var probe in RecordedNativeToolCallProbes.All)
            Assert.Equal(
                expected: ToolCallEmulationScenarios.All.Select(s => s.Name).Order(),
                actual: probe.ResponseByScenario.Keys.Order());
    }
}