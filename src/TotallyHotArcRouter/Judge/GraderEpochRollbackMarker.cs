namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// The <c>rollback.json</c> marker phase 8 writes when a spot-check window misses the bar. It blocks
/// the next artifact from grading alone until that artifact's own window passes. A teacher, prompt,
/// BGE, or feature-schema change does not create this file and does not delete one that exists.
/// </summary>
internal sealed record GraderEpochRollbackMarker
{
    /// <summary>Gets the artifact id whose pointer was cleared.</summary>
    public string RolledBackArtifactId { get; init; } = string.Empty;

    /// <summary>Gets the window's mean absolute miss against the teacher.</summary>
    public double Mae { get; init; }

    /// <summary>Gets how many spot-check rows were in the window.</summary>
    public int WindowSize { get; init; }

    /// <summary>Gets when the marker was written.</summary>
    public DateTimeOffset AtUtc { get; init; }
}
