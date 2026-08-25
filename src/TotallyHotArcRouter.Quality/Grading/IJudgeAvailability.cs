namespace TotallyHot.ArcRouter.Quality.Grading;

/// <summary>
/// Reports whether a judge grade should be expected for a given result, so the aggregator knows whether to
/// write the static score straight away or hold it open for a blend.
/// </summary>
/// <remarks>
/// This is a seam, not a policy: the judge lives in the host application (it needs provider configuration,
/// an HTTP stack, and the operator's model choice), and this assembly deliberately does not reference it.
/// The host supplies the real implementation; <see cref="NoJudgeAvailability"/> is the safe default that
/// keeps the verifier fully functional on its own.
/// </remarks>
public interface IJudgeAvailability
{
    /// <summary>Determines whether a judge grade is expected to arrive for this result.</summary>
    /// <param name="result">The freshly graded static result.</param>
    /// <returns>
    /// <see langword="true"/> to hold the result open awaiting a judge grade; <see langword="false"/> to
    /// write the static score immediately. A <see langword="true"/> that never materializes is safe - the
    /// join times out and writes static-only - but a <see langword="false"/> is final.
    /// </returns>
    bool WillJudge(QualityResult result);
}

/// <summary>
/// The default <see cref="IJudgeAvailability"/>: no judge is expected, so every result is written from its
/// static score alone. Registered whenever the host has not supplied its own, which keeps this assembly
/// usable standalone and in tests.
/// </summary>
public sealed class NoJudgeAvailability : IJudgeAvailability
{
    /// <inheritdoc />
    public bool WillJudge(QualityResult result) => false;
}
