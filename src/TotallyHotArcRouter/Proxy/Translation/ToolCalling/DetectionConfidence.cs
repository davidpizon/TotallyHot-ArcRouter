namespace TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

/// <summary>
/// How a model's tool-call dialect came to be known, ordered weakest to strongest (see
/// <c>docs/router/tool-call-normalization.md</c> §3.2).
///
/// <para>
/// The numeric order is load-bearing, not decorative: a capability write is accepted only when its
/// confidence is greater than or equal to the stored row's, so a cheap startup guess can never clobber
/// something learned from a real response. This mirrors the price catalog's <c>priority_score &gt;=</c>
/// upsert gate, which solves the same "two sources disagree, the better-sourced one must win" problem.
/// </para>
/// </summary>
public enum DetectionConfidence
{
    /// <summary>
    /// Guessed from the model id alone (<c>qwen*</c>, <c>mistral*</c>, …). Free and instant, but a
    /// renamed or fine-tuned GGUF defeats it, so anything better overwrites it.
    /// </summary>
    Heuristic = 0,

    /// <summary>
    /// Read out of the model's actual chat template - Ollama's <c>/api/show</c> returns the literal
    /// template text, and LM Studio's native model list carries similar metadata. Strong evidence, since
    /// the template is precisely what decides the model's reply syntax, but still not proof of what the
    /// model does at runtime.
    /// </summary>
    Template = 1,

    /// <summary>
    /// Seen in a real response to a real tools-carrying request. The only tier that is empirical rather
    /// than predictive, which is why it outranks a template read.
    /// </summary>
    Observed = 2,

    /// <summary>
    /// Set deliberately by an operator. No scan may overwrite it - the escape hatch for a model whose
    /// automatic detection misfires, which is worthless if the next request silently undoes it.
    /// </summary>
    Operator = 3,
}

