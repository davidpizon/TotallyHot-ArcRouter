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
        new RoutingCandidate("model-a", "openai", IsFree: false),
        new RoutingCandidate("model-b", "anthropic", IsFree: false),
    ];

    [Fact]
    public async Task VoteAsync_DirectJson_PicksParsedModel()
    {
        var voter = CreateVoter("""{"model": "model-a", "reasoning": "cheapest that clears the bar"}""");
        var context = NewContext();

        var vote = await voter.VoteAsync(context, TestContext.Current.CancellationToken);

        Assert.False(vote.IsAbstain);
        Assert.Equal("model-a", vote.ModelName);
        Assert.Equal("llm_router", voter.Name);
    }

    [Fact]
    public async Task VoteAsync_FencedJsonCodeBlock_FallsBackToRegexExtraction()
    {
        var voter = CreateVoter("Here is my pick:\n```json\n{\"model\": \"model-b\", \"reasoning\": \"needs more care\"}\n```\n");
        var context = NewContext();

        var vote = await voter.VoteAsync(context, TestContext.Current.CancellationToken);

        Assert.False(vote.IsAbstain);
        Assert.Equal("model-b", vote.ModelName);
    }

    [Fact]
    public async Task VoteAsync_UnparseableTextNamingACandidate_FallsBackToNameMatching()
    {
        var voter = CreateVoter("I think model-a is the best fit here, no JSON today.");
        var context = NewContext();

        var vote = await voter.VoteAsync(context, TestContext.Current.CancellationToken);

        Assert.False(vote.IsAbstain);
        Assert.Equal("model-a", vote.ModelName);
    }

    [Fact]
    public async Task VoteAsync_ResponseNamesNoCandidate_Abstains()
    {
        var voter = CreateVoter("I would pick gpt-5.4, which is not in the candidate list.");
        var context = NewContext();

        var vote = await voter.VoteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(vote.IsAbstain);
    }

    [Fact]
    public async Task VoteAsync_JsonNamesModelNotAmongCandidates_FallsThroughToAbstain()
    {
        var voter = CreateVoter("""{"model": "not-a-real-candidate", "reasoning": "oops"}""");
        var context = NewContext();

        var vote = await voter.VoteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(vote.IsAbstain);
    }

    [Fact]
    public async Task VoteAsync_NoTaskText_Abstains()
    {
        var generationClient = new FakeTextGenerationClient("""{"model": "model-a"}""");
        var voter = new LlmRouterVoter(generationClient, NullLogger<LlmRouterVoter>.Instance);
        var context = new VotingContext("live:code_generation", Candidates, TaskText: null);

        var vote = await voter.VoteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(vote.IsAbstain);
        Assert.False(generationClient.WasCalled);
    }

    [Fact]
    public async Task VoteAsync_BlankTaskText_Abstains()
    {
        var generationClient = new FakeTextGenerationClient("""{"model": "model-a"}""");
        var voter = new LlmRouterVoter(generationClient, NullLogger<LlmRouterVoter>.Instance);
        var context = new VotingContext("live:code_generation", Candidates, TaskText: "   ");

        var vote = await voter.VoteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(vote.IsAbstain);
        Assert.False(generationClient.WasCalled);
    }

    [Fact]
    public async Task VoteAsync_GenerationClientThrows_Abstains()
    {
        var generationClient = new FakeTextGenerationClient(exception: new InvalidOperationException("model not loaded"));
        var voter = new LlmRouterVoter(generationClient, NullLogger<LlmRouterVoter>.Instance);
        var context = NewContext();

        var vote = await voter.VoteAsync(context, TestContext.Current.CancellationToken);

        Assert.True(vote.IsAbstain);
    }

    [Fact]
    public async Task VoteAsync_Cancelled_Throws()
    {
        var voter = CreateVoter("""{"model": "model-a"}""");
        var context = NewContext();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => voter.VoteAsync(context, cts.Token));
    }

    private static VotingContext NewContext() =>
        new("live:code_generation", Candidates, TaskText: "Fix the null reference exception in Foo.cs.");

    private static LlmRouterVoter CreateVoter(string generatedResponse) =>
        new(new FakeTextGenerationClient(generatedResponse), NullLogger<LlmRouterVoter>.Instance);

    private sealed class FakeTextGenerationClient : ITextGenerationClient
    {
        private readonly string? _response;
        private readonly Exception? _exception;

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
