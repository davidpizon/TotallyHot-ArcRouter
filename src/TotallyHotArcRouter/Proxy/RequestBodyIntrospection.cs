using System.Text;
using System.Text.Json.Nodes;
using TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// Pure, stateless reads over an already-parsed request body's <see cref="JsonObject"/> - whether it
/// carries tools, a <c>response_format</c>, or tool-calling history - plus the writes this class performs:
/// rewriting <c>model</c> to a candidate's upstream id, and removing from a candidate's own copy the features its
/// model's capability record reports unsupported (<see cref="MessagesFeatureStripper"/>). Every member here is a
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
    /// <remarks>
    /// With <paramref name="featureSupport"/>, the strip is first planned read-only against
    /// <paramref name="jsonObject"/>; only when it removes something is a separate copy parsed and changed, so the
    /// common case costs no extra parse. <paramref name="jsonObject"/> itself never loses a feature, and the
    /// unstripped serialization is kept on the candidate for failover candidates to start from.
    /// </remarks>
    /// <param name="jsonObject">The already-parsed request body; its <c>model</c> is rewritten in place.</param>
    /// <param name="route">The resolved route whose upstream model id replaces <c>model</c>.</param>
    /// <param name="featureSupport">
    /// The route's model capability record when this candidate may be stripped, or <see langword="null"/> when it
    /// may not (an explicit pick's own first attempt, a path other than <c>/v1/messages</c>, or no record).
    /// </param>
    /// <returns>The rewritten failover candidate.</returns>
    public static RouteCandidate BuildCandidate(JsonObject jsonObject, ResolvedModelRoute route,
        ModelFeatureSupport? featureSupport = null)
    {
        jsonObject["model"] = route.ProviderModelId;
        var rewrittenBody = Encoding.UTF8.GetBytes(jsonObject.ToJsonString());

        var strip = featureSupport is null
            ? MessagesFeatureStrip.None
            : MessagesFeatureStripper.Plan(body: jsonObject, support: featureSupport);
        var strippedBody = strip.IsEmpty ? null : ApplyStrip(source: rewrittenBody, strip: strip);

        return new RouteCandidate(
            Route: route,
            LazyRewrittenBody: new Lazy<byte[]>(strippedBody ?? rewrittenBody),
            CarriesTools: CarriesTools(jsonObject),
            CarriesToolHistory: CarriesToolHistory(jsonObject),
            CarriesResponseFormat: CarriesResponseFormat(jsonObject),
            LazyFeatureStrip: strip.IsEmpty ? null : new Lazy<MessagesFeatureStrip>(strip),
            UnstrippedBody: strippedBody is null ? null : rewrittenBody);
    }

    /// <summary>
    /// Builds a failover candidate that shares the primary candidate's route-independent flags
    /// (<c>Carries*</c> depend only on the request, not the route, so they are not re-walked per model) and
    /// defers its body rewrite until first read. The rewrite works from the primary's already-serialized
    /// body, a snapshot that later mutation of the parsed request cannot disturb, and changes only
    /// <c>model</c>.
    /// </summary>
    /// <remarks>
    /// Starts from the primary's <see cref="RouteCandidate.SourceBody"/> - its body before any strip - so a
    /// feature removed for the primary's model still reaches a fallback model that supports it. The fallback's own
    /// strip, when <paramref name="featureSupport"/> is given, is planned and applied inside the same lazy step.
    /// </remarks>
    /// <param name="primary">The already-built primary candidate whose body and flags are reused.</param>
    /// <param name="route">The fallback route whose upstream model id replaces <c>model</c>.</param>
    /// <param name="featureSupport">
    /// The fallback model's capability record when it may be stripped, or <see langword="null"/>.
    /// </param>
    /// <returns>The failover candidate with a lazily produced body.</returns>
    public static RouteCandidate BuildFallbackCandidate(RouteCandidate primary, ResolvedModelRoute route,
        ModelFeatureSupport? featureSupport = null)
    {
        var sourceBody = primary.SourceBody;
        var built = new Lazy<(byte[] Body, MessagesFeatureStrip Strip)>(() =>
        {
            var body = (JsonObject)JsonNode.Parse(sourceBody)!;
            body["model"] = route.ProviderModelId;

            var strip = featureSupport is null
                ? MessagesFeatureStrip.None
                : MessagesFeatureStripper.Plan(body: body, support: featureSupport);
            if (!strip.IsEmpty) MessagesFeatureStripper.Apply(body: body, strip: strip);

            return (Encoding.UTF8.GetBytes(body.ToJsonString()), strip);
        });

        return new RouteCandidate(
            Route: route,
            LazyRewrittenBody: new Lazy<byte[]>(() => built.Value.Body),
            CarriesTools: primary.CarriesTools,
            CarriesToolHistory: primary.CarriesToolHistory,
            CarriesResponseFormat: primary.CarriesResponseFormat,
            LazyFeatureStrip: featureSupport is null ? null : new Lazy<MessagesFeatureStrip>(() => built.Value.Strip));
    }

    /// <summary>
    /// Parses a fresh copy of <paramref name="source"/>, removes what <paramref name="strip"/> names, and
    /// serializes it - the candidate's own copy, so the shared parsed body is never changed.
    /// </summary>
    private static byte[] ApplyStrip(byte[] source, MessagesFeatureStrip strip)
    {
        var copy = (JsonObject)JsonNode.Parse(source)!;
        MessagesFeatureStripper.Apply(body: copy, strip: strip);
        return Encoding.UTF8.GetBytes(copy.ToJsonString());
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
    private static bool CarriesTools(JsonObject jsonObject)
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
    private static bool CarriesResponseFormat(JsonObject jsonObject)
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
    private static bool CarriesToolHistory(JsonObject jsonObject)
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