namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// Out-of-band signals about the current request that <see cref="RoutingContext"/> itself does not carry
/// - the prompt text and its embedding, both optional and both computed on the request path
/// (docs/router/live-feedback-learning-plan.md Phase 2a). Only <see cref="Orchestrator.OrchestratorRoutingPolicy"/>
/// currently consumes these, forwarding them to <see cref="Orchestrator.OrchestratorRoutingPolicy.DecideAsync"/>
/// so <see cref="Orchestrator.MemoryKnnVoter"/> and <see cref="Orchestrator.LogRegVoter"/> can vote instead
/// of abstaining; every other <see cref="IRoutingPolicy"/> ignores them via
/// <see cref="IRoutingPolicy.SelectModelAsync(RoutingContext, RoutingSignals?, CancellationToken)"/>'s
/// default interface implementation.
/// </summary>
/// <param name="TaskText">The request's newest user-turn text, or <see langword="null"/> if it could not be extracted.</param>
/// <param name="TaskEmbedding">The request's task embedding, or <see langword="null"/> if it was not computed (budget exceeded, client unavailable, or still warming up).</param>
public sealed record RoutingSignals(string? TaskText, float[]? TaskEmbedding);
