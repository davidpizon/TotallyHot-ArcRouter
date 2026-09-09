namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Configuration for Phase Q3's LLM grader portfolio - CodeJudge (correctness), ICE-Score (usefulness), and
/// RACE (readability/maintainability) - each an independent axis contributed to
/// <see cref="Quality.QualityResult.GraderScores"/> alongside the G-Eval judge's named
/// <see cref="Quality.QualityResult.JudgeScore"/> axis (docs/research/code-quality-metrics-assessment.md
/// §5.1's "construct diversity, not more correctness judges" portfolio rationale).
/// </summary>
/// <remarks>
/// Not bound from <c>appsettings.json</c>, exactly like <see cref="JudgeOptions.Enabled"/>/
/// <see cref="JudgeOptions.ModelName"/>: these three flags are operator-facing settings owned by the
/// <c>router_settings</c> table and layered on by <see cref="PortfolioGraderSettingsConfigureOptions"/>. All
/// three graders share the judge's own backbone selection (<see cref="JudgeModelSelector"/>) and its
/// <see cref="JudgeOptions.RequestTimeoutSeconds"/>/<see cref="JudgeOptions.QueueCapacity"/> bounds - a
/// separate per-grader backbone or queue would triple the eligibility/settings surface for a portfolio that
/// exists for construct diversity in scoring, not in infrastructure.
/// </remarks>
public sealed class PortfolioGraderOptions
{
    /// <summary>
    /// Gets whether the CodeJudge correctness grader is enabled. The literal initializer is
    /// <see langword="false"/>, but the effective default is computed by
    /// <see cref="PortfolioGraderSettingsConfigureOptions"/> the same way
    /// <see cref="JudgeOptions.Enabled"/>'s is: on when an eligible free backbone exists, off when none
    /// does, unless an operator has explicitly stored a choice. Overridden by the <c>router_settings</c> row
    /// <see cref="Router.RouterSettingsStore.CodeJudgeEnabledKey"/>.
    /// </summary>
    public bool CodeJudgeEnabled { get; init; }

    /// <summary>
    /// Gets whether the ICE-Score usefulness grader is enabled. Same computed-default and live-toggle
    /// treatment as <see cref="CodeJudgeEnabled"/>. Overridden by the <c>router_settings</c> row
    /// <see cref="Router.RouterSettingsStore.IceScoreEnabledKey"/>.
    /// </summary>
    public bool IceScoreEnabled { get; init; }

    /// <summary>
    /// Gets whether the RACE readability/maintainability grader is enabled. Same computed-default and
    /// live-toggle treatment as <see cref="CodeJudgeEnabled"/>. Overridden by the <c>router_settings</c> row
    /// <see cref="Router.RouterSettingsStore.RaceEnabledKey"/>.
    /// </summary>
    public bool RaceEnabled { get; init; }

    /// <summary>Gets whether at least one portfolio grader is enabled.</summary>
    /// <remarks>
    /// Used to decide whether raw prompt/response text needs retaining in
    /// <see cref="PendingResponseTextCache"/>/<see cref="PendingPromptCache"/> at all when the G-Eval judge
    /// itself is off - retention is authorized by "some LLM grader needs this text", not by the judge
    /// specifically.
    /// </remarks>
    public bool AnyEnabled => CodeJudgeEnabled || IceScoreEnabled || RaceEnabled;
}
