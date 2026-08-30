namespace TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

/// <summary>
/// Maps what the router knows about a model's tool calling onto the <c>capabilities</c> array Ollama's
/// <c>POST /api/show</c> publishes, which capability-filtering clients (Visual Studio's Copilot chat among
/// them) use to decide whether a model may be selected at all.
///
/// <para>
/// The governing principle is that <c>/api/show</c> describes what <em>this endpoint</em> can do with
/// <em>this model name</em> - the client is talking to the router, not to the weights. Every dialect the
/// router records is one it will accept a <c>tools</c> request for and return real <c>tool_calls</c> from,
/// whether by forwarding natively, by normalizing a text dialect, or by emulating tools outright. So every
/// state maps to <c>tools</c> today, and that uniformity is deliberate rather than an oversight - see
/// <c>docs/adr/0003-declare-tool-support-for-emulated-and-unclassified-models.md</c> for the full argument,
/// including why <c>emulated</c> and an unclassified model in particular must both declare it.
/// </para>
/// </summary>
/// <remarks>
/// Written as an explicit mapping rather than a constant despite being branchless today. It is the single
/// place a future dialect meaning "cannot express a tool call at all" has to land, and its test asserts
/// exhaustiveness over <see cref="ToolCallDialectRegistry.All"/> - so adding a dialect fails the build
/// until someone decides, consciously, what it can do.
/// </remarks>
internal static class OllamaModelCapabilities
{
    /// <summary>Text generation. Every model the router routes can do this, by definition.</summary>
    internal const string Completion = "completion";

    /// <summary>Tool/function calling - the capability clients filter their model pickers on.</summary>
    internal const string Tools = "tools";

    // The canonical order every result is emitted in. Fixed rather than derived from set enumeration so the
    // serialized JSON is byte-stable across runs and processes, which keeps both response diffing and the
    // tests' assertions meaningful.
    private static readonly string[] CanonicalOrder = [Completion, Tools];

    /// <summary>
    /// The capabilities to declare for a model recorded with <paramref name="dialectName"/>.
    /// </summary>
    /// <param name="dialectName">
    /// The stored <see cref="ModelToolCapability.Dialect"/>, or <see langword="null"/> when the model has
    /// never been classified. Null is the dominant case in practice - a fresh install has run no scan, and
    /// no hosted provider can be probed at all - so it must declare tools, not withhold them.
    /// </param>
    /// <returns>The capability tokens, in <see cref="CanonicalOrder"/>. Never empty.</returns>
    internal static IReadOnlyList<string> ForDialect(string? dialectName)
    {
        // Every branch currently yields the same pair; see the type-level remarks for why this is still a
        // function. An unrecognized name - a row written by a newer build, or hand-edited - lands here too
        // and is treated as unclassified, matching how ToolCallNormalizerFactory reads it.
        _ = ToolCallDialectRegistry.TryGet(dialectName, out _);

        return CanonicalOrder;
    }

    /// <summary>
    /// Merges several models' capabilities into the set the synthetic router alias declares.
    /// </summary>
    /// <remarks>
    /// A union, not an intersection: the alias resolves to whichever model auto-select picks, and the
    /// router's emulation layer covers a pick that cannot call tools natively. An intersection would let a
    /// single unclassifiable model strip <c>tools</c> from the alias and hide it from every filtering
    /// client, which is the exact failure this whole feature exists to fix.
    /// </remarks>
    /// <param name="sets">Each eligible model's capabilities.</param>
    /// <returns>
    /// The union in <see cref="CanonicalOrder"/>. Always contains <see cref="Completion"/>, even for an
    /// empty input - an alias backed by no eligible model can still complete, it just cannot promise tools.
    /// </returns>
    internal static IReadOnlyList<string> Union(IEnumerable<IReadOnlyList<string>> sets)
    {
        ArgumentNullException.ThrowIfNull(sets);

        var present = new HashSet<string>(StringComparer.Ordinal) { Completion };
        foreach (var set in sets)
        {
            foreach (var capability in set)
            {
                present.Add(capability);
            }
        }

        // Filtered through the canonical order rather than emitted from the set, so ordering is fixed and a
        // capability from a newer build that this one does not know how to order cannot appear in an
        // arbitrary position.
        return CanonicalOrder.Where(present.Contains).ToArray();
    }
}
