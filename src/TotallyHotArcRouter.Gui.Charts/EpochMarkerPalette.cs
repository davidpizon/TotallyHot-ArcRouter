namespace TotallyHot.ArcRouter.Gui.Charts;

/// <summary>
/// Fixed colors and symbols for grader-epoch marks (issue #304 phase 9). Colors are not hashed, so the
/// legend stays put. None of them is the context-breach red or the token-runaway amber.
/// </summary>
public static class EpochMarkerPalette
{
    /// <summary>A local grader began grading on its own.</summary>
    public const string LocalTakeover = "#1ed760";

    /// <summary>The teacher took charge of a kind again.</summary>
    public const string TeacherResumed = "#3b82f6";

    /// <summary>The teacher model or prompt version changed.</summary>
    private const string TeacherChanged = "#a855f7";

    /// <summary>The embedding model changed.</summary>
    private const string BgeChanged = "#14b8a6";

    /// <summary>The feature list changed.</summary>
    private const string FeatureSchemaChanged = "#6366f1";

    /// <summary>An epoch type this phase does not name. The mark is still drawn.</summary>
    private const string Unknown = "#64748b";

    /// <summary>The context-breach alert. Epoch marks do not use it.</summary>
    public const string AlertRed = "#ef4444";

    /// <summary>The token-runaway alert. Epoch marks do not use it.</summary>
    public const string AlertAmber = "#f59e0b";

    /// <summary>Code epochs use a circle.</summary>
    private const string CodeSymbol = "circle";

    /// <summary>Non-code epochs use a diamond.</summary>
    private const string NonCodeSymbol = "diamond";

    /// <summary>The shape key under every real Cost Analytics pill.</summary>
    public const string ShapeLegend = "circle = code, diamond = non-code";

    /// <summary>The five colors beside their legend names. An unknown type is not in this list; its mark uses <see cref="Unknown"/>.</summary>
    public static readonly (string Color, string Name)[] Legend =
    [
        (LocalTakeover, "Local grader took over"),
        (TeacherResumed, "Teacher took charge again"),
        (TeacherChanged, "Teacher model or prompt changed"),
        (BgeChanged, "Embedding model changed"),
        (FeatureSchemaChanged, "Feature list changed")
    ];

    /// <summary>The color for <paramref name="epochType"/>. An unknown type is <see cref="Unknown"/>, not dropped.</summary>
    /// <param name="epochType">The stored epoch type.</param>
    /// <returns>A fixed hex color.</returns>
    public static string ColorFor(string epochType)
    {
        return epochType switch
        {
            "local_takeover" => LocalTakeover,
            "teacher_resumed" => TeacherResumed,
            "teacher_changed" => TeacherChanged,
            "bge_changed" => BgeChanged,
            "feature_schema_changed" => FeatureSchemaChanged,
            _ => Unknown
        };
    }

    /// <summary>The legend name for <paramref name="epochType"/>. An unknown type keeps its raw text.</summary>
    /// <param name="epochType">The stored epoch type.</param>
    /// <returns>The legend label.</returns>
    public static string LegendName(string epochType)
    {
        return epochType switch
        {
            "local_takeover" => Legend[0].Name,
            "teacher_resumed" => Legend[1].Name,
            "teacher_changed" => Legend[2].Name,
            "bge_changed" => Legend[3].Name,
            "feature_schema_changed" => Legend[4].Name,
            _ => epochType
        };
    }

    /// <summary><c>circle</c> for code. <c>diamond</c> for non-code. Any other kind stays a circle and is still drawn.</summary>
    /// <param name="kind"><c>code</c> or <c>non-code</c>.</param>
    /// <returns>The ECharts symbol name.</returns>
    public static string SymbolFor(string kind)
    {
        return kind == "non-code" ? NonCodeSymbol : CodeSymbol;
    }
}
