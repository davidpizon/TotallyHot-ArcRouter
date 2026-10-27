using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Tests;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Transcripts;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>
/// Issue #304 phase 9: token efficiency, epoch marks, and incomplete-grade counts. Recorded rows only.
/// No usage rollup, no live provider, and no embedding-model download.
/// </summary>
public sealed class GraderEpochPhase9Tests
{
    private static readonly DateTimeOffset Hour = new(year: 2026, month: 10, day: 26, hour: 10, minute: 0, second: 0, offset: TimeSpan.Zero);

    [Fact]
    public void Efficiency_BothViews_MatchTheWorkedExample()
    {
        var points = new TranscriptTokenCostPoint[]
        {
            Point(at: Hour, input: 100, output: 50, cost: 0.25m, id: 1),
            Point(at: Hour.AddMinutes(1), input: null, output: 10, cost: 0.10m, id: 2),
            Point(at: Hour.AddMinutes(2), input: 10, output: 0, cost: null, id: 3),
            Point(at: Hour.AddMinutes(3), input: 20, output: 0, cost: 0.10m, id: 4)
        };

        var bucket = Assert.Single(GraderEpochEfficiency.Bucket(points: points, width: "hour"));

        Assert.Equal(expected: 2, actual: bucket.KnownRows);
        Assert.Equal(expected: 2, actual: bucket.SkippedRows);
        Assert.Equal(expected: 170m / 0.35m, actual: Parse(bucket.TokensPerDollar));
        Assert.Equal(expected: 0.35m / 170m * 1000m, actual: Parse(bucket.DollarsPerThousandTokens));
    }

    [Fact]
    public void Efficiency_NullCost_IsAGap_InBothViews()
    {
        var bucket = Assert.Single(GraderEpochEfficiency.Bucket(
            points: [Point(at: Hour, input: 10, output: 10, cost: null, id: 1)], width: "hour"));

        Assert.Null(bucket.TokensPerDollar);
        Assert.Null(bucket.DollarsPerThousandTokens);
        Assert.Equal(expected: 0, actual: bucket.KnownRows);
        Assert.Equal(expected: 1, actual: bucket.SkippedRows);
    }

    [Fact]
    public void Efficiency_NullToken_IsAGap_InBothViews()
    {
        var points = new TranscriptTokenCostPoint[]
        {
            Point(at: Hour, input: null, output: 10, cost: 0.10m, id: 1),
            Point(at: Hour.AddMinutes(1), input: 10, output: null, cost: 0.10m, id: 2)
        };

        var bucket = Assert.Single(GraderEpochEfficiency.Bucket(points: points, width: "hour"));

        Assert.Null(bucket.TokensPerDollar);
        Assert.Null(bucket.DollarsPerThousandTokens);
        Assert.Equal(expected: 2, actual: bucket.SkippedRows);
    }

    [Fact]
    public void Efficiency_ZeroCost_IsAGap_InBothViews()
    {
        var bucket = Assert.Single(GraderEpochEfficiency.Bucket(
            points: [Point(at: Hour, input: 100, output: 0, cost: 0m, id: 1)], width: "hour"));

        Assert.Null(bucket.TokensPerDollar);
        Assert.Null(bucket.DollarsPerThousandTokens);
    }

    [Fact]
    public void Efficiency_ZeroTokens_IsAGap_InBothViews()
    {
        var bucket = Assert.Single(GraderEpochEfficiency.Bucket(
            points: [Point(at: Hour, input: 0, output: 0, cost: 0.10m, id: 1)], width: "hour"));

        Assert.Null(bucket.TokensPerDollar);
        Assert.Null(bucket.DollarsPerThousandTokens);
    }

    [Fact]
    public void Efficiency_NegativeCostAndTokens_AreGapsInBothViews()
    {
        var points = new TranscriptTokenCostPoint[]
        {
            Point(at: Hour, input: 10, output: 0, cost: -0.10m, id: 1),
            Point(at: Hour.AddMinutes(1), input: -5, output: 0, cost: 0.10m, id: 2)
        };

        var buckets = GraderEpochEfficiency.Bucket(points: points, width: "hour");

        Assert.All(buckets, bucket =>
        {
            Assert.Null(bucket.TokensPerDollar);
            Assert.Null(bucket.DollarsPerThousandTokens);
        });
    }

