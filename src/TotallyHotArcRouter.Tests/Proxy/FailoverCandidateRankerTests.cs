using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Router;

namespace TotallyHot.ArcRouter.Tests.Proxy;

/// <summary>
/// Covers <see cref="FailoverCandidateRanker"/> in isolation: policy scores must beat
/// <see cref="RouterMemory"/> so a same-request failover retries the next voter pick, and the
/// memory-only path must stay bit-for-bit with the historical ranking when no vote happened.
/// </summary>
public class FailoverCandidateRankerTests
{
    private static readonly string LiveDimension =
        RouterDimension.ToLiveKey(liveMemoryPrefix: new QualityOptions().LiveMemoryPrefix,
            dimension: RouterDimension.CodeGeneration);

    [Fact]
    public async Task Rank_PolicyScoresPresent_WalksVoterPicksAheadOfHigherMemoryScore()
    {
        var memory = new RouterMemory();
        await memory.AddScoreAsync(dimension: LiveDimension, model: "decoy", 0.99);
        await memory.AddScoreAsync(dimension: LiveDimension, model: "runner-up", 0.1);
        await memory.AddScoreAsync(dimension: LiveDimension, model: "winner", 0.2);

        var ranked = FailoverCandidateRanker.Rank(
            eligible:
            [
                ("winner", Route("winner")),
                ("runner-up", Route("runner-up")),
                ("decoy", Route("decoy"))
            ],
            policyScores: new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["winner"] = 3,
                ["runner-up"] = 2,
                ["decoy"] = 1
            },
            routerMemory: memory,
            liveDimension: LiveDimension);

        Assert.Equal(["winner", "runner-up", "decoy"], actual: ranked.Select(r => r.ModelName));
    }

    [Fact]
    public async Task Rank_UnscoredModel_FollowsScoredVoterPicks_ThenMemory()
    {
        var memory = new RouterMemory();
        await memory.AddScoreAsync(dimension: LiveDimension, model: "unvoted", 0.99);
        await memory.AddScoreAsync(dimension: LiveDimension, model: "runner-up", 0.1);

        var ranked = FailoverCandidateRanker.Rank(
            eligible:
            [
                ("unvoted", Route("unvoted")),
                ("runner-up", Route("runner-up")),
                ("winner", Route("winner"))
            ],
            policyScores: new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["winner"] = 3,
                ["runner-up"] = 2
            },
            routerMemory: memory,
            liveDimension: LiveDimension);

        Assert.Equal(["winner", "runner-up", "unvoted"], actual: ranked.Select(r => r.ModelName));
    }

    [Fact]
    public async Task Rank_NoPolicyScores_KeepsMemoryRankingIncludingColdStart()
    {
        var memory = new RouterMemory();
        // Below cold-start 0.5, so the unscored model must outrank it - historical RankEligibleModels.
        await memory.AddScoreAsync(dimension: LiveDimension, model: "low-scored", 0.1);

        var ranked = FailoverCandidateRanker.Rank(
            eligible:
            [
                ("low-scored", Route("low-scored")),
                ("unscored", Route("unscored"))
            ],
            null,
            routerMemory: memory,
            liveDimension: LiveDimension);

        Assert.Equal(["unscored", "low-scored"], actual: ranked.Select(r => r.ModelName));
    }

    [Fact]
    public void Rank_VoterBreakdownKeysAreIgnored_LookupIsByModelName()
    {
        var ranked = FailoverCandidateRanker.Rank(
            eligible:
            [
                ("winner", Route("winner")),
                ("runner-up", Route("runner-up"))
            ],
            policyScores: new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["winner"] = 1.2,
                ["runner-up"] = 0.8,
                ["voter:dim_best:winner"] = 99
            },
            null,
            liveDimension: LiveDimension);

        Assert.Equal(["winner", "runner-up"], actual: ranked.Select(r => r.ModelName));
    }

    private static ResolvedModelRoute Route(string modelName)
    {
        return new ResolvedModelRoute(
            ModelName: modelName,
            Provider: "prov-" + modelName,
            ProviderModelId: modelName + "-upstream",
            UpstreamBaseUrl: new Uri($"https://{modelName}.test"),
            ExtraHeaders: []);
    }
}
