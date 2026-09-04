namespace TotallyHot.ArcRouter.Quality;

/// <summary>
/// Infers the task dimension (the paper's d(t_i)) for a live request from its prompt and the language of
/// any extracted code. Deliberately a simple heuristic to start (see architecture doc §10).
/// </summary>
public interface IDimensionInferrer
{
    /// <summary>Infers a dimension key from the prompt and detected code language.</summary>
    /// <param name="prompt">The user prompt.</param>
    /// <param name="language">The detected language of the extracted code.</param>
    /// <returns>A dimension key such as <c>code_generation</c> or <c>bug_fixing</c>.</returns>
    string Infer(string prompt, CodeLanguage language);
}