    [Fact]
    public void Efficiency_InteriorGap_FillsOnlyBetweenKnownBuckets()
    {
        var nine = Hour.AddHours(-1);
        var eleven = Hour.AddHours(1);
        var noon = Hour.AddHours(2);
        var one = Hour.AddHours(3);
        var points = new TranscriptTokenCostPoint[]
        {
            Point(at: nine, input: 1, output: 0, cost: null, id: 1),
            Point(at: Hour, input: 10, output: 0, cost: 0.10m, id: 2),
            Point(at: noon, input: 10, output: 0, cost: 0.10m, id: 3),
            Point(at: one, input: 1, output: 0, cost: null, id: 4)
        };

        var buckets = GraderEpochEfficiency.Bucket(points: points, width: "hour");

        Assert.Equal(
            expected: new[] { nine, Hour, eleven, noon, one },
            actual: buckets.Select(bucket => bucket.BucketStartUtc).ToArray());
        Assert.Null(buckets[2].TokensPerDollar);
        Assert.Null(buckets[2].DollarsPerThousandTokens);
        Assert.Equal(expected: 0, actual: buckets[2].KnownRows);
        Assert.Equal(expected: 0, actual: buckets[2].SkippedRows);
        Assert.DoesNotContain(buckets, bucket => bucket.BucketStartUtc == Hour.AddHours(-2));
    }

