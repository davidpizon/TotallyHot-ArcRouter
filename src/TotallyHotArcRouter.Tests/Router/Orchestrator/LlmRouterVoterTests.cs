using Microsoft.Extensions.Logging.Abstractions;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Orchestrator;
using TotallyHot.ArcRouter.Router.TextGeneration;

namespace TotallyHot.ArcRouter.Tests.Router.Orchestrator;

/// <summary>
/// Covers <see cref="LlmRouterVoter"/>'s prompt-response parsing chain and abstention conditions -
/// PLAN.md Phase L's llm_router voter - against a <see cref="FakeTextGenerationClient"/> so no ONNX
/// model is needed to exercise the voter's own logic.
/// </summary>
public class LlmRouterVoterTests
{
    private static readonly RoutingCandidate[] Candidates =
    [
        new(ModelName: "model-a", Provider: "openai", false),
        new(ModelName: "model-b", Provider: "anthropic", false)
    ];

    [Fact]
    public async Task VoteAsync_DirectJson_PicksParsedModel()
    {
        var voter = CreateVoter("""{"model": "model-a", "reasoning": "cheapest that clears the bar"}""");
        var context = NewContext();

        var vote = await voter.VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(vote.IsAbstain);
        Assert.Equal(expected: "model-a", actual: vote.ModelName);
        Assert.Equal(expected: "llm_router", actual: voter.Name);
    }

    [Fact]
    public async Task VoteAsync_FencedJsonCodeBlock_FallsBackToRegexExtraction()
    {
        var voter = CreateVoter(
            "Here is my pick:\n```json\n{\"model\": \"model-b\", \"reasoning\": \"needs more care\"}\n```\n");
        var context = NewContext();

        var vote = await voter.VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(vote.IsAbstain);
        Assert.Equal(expected: "model-b", actual: vote.ModelName);
    }

    [Fact]
    public async Task VoteAsync_UnparseableTextNamingACandidate_FallsBackToNameMatching()
    {
        var voter = CreateVoter("I think model-a is the best fit here, no JSON today.");
        var context = NewContext();

        var vote = await voter.VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(vote.IsAbstain);
        Assert.Equal(expected: "model-a", actual: vote.ModelName);
    }

    [Fact]
    public async Task VoteAsync_TextMentionsMultipleCandidates_PicksLastMentioned()
    {
        var voter = CreateVoter(
            "Between model-a and model-b, I'll restate: model-a first, but model-b is the better fit.");
        var context = NewContext();

        var vote = await voter.VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(vote.IsAbstain);
        Assert.Equal(expected: "model-b", actual: vote.ModelName);
    }

    [Fact]
    public async Task VoteAsync_NameMatchWithPrefixOverlappingCandidateNames_PicksLongerMatch()
    {
        RoutingCandidate[] candidates =
        [
            new(ModelName: "llama3", Provider: "bedrock", false),
            new(ModelName: "llama3-70b-bedrock", Provider: "bedrock", false)
        ];
        var voter = CreateVoter("I'll go with llama3-70b-bedrock for this task, no JSON today.");
        var context = new VotingContext(Dimension: "live:code_generation", Candidates: candidates,
            TaskText: "Fix the bug.");

        var vote = await voter.VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(vote.IsAbstain);
        Assert.Equal(expected: "llama3-70b-bedrock", actual: vote.ModelName);
    }

    [Fact]
    public async Task VoteAsync_FencedJsonWithNestedBracesInString_ParsesFullBody()
    {
        var voter = CreateVoter(
            "Here is my pick:\n```json\n{\"model\": \"model-b\", \"reasoning\": \"handles {edge} cases well\"}\n```\n");
        var context = NewContext();

        var vote = await voter.VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(vote.IsAbstain);
        Assert.Equal(expected: "model-b", actual: vote.ModelName);
    }

