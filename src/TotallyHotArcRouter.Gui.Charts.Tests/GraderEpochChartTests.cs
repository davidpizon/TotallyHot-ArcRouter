using System.Diagnostics;
using System.Text.Json;
using TotallyHot.ArcRouter.Gui.Charts;

namespace TotallyHot.ArcRouter.Gui.Charts.Tests;

/// <summary>
/// Epoch colors, the gap flag, and the ECharts strip (issue #304 phase 9). The strip is checked by
/// running the same <c>_buildOption</c> the page uses. No browser and no chart library download.
/// </summary>
public sealed class GraderEpochChartTests
{
    [Fact]
    public void EfficiencyGap_OmitsValue()
    {
        var json = ChartJson.Serialize(new EfficiencyPoint(T: 1_700_000_000_000, Gap: true, Value: null, Tip: []));

        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("gap").GetBoolean());
        Assert.Equal(expected: JsonValueKind.Array, actual: document.RootElement.GetProperty("tip").ValueKind);
        Assert.Equal(expected: 0, actual: document.RootElement.GetProperty("tip").GetArrayLength());
        Assert.False(document.RootElement.TryGetProperty(propertyName: "value", value: out _));
    }

    [Fact]
    public void EpochColors_OnePerType_NotAlertRedOrAmber()
    {
        Assert.Equal(expected: "#1ed760", actual: EpochMarkerPalette.ColorFor("local_takeover"));
        Assert.Equal(expected: "#3b82f6", actual: EpochMarkerPalette.ColorFor("teacher_resumed"));
        Assert.Equal(expected: "#a855f7", actual: EpochMarkerPalette.ColorFor("teacher_changed"));
        Assert.Equal(expected: "#14b8a6", actual: EpochMarkerPalette.ColorFor("bge_changed"));
        Assert.Equal(expected: "#6366f1", actual: EpochMarkerPalette.ColorFor("feature_schema_changed"));
        Assert.Equal(expected: "#64748b", actual: EpochMarkerPalette.ColorFor("something_else"));
        Assert.NotEqual(expected: EpochMarkerPalette.AlertRed, actual: EpochMarkerPalette.LocalTakeover);
        Assert.NotEqual(expected: EpochMarkerPalette.AlertAmber, actual: EpochMarkerPalette.TeacherResumed);
        Assert.DoesNotContain(EpochMarkerPalette.Legend, entry => entry.Color is "#ef4444" or "#f59e0b");
    }

    [Fact]
    public void EpochSymbol_CodeIsCircle_NonCodeIsDiamond()
    {
        Assert.Equal(expected: "circle", actual: EpochMarkerPalette.SymbolFor("code"));
        Assert.Equal(expected: "diamond", actual: EpochMarkerPalette.SymbolFor("non-code"));
    }

    [Fact]
    public void EpochLegend_ListsFiveTypes_AndBothShapes()
    {
        var text = string.Join(separator: " ", values: EpochMarkerPalette.Legend.Select(entry => entry.Name))
                   + " " + EpochMarkerPalette.ShapeLegend;

        Assert.Contains(expectedSubstring: "Local grader took over", actualString: text, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "Teacher took charge again", actualString: text, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "Teacher model or prompt changed", actualString: text, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "Embedding model changed", actualString: text, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "Feature list changed", actualString: text, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "circle = code, diamond = non-code", actualString: text, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void EpochTooltip_HoverLines_IncludeUtcAndNoText()
    {
        var lines = EpochMarkerText.Lines(new EpochEventPlot(
            Id: 4,
            AtUtc: new DateTimeOffset(year: 2026, month: 10, day: 26, hour: 15, minute: 4, second: 5, offset: TimeSpan.Zero),
            Kind: "code",
            EpochType: "local_takeover",
            Cause: "takeover",
            ArtifactId: null,
            PreviousArtifactId: null,
            WindowSize: null,
            MeanMiss: null,
            PreviousTeacherModel: null,
            TeacherModel: null,
            PreviousPromptVersion: null,
            PromptVersion: null,
            RollbackMarkPresent: null,
            PreviousBgeModel: null,
            BgeModel: null,
            PreviousFeatureSchemaVersion: null,
            FeatureSchemaVersion: null));

        Assert.Equal(expected: "Time (UTC): 2026-10-26 15:04:05", actual: lines[0]);
        Assert.Equal(expected: EpochMarkerText.NoCause, actual: lines[^1]);
        var joined = string.Join(separator: "\n", values: lines);
        Assert.DoesNotContain(expectedSubstring: "prompt_text", actualString: joined, comparisonType: StringComparison.Ordinal);
        Assert.DoesNotContain(expectedSubstring: "response_text", actualString: joined, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "unknown", actualString: joined, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void EpochOverlay_OnAllEightCostKinds()
    {
        var report = BuildOptions();

        foreach (var kind in new[]
                 {
                     "DualDirectionalBars", "SteppedCumulativeArea", "RunawayArea", "SegmentedStepBars",
                     "CacheGradientLine", "ZonedLatencyLine", "ThresholdLine", "TokenEfficiencyLine"
                 })
        {
            Assert.True(report.GetProperty(kind).GetProperty("hasEpochs").GetBoolean(), kind);
        }

        Assert.False(report.GetProperty("GroupedBars").GetProperty("hasEpochs").GetBoolean());
        Assert.False(report.GetProperty("Donut").GetProperty("hasEpochs").GetBoolean());
        Assert.False(report.GetProperty("RemainingLine").GetProperty("hasEpochs").GetBoolean());
    }

    [Fact]
    public void EpochOverlay_NotOnContextBreachSeries()
    {
        var report = BuildOptions();
        var threshold = report.GetProperty("ThresholdLine");

        Assert.Equal(expected: "#ef4444", actual: threshold.GetProperty("breachColor").GetString());
        Assert.False(threshold.GetProperty("breachHasId").GetBoolean());
        Assert.Equal(expected: 1, actual: threshold.GetProperty("epochY").GetInt32());
    }

    private static JsonElement BuildOptions()
    {
        var script = Path.Combine(path1: Path.GetTempPath(), path2: "epoch-overlay-" + Guid.NewGuid().ToString("N") + ".cjs");
        File.WriteAllText(path: script, contents: """
            const fs = require('fs');
            const vm = require('vm');
            const src = fs.readFileSync(process.argv[2], 'utf8');
            const sandbox = { window: {}, console, echarts: { graphic: { LinearGradient: function () { return {}; } } } };
            vm.createContext(sandbox);
            vm.runInContext(src, sandbox);
            const build = sandbox.window.echartsInterop._buildOption;
            const epoch = { t: 1700000000000, id: 42, color: '#1ed760', symbol: 'circle', tip: ['Time (UTC): 2026-10-26 15:04:05'] };
            const point = { t: 5, value: 95, color: '#3b82f6', label: 'x', tip: [] };
            const model = {
              points: [point],
              efficiency: [{ t: 5, value: 2, gap: false, tip: ['2'] }],
              epochs: [epoch],
              unit: 'tok/$',
              threshold: 90,
              categories: [],
              series: [],
              slices: [],
              models: []
            };
            const kinds = ['DualDirectionalBars','SteppedCumulativeArea','RunawayArea','SegmentedStepBars','CacheGradientLine','ZonedLatencyLine','ThresholdLine','TokenEfficiencyLine','GroupedBars','Donut','RemainingLine'];
            const report = {};
            for (const kind of kinds) {
              const option = build(Object.assign({ kind }, model));
              const series = option.series || [];
              const epochs = series.find((s) => s.name === 'epochs');
              const breach = series.find((s) => s.type === 'effectScatter' && s.name !== 'epochs');
              report[kind] = {
                hasEpochs: !!epochs,
                epochY: epochs && epochs.data[0].value[1],
                breachColor: breach && breach.itemStyle && breach.itemStyle.color,
                breachHasId: !!(breach && breach.data.some((d) => d.id != null))
              };
            }
            process.stdout.write(JSON.stringify(report));
            """);

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "node",
                Arguments = $"\"{script}\" \"{FindInterop()}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            Assert.NotNull(process);
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, stderr + stdout);
            using var document = JsonDocument.Parse(stdout);
            return document.RootElement.Clone();
        }
        finally
        {
            File.Delete(script);
        }
    }

    private static string FindInterop()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "TotallyHotArcRouter.Gui.Web", "wwwroot", "js", "echarts-interop.js");
            if (File.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new InvalidOperationException("echarts-interop.js was not found above the test output directory.");
    }
}
