using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Orchestrator;

namespace TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

/// <summary>
/// The Orchestrator arm (research-doc Table 3, N5): replays the real
/// <see cref="OrchestratorRoutingPolicy"/> - not a re-implementation - built by
/// <see cref="OrchestratorArmFactory"/> against each task's available signals, so its
/// <c>CumReg</c>/<c>AvgPerf</c>/<c>Perf/$</c> are computed by the exact same
/// <see cref="RegretReplayEngine"/> loop every baseline runs through.
/// </summary>
/// <remarks>
/// <b>No live embedding calls during replay.</b> Like <see cref="KnnRetrievalBaseline"/>, this type never
/// embeds <see cref="RegretReplayContext.TaskText"/> itself - it only ever looks up a task's own
/// precomputed embedding by id (built once, offline, by <see cref="OrchestratorArmFactory.Build"/>). A task
/// outside that precomputed set (every ID-test/probing task) is voted on with a <see langword="null"/>
/// embedding, exactly the signal the live proxy path would give the ensemble for text-free traffic - which
/// is what lets <c>logreg</c> abstain there while <c>dim_best</c> still fires, the same degrade path
/// documented in "The Orchestrator arm" section of regret-evaluation-harness-plan.md.
/// </remarks>
public sealed class OrchestratorArmBaseline : IRegretBaselineRouter
{
    /// <summary>The placeholder provider stamped on every synthetic <see cref="RoutingCandidate"/> this arm builds - CodeRouterBench model ids carry no real provider, and this value is applied identically on both the candidate and the voters' own canonicalization, so it is a no-op rather than a source of mismatches.</summary>
    internal const string CandidateProvider = "coderouterbench";

    private readonly OrchestratorRoutingPolicy _policy;
    private readonly IReadOnlyDictionary<string, float[]> _embeddingsByTaskId;

    /// <summary>Initializes a new instance of the <see cref="OrchestratorArmBaseline"/> class.</summary>
    /// <param name="policy">The isolated, offline-safe <see cref="OrchestratorRoutingPolicy"/> instance, e.g. from <see cref="OrchestratorArmFactory.Build"/>.</param>
    /// <param name="embeddingsByTaskId">Precomputed embeddings keyed by task id - present only for tasks the split publishes text for (OOD). A task with no entry is voted on with a <see langword="null"/> embedding.</param>
    public OrchestratorArmBaseline(OrchestratorRoutingPolicy policy, IReadOnlyDictionary<string, float[]> embeddingsByTaskId)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(embeddingsByTaskId);

        _policy = policy;
        _embeddingsByTaskId = embeddingsByTaskId;
    }

    /// <inheritdoc />
    public string Name => "orchestrator";

    /// <inheritdoc />
    /// <remarks>
    /// Returns <see langword="null"/> when every voter abstained - <see cref="OrchestratorRoutingPolicy.DecideAsync"/>
    /// then falls back to <see cref="Models.RoutingOptions.DefaultModel"/>, which is never one of this
    /// task's <see cref="RegretReplayContext.CandidateModelIds"/>, so that fallback is read the same way
    /// every other baseline reads "could not route this task" - excluded from this arm's metrics, not
    /// counted as a zero-reward pick.
    /// </remarks>
    public string? Route(RegretReplayContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var candidates = context.CandidateModelIds
            .Select(id => new RoutingCandidate(id, CandidateProvider, IsFree: false))
            .ToList();
        var routingContext = new RoutingContext(context.Dimension, IsUtility: false, candidates);

        var embedding = _embeddingsByTaskId.TryGetValue(context.TaskId, out var vector) ? vector : null;

        var decision = _policy.DecideAsync(routingContext, embedding, context.TaskText, CancellationToken.None)
            .GetAwaiter().GetResult();

        return context.CandidateModelIds.Contains(decision.SelectedModel) ? decision.SelectedModel : null;
    }
}