    [Fact]
    public async Task VoteAsync_MultipleFencedBlocks_DoesNotSpanBlocksWithGreedyMatch()
    {
        // A greedy body capture unbounded by the closing fence would span from the first block's '{' to
        // the second block's final '}', splicing in the fence markers between them and producing invalid
        // JSON - which would fail this stage entirely and fall back to (lower-confidence) name matching,
        // picking "model-b" as the last-mentioned name instead of parsing the first block's own JSON.
        var voter = CreateVoter(
            "First:\n```json\n{\"model\": \"model-a\", \"reasoning\": \"cheapest\"}\n```\n" +
            "Unrelated second block:\n```json\n{\"model\": \"model-b\"}\n```\n");
        var context = NewContext();

        var vote = await voter.VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(vote.IsAbstain);
        Assert.Equal(expected: "model-a", actual: vote.ModelName);
        Assert.Equal(0.75, actual: vote.Confidence);
    }

    [Fact]
    public async Task VoteAsync_ResponseNamesNoCandidate_Abstains()
    {
        var voter = CreateVoter("I would pick gpt-5.4, which is not in the candidate list.");
        var context = NewContext();

        var vote = await voter.VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(vote.IsAbstain);
    }

    [Fact]
    public async Task VoteAsync_JsonNamesModelNotAmongCandidates_FallsThroughToAbstain()
    {
        var voter = CreateVoter("""{"model": "not-a-real-candidate", "reasoning": "oops"}""");
        var context = NewContext();

        var vote = await voter.VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(vote.IsAbstain);
    }

    [Fact]
    public async Task VoteAsync_NoTaskText_Abstains()
    {
        var generationClient = new FakeTextGenerationClient("""{"model": "model-a"}""");
        var voter = new LlmRouterVoter(generationClient: generationClient, logger: NullLogger<LlmRouterVoter>.Instance);
        var context = new VotingContext(Dimension: "live:code_generation", Candidates: Candidates, TaskText: null);

        var vote = await voter.VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(vote.IsAbstain);
        Assert.False(generationClient.WasCalled);
    }

    [Fact]
    public async Task VoteAsync_BlankTaskText_Abstains()
    {
        var generationClient = new FakeTextGenerationClient("""{"model": "model-a"}""");
        var voter = new LlmRouterVoter(generationClient: generationClient, logger: NullLogger<LlmRouterVoter>.Instance);
        var context = new VotingContext(Dimension: "live:code_generation", Candidates: Candidates, TaskText: "   ");

        var vote = await voter.VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(vote.IsAbstain);
        Assert.False(generationClient.WasCalled);
    }

    [Fact]
    public async Task VoteAsync_GenerationClientThrows_Abstains()
    {
        var generationClient =
            new FakeTextGenerationClient(exception: new InvalidOperationException("model not loaded"));
        var voter = new LlmRouterVoter(generationClient: generationClient, logger: NullLogger<LlmRouterVoter>.Instance);
        var context = NewContext();

        var vote = await voter.VoteAsync(context: context, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(vote.IsAbstain);
    }

    [Fact]
    public async Task VoteAsync_Cancelled_Throws()
    {
        var voter = CreateVoter("""{"model": "model-a"}""");
        var context = NewContext();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            voter.VoteAsync(context: context, cancellationToken: cts.Token));
    }

    private static VotingContext NewContext()
    {
        return new VotingContext(Dimension: "live:code_generation", Candidates: Candidates,
            TaskText: "Fix the null reference exception in Foo.cs.");
    }

    private static LlmRouterVoter CreateVoter(string generatedResponse)
    {
        return new LlmRouterVoter(generationClient: new FakeTextGenerationClient(generatedResponse),
            logger: NullLogger<LlmRouterVoter>.Instance);
    }

    private sealed class FakeTextGenerationClient : ITextGenerationClient
    {
        private readonly Exception? _exception;
        private readonly string? _response;

        public FakeTextGenerationClient(string? response = null, Exception? exception = null)
        {
            _response = response;
            _exception = exception;
        }

        public bool WasCalled { get; private set; }

        public Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            cancellationToken.ThrowIfCancellationRequested();

            return _exception is not null
                ? Task.FromException<string>(_exception)
                : Task.FromResult(_response ?? string.Empty);
        }
    }
}