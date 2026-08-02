using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;
using Microsoft.Extensions.Logging;
using Moq;

namespace TotallyHot.ArcRouter.Tests.Proxy.Translation.ToolCalling;

/// <summary>
/// Replays every recorded stream in <see cref="RecordedStreamTranscripts"/> through the shipped stream
/// translator, one delta per <c>Push</c>, exactly as the deltas arrived.
///
/// <para>
/// The buffered replay in <see cref="ToolCallEmulationReplayTests"/> cannot stand in for this. That path
/// hands the scanner one complete string; this one hands it forty-three fragments and requires the same
/// answer, and the difference between those is a state machine holding partial delimiters across calls.
/// A model that emits <c>"```"</c>, <c>"json"</c>, <c>"\n"</c>, <c>"{\""</c> as four separate deltas is
/// not an edge case here - it is every single recording.
/// </para>
///
/// <para>
/// <b>These all assert that nothing is extracted</b>, which is a weaker-sounding claim than it is. The
/// recorded model frames its calls in shapes no dialect registers, so the correct behavior is to find
/// nothing and forward the text untouched - and "forward the text untouched" is the assertion that carries
/// the weight. A scanner that opens a region on a near-miss and never closes it would swallow the rest of
/// the response, turning a model that answers unhelpfully into one that answers not at all.
/// </para>
/// </summary>
public class ToolCallEmulationStreamReplayTests
{
    public static TheoryData<int> Cases()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < RecordedStreamTranscripts.All.Count; i++)
        {
            data.Add(i);
        }

        return data;
    }

    // The envelope fields LM Studio actually sent, copied from the recorded capture rather than reduced to
    // the ones the assertions read. ToolCallNormalizingStreamTranslator snapshots `id`, `created` and
    // `model` off the last parsed chunk so a call it synthesizes in Flush carries them, and a fixture that
    // omitted them would leave that path fed with nulls - testing a stream no server sends. Same reasoning
    // as storing whole response envelopes rather than assistant text.
    private const string ChunkId = "chatcmpl-sk3tcyjbacn9yw6gxp6q";
    private const long ChunkCreated = 1785505547;
    private const string ChunkModel = "deepseek-r1-distill-qwen-7b";

    /// <summary>Wraps one recorded content delta in the SSE event LM Studio sent it in.</summary>
    private static byte[] Chunk(string content)
    {
        var chunk = new JsonObject
        {
            ["id"] = ChunkId,
            ["object"] = "chat.completion.chunk",
            ["created"] = ChunkCreated,
            ["model"] = ChunkModel,
            ["system_fingerprint"] = ChunkModel,
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = new JsonObject { ["content"] = content },
                    ["logprobs"] = null,
                    ["finish_reason"] = null,
                },
            },
        };

        return Encoding.UTF8.GetBytes($"data: {chunk.ToJsonString()}\n\n");
    }

    /// <summary>The terminating pair LM Studio sends: an empty delta carrying finish_reason, then [DONE].</summary>
    private static byte[] StopChunk()
    {
        // Carries the same id/created/model as the content chunks, because the real one does. A stop chunk
        // stripped of them would blank the translator's snapshot at the last moment before Flush - the
        // exact point where a synthesized tool-call chunk needs it.
        var chunk = new JsonObject
        {
            ["id"] = ChunkId,
            ["object"] = "chat.completion.chunk",
            ["created"] = ChunkCreated,
            ["model"] = ChunkModel,
            ["system_fingerprint"] = ChunkModel,
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = new JsonObject(),
                    ["logprobs"] = null,
                    ["finish_reason"] = "stop",
                },
            },
        };

        return Encoding.UTF8.GetBytes($"data: {chunk.ToJsonString()}\n\ndata: [DONE]\n\n");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ARecordedStream_ExtractsNothingAndForwardsEveryCharacter(int index)
    {
        var transcript = RecordedStreamTranscripts.All[index];

        // Emulated requests arm only the taught dialect, matching what the factory builds for an emulated
        // model. Native requests arm the union, which is what an unknown model's first tools-carrying
        // request gets - so each recording is judged under the plan its own request would have produced.
        var plan = transcript.Emulated
            ? new ToolCallNormalizationPlan(
                "replay", transcript.ModelName, [ToolCallDialectRegistry.Emulated], IsObserving: false, IsEmulating: true)
            : new ToolCallNormalizationPlan(
                "replay", transcript.ModelName, ToolCallDialectRegistry.ScannableDialects, IsObserving: false);

        var translator = new ToolCallNormalizingStreamTranslator(plan, capabilityStore: null, Mock.Of<ILogger>());

        var output = new StringBuilder();
        foreach (var delta in transcript.ContentDeltas)
        {
            output.Append(Encoding.UTF8.GetString(translator.Push(Chunk(delta))));
        }

        output.Append(Encoding.UTF8.GetString(translator.Push(StopChunk())));
        output.Append(Encoding.UTF8.GetString(translator.Flush()));

        var (calls, content, envelopes) = ReadStream(output.ToString());

        Assert.Empty(calls);

        // The load-bearing half. Every character the model produced must still reach the client, in order,
        // with nothing held back in a region that never closed.
        Assert.Equal(string.Concat(transcript.ContentDeltas), content);

        // And the envelope the client keys off must survive the rewrite. A translator that rebuilds chunks
        // has to carry id/created/model through unchanged - a client correlating a streamed response by id,
        // or reading `model` to learn what actually served it, breaks if a rewritten chunk drops them.
        Assert.NotEmpty(envelopes);
        Assert.All(envelopes, e => Assert.Equal((ChunkId, ChunkCreated, ChunkModel), e));
    }

    [Fact]
    public void NoRecordedStream_CarriedAToolCallsDeltaAtAll()
    {
        // The asymmetry worth knowing: LM Studio attaches "tool_calls": [] to every *buffered* message and
        // omits the field entirely when streaming. ToolCallNormalizingStreamTranslator states this in a
        // comment as the reason its own empty-array check is count-guarded rather than null-guarded; these
        // ten recordings are the measurement behind that comment.
        Assert.All(RecordedStreamTranscripts.All, t => Assert.Equal(0, t.ToolCallDeltaCount));
    }

    [Fact]
    public void TheSameRequestStreamedAndBuffered_DidNotProduceTheSameReply()
    {
        // Recorded because it constrains how these transcripts may be used, not because it is a defect.
        // Both requests set temperature 0 and differ only in `stream`, yet the buffered reply frames its
        // call in an invented <function-call> element while the streamed one uses a ```json fence.
        //
        // The consequence is that a streamed transcript cannot be derived by re-chunking a buffered one,
        // which is exactly the shortcut that would otherwise be tempting given the size of the raw capture.
        // Each path has to be recorded from its own request.
        var buffered = JsonDocument.Parse(RecordedModelTranscripts.DeepSeekR1DistillQwen7B.ResponseByScenario["single"])
            .RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();

        var streamed = string.Concat(
            RecordedStreamTranscripts.All.Single(t => t.Emulated && t.ScenarioName == "single").ContentDeltas);

        Assert.NotEqual(buffered, streamed);
        Assert.Contains("<function-call>", buffered, StringComparison.Ordinal);
        Assert.DoesNotContain("<function-call>", streamed, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryScenario_WasRecordedOnBothPaths()
    {
        // Guards the same gap the buffered suite guards: a scenario recorded only natively would leave the
        // emulated stream untested while the list still looks complete.
        foreach (var emulated in new[] { true, false })
        {
            Assert.Equal(
                ToolCallEmulationScenarios.All.Select(s => s.Name).Order(),
                RecordedStreamTranscripts.All.Where(t => t.Emulated == emulated).Select(t => t.ScenarioName).Order());
        }
    }

    /// <summary>Reads a translated SSE body back into the calls, the text, and each chunk's envelope.</summary>
    private static (List<string> Calls, string Content, List<(string?, long?, string?)> Envelopes) ReadStream(string sse)
    {
        var calls = new List<string>();
        var content = new StringBuilder();
        var envelopes = new List<(string?, long?, string?)>();

        foreach (var line in sse.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var payload = line["data: ".Length..].Trim();
            if (payload == "[DONE]")
            {
                continue;
            }

            var root = JsonDocument.Parse(payload).RootElement;

            envelopes.Add((
                root.TryGetProperty("id", out var id) ? id.GetString() : null,
                root.TryGetProperty("created", out var created) ? created.GetInt64() : null,
                root.TryGetProperty("model", out var model) ? model.GetString() : null));

            var delta = root.GetProperty("choices")[0].GetProperty("delta");

            if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            {
                content.Append(c.GetString());
            }

            if (delta.TryGetProperty("tool_calls", out var t) && t.ValueKind == JsonValueKind.Array)
            {
                calls.AddRange(t.EnumerateArray().Select(x => x.GetProperty("function").GetProperty("name").GetString()!));
            }
        }

        return (calls, content.ToString(), envelopes);
    }
}

