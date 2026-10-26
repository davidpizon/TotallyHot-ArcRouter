namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// One complete spot-check receipt paired with its single judge score. A low-confidence row is not
/// in this set, and a spot-check whose teacher produced no score is not in it either.
/// </summary>
/// <param name="CorrelationId">The receipt's correlation id.</param>
/// <param name="CreatedAtUtc">The receipt timestamp, parsed. Unparseable stamps sort as <see cref="DateTimeOffset.MinValue"/>.</param>
/// <param name="LocalScore">The local grader's score. Never null on a loaded pair.</param>
/// <param name="JudgeScore">The teacher score from the single judge grader row.</param>
internal sealed record GraderEpochSpotCheckPair(
    string CorrelationId,
    DateTimeOffset CreatedAtUtc,
    double LocalScore,
    double JudgeScore);
