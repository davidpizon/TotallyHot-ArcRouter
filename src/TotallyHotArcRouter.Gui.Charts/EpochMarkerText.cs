using System.Globalization;

namespace TotallyHot.ArcRouter.Gui.Charts;

/// <summary>
/// The epoch fields the chart tooltip is allowed to show (issue #304 phase 9). No property holds
/// question text or response text.
/// </summary>
/// <param name="Id">The epoch row id.</param>
/// <param name="AtUtc">The event instant. Already UTC.</param>
/// <param name="Kind"><c>code</c> or <c>non-code</c>.</param>
/// <param name="EpochType">One of the five epoch types, or an unknown type that is still drawn.</param>
/// <param name="Cause">Why the row was appended.</param>
/// <param name="ArtifactId">The artifact this row is about, or null.</param>
/// <param name="PreviousArtifactId">The artifact that was in charge before, or null.</param>
/// <param name="WindowSize">Spot-check window size, or null. Null is not 0.</param>
/// <param name="MeanMiss">Spot-check mean miss, or null. Null is not 0.</param>
/// <param name="PreviousTeacherModel">Teacher model before a teacher change, or null.</param>
/// <param name="TeacherModel">Teacher model on a teacher change, or null.</param>
/// <param name="PreviousPromptVersion">Prompt version before a teacher change, or null.</param>
/// <param name="PromptVersion">Prompt version on a teacher change, or null.</param>
/// <param name="RollbackMarkPresent">1, 0, or null. Null omits the line. 0 is a real no.</param>
/// <param name="PreviousBgeModel">BGE id before a change, or null.</param>
/// <param name="BgeModel">BGE id on a change, or null.</param>
/// <param name="PreviousFeatureSchemaVersion">Schema version before a change, or null. 1 is a real version.</param>
/// <param name="FeatureSchemaVersion">Schema version on a change, or null. 1 is a real version.</param>
public sealed record EpochEventPlot(
    long Id,
    DateTimeOffset AtUtc,
    string Kind,
    string EpochType,
    string Cause,
    string? ArtifactId,
    string? PreviousArtifactId,
    int? WindowSize,
    double? MeanMiss,
    string? PreviousTeacherModel,
    string? TeacherModel,
    string? PreviousPromptVersion,
    string? PromptVersion,
    int? RollbackMarkPresent,
    string? PreviousBgeModel,
    string? BgeModel,
    int? PreviousFeatureSchemaVersion,
    int? FeatureSchemaVersion);

/// <summary>
/// Builds the epoch detail lines in C#. Hover, click, keyboard, and touch show this same list.
/// The script does not invent a line. The last line says the mark does not claim it moved the series.
/// </summary>
public static class EpochMarkerText
{
    /// <summary>The last line of every mark. It refuses a causal claim.</summary>
    public const string NoCause = "This mark does not say it caused the line to move.";

    /// <summary>Builds the marker the chart draws for <paramref name="row"/>.</summary>
    /// <param name="row">One epoch inside the loaded window.</param>
    /// <returns>The strip point and its detail lines.</returns>
    public static EpochMarker Marker(EpochEventPlot row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new EpochMarker(
            T: row.AtUtc.ToUnixTimeMilliseconds(),
            Id: row.Id,
            Color: EpochMarkerPalette.ColorFor(row.EpochType),
            Symbol: EpochMarkerPalette.SymbolFor(row.Kind),
            Tip: Lines(row));
    }

    /// <summary>The detail lines, in the order hover and the detail region both show.</summary>
    /// <param name="row">One epoch.</param>
    /// <returns>The lines. The first is the UTC time. The last is <see cref="NoCause"/>.</returns>
    public static IReadOnlyList<string> Lines(EpochEventPlot row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var lines = new List<string>
        {
            "Time (UTC): " + row.AtUtc.ToUniversalTime().ToString(format: "yyyy-MM-dd HH:mm:ss", formatProvider: CultureInfo.InvariantCulture),
            EpochMarkerPalette.LegendName(row.EpochType),
            row.Kind,
            row.Cause,
            "Artifact id: " + (row.ArtifactId ?? "unknown"),
            "Previous artifact id: " + (row.PreviousArtifactId ?? "none")
        };

        if (row.Cause is "spot_check_rollback" or "live_check_pass")
        {
            lines.Add("Window size: " + FormatInt(row.WindowSize));
            lines.Add("Mean miss: " + FormatDouble(row.MeanMiss));
        }

        if (row.EpochType == "teacher_changed")
        {
            lines.Add("Previous teacher model: " + (row.PreviousTeacherModel ?? "unknown"));
            lines.Add("Teacher model: " + (row.TeacherModel ?? "unknown"));
            lines.Add("Previous prompt version: " + (row.PreviousPromptVersion ?? "unknown"));
            lines.Add("Prompt version: " + (row.PromptVersion ?? "unknown"));
        }

        if (row.EpochType == "bge_changed")
        {
            lines.Add("Previous BGE id: " + (row.PreviousBgeModel ?? "unknown"));
            lines.Add("BGE id: " + (row.BgeModel ?? "unknown"));
        }

        if (row.EpochType == "feature_schema_changed")
        {
            lines.Add("Previous schema version: " + FormatInt(row.PreviousFeatureSchemaVersion));
            lines.Add("Schema version: " + FormatInt(row.FeatureSchemaVersion));
        }

        if (row.RollbackMarkPresent is { } mark)
            lines.Add(mark == 1 ? "rollback mark: yes" : "rollback mark: no");

        lines.Add(NoCause);
        return lines;
    }

    /// <summary>A null count is the word unknown, not 0. A stored 1 stays 1.</summary>
    private static string FormatInt(int? value)
    {
        return value?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
    }

    /// <summary>A null mean miss is the word unknown, not 0.</summary>
    private static string FormatDouble(double? value)
    {
        return value?.ToString(format: "G", provider: CultureInfo.InvariantCulture) ?? "unknown";
    }
}