    [Fact]
    public void Efficiency_DoesNotReadUsageRollup()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var constructor in typeof(GraderEpochChartQuery).GetConstructors(flags))
        {
            Assert.DoesNotContain(
                constructor.GetParameters(),
                parameter => parameter.ParameterType.Name.Contains(value: "UsageRollup", comparisonType: StringComparison.Ordinal));
        }
    }

    [Fact]
    public void TokenCostQuery_SelectsFourColumns_NoText()
    {
        var sql = TranscriptTokenCostReader.SelectCommandText;
        Assert.Contains(expectedSubstring: "input_tokens", actualString: sql, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "output_tokens", actualString: sql, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "cost", actualString: sql, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "created_at_utc", actualString: sql, comparisonType: StringComparison.Ordinal);
        Assert.DoesNotContain(expectedSubstring: "prompt_text", actualString: sql, comparisonType: StringComparison.Ordinal);
        Assert.DoesNotContain(expectedSubstring: "response_text", actualString: sql, comparisonType: StringComparison.Ordinal);
        Assert.DoesNotContain(expectedSubstring: "ATTACH", actualString: sql, comparisonType: StringComparison.OrdinalIgnoreCase);

        using var files = new ChartFiles();
        InsertTranscript(database: files.Transcripts, at: Hour, input: 10, output: 5, cost: null, utility: 1, created: null);
        InsertTranscript(database: files.Transcripts, at: Hour.AddMinutes(1), input: 20, output: 0, cost: 0m, utility: 0, created: null);
        InsertTranscript(database: files.Transcripts, at: Hour, input: 1, output: 1, cost: 0.10m, utility: 0, created: "2026-10-26T10:30:00ZZZ");

        var points = new TranscriptTokenCostReader(files.Transcripts).List(from: Hour, to: Hour.AddHours(1));

        Assert.Equal(expected: 2, actual: points.Count);
        Assert.Null(points[0].Cost);
        Assert.Equal(expected: 0m, actual: points[1].Cost);
    }

    [Fact]
    public void Markers_AllFiveTypes_AndBothKinds()
    {
        using var files = new ChartFiles();
        var types = new[]
        {
            IGraderEpochEventStore.LocalTakeover,
            IGraderEpochEventStore.TeacherResumed,
            IGraderEpochEventStore.TeacherChanged,
            IGraderEpochEventStore.BgeChanged,
            IGraderEpochEventStore.FeatureSchemaChanged
        };
        var planned = new List<(DateTimeOffset At, string Kind, string Type)>();
        var index = 0;
        foreach (var type in types)
        {
            planned.Add((Hour.AddMinutes(index++), "code", type));
            planned.Add((Hour.AddMinutes(index++), "non-code", type));
        }

        files.Epochs.Execute(() =>
        {
            for (var i = planned.Count - 1; i >= 0; i--)
                files.Epochs.Insert(Marker(at: planned[i].At, kind: planned[i].Kind, type: planned[i].Type));
        });

        var snapshot = Read(files: files, from: Hour, to: Hour.AddHours(2));

        Assert.Equal(expected: 10, actual: snapshot.Markers.Count);
        Assert.Equal(
            expected: planned.Select(row => row.At.ToString(format: "O", formatProvider: CultureInfo.InvariantCulture)).ToArray(),
            actual: snapshot.Markers.Select(row => row.AtUtc).ToArray());
        Assert.Equal(expected: planned.Select(row => row.Kind).ToArray(), actual: snapshot.Markers.Select(row => row.Kind).ToArray());
    }

    [Fact]
    public void Markers_OutsideTheWindow_AreDropped()
    {
        using var files = new ChartFiles();
        var inside = Hour.AddMinutes(30);
        files.Epochs.Execute(() =>
        {
            files.Epochs.Insert(Marker(at: Hour.AddHours(-1), kind: "code", type: IGraderEpochEventStore.LocalTakeover));
            files.Epochs.Insert(Marker(at: inside, kind: "code", type: IGraderEpochEventStore.TeacherResumed));
            files.Epochs.Insert(Marker(at: Hour.AddHours(2), kind: "code", type: IGraderEpochEventStore.BgeChanged));
        });

        var snapshot = Read(files: files, from: Hour, to: Hour.AddHours(2));

        var marker = Assert.Single(snapshot.Markers);
        Assert.Equal(expected: inside.ToString(format: "O", formatProvider: CultureInfo.InvariantCulture), actual: marker.AtUtc);
    }

    [Fact]
    public void Markers_NullMeanMiss_StaysNull()
    {
        using var files = new ChartFiles();
        files.Epochs.Execute(() => files.Epochs.Insert(new GraderEpochEventRow
        {
            AtUtc = Hour.ToString(format: "O", formatProvider: CultureInfo.InvariantCulture),
            Kind = "non-code",
            EpochType = IGraderEpochEventStore.TeacherResumed,
            Cause = "spot_check_rollback"
        }));

        var marker = Assert.Single(Read(files: files, from: Hour, to: Hour.AddHours(1)).Markers);

        Assert.Null(marker.MeanMiss);
        Assert.Null(marker.WindowSize);
    }

    [Fact]
    public void Markers_SchemaVersionOne_IsNotNull()
    {
        using var files = new ChartFiles();
        files.Epochs.Execute(() => files.Epochs.Insert(new GraderEpochEventRow
        {
            AtUtc = Hour.ToString(format: "O", formatProvider: CultureInfo.InvariantCulture),
            Kind = "code",
            EpochType = IGraderEpochEventStore.FeatureSchemaChanged,
            Cause = "schema",
            FeatureSchemaVersion = 1
        }));

        var marker = Assert.Single(Read(files: files, from: Hour, to: Hour.AddHours(1)).Markers);

        Assert.Equal(expected: 1, actual: marker.FeatureSchemaVersion);
    }

    [Fact]
    public void Markers_RollbackMarkZero_IsZero()
    {
        using var files = new ChartFiles();
        files.Epochs.Execute(() =>
        {
            files.Epochs.Insert(new GraderEpochEventRow
            {
                AtUtc = Hour.ToString(format: "O", formatProvider: CultureInfo.InvariantCulture),
                Kind = "code",
                EpochType = IGraderEpochEventStore.TeacherChanged,
                Cause = "teacher",
                RollbackMarkPresent = 0
            });
            files.Epochs.Insert(new GraderEpochEventRow
            {
                AtUtc = Hour.AddMinutes(1).ToString(format: "O", formatProvider: CultureInfo.InvariantCulture),
                Kind = "code",
                EpochType = IGraderEpochEventStore.TeacherResumed,
                Cause = "spot_check_rollback"
            });
        });

        var markers = Read(files: files, from: Hour, to: Hour.AddHours(1)).Markers;

        Assert.Equal(expected: 0, actual: markers[0].RollbackMarkPresent);
        Assert.Null(markers[1].RollbackMarkPresent);
    }

    [Fact]
    public void IncompleteCount_NamedReasons_IncludeZero()
    {
        using var files = new ChartFiles();
        InsertReceipt(database: files.Memory, id: "complete", at: Hour, status: "Complete", reason: null, abstained: "");

        var counts = new IncompleteGradeCountQuery(files.Memory).Count(from: Hour, to: Hour.AddDays(1));

        Assert.Equal(expected: 0, actual: counts.IncompleteReceipts);
        Assert.Equal(expected: 8, actual: counts.ByReason.Count);
        Assert.Equal(expected: IncompleteReasons.QueueFull, actual: counts.ByReason[0].Reason);
        Assert.Equal(expected: 0, actual: counts.ByReason[0].Count);
        Assert.Equal(expected: IncompleteReasons.LoopUnlinked, actual: counts.ByReason[7].Reason);
        Assert.DoesNotContain(counts.ByReason, row => row.Reason == IncompleteGradeCountQuery.NoReasonStored);
    }

    [Fact]
    public void IncompleteCount_AbstainedToken_IsSeparate()
    {
        using var files = new ChartFiles();
        InsertReceipt(
            database: files.Memory, id: "one", at: Hour, status: "Incomplete",
            reason: IncompleteReasons.NoJudge, abstained: "judge:judge-join-timeout");

        var counts = new IncompleteGradeCountQuery(files.Memory).Count(from: Hour, to: Hour.AddDays(1));

        Assert.Equal(expected: 1, actual: counts.IncompleteReceipts);
        Assert.Equal(expected: 1, actual: counts.ByReason.Single(row => row.Reason == IncompleteReasons.NoJudge).Count);
        Assert.Equal(expected: 0, actual: counts.ByReason.Single(row => row.Reason == IncompleteReasons.QueueFull).Count);
        var token = Assert.Single(counts.Abstained);
        Assert.Equal(expected: "judge:judge-join-timeout", actual: token.Token);
        Assert.Equal(expected: 1, actual: token.Count);
    }

    [Fact]
    public void IncompleteCount_EmptyReason_OnlyWhenPresent()
    {
        using var files = new ChartFiles();
        InsertReceipt(
            database: files.Memory, id: "named", at: Hour, status: "Incomplete",
            reason: IncompleteReasons.Empty, abstained: "");
        var named = new IncompleteGradeCountQuery(files.Memory).Count(from: Hour, to: Hour.AddDays(1));
        Assert.DoesNotContain(named.ByReason, row => row.Reason == IncompleteGradeCountQuery.NoReasonStored);

        InsertReceipt(database: files.Memory, id: "blank", at: Hour.AddMinutes(1), status: "Incomplete", reason: null, abstained: " ,plain");
        var withBlank = new IncompleteGradeCountQuery(files.Memory).Count(from: Hour, to: Hour.AddDays(1));

        Assert.Equal(expected: 2, actual: withBlank.IncompleteReceipts);
        Assert.Equal(expected: 1, actual: withBlank.ByReason.Single(row => row.Reason == IncompleteGradeCountQuery.NoReasonStored).Count);
        Assert.Equal(expected: 1, actual: withBlank.Abstained.Single(row => row.Token == "plain").Count);
        Assert.DoesNotContain(withBlank.Abstained, row => row.Token.Length == 0);
    }

    [Fact]
    public void ChartQuery_ReadsTheTwoFilesSeparately()
    {
        using var files = new ChartFiles();
        InsertTranscript(database: files.Transcripts, at: Hour, input: 10, output: 0, cost: 0.10m, utility: 0, created: null);
        var counting = new CountingEpochStore(files.Epochs);
        counting.Execute(() => counting.Insert(Marker(at: Hour, kind: "code", type: IGraderEpochEventStore.LocalTakeover)));
        var query = new GraderEpochChartQuery(
            epochEvents: counting, transcripts: files.Transcripts, gradeReceipts: files.Memory, logger: null);

        var result = query.Get(from: Hour, to: Hour.AddHours(1), width: "hour");

        Assert.True(result.Success);
        Assert.True(counting.ListForPlotCalls > 0);
        Assert.NotEqual(expected: files.Transcripts.DatabasePath, actual: files.Memory.DatabasePath);
        Assert.NotNull(result.Value!.Points[0].TokensPerDollar);
        Assert.Single(result.Value.Markers);
        Assert.DoesNotContain(expectedSubstring: "ATTACH", actualString: TranscriptTokenCostReader.SelectCommandText, comparisonType: StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(expectedSubstring: "ATTACH", actualString: IncompleteGradeCountQuery.SelectCommandText, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    private static GraderEpochChartSnapshot Read(ChartFiles files, DateTimeOffset from, DateTimeOffset to)
    {
        var query = new GraderEpochChartQuery(
            epochEvents: files.Epochs, transcripts: files.Transcripts, gradeReceipts: files.Memory, logger: null);
        var result = query.Get(from: from, to: to, width: "hour");
        Assert.True(result.Success);
        return result.Value!;
    }

    private static decimal Parse(string? text)
    {
        Assert.NotNull(text);
        Assert.DoesNotContain(expectedSubstring: "$", actualString: text, comparisonType: StringComparison.Ordinal);
        return decimal.Parse(s: text, provider: CultureInfo.InvariantCulture);
    }

    private static TranscriptTokenCostPoint Point(DateTimeOffset at, int? input, int? output, decimal? cost, long id)
    {
        return new TranscriptTokenCostPoint(Id: id, CreatedAtUtc: at, InputTokens: input, OutputTokens: output, Cost: cost);
    }

    private static GraderEpochEventRow Marker(DateTimeOffset at, string kind, string type)
    {
        return new GraderEpochEventRow
        {
            AtUtc = at.ToString(format: "O", formatProvider: CultureInfo.InvariantCulture),
            Kind = kind,
            EpochType = type,
            Cause = "fixture"
        };
    }

    private static void InsertTranscript(
        TranscriptDatabase database, DateTimeOffset at, int? input, int? output, decimal? cost, int utility, string? created)
    {
        database.EnsureCreated();
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO request_transcripts (
                                  correlation_id, created_at_utc, requested_model, routed_model,
                                  is_utility, is_exploratory, propensity, input_tokens, output_tokens, cost)
                              VALUES ($id, $at, 'm', 'm', $utility, 0, 0, $input, $output, $cost);
                              """;
        command.Parameters.AddWithValue(parameterName: "$id", value: Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue(parameterName: "$at", value: created ?? at.ToString(format: "O", formatProvider: CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(parameterName: "$utility", value: utility);
        command.Parameters.AddWithValue(parameterName: "$input", value: (object?)input ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$output", value: (object?)output ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$cost", value: (object?)cost ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void InsertReceipt(
        RouterMemoryDatabase database, string id, DateTimeOffset at, string status, string? reason, string? abstained)
    {
        database.EnsureCreated();
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO grade_receipts (
                                  correlation_id, created_at_utc, dimension, model, grade_status, unified_score,
                                  scorer_version, weights_version, incomplete_reason, abstained_graders)
                              VALUES ($id, $at, 'chat', 'm', $status, 0, '3.0', '1', $reason, $abstained);
                              """;
        command.Parameters.AddWithValue(parameterName: "$id", value: id);
        command.Parameters.AddWithValue(parameterName: "$at", value: at.ToString(format: "O", formatProvider: CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(parameterName: "$status", value: status);
        command.Parameters.AddWithValue(parameterName: "$reason", value: (object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$abstained", value: (object?)abstained ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private sealed class CountingEpochStore(SqliteGraderEpochEventStore inner) : IGraderEpochEventStore
    {
        public int ListForPlotCalls { get; private set; }

        public void Insert(GraderEpochEventRow row)
        {
            inner.Insert(row);
        }

        public void Execute(Action work)
        {
            inner.Execute(work);
        }

        public IReadOnlyList<GraderEpochEventRow> ListForPlot()
        {
            ListForPlotCalls++;
            return inner.ListForPlot();
        }

        public GraderEpochEventRow? LatestAuthority(string kind)
        {
            return inner.LatestAuthority(kind);
        }

        public GraderEpochEventRow? LatestOfType(string kind, string epochType)
        {
            return inner.LatestOfType(kind: kind, epochType: epochType);
        }
    }

    private sealed class ChartFiles : IDisposable
    {
        private readonly string _directory = Path.Combine(path1: TestScratchDirectory.RunRoot, path2: Guid.NewGuid().ToString("N"));

        public ChartFiles()
        {
            Directory.CreateDirectory(_directory);
            Transcripts = new TranscriptDatabase(Options.Create(new StorageOptions
            {
                TranscriptDatabasePath = Path.Combine(path1: _directory, path2: "transcripts.db")
            }));
            Memory = new RouterMemoryDatabase(Options.Create(new RoutingOptions
            {
                EmbeddingMemoryDatabasePath = Path.Combine(path1: _directory, path2: "router.db")
            }));
            Memory.EnsureCreated();
            Epochs = new SqliteGraderEpochEventStore(Memory);
        }

        public TranscriptDatabase Transcripts { get; }

        public RouterMemoryDatabase Memory { get; }

        public SqliteGraderEpochEventStore Epochs { get; }

        public void Dispose()
        {
            Clear(Transcripts.DatabasePath, () => Transcripts.OpenConnection());
            Clear(Memory.DatabasePath, () => Memory.OpenConnection());
            try
            {
                if (Directory.Exists(_directory))
                    Directory.Delete(path: _directory, recursive: true);
            }
            catch (IOException)
            {
                // A locked sidecar on a busy box is not a test failure.
            }
        }

        private static void Clear(string path, Func<SqliteConnection> open)
        {
            if (!File.Exists(path))
                return;

            try
            {
                using var connection = open();
                SqliteConnection.ClearPool(connection);
            }
            catch (SqliteException)
            {
                // Best-effort cleanup.
            }
        }
    }
}
