using TotallyHot.ArcRouter.Quality;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Fixed dashboard copy for off-path quality signals (issue #293 phase 6).
/// </summary>
public static class QualityDashboardLabels
{
    /// <summary>
    /// Shown on live turns whose grade did not train the router because the judge was absent or abstained.
    /// </summary>
    public const string StructureOnlyLearningPaused = "structure only / learning paused";

    /// <summary>
    /// Returns the dashboard label for <paramref name="status"/>, or <see langword="null"/> when complete.
    /// </summary>
    /// <param name="status">The grade status broadcast with the quality signal.</param>
    /// <returns>The label text, or <see langword="null"/> when no badge is needed.</returns>
    public static string? ForGradeStatus(GradeStatus status) =>
        status is GradeStatus.StructureOnly or GradeStatus.Incomplete ? StructureOnlyLearningPaused : null;
}
