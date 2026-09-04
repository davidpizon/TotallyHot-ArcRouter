using System.Text;
using System.Text.Json.Nodes;

namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// Pure, stateless reads over an already-parsed request body's <see cref="JsonObject"/> - whether it
/// carries tools, a <c>response_format</c>, or tool-calling history - plus the one write this class
/// performs: rewriting <c>model</c> to a candidate's upstream id. Every member here is a
/// <see langword="static"/> function of its arguments with no field/collaborator dependency, split out
/// of <see cref="RequestInterceptor"/> so that class's constructor-injected routing logic isn't mixed
/// with body-shape inspection that needs none of it.
/// </summary>
internal static class RequestBodyIntrospection
{
    /// <summary>
    /// Rewrites the request body's <c>model</c> field to the given route's upstream model id and
    /// serializes it, producing one failover candidate. Reuses (and mutates) <paramref name="jsonObject"/>
    /// in place - callers invoke this sequentially per candidate, and only the serialized snapshot each
    /// call returns is retained, so the shared node's transient state between calls is never observed.
    /// </summary>
    /// <param name="jsonObject">The already-parsed request body, mutated in place.</param>
    /// <param name="route">The resolved route whose upstream model id replaces <c>model</c>.</param>
    /// <returns>The rewritten failover candidate.</returns>
    public static RouteCandidate BuildCandidate(JsonObject jsonObject, ResolvedModelRoute route)
    {
        jsonObject["model"] = route.ProviderModelId;
        var rewrittenBody = Encoding.UTF8.GetBytes(jsonObject.ToJsonString());
        return new RouteCandidate(
            Route: route,
            RewrittenBody: rewrittenBody,
            CarriesTools: CarriesTools(jsonObject),
            CarriesToolHistory: CarriesToolHistory(jsonObject),
            CarriesResponseFormat: CarriesResponseFormat(jsonObject));
    }

    /// <summary>
    /// Whether this request offers the model any tools at all - the gate on installing tool-call
    /// normalization downstream (<c>docs/router/tool-call-normalization.md</c> §3.4 performance rule 1).
    /// Read from the body this method has already parsed rather than re-parsing it, which is what makes
    /// the check free.
    /// </summary>
    /// <remarks>
    /// An empty <c>tools</c> array counts as no tools: the model was offered nothing, so any tool-call
    /// syntax in its reply is prose about tool calling, not an invocation - exactly the false positive
    /// per-model arming exists to avoid.
    /// </remarks>
    /// <param name="jsonObject">The already-parsed request body.</param>
    public static bool CarriesTools(JsonObject jsonObject)
    {
        return jsonObject["tools"] is JsonArray { Count: > 0 };
    }

    /// <summary>
    /// Whether the client set its own <c>response_format</c>, which makes constrained tool calling
    /// unavailable for this request - see <see cref="RouteCandidate.CarriesResponseFormat"/>.
    /// </summary>
    /// <remarks>
    /// Any non-null value counts, including a shape this build does not recognize. The question is not
    /// "did the client ask for something we understand" but "would setting our own overwrite theirs",
    /// and the answer to that is yes for every value they could have sent.
    /// </remarks>
    /// <param name="jsonObject">The already-parsed request body.</param>
    public static bool CarriesResponseFormat(JsonObject jsonObject)
    {
        return jsonObject["response_format"] is not null;
    }

    /// <summary>
    /// Whether the conversation already contains tool-calling turns, which an emulated model's chat
    /// template cannot render (<c>docs/router/tool-call-normalization.md</c> Phase 5). Read from the
    /// same already-parsed body as <see cref="CarriesTools"/>.
    /// </summary>
    /// <remarks>
    /// Stops at the first match rather than surveying the whole conversation: the answer is a single
    /// bool, and a long chat's message list is the largest thing in the request body. Both shapes are
    /// checked because either alone is enough to confuse the model - an assistant turn it cannot read
    /// as its own, or a result whose role its template has never seen.
    /// </remarks>
    /// <param name="jsonObject">The already-parsed request body.</param>
    public static bool CarriesToolHistory(JsonObject jsonObject)
    {
        if (jsonObject["messages"] is not JsonArray messages) return false;

        foreach (var node in messages)
        {
            if (node is not JsonObject message) continue;

            if (message["tool_calls"] is JsonArray { Count: > 0 }) return true;

            if (message["role"] is JsonValue role &&
                role.TryGetValue<string>(out var value) &&
                string.Equals(a: value, b: "tool", comparisonType: StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}