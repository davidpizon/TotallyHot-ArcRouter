namespace TotallyHot.ArcRouter.Quality.Grading;

/// <summary>
/// Determines which grader keys beyond <see cref="GraderKeys.Judge"/> a submitted result should be held
/// open for (Phase Q3: <see cref="GraderKeys.CodeJudge"/>, <see cref="GraderKeys.IceScore"/>,
/// <see cref="GraderKeys.Race"/>). A separate seam from <see cref="IJudgeAvailability"/> rather than a
/// generalization of it, so the judge's existing single-key contract and its tests are undisturbed; the
/// aggregator unions both seams' answers into one pending-grader set.
/// </summary>
/// <remarks>
/// The host supplies the real implementation; <see cref="NoPortfolioGraderAvailability"/> is the safe
/// default that keeps the verifier fully functional - and byte-identical to pre-Q3 behavior - on its own.
/// </remarks>
public interface IPortfolioGraderAvailability
{
    /// <summary>Determines which extra grader keys are expected to arrive for this result.</summary>
    /// <param name="result">The freshly graded static result.</param>
    /// <returns>
    /// The subset of <see cref="GraderKeys.CodeJudge"/>/<see cref="GraderKeys.IceScore"/>/
    /// <see cref="GraderKeys.Race"/> the caller should hold the result open for. Empty is always safe - the
    /// same "a true that never materializes just times out" tolerance <see cref="IJudgeAvailability"/>
    /// documents applies to each key here too.
    /// </returns>
    IReadOnlySet<string> DetermineGraderKeys(QualityResult result);
}

/// <summary>
/// The default <see cref="IPortfolioGraderAvailability"/>: no portfolio grader is ever expected, so a
/// result's join set is exactly whatever <see cref="IJudgeAvailability"/> alone contributed - the
/// byte-identical-to-pre-Q3 behavior a host that has not registered the portfolio graders keeps by
/// construction.
/// </summary>
public sealed class NoPortfolioGraderAvailability : IPortfolioGraderAvailability
{
    private static readonly IReadOnlySet<string> Empty = new HashSet<string>();

    /// <inheritdoc/>
    public IReadOnlySet<string> DetermineGraderKeys(QualityResult result)
    {
        return Empty;
    }
}
