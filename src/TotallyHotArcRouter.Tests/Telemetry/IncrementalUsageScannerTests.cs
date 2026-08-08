using System.Text;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers <see cref="IncrementalUsageScanner"/>'s tail-window retention and extraction fallback.</summary>
public class IncrementalUsageScannerTests
{
    private readonly UsageExtractor _extractor = new();

    [Fact]
    public void TryExtractUsage_NothingAppended_ReturnsFalse()
    {
        var scanner = new IncrementalUsageScanner();

        var result = scanner.TryExtractUsage("openai", isStreaming: true, _extractor, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryExtractUsage_SingleChunkWithUsage_ExtractsIt()
    {
        var scanner = new IncrementalUsageScanner();
        var sse = "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5}}\n\ndata: [DONE]\n\n";
        scanner.Append(Encoding.UTF8.GetBytes(sse));

        var result = scanner.TryExtractUsage("openai", isStreaming: true, _extractor, out var usage);

        Assert.True(result);
        Assert.Equal(10, usage.PromptTokens);
        Assert.Equal(5, usage.CompletionTokens);
    }

    [Fact]
    public void Append_ChunkLargerThanTailWindow_DiscardsUsageEventThatFellOutOfTheWindow()
    {
        // The usage event is followed by enough trailing SSE noise to push it out of a small tail window -
        // the whole point of a bounded tail (§5.11: don't grow unbounded for an oversized response).
        var usageEvent = "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5}}\n\n";
        var trailingNoise = "data: {\"choices\":[{\"delta\":{\"content\":\"" + new string('x', 200) + "\"}}]}\n\n";
        var scanner = new IncrementalUsageScanner(maxTailBytes: 32);

        scanner.Append(Encoding.UTF8.GetBytes(usageEvent + trailingNoise));

        var result = scanner.TryExtractUsage("openai", isStreaming: true, _extractor, out _);

        Assert.False(result);
    }

    [Fact]
    public void Append_UsageEventWithinTheTailWindow_ExtractsIt()
    {
        var leadingNoise = "data: {\"choices\":[{\"delta\":{\"content\":\"" + new string('x', 200) + "\"}}]}\n\n";
        var usageEvent = "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5}}\n\n";
        var scanner = new IncrementalUsageScanner(maxTailBytes: 4096);

        scanner.Append(Encoding.UTF8.GetBytes(leadingNoise + usageEvent));

        var result = scanner.TryExtractUsage("openai", isStreaming: true, _extractor, out var usage);

        Assert.True(result);
        Assert.Equal(10, usage.PromptTokens);
        Assert.Equal(5, usage.CompletionTokens);
    }

    [Fact]
    public void Append_MultipleSmallChunks_KeepsOnlyTrailingWindowAcrossCalls()
    {
        // A large leading chunk (bigger than the tail window) followed by a small final chunk carrying
        // usage - mirrors the real failure mode: a huge streamed answer whose trailing usage event would
        // otherwise fall outside a head-capped capture.
        var scanner = new IncrementalUsageScanner(maxTailBytes: 65_536);
        var filler = Encoding.UTF8.GetBytes($"data: {{\"choices\":[{{\"delta\":{{\"content\":\"{new string('a', 200_000)}\"}}}}]}}\n\n");

        const int chunkSize = 8192;
        for (var offset = 0; offset < filler.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, filler.Length - offset);
            scanner.Append(filler.AsSpan(offset, length));
        }

        scanner.Append(Encoding.UTF8.GetBytes("data: {\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5}}\n\ndata: [DONE]\n\n"));

        var result = scanner.TryExtractUsage("openai", isStreaming: true, _extractor, out var usage);

        Assert.True(result);
        Assert.Equal(10, usage.PromptTokens);
        Assert.Equal(5, usage.CompletionTokens);
    }

    [Fact]
    public void Constructor_NonPositiveMaxTailBytes_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new IncrementalUsageScanner(maxTailBytes: 0));
    }
}
