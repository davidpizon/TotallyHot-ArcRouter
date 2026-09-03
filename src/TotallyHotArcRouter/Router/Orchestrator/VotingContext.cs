namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// The input an <see cref="IRoutingVoter"/> votes from - PLAN.md Phase L, research-doc §3.3's "task
/// prompt/metadata + DimensionBest prior + top-10 historical neighbors from Memory" inputs. Deliberately
/// wider than <see cref="RoutingContext"/> (which an <see cref="IRoutingPolicy"/> consumes): it adds the
/// optional task embedding and task text individual voters need, while a voter that needs neither
/// (<see cref="DimBestVoter"/>) simply never reads those two properties - no voter is forced to depend on
/// signals it doesn't use.
/// </summary>
/// <param name="Dimension">
/// The request's live <see cref="RouterMemory"/> dimension key, as in
/// <see cref="RoutingContext.Dimension"/>.
/// </param>
/// <param name="Candidates">
/// Every currently-eligible model a voter may pick, as in <see cref="RoutingContext.Candidates"/>
/// .
/// </param>
/// <param name="TaskEmbedding">
/// The task's unit-normalized embedding vector, when the caller has computed one - <see langword="null"/>
/// when unavailable. <see cref="MemoryKnnVoter"/> abstains without it; other voters ignore it.
/// </param>
/// <param name="TaskText">
/// The task prompt text, when the caller has it - <see langword="null"/> when unavailable.
/// <see cref="LogRegVoter"/> abstains without it; other voters ignore it.
/// </param>
public sealed record VotingContext(
    string Dimension,
    IReadOnlyList<RoutingCandidate> Candidates,
    float[]? TaskEmbedding = null,
    string? TaskText = null);