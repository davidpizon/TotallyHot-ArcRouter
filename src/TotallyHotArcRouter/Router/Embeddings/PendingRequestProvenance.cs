namespace TotallyHot.ArcRouter.Router.Embeddings;

/// <summary>
/// Exploration provenance known once routing resolves, cached under the request's correlation id until
/// the later-arriving verifier score can recover it.
/// </summary>
/// <param name="IsExploratory">Whether the request's routing decision was an epsilon-greedy exploratory pick.</param>
/// <param name="Propensity">The propensity of the arm actually chosen.</param>
/// <param name="Dimension">
/// The heuristic classifier's dimension label for this request, or <see langword="null"/> when
/// unavailable.
/// </param>
public sealed record PendingRequestProvenance(bool IsExploratory, double Propensity, string? Dimension);
