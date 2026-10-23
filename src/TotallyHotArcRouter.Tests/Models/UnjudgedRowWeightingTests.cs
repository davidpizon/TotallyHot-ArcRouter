using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.Tests.Models;

/// <summary>
/// Guards the issue #326 invariant: a judge-scored row always weighs <c>1.0</c>, and an unjudged row
/// never weighs more than that under any <see cref="UnjudgedRowPolicy"/> the options pipeline accepts.
/// </summary>
public class UnjudgedRowWeightingTests
{
    [Fact]
    public void ResolveWeight_JudgeScoredRowIsAlwaysOne_AndUnjudgedNeverWeighsMore()
    {
        double[] weights = [0, 0.5, 1];
        foreach (var policy in Enum.GetValues<UnjudgedRowPolicy>())
        {
            foreach (var weight in weights)
            {
                var judged = UnjudgedRowWeighting.ResolveWeight(isJudgeScored: true, policy: policy,
                    unjudgedRowWeight: weight);
                Assert.Equal(1.0, actual: judged);

                var unjudged = UnjudgedRowWeighting.ResolveWeight(isJudgeScored: false, policy: policy,
                    unjudgedRowWeight: weight);
                switch (policy)
                {
                    case UnjudgedRowPolicy.Include:
                        Assert.Equal(1.0, actual: unjudged);
                        break;
                    case UnjudgedRowPolicy.DownWeight:
                        if (weight == 0)
                            Assert.Null(unjudged);
                        else
                            Assert.Equal(weight, actual: unjudged);
                        break;
                    case UnjudgedRowPolicy.Exclude:
                        Assert.Null(unjudged);
                        break;
                    default:
                        Assert.Fail($"UnjudgedRowPolicy.{policy} has no invariant line.");
                        break;
                }

                if (judged is not null && unjudged is not null)
                    Assert.True(unjudged.Value <= judged.Value);
            }
        }
    }

    [Fact]
    public void ResolveRowWeight_ForwardsDownWeightAtHalfToTheStaticRule()
    {
        var options = new RoutingOptions
        {
            UnjudgedRowPolicy = UnjudgedRowPolicy.DownWeight, UnjudgedRowWeight = 0.5
        };

        Assert.Equal(
            expected: UnjudgedRowWeighting.ResolveWeight(isJudgeScored: false,
                policy: UnjudgedRowPolicy.DownWeight, unjudgedRowWeight: 0.5),
            actual: options.ResolveRowWeight(isJudgeScored: false));
        Assert.Equal(1.0, actual: options.ResolveRowWeight(isJudgeScored: true));
    }
}
