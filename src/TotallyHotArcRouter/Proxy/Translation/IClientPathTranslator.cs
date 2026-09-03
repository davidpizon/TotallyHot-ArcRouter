namespace TotallyHot.ArcRouter.Proxy.Translation;

/// <summary>
/// Marks an <see cref="IPayloadTranslator"/> that rewrites the request <em>body</em> but leaves the
/// upstream URL to <see cref="ProxyMiddleware"/>'s ordinary client-path form, exactly as a provider with
/// no translator gets.
/// <para>
/// This exists because "reshapes the request" and "owns the upstream URL" turned out to be two axes, not
/// one. Before Phase 5 they were the same bit: a translator either did both (Gemini, which encodes the
/// model id and the streaming choice in the path) or neither
/// (<see cref="IResponseOnlyTranslator"/>). Tool-call emulation
/// (<c>docs/router/tool-call-normalization.md</c> Phase 5) is the first translator that needs one without
/// the other - it rewrites the body heavily (strips <c>tools</c>, injects a system prompt, re-renders
/// history) while still talking to the same OpenAI-compatible endpoint on the same path the client asked
/// for. Routing it through <see cref="IPayloadTranslator.BuildRequestUri"/> would drop that path, for the
/// reason <see cref="IResponseOnlyTranslator"/> already documents: that method never sees it.
/// </para>
/// <para>
/// An implementer's <see cref="IPayloadTranslator.BuildRequestUri"/> is therefore never called, and
/// should be an identity placeholder rather than a real implementation.
/// </para>
/// </summary>
internal interface IClientPathTranslator : IPayloadTranslator
{
}