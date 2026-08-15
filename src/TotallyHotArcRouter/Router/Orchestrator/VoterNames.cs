namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// The fixed, canonical voter identities research-doc §3.3/A.1 names - <c>dim_best</c>,
/// <c>memory_kNN</c>, <c>logreg</c>, <c>llm_router</c>. Shared between each <see cref="IRoutingVoter"/>'s
/// own <see cref="IRoutingVoter.Name"/> and <see cref="OrchestratorRoutingPolicy"/>'s per-voter
/// weight/enablement lookup in <see cref="Models.RoutingOptions"/>, so the two sides can never drift
/// onto different spellings of the same voter.
/// </summary>
public static class VoterNames
{
    /// <summary>The DimensionBest-lookup voter (<see cref="DimBestVoter"/>).</summary>
    public const string DimBest = "dim_best";

    /// <summary>The embedding-kNN voter (<see cref="MemoryKnnVoter"/>).</summary>
    public const string MemoryKnn = "memory_kNN";

    /// <summary>The TF-IDF logistic-regression voter (<see cref="LogRegVoter"/>).</summary>
    public const string LogReg = "logreg";

    /// <summary>The fine-tuned-LLM voter (<see cref="LlmRouterVoter"/>).</summary>
    public const string LlmRouter = "llm_router";
}
