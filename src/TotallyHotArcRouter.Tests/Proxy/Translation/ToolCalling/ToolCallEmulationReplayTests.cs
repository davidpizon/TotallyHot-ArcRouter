using Microsoft.Extensions.Logging;
using Moq;
using System.Text;
using System.Text.Json;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

namespace TotallyHot.ArcRouter.Tests.Proxy.Translation.ToolCalling;

/// <summary>
/// Runs every recorded model in <see cref="RecordedModelTranscripts"/> through the whole emulation path -
/// the real rewriter building the request, the real translator reading the reply - and asserts each
/// scenario normalizes to the calls it should.
/// <para>
/// <b>No server, no network, no clock.</b> This replaces an earlier probe that talked to a live LM Studio:
/// a test that needs a particular model loaded is a test that reports the operator's environment rather
/// than the code, and it cannot run in CI at all. Replaying recorded replies keeps every bit of the signal
/// that mattered - the shipped prompt is what produced them, and the shipped scanner is what reads them -
/// while making the result depend only on the repository.
/// </para>
/// <para>
/// What this can and cannot tell you is worth being precise about. It proves the code still handles what
/// these models actually said, so a change to the prompt, the delimiters, the schema format, or the
/// scanner that would have broken them breaks here first. It cannot prove a *new* prompt works, because
/// the recorded replies were produced by the current one - that question needs a live model and a fresh
/// recording, which is what <see cref="RecordedModelTranscripts"/> documents how to do.
/// </para>
/// </summary>
public class ToolCallEmulationReplayTests
{
    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var transcript in RecordedModelTranscripts.All)
            foreach (var scenario in ToolCallEmulationScenarios.All)
                data.Add(p1: transcript.ModelName, p2: scenario.Name);

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ARecordedModel_NormalizesEveryScenario(string modelName, string scenarioName)
    {
        var transcript = RecordedModelTranscripts.All.Single(t => t.ModelName == modelName);
        var scenario = ToolCallEmulationScenarios.All.Single(s => s.Name == scenarioName);

        // 1. The request half: the shipped rewriter turns the tools into instructions.
        var requestBody =
            $$"""
              {"model":{{JsonSerializer.Serialize(modelName)}},"temperature":0,
               "messages":[{"role":"user","content":{{JsonSerializer.Serialize(scenario.Question)}}}],
               "tools":[{{string.Join(separator: ",", values: scenario.Tools)}}]}
              """;

        var emulated = ToolCallEmulationRewriter.Rewrite(
            openAiShapedBody: Encoding.UTF8.GetBytes(requestBody), dialect: ToolCallDialectRegistry.Emulated,
            logger: Mock.Of<ILogger>());

        var emulatedText = Encoding.UTF8.GetString(emulated);
        Assert.DoesNotContain(expectedSubstring: "\"tools\":", actualString: emulatedText,
            comparisonType: StringComparison.Ordinal);

        // Every tool offered must reach the model by name, or the recorded reply is being judged against a
        // prompt that no longer describes the same toolset.
        foreach (var tool in scenario.Tools)
        {
            var name = JsonDocument.Parse(tool).RootElement.GetProperty("function").GetProperty("name").GetString()!;
            Assert.Contains(expectedSubstring: name, actualString: emulatedText,
                comparisonType: StringComparison.Ordinal);
        }

        // 2. The response half: the shipped translator reads what the model actually said.
        var recorded = Assert.Contains(expected: scenarioName,
            collection: (IDictionary<string, string>)transcript.ResponseByScenario);

        var plan = new ToolCallNormalizationPlan(
            ProviderKey: "replay", ModelName: modelName, Candidates: [ToolCallDialectRegistry.Emulated], false, true);
        var normalized = new ToolCallNormalizingTranslator(plan: plan, null, logger: Mock.Of<ILogger>())
            .TranslateResponse(Encoding.UTF8.GetBytes(recorded));

        using var parsed = JsonDocument.Parse(normalized);
        var message = parsed.RootElement.GetProperty("choices")[0].GetProperty("message");

        var actual = message.TryGetProperty(propertyName: "tool_calls", value: out var calls) &&
                     calls.ValueKind == JsonValueKind.Array
            ? calls.EnumerateArray().Select(c => c.GetProperty("function").GetProperty("name").GetString()!).ToArray()
            : [];

        var said = message.TryGetProperty(propertyName: "content", value: out var c) &&
                   c.ValueKind == JsonValueKind.String
            ? c.GetString()
            : "(null)";

        if (transcript.EmulationFailures?.TryGetValue(key: scenarioName, value: out var recordedFailure) == true)
        {
            // A model recorded as failing this scenario must still fail it in exactly the recorded way.
            // Both halves are load-bearing. Asserting the observed result pins the behavior, so a change
            // that alters *how* it fails - yielding a wrong call where it used to yield none - surfaces
            // here rather than hiding under a blanket "expected to fail".
            Assert.True(
                condition: recordedFailure.SequenceEqual(actual),
                userMessage: $"{modelName} / {scenarioName} is recorded as failing emulation by yielding "
                             + $"[{string.Join(separator: ", ", values: recordedFailure)}], but it now yields [{string.Join(separator: ", ", value: actual)}]. "
                             + $"Re-record the transcript.{Environment.NewLine}Model said: {said}");

            // And asserting it still differs from the expectation is what stops the record going stale:
            // a prompt change that finally makes this model work breaks this line, forcing the entry to be
            // deleted rather than left behind claiming a failure that no longer happens.
            Assert.False(
                condition: scenario.ExpectedCalls.SequenceEqual(actual),
                userMessage: $"{modelName} / {scenarioName} is recorded as failing emulation, but it now produces the "
                             + "expected calls. Emulation improved - delete this entry from EmulationFailures.");

            return;
        }

        Assert.True(
            condition: scenario.ExpectedCalls.SequenceEqual(actual),
            userMessage:
            $"{modelName} / {scenarioName}: expected [{string.Join(separator: ", ", values: scenario.ExpectedCalls)}], "
            + $"got [{string.Join(separator: ", ", value: actual)}].{Environment.NewLine}Model said: {said}");
    }

    [Fact]
    public void EveryRecordedFailure_NamesAScenarioThatExists()
    {
        // A typo in a failure key would otherwise read as "this model passes that scenario" - the entry
        // would simply never be looked up, and the theory above would assert the passing branch against a
        // model known not to pass it.
        var known = ToolCallEmulationScenarios.All.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var transcript in RecordedModelTranscripts.All)
            foreach (var name in transcript.EmulationFailures?.Keys ?? [])
                Assert.Contains(expected: name, set: known);
    }

    [Fact]
    public void EveryRecordedModel_CoversEveryScenario()
    {
        // A transcript missing a scenario would otherwise silently narrow what a model was judged on -
        // the omitted case reads as "not tested" rather than "not recorded", and a model that fails the
        // false-positive scenario is exactly the one whose transcript is tempting to leave incomplete.
        foreach (var transcript in RecordedModelTranscripts.All)
            Assert.Equal(
                expected: ToolCallEmulationScenarios.All.Select(s => s.Name).Order(),
                actual: transcript.ResponseByScenario.Keys.Order());
    }

    [Fact]
    public void TheRecordedRepliesCarryLmStudiosEmptyToolCallsArray()
    {
        // Not incidental. The recordings are whole envelopes precisely so this stays in them: LM Studio
        // sends "tool_calls": [] on every buffered response, prose included, and reading that as a native
        // call recorded openai-native at Observed confidence - permanently disabling normalization for the
        // model. If a future refactor reintroduces that, these replays fail rather than passing quietly.
        //
        // Asserted across every recorded model, not just the one from the incident. Two model families
        // recorded a year apart both carry it, which is what makes it a property of the server rather than
        // a quirk of one model's template - and therefore something no future model's transcript may
        // silently lack.
        foreach (var transcript in RecordedModelTranscripts.All)
            Assert.All(collection: transcript.ResponseByScenario.Values, action: body =>
            {
                var toolCalls = JsonDocument.Parse(body).RootElement.GetProperty("choices")[0]
                    .GetProperty("message").GetProperty("tool_calls");

                Assert.Equal(expected: JsonValueKind.Array, actual: toolCalls.ValueKind);
                Assert.Empty(toolCalls.EnumerateArray());
            });
    }
}