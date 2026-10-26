namespace TotallyHot.ArcRouter.Quality.Grading;

/// <summary>
/// Decides, per grade, whether an accepted local grader may keep the judge axis. The host registers
/// the real implementation before <c>AddQuality</c>; this assembly's default does nothing so the
/// quality library still builds without the router.
/// </summary>
public interface ILocalGradeTakeover
{
    /// <summary>
    /// Stamps a local score onto <paramref name="result"/> when an accepted artifact can score the
    /// reply, and says whether the pinned teacher still has to run. A false <see cref="LocalGradeDecision.Handled"/>
    /// leaves <paramref name="result"/> unchanged so today's teacher path runs.
    /// </summary>
    /// <param name="result">The static result for this grade. The method may set its local-score fields.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>Whether this phase handled the row, and whether the teacher must still be called.</returns>
    Task<LocalGradeDecision> ApplyAsync(QualityResult result, CancellationToken cancellationToken);
}

/// <summary>
/// The takeover decision for one grade. <see cref="TeacherCallReason"/> is null, <see cref="GradeSources.LowConfidence"/>,
/// or <see cref="GradeSources.SpotCheck"/>.
/// </summary>
/// <param name="Handled">Whether an accepted artifact scored this row.</param>
/// <param name="CallTeacher">Whether <see cref="GraderKeys.Judge"/> stays in the pending set.</param>
/// <param name="TeacherCallReason">Why the teacher was called, or null when it was not.</param>
public readonly record struct LocalGradeDecision(bool Handled, bool CallTeacher, string? TeacherCallReason);

/// <summary>
/// The quality-library default. <see cref="ApplyAsync"/> reports the row unhandled so the aggregator
/// keeps today's pin check and teacher enqueue.
/// </summary>
public sealed class NoLocalGradeTakeover : ILocalGradeTakeover
{
    /// <inheritdoc/>
    public Task<LocalGradeDecision> ApplyAsync(QualityResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new LocalGradeDecision(false, false, null));
    }
}
