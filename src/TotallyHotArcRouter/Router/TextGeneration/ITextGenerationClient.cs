namespace TotallyHot.ArcRouter.Router.TextGeneration;

/// <summary>
/// Produces free-form text from a local, in-process autoregressive language model, for the
/// <c>llm_router</c> voter (PLAN.md Phase L; <see cref="Orchestrator.LlmRouterVoter"/>). Mirrors
/// <see cref="Embeddings.IEmbeddingClient"/>'s role for that voter's single-pass embedding sibling -
/// an interface seam so the voter's prompt-building and response-parsing logic stays unit-testable
/// without a real ONNX model.
/// </summary>
public interface ITextGenerationClient
{
    /// <summary>
    /// Generates a completion for the given prompt.
    /// </summary>
    /// <param name="prompt">
    /// The fully-formatted prompt, including any chat-template wrapping the implementation requires.
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The generated text.</returns>
    /// <exception cref="OperationCanceledException">
    /// Thrown when generation does not finish before <paramref name="cancellationToken"/> is
    /// cancelled or the implementation's own generation timeout elapses.
    /// </exception>
    Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken = default);
}