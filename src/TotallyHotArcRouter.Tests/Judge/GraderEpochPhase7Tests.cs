using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Hosting;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Tests.TestSupport;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Phase 7 grader epoch (issue #304): the shadow head, its gate, and the reservoir.
/// Recorded fixtures only. No live provider, no BGE download, and no ONNX session.
/// </summary>
public class GraderEpochPhase7Tests
{
    private const int Dimension = 2;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void Defaults_EmbeddingDimensionIs1024_AndReservoirCapacityIs5000()
    {
        Assert.Equal(1024, new EmbeddingOptions().EmbeddingDimension);
        Assert.Equal(5000, new GraderEpochOptions().ReservoirCapacity);
    }

    [Fact]
    public async Task GateFail_BelowBar_DoesNotAcceptOrRewriteGrades()
    {
        using var world = World.Create(World.Small());
        await world.SeedLinear(count: 24, holdout: 6, invertHoldout: true);
        var grades = world.ScoreSnapshot();
        var logreg = world.LogRegBytes();

        await world.Tick();

        Assert.Null(world.Store.TryReadAccepted(GraderEpochKinds.Code));
        Assert.Equal(grades, world.ScoreSnapshot());
        Assert.Equal(logreg, world.LogRegBytes());
        Assert.Contains(world.ServiceLog.Lines, line => line.Contains("left unchanged", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GatePass_AcceptsShadowArtifact_KeepsPreviousAsRollbackCopy()
    {
        using var world = World.Create(World.Small());
        await world.SeedLinear(count: 24, holdout: 6);
        var previous = world.AcceptLayout(weightOnResponse0: 0f, bias: 0f);
        var grades = world.ScoreSnapshot();
        var logreg = world.LogRegBytes();
        var weights = world.Quality.CurrentValue.WeightsVersion;

        await world.Tick();

        var accepted = world.Store.TryReadAccepted(GraderEpochKinds.Code);
        Assert.NotNull(accepted);
        Assert.Equal(GraderEpochArtifact.AcceptedShadowStatus, accepted.Status);
        Assert.Equal(previous, accepted.PreviousArtifactId);
        Assert.True(File.Exists(world.ArtifactPath(GraderEpochKinds.Code, previous)));
        Assert.Equal(grades, world.ScoreSnapshot());
        Assert.Equal(logreg, world.LogRegBytes());
        Assert.Equal(weights, world.Quality.CurrentValue.WeightsVersion);
        Assert.Contains(world.ServiceLog.Lines, line =>
            line.Contains("accepted, shadow", StringComparison.Ordinal) &&
            line.Contains("bootstrap low ", StringComparison.Ordinal) &&
            !line.Contains("bootstrap low null", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GatePass_FirstArtifact_PreviousIdIsNull()
    {
        using var world = World.Create(World.Small());
        await world.SeedLinear(count: 24, holdout: 6);

        await world.Tick();

        var accepted = world.Store.TryReadAccepted(GraderEpochKinds.Code);
        Assert.NotNull(accepted);
        Assert.Null(accepted.PreviousArtifactId);
        Assert.Equal(GraderEpochArtifact.AcceptedShadowStatus, accepted.Status);
        Assert.Contains(world.ServiceLog.Lines, line => line.Contains("bootstrap low null", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Holdout_TakesFiftyNewestPerKind_TieBreakCorrelationId()
    {
        using var world = World.Create(World.Small());
        var origin = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 49; i++)
        {
            await world.Add(new Row
            {
                Id = $"code-{i:D4}",
                Kind = GraderEpochKinds.Code,
                CreatedAt = origin.AddMinutes(10 + i)
            });
        }

        await world.Add(new Row { Id = "tie-b", Kind = GraderEpochKinds.Code, CreatedAt = origin.AddMinutes(1) });
        await world.Add(new Row { Id = "tie-a", Kind = GraderEpochKinds.Code, CreatedAt = origin.AddMinutes(1) });
        await world.Add(new Row { Id = "nc-0001", Kind = GraderEpochKinds.NonCode, CreatedAt = origin.AddMinutes(1000) });

        var code = await world.Query.LoadForKindAsync(GraderEpochKinds.Code, CancellationToken.None);
        var split = GraderEpochFit.Split(code, holdoutSize: 50);

        Assert.Equal(50, split.Holdout.Count);
        Assert.Contains(split.Holdout, label => label.CorrelationId == "tie-a");
        Assert.DoesNotContain(split.Holdout, label => label.CorrelationId == "tie-b");
        Assert.DoesNotContain(split.Holdout, label => label.CorrelationId.StartsWith("nc-", StringComparison.Ordinal));
        var nonCode = await world.Query.LoadForKindAsync(GraderEpochKinds.NonCode, CancellationToken.None);
        Assert.DoesNotContain(GraderEpochFit.Split(nonCode, 50).Holdout,
            label => label.CorrelationId.StartsWith("code-", StringComparison.Ordinal) ||
                     label.CorrelationId.StartsWith("tie-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Holdout_ExcludesSessionSiblingsFromTraining()
    {
        using var world = World.Create(World.Small());
        var origin = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        await world.Add(new Row { Id = "train", Kind = GraderEpochKinds.Code, CreatedAt = origin, SessionId = "sess-c" });
        await world.Add(new Row
        {
            Id = "sibling", Kind = GraderEpochKinds.Code, CreatedAt = origin.AddMinutes(1), SessionId = "sess-a"
        });
        await world.Add(new Row
        {
            Id = "hold-b", Kind = GraderEpochKinds.Code, CreatedAt = origin.AddMinutes(2), SessionId = "sess-b"
        });
        await world.Add(new Row
        {
            Id = "hold-a", Kind = GraderEpochKinds.Code, CreatedAt = origin.AddMinutes(3), SessionId = "sess-a"
        });

        var split = GraderEpochFit.Split(
            await world.Query.LoadForKindAsync(GraderEpochKinds.Code, CancellationToken.None), holdoutSize: 2);

        Assert.Equal(["hold-a", "hold-b"], split.Holdout.Select(label => label.CorrelationId).ToArray());
        Assert.Equal(["train"], split.Training.Select(label => label.CorrelationId).ToArray());
        Assert.Equal(1, split.SessionExcluded);
        Assert.DoesNotContain(split.Holdout, label => label.CorrelationId == "sibling");
    }

    [Fact]
    public async Task Refit_MatchingArtifact_StillRequiresTwoHundredAndFifty()
    {
        using (var below = World.Create(new GraderEpochOptions()))
        {
            var accepted = below.AcceptLayout(weightOnResponse0: 0f, bias: 0f);
            await below.SeedLinear(count: 199, holdout: 50);
            await below.Tick();
            Assert.Equal(accepted, below.Store.TryReadAccepted(GraderEpochKinds.Code)?.ArtifactId);
            Assert.Contains(below.ServiceLog.Lines, line =>
                line.Contains("eligible labels 199", StringComparison.Ordinal));
        }

        using var shortHoldout = World.Create(new GraderEpochOptions());
        var pointer = shortHoldout.AcceptLayout(weightOnResponse0: 0f, bias: 0f);
        await shortHoldout.SeedLinear(count: 200, holdout: 50, unparseable: 160);
        await shortHoldout.Tick();
        Assert.Equal(pointer, shortHoldout.Store.TryReadAccepted(GraderEpochKinds.Code)?.ArtifactId);
        Assert.Contains(shortHoldout.ServiceLog.Lines, line =>
            line.Contains("Eligible labels 200", StringComparison.Ordinal) &&
            line.Contains("shorter than", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SessionExclusion_BelowMinimumTrainingRows_SkipsFit()
    {
        using (var skipped = World.Create(new GraderEpochOptions()))
        {
            await skipped.SeedLinear(count: 200, holdout: 50, sharedHoldoutSession: 1);
            await skipped.Tick();
            Assert.Null(skipped.Store.TryReadAccepted(GraderEpochKinds.Code));
            Assert.Contains(skipped.ServiceLog.Lines, line =>
                line.Contains("training rows 149", StringComparison.Ordinal) &&
                line.Contains("holdout 50", StringComparison.Ordinal));
        }

        using var enough = World.Create(new GraderEpochOptions());
        await enough.SeedLinear(count: 200, holdout: 50);
        await enough.Tick();
        Assert.Contains(enough.ServiceLog.Lines, line =>
            line.Contains("length correlation for kind code", StringComparison.Ordinal));
        Assert.DoesNotContain(enough.ServiceLog.Lines, line =>
            line.Contains("training rows 150 are below", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Holdout_ZeroVariance_SpearmanNull_GateFails()
    {
        using var world = World.Create(World.Small());
        await world.SeedLinear(count: 24, holdout: 6, constantHoldout: true);

        await world.Tick();

        Assert.Null(world.Store.TryReadAccepted(GraderEpochKinds.Code));
        var failed = world.ReadFailed(GraderEpochKinds.Code).Single();
        Assert.Null(failed.Spearman);
        Assert.Null(failed.Status);
        Assert.Contains(world.ServiceLog.Lines, line =>
            line.Contains("Spearman null", StringComparison.Ordinal) &&
            line.Contains("left unchanged", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bootstrap_NotRun_WhenNoComparableLiveArtifact()
    {
        using var world = World.Create(World.Small());
        await world.SeedLinear(count: 24, holdout: 6);
        world.AcceptLayout(weightOnResponse0: 0f, bias: 0f, teacherModel: "retired-teacher");

        var accepted = await RequireCodeArtifact(world, "No accepted artifact after retiring a non-comparable teacher.");
        Assert.Null(accepted.PreviousArtifactId);
        Assert.Null(accepted.BootstrapLow);
        Assert.Null(accepted.BootstrapHigh);
        Assert.Contains(world.ServiceLog.Lines, line =>
            line.Contains("teacher model", StringComparison.Ordinal) &&
            line.Contains("does not match", StringComparison.Ordinal));
        Assert.Contains(world.ServiceLog.Lines, line => line.Contains("bootstrap low null", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bootstrap_ReplacesOnlyWhenLowerBoundGreaterThanZero()
    {
        // SDCA does not repeat its weights. Some draws lose to a model that always
        // predicts 0, so that draw never replaces. Others clear the rank bar and still
        // miss the default 0.10 error bar, so the bound is never computed. This test is
        // about the bound. It keeps the first draw that shows each outcome.
        const int draws = 8;
        World? replaces = null;
        string? bad = null;
        GraderEpochArtifact? accepted = null;
        try
        {
            for (var draw = 0; draw < draws; draw++)
            {
                replaces?.Dispose();
                replaces = World.Create(BootstrapComparison());
                await replaces.SeedLinear(count: 24, holdout: 6);
                bad = replaces.AcceptLayout(weightOnResponse0: 0f, bias: 0f);
                await replaces.Tick();
                accepted = replaces.Store.TryReadAccepted(GraderEpochKinds.Code);
                if (accepted?.PreviousArtifactId == bad && accepted.BootstrapLow > 0)
                    break;
            }

            Assert.True(accepted?.PreviousArtifactId == bad && accepted?.BootstrapLow > 0,
                string.Join(Environment.NewLine, replaces?.ServiceLog.Lines ?? []));
            Assert.NotNull(accepted);
            Assert.NotNull(accepted.BootstrapHigh);
            Assert.Contains(replaces!.ServiceLog.Lines, line =>
                line.Contains("bootstrap low ", StringComparison.Ordinal) &&
                line.Contains("bootstrap high ", StringComparison.Ordinal) &&
                !line.Contains("bootstrap low null", StringComparison.Ordinal));
        }
        finally
        {
            replaces?.Dispose();
        }

        World? stays = null;
        try
        {
            string? perfect = null;
            GraderEpochArtifact? failed = null;
            for (var draw = 0; draw < draws; draw++)
            {
                stays?.Dispose();
                stays = World.Create(BootstrapComparison());
                await stays.SeedLinear(count: 24, holdout: 6);
                perfect = stays.AcceptLayout(weightOnResponse0: 1f, bias: 0f);
                await stays.Tick();
                failed = stays.ReadFailed(GraderEpochKinds.Code).SingleOrDefault();
                if (stays.Store.TryReadAccepted(GraderEpochKinds.Code)?.ArtifactId == perfect &&
                    failed?.BootstrapLow is not null)
                    break;
            }

            Assert.Equal(perfect, stays!.Store.TryReadAccepted(GraderEpochKinds.Code)?.ArtifactId);
            Assert.NotNull(failed);
            Assert.NotNull(failed.BootstrapLow);
            Assert.True(failed.BootstrapLow <= 0);
            var stayedOnTheBound = stays.ServiceLog.Lines.Any(line =>
                line.Contains("left unchanged", StringComparison.Ordinal) &&
                line.Contains("bootstrap low ", StringComparison.Ordinal) &&
                !line.Contains("bootstrap low null", StringComparison.Ordinal));
            Assert.True(stayedOnTheBound, string.Join(Environment.NewLine, stays.ServiceLog.Lines));
        }
        finally
        {
            stays?.Dispose();
        }
    }

    /// <summary>
    /// The small epoch, with the holdout mean-absolute-error bar opened to 1.
    /// A score clamped to [0, 1] cannot miss by more than that, so a draw that clears
    /// the Spearman bar always computes a bootstrap bound. The default 0.10 bar skips
    /// the bound on some SDCA draws of this fixture.
    /// </summary>
    private static GraderEpochOptions BootstrapComparison() => new()
    {
        Enabled = true,
        MinimumLabels = 24,
        HoldoutSize = 6,
        MinimumTrainingRows = 12,
        ReservoirCapacity = 30,
        MaxHoldoutMae = 1
    };

    [Fact]
    public async Task Reservoir_BuiltFromTrainingRowsOnly_ExcludesHoldoutAndMissingVectors()
    {
        using var world = World.Create(new GraderEpochOptions
        {
            MinimumLabels = 6, HoldoutSize = 1, MinimumTrainingRows = 1, ReservoirCapacity = 10
        });
        var origin = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        float[] usable = [0.6f, 0.8f];
        await world.Add(new Row { Id = "t-usable", Kind = GraderEpochKinds.Code, CreatedAt = origin, Question = usable });
        await world.Add(new Row
        {
            Id = "t-flag1", Kind = GraderEpochKinds.Code, CreatedAt = origin.AddMinutes(1),
            Question = usable, QuestionFlag = true
        });
        await world.Add(new Row { Id = "t-nomem", Kind = GraderEpochKinds.Code, CreatedAt = origin.AddMinutes(2) });
        await world.Add(new Row
        {
            Id = "t-nullmodel", Kind = GraderEpochKinds.Code, CreatedAt = origin.AddMinutes(3),
            Question = usable, QuestionModelNull = true
        });
        await world.Add(new Row
        {
            Id = "t-othermodel", Kind = GraderEpochKinds.Code, CreatedAt = origin.AddMinutes(4),
            Question = usable, QuestionModel = "other-bge"
        });
        await world.Add(new Row
        {
            Id = "h-usable", Kind = GraderEpochKinds.Code, CreatedAt = origin.AddMinutes(5), Question = usable
        });

        await world.Tick();

        var artifact = world.ReadWritten(GraderEpochKinds.Code);
        Assert.Equal(["t-usable"], world.ReservoirIds(GraderEpochKinds.Code, artifact.ArtifactId));
    }

    [Fact]
    public async Task Reservoir_SealedWithVectorKey_NotPlaintext()
    {
        using var world = World.Create(new GraderEpochOptions
        {
            MinimumLabels = 4, HoldoutSize = 1, MinimumTrainingRows = 1, ReservoirCapacity = 10
        });
        float[] question = [0.6f, 0.8f];
        var origin = new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);
        await world.Add(new Row { Id = "sealed-train", Kind = GraderEpochKinds.Code, CreatedAt = origin, Question = question });
        await world.Add(new Row
        {
            Id = "sealed-hold", Kind = GraderEpochKinds.Code, CreatedAt = origin.AddMinutes(1), Question = question
        });
        await world.Add(new Row { Id = "sealed-extra-a", Kind = GraderEpochKinds.Code, CreatedAt = origin.AddMinutes(2) });
        await world.Add(new Row { Id = "sealed-extra-b", Kind = GraderEpochKinds.Code, CreatedAt = origin.AddMinutes(3) });

        await world.Tick();

        var artifact = world.ReadWritten(GraderEpochKinds.Code);
        var file = world.ReservoirBytes(GraderEpochKinds.Code, artifact.ArtifactId);
        Assert.True(file.AsSpan(0, 4).SequenceEqual("THGR"u8));
        var plain = new byte[question.Length * sizeof(float)];
        Buffer.BlockCopy(question, 0, plain, 0, plain.Length);
        Assert.False(file.AsSpan().IndexOf(plain) >= 0);
        Assert.True(ResponseVectorSeal.TryOpenReservoir(key: world.Keys.GetOrCreate(), artifactId: artifact.ArtifactId,
            correlationId: "sealed-train", modelId: world.Bge, blob: ReservoirBlob(file, "sealed-train"),
            vector: out var opened));
        Assert.Equal(question, opened);
        var responseBlob = world.Vectors.ReadSealed("sealed-train");
        Assert.NotNull(responseBlob);
        Assert.False(ResponseVectorSeal.TryOpenReservoir(key: world.Keys.GetOrCreate(), artifactId: artifact.ArtifactId,
            correlationId: "sealed-train", modelId: world.Bge, blob: responseBlob, vector: out _));
    }

    [Fact]
    public async Task Reservoir_RebuildDropsDeletedSession()
    {
        using var world = World.Create(new GraderEpochOptions
        {
            MinimumLabels = 6, HoldoutSize = 2, MinimumTrainingRows = 3, ReservoirCapacity = 20
        });
        await world.SeedLinear(count: 8, holdout: 2, question: [0.6f, 0.8f]);
        await world.Tick();
        var first = world.Store.TryReadAccepted(GraderEpochKinds.Code);
        Assert.NotNull(first);
        Assert.Contains("c-0000", world.ReservoirIds(GraderEpochKinds.Code, first.ArtifactId));

        world.DeleteCorrelation("c-0000");
        world.Store.RollbackToTeacher(GraderEpochKinds.Code);
        await world.Tick();

        var second = world.Store.TryReadAccepted(GraderEpochKinds.Code);
        Assert.NotNull(second);
        Assert.NotEqual(first.ArtifactId, second.ArtifactId);
        Assert.DoesNotContain("c-0000", world.ReservoirIds(GraderEpochKinds.Code, second.ArtifactId));
        Assert.Contains("c-0000", world.ReservoirIds(GraderEpochKinds.Code, first.ArtifactId));
    }

    [Fact]
    public async Task CosineFloor_UsesTheReservoirLiveRowsSee()
    {
        using (var populated = World.Create(new GraderEpochOptions
        {
            MinimumLabels = 4, HoldoutSize = 2, MinimumTrainingRows = 1, ReservoirCapacity = 10
        }))
        {
            var origin = new DateTimeOffset(2026, 3, 3, 0, 0, 0, TimeSpan.Zero);
            float[] vector = [0.6f, 0.8f];
            await populated.Add(new Row
            {
                Id = "floor-train", Kind = GraderEpochKinds.Code, CreatedAt = origin, Question = vector
            });
            await populated.Add(new Row { Id = "floor-pad", Kind = GraderEpochKinds.Code, CreatedAt = origin.AddMinutes(1) });
            await populated.Add(new Row
            {
                Id = "floor-hold", Kind = GraderEpochKinds.Code, CreatedAt = origin.AddMinutes(2), Question = vector
            });
            await populated.Add(new Row
            {
                Id = "floor-missing", Kind = GraderEpochKinds.Code, CreatedAt = origin.AddMinutes(3)
            });

            await populated.Tick();

            var artifact = populated.ReadWritten(GraderEpochKinds.Code);
            Assert.Equal(1, artifact.CosineFloor);
            var records = GraderEpochReservoir.Read(populated.ReservoirBytes(GraderEpochKinds.Code, artifact.ArtifactId));
            Assert.Single(records);
            Assert.True(ResponseVectorSeal.TryOpenReservoir(key: populated.Keys.GetOrCreate(),
                artifactId: artifact.ArtifactId, correlationId: records[0].CorrelationId, modelId: populated.Bge,
                blob: records[0].Blob, vector: out var neighbor));
            Assert.Equal(vector, neighbor);
        }

        using var empty = World.Create(new GraderEpochOptions
        {
            MinimumLabels = 4, HoldoutSize = 1, MinimumTrainingRows = 2, ReservoirCapacity = 10
        });
        var start = new DateTimeOffset(2026, 3, 4, 0, 0, 0, TimeSpan.Zero);
        await empty.Add(new Row
        {
            Id = "empty-train-a", Kind = GraderEpochKinds.Code, CreatedAt = start, QuestionFlag = true,
            Question = [0.6f, 0.8f]
        });
        await empty.Add(new Row
        {
            Id = "empty-train-b", Kind = GraderEpochKinds.Code, CreatedAt = start.AddMinutes(1), QuestionFlag = true,
            Question = [0.6f, 0.8f]
        });
        await empty.Add(new Row { Id = "empty-pad", Kind = GraderEpochKinds.Code, CreatedAt = start.AddMinutes(2) });
        await empty.Add(new Row
        {
            Id = "empty-hold", Kind = GraderEpochKinds.Code, CreatedAt = start.AddMinutes(3),
            Question = [0.6f, 0.8f]
        });
        await empty.Tick();
        Assert.Null(empty.ReadWritten(GraderEpochKinds.Code).CosineFloor);
    }

    [Fact]
    public async Task Rollback_FirstArtifact_NullPreviousId_ReturnsToTeacher()
    {
        using var world = World.Create(World.Small());
        await world.SeedLinear(count: 4, holdout: 1);
        var id = world.AcceptLayout(weightOnResponse0: 1f, bias: 0f);
        Assert.Null(world.Store.TryReadAccepted(GraderEpochKinds.Code)?.PreviousArtifactId);

        world.Store.RollbackToTeacher(GraderEpochKinds.Code);

        Assert.Null(world.Store.TryReadAccepted(GraderEpochKinds.Code));
        Assert.True(File.Exists(world.ArtifactPath(GraderEpochKinds.Code, id)));
        Assert.False(File.Exists(world.PointerPath(GraderEpochKinds.Code)));
        var result = await world.Shadow("c-0003");
        Assert.Null(result.LocalScore);
        Assert.Contains(world.StoreLog.Lines, line =>
            line.Contains("previous artifact null", StringComparison.Ordinal) &&
            line.Contains("Shadow scores stay off", StringComparison.Ordinal));
    }

    [Fact]
    public void Rollback_KeepsPreviousFile_AndDoesNotPromoteIt()
    {
        using var world = World.Create(World.Small());
        var first = world.AcceptLayout(weightOnResponse0: 0f, bias: 0f);
        var second = world.AcceptLayout(weightOnResponse0: 1f, bias: 0f, previousArtifactId: first);

        world.Store.RollbackToTeacher(GraderEpochKinds.Code);

        Assert.Null(world.Store.TryReadAccepted(GraderEpochKinds.Code));
        Assert.True(File.Exists(world.ArtifactPath(GraderEpochKinds.Code, first)));
        Assert.True(File.Exists(world.ArtifactPath(GraderEpochKinds.Code, second)));
        Assert.False(File.Exists(world.PointerPath(GraderEpochKinds.Code)));
    }

    [Fact]
    public async Task ShadowScore_WritesLocalScoreBesideJudgeScore_WhenArtifactMatches()
    {
        using var world = World.Create(World.Small());
        await world.Add(new Row
        {
            Id = "shadow-match",
            Kind = GraderEpochKinds.Code,
            CreatedAt = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
            Response = [0.5f, 0.25f],
            Judge = 0.7,
            Question = [0.6f, 0.8f],
            JudgeScoredMemory = true
        });
        var artifact = world.AcceptLayout(weightOnResponse0: 1f, bias: 0f, cosineFloor: 0.2,
            reservoir: [("shadow-match", [0.6f, 0.8f])]);
        var judgeRows = world.JudgeRowCount("shadow-match");

        var result = await world.Shadow("shadow-match", judgeScore: 0.7);

        Assert.Equal(0.5, result.LocalScore);
        Assert.Equal(artifact, result.LocalGraderVersion);
        Assert.Equal(GradeSources.Teacher, result.GradeSource);
        Assert.Equal(0.7, result.JudgeScore);
        Assert.Equal(judgeRows, world.JudgeRowCount("shadow-match"));
        Assert.Equal(1, world.JudgeScoredFlag("shadow-match"));
        Assert.DoesNotContain(typeof(ShadowLocalScoreObserver).GetConstructors().Single().GetParameters(),
            parameter => parameter.ParameterType.Name is "IEmbeddingClient" or "PendingValueCache`1");
    }

    [Fact]
    public async Task ShadowScore_LocalScoreNull_WhenNoArtifactOrArtifactMismatch()
    {
        using var world = World.Create(World.Small());
        await world.Add(new Row { Id = "shadow-miss", Kind = GraderEpochKinds.Code, Response = [0.5f, 0.25f] });

        Assert.Null((await world.Shadow("shadow-miss")).LocalScore);

        world.AcceptLayout(weightOnResponse0: 1f, bias: 0f, teacherModel: "other-teacher");
        Assert.Null((await world.Shadow("shadow-miss")).LocalScore);

        world.AcceptLayout(weightOnResponse0: 1f, bias: 0f, promptVersion: "other-prompt");
        Assert.Null((await world.Shadow("shadow-miss")).LocalScore);

        world.AcceptLayout(weightOnResponse0: 1f, bias: 0f, bgeModelId: "other-bge");
        Assert.Null((await world.Shadow("shadow-miss")).LocalScore);
    }

    [Fact]
    public async Task ShadowScore_MissingQuestionVector_UsesIndicator_NotZeroVector()
    {
        using var world = World.Create(World.Small());
        await world.Add(new Row
        {
            Id = "shadow-no-question",
            Kind = GraderEpochKinds.Code,
            Response = [0.5f, 0.25f],
            QuestionFlag = true
        });
        world.AcceptQuestionMeanLayout();

        var result = await world.Shadow("shadow-no-question", questionMissing: true);

        Assert.Equal(0.25, result.LocalScore);
        Assert.Null(result.LocalConfidence);
        Assert.DoesNotContain(typeof(ShadowLocalScoreObserver).GetConstructors().Single().GetParameters(),
            parameter => parameter.ParameterType.Name.Contains("PendingValueCache", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ShadowScore_NullFloor_StoresLocalConfidenceNull()
    {
        using var world = World.Create(World.Small());
        await world.Add(new Row
        {
            Id = "shadow-null-floor",
            Kind = GraderEpochKinds.Code,
            Response = [0.5f, 0.25f],
            Question = [0.6f, 0.8f]
        });
        world.AcceptLayout(weightOnResponse0: 1f, bias: 0f, cosineFloor: null,
            reservoir: [("shadow-null-floor", [0.6f, 0.8f])]);

        var result = await world.Shadow("shadow-null-floor", questionMissing: false);

        Assert.Equal(0.5, result.LocalScore);
        Assert.Null(result.LocalConfidence);
    }

    [Fact]
    public async Task QuestionVectorMissing_NullFlag_FollowsMemoryRow()
    {
        using var world = World.Create(World.Small());
        float[] vector = [0.6f, 0.8f];
        await world.Add(new Row { Id = "null-hit", Kind = GraderEpochKinds.Code, Question = vector, QuestionFlag = null });
        await world.Add(new Row { Id = "null-none", Kind = GraderEpochKinds.Code, QuestionFlag = null });
        await world.Add(new Row
        {
            Id = "null-model", Kind = GraderEpochKinds.Code, Question = vector, QuestionFlag = null, QuestionModelNull = true
        });
        await world.Add(new Row
        {
            Id = "null-other", Kind = GraderEpochKinds.Code, Question = vector, QuestionFlag = null, QuestionModel = "other-bge"
        });

        var labels = await world.Query.LoadForKindAsync(GraderEpochKinds.Code, CancellationToken.None);
        var hit = labels.Single(label => label.CorrelationId == "null-hit").ToInput(world.Bge, Dimension);
        Assert.False(hit.QuestionMissing);
        Assert.Equal(vector, hit.QuestionVector);
        foreach (var id in new[] { "null-none", "null-model", "null-other" })
        {
            var input = labels.Single(label => label.CorrelationId == id).ToInput(world.Bge, Dimension);
            Assert.True(input.QuestionMissing);
            Assert.Null(input.QuestionVector);
        }

        Assert.Equal(4L, (long)world.Scalar(
            "SELECT COUNT(*) FROM grade_receipts WHERE question_vector_missing IS NULL;"));
    }

    [Fact]
    public async Task GateFail_KeepsCandidateUnderFailed_DoesNotTouchAccepted()
    {
        using var world = World.Create(World.Small());
        await world.SeedLinear(count: 24, holdout: 6, invertHoldout: true);
        var accepted = world.AcceptLayout(weightOnResponse0: 0f, bias: 0.5f);
        var pointer = File.ReadAllBytes(world.PointerPath(GraderEpochKinds.Code));
        string? oldest = null;
        for (var tick = 0; tick < 4; tick++)
        {
            await world.Tick();
            var ids = world.FailedIds(GraderEpochKinds.Code);
            oldest ??= ids.Single();
            if (tick < 3) Assert.Contains(oldest, ids);
        }

        var kept = world.FailedIds(GraderEpochKinds.Code);
        Assert.Equal(3, kept.Count);
        Assert.DoesNotContain(oldest, kept);
        Assert.Equal(pointer, File.ReadAllBytes(world.PointerPath(GraderEpochKinds.Code)));
        Assert.Equal(accepted, world.Store.TryReadAccepted(GraderEpochKinds.Code)?.ArtifactId);
        foreach (var id in kept)
        {
            var failed = world.ReadArtifact(world.FailedPath(GraderEpochKinds.Code, id));
            Assert.Null(failed.Status);
            Assert.True(File.Exists(world.FailedReservoirPath(GraderEpochKinds.Code, id)));
        }

        var shadow = await world.Shadow("c-0023");
        Assert.Equal(accepted, shadow.LocalGraderVersion);
        Assert.Equal(0.5, shadow.LocalScore);
    }

    [Fact]
    public async Task ShadowScore_UnusableResponseVector_LeavesLocalScoreNull()
    {
        using var world = World.Create(World.Small());
        await world.Add(new Row { Id = "no-vector", Kind = GraderEpochKinds.Code, SealResponse = false });
        await world.Add(new Row
        {
            Id = "other-model", Kind = GraderEpochKinds.Code, Response = [0.5f, 0.25f], ResponseModel = "other-bge"
        });
        await world.Add(new Row { Id = "bad-seal", Kind = GraderEpochKinds.Code, GarbageResponse = true });
        await world.Add(new Row { Id = "empty-vector", Kind = GraderEpochKinds.Code, Response = [] });
        world.AcceptLayout(weightOnResponse0: 1f, bias: 0f);

        Assert.Null((await world.Shadow("no-vector")).LocalScore);
        Assert.Null((await world.Shadow("other-model")).LocalScore);
        Assert.NotNull(world.Vectors.ReadSealed("other-model"));
        Assert.Null((await world.Shadow("bad-seal")).LocalScore);
        Assert.Null((await world.Shadow("empty-vector")).LocalScore);
    }

    [Fact]
    public async Task ShadowScore_SyntaxComesFromTheResult_NotTheUnwrittenColumn()
    {
        using var world = World.Create(World.Small());
        await world.Add(new Row
        {
            Id = "syntax-code", Kind = GraderEpochKinds.Code, Response = [0.5f, 0.25f], Syntax = 0
        });
        await world.Add(new Row
        {
            Id = "syntax-boot", Kind = GraderEpochKinds.NonCode, Response = [0.5f, 0.25f], Syntax = null
        });
        world.AcceptSyntaxLayout(GraderEpochKinds.Code);
        world.AcceptSyntaxLayout(GraderEpochKinds.NonCode);

        var code = await world.Shadow("syntax-code", recipe: GradingRecipes.Code, syntaxValid: true);
        var bootstrap = await world.Shadow("syntax-boot", recipe: GradingRecipes.Bootstrap, syntaxValid: false);

        Assert.Equal(1, code.LocalScore);
        Assert.Equal(0.5, bootstrap.LocalScore);
    }

    [Fact]
    public async Task LocalScore_IsNeverTheTrainingLabel()
    {
        using var world = World.Create(World.Small());
        await world.Add(new Row
        {
            Id = "local-only", Kind = GraderEpochKinds.Code, WriteJudge = false, LocalScore = 0.1
        });
        await world.Add(new Row { Id = "both-scores", Kind = GraderEpochKinds.Code, Judge = 0.9, LocalScore = 0.1 });

        var labels = await world.Query.LoadForKindAsync(GraderEpochKinds.Code, CancellationToken.None);
        Assert.DoesNotContain(labels, label => label.CorrelationId == "local-only");
        Assert.Equal(0.9, labels.Single(label => label.CorrelationId == "both-scores").JudgeScore);
    }

    [Fact]
    public async Task Retirement_TeacherModelPromptBgeOrFeatureSchema_RetiresArtifact()
    {
        await AssertRetire(field: "teacher model", mutate: world =>
        {
            world.Execute("UPDATE grade_receipts SET judge_model = 'teacher-b';");
            world.Judge.Set(world.JudgeOptions(model: "teacher-b"));
        });
        await AssertRetire(field: "prompt version", mutate: world =>
        {
            world.Execute("UPDATE grade_receipts SET prompt_version = 'prompt-b';");
            world.Judge.Set(world.JudgeOptions(prompt: "prompt-b"));
        });
        await AssertRetire(field: "BGE model id", mutate: world => world.RetargetBge("bge-next"));
        await AssertRetire(field: "feature schema", mutate: _ => { }, schemaVersion: 2);
    }

    [Fact]
    public async Task SpotCheckWindow_IsNotActive_DoesNotRetire()
    {
        using var world = World.Create(new GraderEpochOptions());
        var origin = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var accepted = world.AcceptLayout(weightOnResponse0: 1f, bias: 0f);
        for (var i = 0; i < 50; i++)
        {
            await world.Add(new Row
            {
                Id = $"spot-{i:D2}",
                Kind = GraderEpochKinds.Code,
                CreatedAt = origin.AddMinutes(i),
                Judge = 1,
                LocalScore = 0,
                LocalGraderVersion = accepted,
                SpotCheck = true,
                Response = [0.2f, 0.05f]
            });
        }

        await world.Tick();

        Assert.Null(world.Store.TryReadAccepted(GraderEpochKinds.Code));
        Assert.Equal(accepted, world.Store.TryReadRollbackMarker(GraderEpochKinds.Code)?.RolledBackArtifactId);
        Assert.DoesNotContain(world.ServiceLog.Lines, line =>
            line.Contains("spot-check window is not active", StringComparison.Ordinal));
        Assert.Contains(world.ServiceLog.Lines, line =>
            line.Contains("spot-check window", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FitAndScore_DoNotReadOrZeroFillToolCounts()
    {
        Assert.DoesNotContain("tool_definition_count", GraderEpochLabelQuery.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("prior_tool_call_count", GraderEpochLabelQuery.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT *", GraderEpochLabelQuery.CommandText, StringComparison.Ordinal);

        using var world = World.Create(World.Small());
        await world.SeedLinear(count: 24, holdout: 6);
        var labels = (await world.Query.LoadForKindAsync(GraderEpochKinds.Code, CancellationToken.None)).ToList();
        foreach (var label in labels)
        {
            label.AddProbe("tool_definition_count", 3);
            label.AddProbe("prior_tool_call_count", 1);
        }

        var outcome = GraderEpochFit.Run(
            kind: GraderEpochKinds.Code,
            eligibleCount: labels.Count,
            split: GraderEpochFit.Split(labels, world.Epoch.CurrentValue.HoldoutSize),
            options: world.Epoch.CurrentValue,
            teacherModel: world.TeacherModel,
            promptVersion: world.Prompt,
            bgeModelId: world.Bge,
            dimension: Dimension,
            live: null,
            key: world.Keys.GetOrCreate(),
            createdAtUtc: DateTimeOffset.UnixEpoch);

        Assert.Equal(outcome.Artifact.FeatureNames.Length, outcome.Artifact.Weights.Length);
        Assert.DoesNotContain(outcome.Artifact.FeatureNames, name =>
            name.Contains("tool_definition_count", StringComparison.Ordinal) ||
            name.Contains("prior_tool_call_count", StringComparison.Ordinal));
        Assert.Equal(GraderEpochFeatures.Names(
            includeQuestion: outcome.Artifact.QuestionMean is not null,
            includeAnalysis: outcome.Artifact.AnalysisMean is not null,
            includeSyntax: outcome.Artifact.SyntaxMean is not null,
            includeLength: outcome.Artifact.LengthMean is not null,
            dimension: Dimension), outcome.Artifact.FeatureNames);
        Assert.Throws<InvalidOperationException>(() => labels[0].ReadColumn("tool_definition_count"));
    }

    [Fact]
    public async Task SyntaxDefault_IsNotAFeature()
    {
        using var world = World.Create(new GraderEpochOptions
        {
            MinimumLabels = 4, HoldoutSize = 1, MinimumTrainingRows = 2, ReservoirCapacity = 5
        });
        var observer = world.ReceiptObserver();
        await observer.ObserveAsync(new QualityResult
        {
            RequestCorrelationId = "syntax-default",
            Model = "answer",
            Dimension = "chat",
            RecipeId = GradingRecipes.Bootstrap,
            SyntaxValid = false,
            SyntaxConfidence = 1.0,
            GradeStatus = GradeStatus.Complete,
            JudgeScore = 0.4,
            GradeSource = GradeSources.Teacher,
            JudgeModel = world.TeacherModel
        });
        var stored = await world.Receipts.TryGetAsync("syntax-default");
        Assert.NotNull(stored);
        Assert.Null(stored.SyntaxScore);

        var origin = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 4; i++)
        {
            await world.Add(new Row
            {
                Id = $"boot-{i}",
                Kind = GraderEpochKinds.NonCode,
                CreatedAt = origin.AddMinutes(i),
                Syntax = null
            });
        }

        await world.Tick();
        var artifact = world.ReadWritten(GraderEpochKinds.NonCode);
        Assert.Contains(GraderEpochFeatures.Syntax, artifact.DroppedSlots);
        Assert.DoesNotContain(artifact.FeatureNames, name => name.Contains("syntax", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BucketReform_DoesNotResetArtifact()
    {
        using var world = World.Create(World.Small());
        await world.Add(new Row { Id = "reform", Kind = GraderEpochKinds.Code });
        var accepted = world.AcceptLayout(weightOnResponse0: 1f, bias: 0f);
        world.Quality.Set(new QualityOptions { WeightsVersion = "3.1-reformed" });

        await world.Tick();

        Assert.Equal(accepted, world.Store.TryReadAccepted(GraderEpochKinds.Code)?.ArtifactId);
        Assert.DoesNotContain(world.ServiceLog.Lines, line => line.Contains("retired artifact", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LengthCorrelation_IsReported_GradesUnchanged()
    {
        using var world = World.Create(World.Small());
        await world.SeedLinear(count: 24, holdout: 6);
        var grades = world.ScoreSnapshot();

        await world.Tick();

        Assert.Equal(grades, world.ScoreSnapshot());
        Assert.Contains(world.ServiceLog.Lines, line =>
            line.Contains("length correlation for kind code", StringComparison.Ordinal) &&
            line.Contains(" is null.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IncompleteDisputedAndJudgeIsAnswerer_AreNotLabels()
    {
        using var world = World.Create(World.Small());
        await world.Add(new Row
        {
            Id = "incomplete", Kind = GraderEpochKinds.Code, Status = GradeStatus.Incomplete,
            Incomplete = IncompleteReasons.QueueFull
        });
        await world.Add(new Row
        {
            Id = "disputed", Kind = GraderEpochKinds.Code, Status = GradeStatus.Incomplete,
            Incomplete = IncompleteReasons.Disputed
        });
        await world.Add(new Row
        {
            Id = "answerer", Kind = GraderEpochKinds.Code, Status = GradeStatus.Incomplete,
            Incomplete = IncompleteReasons.JudgeIsAnswerer
        });

        var labels = await world.Query.LoadForKindAsync(GraderEpochKinds.Code, CancellationToken.None);
        Assert.Empty(labels);
    }

    [Fact]
    public async Task QuestionVectorMissing_FitsWithIndicator_NotZero()
    {
        using var world = World.Create(new GraderEpochOptions
        {
            MinimumLabels = 3, HoldoutSize = 1, MinimumTrainingRows = 1, ReservoirCapacity = 5
        });
        var origin = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        float[] good = [0.25f, 0.25f];
        float[] flagged = [0.9f, 0.1f];
        await world.Add(new Row
        {
            Id = "indicator-good", Kind = GraderEpochKinds.Code, CreatedAt = origin, Question = good, QuestionFlag = false
        });
        await world.Add(new Row
        {
            Id = "indicator-flag", Kind = GraderEpochKinds.Code, CreatedAt = origin.AddMinutes(1),
            Question = flagged, QuestionFlag = true
        });
        await world.Add(new Row
        {
            Id = "indicator-empty", Kind = GraderEpochKinds.Code, CreatedAt = origin.AddMinutes(2), QuestionFlag = false
        });

        var labels = (await world.Query.LoadForKindAsync(GraderEpochKinds.Code, CancellationToken.None)).ToList();
        var outcome = GraderEpochFit.Run(
            kind: GraderEpochKinds.Code,
            eligibleCount: labels.Count,
            split: GraderEpochFit.Split(labels, 1),
            options: world.Epoch.CurrentValue,
            teacherModel: world.TeacherModel,
            promptVersion: world.Prompt,
            bgeModelId: world.Bge,
            dimension: Dimension,
            live: null,
            key: world.Keys.GetOrCreate(),
            createdAtUtc: DateTimeOffset.UnixEpoch);

        Assert.NotNull(outcome.Artifact.QuestionMean);
        Assert.Equal(good, outcome.Artifact.QuestionMean);
        foreach (var id in new[] { "indicator-flag", "indicator-empty" })
        {
            var input = labels.Single(label => label.CorrelationId == id).ToInput(world.Bge, Dimension);
            Assert.True(input.QuestionMissing);
            Assert.Null(input.QuestionVector);
            var encoded = GraderEpochFeatures.Encode(outcome.Artifact, input);
            Assert.NotNull(encoded);
            Assert.Equal(1f, encoded[0]);
            Assert.Equal(good[0], encoded[1]);
            Assert.Equal(good[1], encoded[2]);
        }
    }

    [Fact]
    public async Task UnknownWeightsVersion_IsNotALabel()
    {
        using var world = World.Create(World.Small());
        await world.Add(new Row { Id = "unknown-weights", Kind = GraderEpochKinds.Code, Weights = "bucket-9" });

        Assert.Empty(await world.Query.LoadForKindAsync(GraderEpochKinds.Code, CancellationToken.None));
        Assert.Empty(await world.Query.LoadForKindAsync(GraderEpochKinds.NonCode, CancellationToken.None));
    }

    [Fact]
    public async Task ShadowScore_UnknownWeightsVersion_LeavesLocalScoreNull()
    {
        using var world = World.Create(World.Small());
        await world.Add(new Row { Id = "unknown-shadow", Kind = GraderEpochKinds.Code, Response = [0.5f, 0.25f] });
        world.AcceptLayout(weightOnResponse0: 1f, bias: 0f);
        world.AcceptLayout(weightOnResponse0: 1f, bias: 0f, kind: GraderEpochKinds.NonCode);

        var result = new QualityResult
        {
            RequestCorrelationId = "unknown-shadow",
            Model = "answer",
            RecipeId = GradingRecipes.Code,
            JudgeScore = 0.7,
            GradeSource = GradeSources.Teacher,
            GradeStatus = GradeStatus.Complete
        };
        world.ShadowObserver.Apply(result, "bucket-9");

        Assert.Null(result.LocalScore);
        Assert.Null(result.LocalGraderVersion);
    }

    [Fact]
    public async Task MultipleJudgeRows_AreNotALabel()
    {
        using var world = World.Create(World.Small());
        await world.Add(new Row { Id = "two-judges", Kind = GraderEpochKinds.Code, Judge = 0.2, JudgeRows = 2 });

        var labels = await world.Query.LoadForKindAsync(GraderEpochKinds.Code, CancellationToken.None);
        Assert.DoesNotContain(labels, label => label.CorrelationId == "two-judges");
    }

    [Fact]
    public void Reservoir_AlgorithmR_Seed20261025_MatchesRecordedDraw()
    {
        var items = Enumerable.Range(0, 10).Select(index => $"id-{index}").ToArray();
        var sample = GraderEpochReservoir.Sample(items, capacity: 3, seed: 20261025);
        Assert.Equal(["id-7", "id-1", "id-2"], sample);
    }

    [Fact]
    public async Task Disabled_LogsOnce_AndSkipsSpotCheck()
    {
        using var world = World.Create(World.Small(enabled: false));
        await world.Tick();
        Assert.Contains(world.ServiceLog.Lines, line => line.Contains("disabled", StringComparison.Ordinal));
        Assert.Contains(world.ServiceLog.Lines, line => line.Contains("spot-check window", StringComparison.Ordinal));
        Assert.DoesNotContain(world.ServiceLog.Lines, line =>
            line.Contains("spot-check window is not active", StringComparison.Ordinal));
    }

    private static async Task AssertRetire(string field, Action<World> mutate, int schemaVersion = 1)
    {
        using var world = World.Create(World.Small());
        await world.SeedLinear(count: 24, holdout: 6);
        var retired = world.AcceptLayout(weightOnResponse0: 0f, bias: 0f, schemaVersion: schemaVersion);
        mutate(world);

        var accepted = await RequireCodeArtifact(world, $"No accepted artifact after retirement of {field}.");
        Assert.NotEqual(retired, accepted.ArtifactId);
        Assert.Null(accepted.PreviousArtifactId);
        Assert.True(File.Exists(world.ArtifactPath(GraderEpochKinds.Code, retired)));
        Assert.Contains(world.ServiceLog.Lines, line =>
            line.Contains(retired, StringComparison.Ordinal) &&
            line.Contains(field, StringComparison.Ordinal));
    }

    private static byte[] ReservoirBlob(byte[] file, string correlationId)
    {
        var records = GraderEpochReservoir.Read(file);
        return records.Single(record => record.CorrelationId == correlationId).Blob;
    }

    [Theory]
    [InlineData("Grader epoch gate failed for kind code. Holdout MAE 0.14.", "", true)]
    [InlineData("Grader epoch gate failed for kind code.\nGrader epoch skipped non-code: eligible labels 0 are below the minimum 24.", "", true)]
    [InlineData("Grader epoch skipped code: eligible labels 23 are below the minimum 24.", "", false)]
    [InlineData("Grader epoch gate failed for kind non-code.", "", false)]
    [InlineData("", "Grader epoch accepted artifact for kind code could not be read.", false)]
    [InlineData("", "Grader epoch accepted pointer for kind code is unreadable; treating it as no artifact.", false)]
    [InlineData("", "", false)]
    public void ShouldRetick_OnlyAfterACodeGateMiss(string service, string store, bool expected)
    {
        var serviceLines = service.Length == 0 ? [] : service.Split('\n');
        var storeLines = store.Length == 0 ? [] : store.Split('\n');
        Assert.Equal(expected, ShouldRetick(serviceLines, storeLines));
    }

    /// <summary>
    /// Fails the test when the tick caught an exception and kept going.
    /// The formatted line includes the exception, so the CI log shows the type and message.
    /// </summary>
    /// <param name="lines">The lines the tick's logger stored.</param>
    internal static void AssertTickDidNotThrow(IReadOnlyList<string> lines)
    {
        var thrown = lines.Where(line => line.Contains("threw unexpectedly", StringComparison.Ordinal)).ToArray();
        Assert.True(thrown.Length == 0, string.Join(Environment.NewLine, thrown));
    }

    private const int RetickAttempts = 4;

    /// <summary>
    /// Ticks until the code kind has an accepted artifact, or the logs say this miss is not a bad draw.
    /// Retirement deletes the pointer before the fit. A fit that misses the holdout bars does not write
    /// a new pointer, and the trainer's draw is not deterministic. Another tick is another draw.
    /// The loop stops when the code kind was skipped or the store could not read the pointer, so those
    /// failures still surface on the first tick.
    /// </summary>
    /// <param name="world">The world whose tick and logs to use.</param>
    /// <param name="whenMissing">Text placed above the logs when no artifact appears.</param>
    /// <returns>The accepted code artifact.</returns>
    private static async Task<GraderEpochArtifact> RequireCodeArtifact(World world, string whenMissing)
    {
        for (var attempt = 0; attempt < RetickAttempts; attempt++)
        {
            await world.Tick();
            var accepted = world.Store.TryReadAccepted(GraderEpochKinds.Code);
            if (accepted is not null) return accepted;
            if (!ShouldRetick(world.ServiceLog.Lines, world.StoreLog.Lines)) break;
        }

        Assert.Fail(whenMissing + Environment.NewLine + string.Join(Environment.NewLine,
            world.ServiceLog.Lines.Concat(world.StoreLog.Lines)));
        // Assert.Fail carries DoesNotReturn, and this compiler still requires a throw for CS0161.
        throw new System.Diagnostics.UnreachableException();
    }

    /// <summary>
    /// True when the code kind missed the holdout bars and the store did not report an unreadable pointer.
    /// A skipped code kind, an unreadable code pointer, and a miss on the non-code kind are false.
    /// </summary>
    /// <param name="serviceLines">Lines from the hosted-service logger.</param>
    /// <param name="storeLines">Lines from the artifact-store logger.</param>
    /// <returns>True when another tick is the right next step.</returns>
    private static bool ShouldRetick(IReadOnlyList<string> serviceLines, IReadOnlyList<string> storeLines)
    {
        var kind = GraderEpochKinds.Code;
        if (storeLines.Any(line =>
                line.Contains($"for kind {kind}", StringComparison.Ordinal) &&
                (line.Contains("unreadable", StringComparison.Ordinal) ||
                 line.Contains("could not be read", StringComparison.Ordinal))))
            return false;

        if (serviceLines.Any(line => line.Contains($"skipped {kind}:", StringComparison.Ordinal)))
            return false;

        return serviceLines.Any(line =>
            line.Contains($"gate failed for kind {kind}", StringComparison.Ordinal));
    }

    internal sealed class World : IDisposable
    {
        private readonly string _directory;

        private World(string directory, RouterMemoryDatabase database, ProtectedSecretStore secrets,
            ResponseVectorKeyHolder keys, SqliteResponseVectorStore vectors, SqliteGradeReceiptStore receipts,
            SqliteGraderScoreStore scores, StaticOptionsMonitor<GraderEpochOptions> epoch,
            StaticOptionsMonitor<JudgeOptions> judge, StaticOptionsMonitor<QualityOptions> quality,
            StaticOptionsMonitor<EmbeddingOptions> embedding, StaticOptionsMonitor<StorageOptions> storage,
            GraderEpochArtifactStore store, GraderEpochLabelQuery query, CaptureLog serviceLog, CaptureLog storeLog,
            GraderEpochHostedService service, ShadowLocalScoreObserver shadow, SqliteGraderEpochEventStore events)
        {
            _directory = directory;
            Database = database;
            Secrets = secrets;
            Keys = keys;
            Vectors = vectors;
            Receipts = receipts;
            Scores = scores;
            Epoch = epoch;
            Judge = judge;
            Quality = quality;
            Embedding = embedding;
            Storage = storage;
            Store = store;
            Query = query;
            ServiceLog = serviceLog;
            StoreLog = storeLog;
            Service = service;
            ShadowObserver = shadow;
            Events = events;
        }

        private RouterMemoryDatabase Database { get; }

        private ProtectedSecretStore Secrets { get; }

        public ResponseVectorKeyHolder Keys { get; }

        public SqliteResponseVectorStore Vectors { get; }

        public SqliteGradeReceiptStore Receipts { get; }

        private SqliteGraderScoreStore Scores { get; }

        public StaticOptionsMonitor<GraderEpochOptions> Epoch { get; }

        public StaticOptionsMonitor<JudgeOptions> Judge { get; }

        public StaticOptionsMonitor<QualityOptions> Quality { get; }

        public StaticOptionsMonitor<EmbeddingOptions> Embedding { get; }

        private StaticOptionsMonitor<StorageOptions> Storage { get; }

        public GraderEpochArtifactStore Store { get; }

        public GraderEpochLabelQuery Query { get; }

        public CaptureLog ServiceLog { get; }

        public CaptureLog StoreLog { get; }

        private GraderEpochHostedService Service { get; }

        public ShadowLocalScoreObserver ShadowObserver { get; }

        public IGraderEpochEventStore Events { get; }

        public string TeacherModel => "teacher-a";

        public string Prompt => "prompt-a";

        public string Bge => Embedding.CurrentValue.ResolveModelIdentity();

        public static GraderEpochOptions Small(bool enabled = true) => new()
        {
            Enabled = enabled,
            MinimumLabels = 24,
            HoldoutSize = 6,
            MinimumTrainingRows = 12,
            ReservoirCapacity = 30
        };

        public static World Create(GraderEpochOptions epoch)
        {
            var directory = Path.Combine(Path.GetTempPath(), "grader-epoch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var database = new RouterMemoryDatabase(Options.Create(new RoutingOptions
            {
                EmbeddingMemoryDatabasePath = Path.Combine(directory, "router.db")
            }));
            database.EnsureCreated();
            var secrets = new ProtectedSecretStore(Path.Combine(directory, "secrets.dat"));
            var keys = new ResponseVectorKeyHolder(secrets, secrets, database,
                NullLogger<ResponseVectorKeyHolder>.Instance);
            keys.GetOrCreate();
            var vectors = new SqliteResponseVectorStore(database, keys, NullLogger<SqliteResponseVectorStore>.Instance);
            var receipts = new SqliteGradeReceiptStore(database);
            var scores = new SqliteGraderScoreStore(database);
            var epochMonitor = new StaticOptionsMonitor<GraderEpochOptions>(epoch);
            var judge = new StaticOptionsMonitor<JudgeOptions>(new JudgeOptions
            {
                ModelName = "teacher-a",
                PromptVersion = "prompt-a"
            });
            var quality = new StaticOptionsMonitor<QualityOptions>(new QualityOptions
            {
                WeightsVersion = "3.0-judge-heavy"
            });
            var embedding = new StaticOptionsMonitor<EmbeddingOptions>(new EmbeddingOptions
            {
                ModelUrl = "bge-test",
                EmbeddingDimension = Dimension
            });
            var storage = new StaticOptionsMonitor<StorageOptions>(new StorageOptions
            {
                GraderEpochDirectory = Path.Combine(directory, "epoch"),
                LogRegModelPath = Path.Combine(directory, "logreg_voter_model.json")
            });
            File.WriteAllBytes(storage.CurrentValue.ResolveLogRegModelPath(), [1, 2, 3, 4, 5]);
            var storeLog = new CaptureLog();
            var serviceLog = new CaptureLog();
            var events = new SqliteGraderEpochEventStore(database);
            var store = new GraderEpochArtifactStore(storage, keys, storeLog.For<GraderEpochArtifactStore>(), events);
            var query = new GraderEpochLabelQuery(database, keys, judge, quality, embedding);
            var service = new GraderEpochHostedService(
                logger: serviceLog.For<GraderEpochHostedService>(),
                options: epochMonitor,
                judge: judge,
                embedding: embedding,
                query: query,
                store: store,
                events: events,
                keys: keys,
                timeProvider: new StepClock());
            var shadow = new ShadowLocalScoreObserver(
                store: store,
                query: query,
                vectors: vectors,
                judge: judge,
                quality: quality,
                embedding: embedding,
                logger: NullLogger<ShadowLocalScoreObserver>.Instance);
            return new World(directory, database, secrets, keys, vectors, receipts, scores, epochMonitor, judge,
                quality, embedding, storage, store, query, serviceLog, storeLog, service, shadow, events);
        }

        public JudgeOptions JudgeOptions(string? model = null, string? prompt = null) => new()
        {
            ModelName = model ?? TeacherModel,
            PromptVersion = prompt ?? Prompt
        };

        /// <summary>Drives one tick and fails when that tick logged a swallowed exception.</summary>
        public async Task Tick()
        {
            await Service.RunTickAsync(CancellationToken.None);
            AssertTickDidNotThrow(ServiceLog.Lines);
        }

        public async Task SeedLinear(int count, int holdout, bool invertHoldout = false, bool constantHoldout = false,
            int unparseable = 0, int sharedHoldoutSession = 0, float[]? question = null)
        {
            var origin = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            for (var i = 0; i < count; i++)
            {
                var response0 = (i % 3) switch { 0 => 0.2f, 1 => 0.5f, _ => 0.8f };
                var newest = i >= count - holdout;
                double teacher = response0;
                if (newest && invertHoldout) teacher = 1d - response0;
                if (newest && constantHoldout) teacher = 0.4;
                string? session = null;
                if (sharedHoldoutSession > 0 && (i == count - 1 || i == 0)) session = "shared-holdout";
                await Add(new Row
                {
                    Id = $"c-{i:D4}",
                    Kind = GraderEpochKinds.Code,
                    CreatedAt = origin.AddMinutes(i),
                    RawCreated = i < unparseable ? "not-a-timestamp" : null,
                    Judge = teacher,
                    Response = [response0, 0.15f + 0.5f * response0],
                    HasCode = i % 2 == 0,
                    Question = question,
                    SessionId = session
                });
            }
        }

        public async Task Add(Row row)
        {
            var weights = row.Weights ?? (row.Kind == GraderEpochKinds.NonCode
                ? GradingRecipes.Bootstrap
                : Quality.CurrentValue.WeightsVersion);
            var created = row.CreatedAt ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            await Receipts.UpsertAsync(new GradeReceiptRecord(
                CorrelationId: row.Id,
                CreatedAtUtc: created,
                Dimension: "chat",
                Model: "answer",
                GradeStatus: row.Status,
                UnifiedScore: row.Judge,
                QuestionHmac: null,
                QuestionChars: null,
                ContextMessages: null,
                QuestionTruncated: false,
                FingerprintRef: null,
                ScorerVersion: "3.0",
                PromptVersion: Prompt,
                WeightsVersion: weights,
                AbstainedGraders: string.Empty,
                IncompleteReason: row.Incomplete,
                GradeSource: GradeSources.Teacher,
                ResponseHasCode: row.HasCode,
                JudgeModel: TeacherModel,
                AnswerModel: "answer",
                QuestionVectorMissing: row.QuestionFlag,
                SessionId: row.SessionId,
                SpotCheck: row.SpotCheck,
                ResponseLengthChars: null,
                LocalScore: row.LocalScore,
                LocalGraderVersion: row.LocalGraderVersion,
                SyntaxScore: row.Syntax));
            if (row.RawCreated is not null)
            {
                Execute("UPDATE grade_receipts SET created_at_utc = $raw WHERE correlation_id = $id;",
                    ("$raw", row.RawCreated), ("$id", row.Id));
            }

            if (row.WriteJudge)
            {
                for (var copy = 0; copy < row.JudgeRows; copy++)
                {
                    await Scores.InsertAsync(new GraderScoreRecord(
                        Id: 0,
                        CorrelationId: row.Id,
                        CreatedAtUtc: created,
                        Dimension: "chat",
                        Model: "answer",
                        GraderKey: GraderKeys.Judge,
                        Score: row.Judge,
                        GraderBackboneModel: TeacherModel,
                        ResponseLengthChars: null,
                        PromptVersion: Prompt));
                }
            }

            if (row.GarbageResponse)
            {
                Execute("""
                        INSERT INTO response_vectors (correlation_id, created_at_utc, embedding_model, vector)
                        VALUES ($id, $at, $model, $vector);
                        """,
                    ("$id", row.Id), ("$at", created.ToString("O")), ("$model", Bge), ("$vector", new byte[24]));
            }
            else if (row.SealResponse)
            {
                var response = row.Response ?? [0.2f, 0.05f];
                await Vectors.UpsertAsync(row.Id, row.ResponseModel ?? Bge, response, created);
            }

            if (row.Question is not null || row.QuestionModelNull)
            {
                Execute("""
                        INSERT INTO memory_entries (
                            embedding, chosen_model, score, created_at_utc, is_exploratory, propensity,
                            is_judge_scored, embedding_model, correlation_id)
                        VALUES ($embedding, 'answer', $score, $at, 0, 1, $judged, $model, $id);
                        """,
                    ("$embedding", Floats(row.Question ?? [0.6f, 0.8f])),
                    ("$score", row.Judge),
                    ("$at", created.ToString("O")),
                    ("$judged", row.JudgeScoredMemory ? 1 : 0),
                    ("$model", ModelOrNull(row)),
                    ("$id", row.Id));
            }
        }

        public string AcceptLayout(float weightOnResponse0, float bias, string? teacherModel = null,
            string? promptVersion = null, string? bgeModelId = null, string kind = GraderEpochKinds.Code,
            int schemaVersion = 1, double? cosineFloor = null, string? previousArtifactId = null,
            IReadOnlyList<(string Id, float[] Vector)>? reservoir = null)
        {
            var names = GraderEpochFeatures.Names(includeQuestion: false, includeAnalysis: false,
                includeSyntax: false, includeLength: false, dimension: Dimension);
            var weights = new float[names.Length];
            weights[Array.IndexOf(names, "response_vector[0]")] = weightOnResponse0;
            var id = Guid.NewGuid().ToString("N");
            var records = new List<(string CorrelationId, byte[] Blob)>();
            if (reservoir is not null)
            {
                foreach (var (reservoirId, vector) in reservoir)
                {
                    records.Add((reservoirId, ResponseVectorSeal.SealReservoir(
                        key: Keys.GetOrCreate(), artifactId: id, correlationId: reservoirId,
                        modelId: bgeModelId ?? Bge, vector: vector)));
                }
            }

            Store.CommitAccepted(new GraderEpochArtifact
            {
                ArtifactId = id,
                Kind = kind,
                Status = GraderEpochArtifact.AcceptedShadowStatus,
                FeatureSchemaVersion = schemaVersion,
                TeacherModel = teacherModel ?? TeacherModel,
                TeacherPromptVersion = promptVersion ?? Prompt,
                BgeModelId = bgeModelId ?? Bge,
                PreviousArtifactId = previousArtifactId,
                Trainer = GraderEpochTrainer.SdcaName,
                Weights = weights,
                Bias = bias,
                FeatureNames = names,
                DroppedSlots =
                [
                    GraderEpochFeatures.Question, GraderEpochFeatures.Analysis, GraderEpochFeatures.Syntax,
                    GraderEpochFeatures.Length
                ],
                EmbeddingDimension = Dimension,
                CosineFloor = cosineFloor,
                ReservoirCount = records.Count,
                ReservoirCapacity = Epoch.CurrentValue.ReservoirCapacity,
                RandomSeed = Epoch.CurrentValue.RandomSeed,
                CreatedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
            }, GraderEpochReservoir.Write(records));
            return id;
        }

        public void AcceptQuestionMeanLayout()
        {
            var names = GraderEpochFeatures.Names(includeQuestion: true, includeAnalysis: false,
                includeSyntax: false, includeLength: false, dimension: Dimension);
            var weights = new float[names.Length];
            weights[Array.IndexOf(names, "question_vector[0]")] = 1f;
            Store.CommitAccepted(new GraderEpochArtifact
            {
                ArtifactId = Guid.NewGuid().ToString("N"),
                Kind = GraderEpochKinds.Code,
                Status = GraderEpochArtifact.AcceptedShadowStatus,
                FeatureSchemaVersion = 1,
                TeacherModel = TeacherModel,
                TeacherPromptVersion = Prompt,
                BgeModelId = Bge,
                Trainer = GraderEpochTrainer.SdcaName,
                Weights = weights,
                Bias = 0f,
                FeatureNames = names,
                DroppedSlots = [GraderEpochFeatures.Analysis, GraderEpochFeatures.Syntax, GraderEpochFeatures.Length],
                QuestionMean = [0.25f, 0.5f],
                EmbeddingDimension = Dimension,
                CosineFloor = 0.2,
                CreatedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
            }, GraderEpochReservoir.Write([]));
        }

        public void AcceptSyntaxLayout(string kind)
        {
            var names = GraderEpochFeatures.Names(includeQuestion: false, includeAnalysis: false,
                includeSyntax: true, includeLength: false, dimension: Dimension);
            var weights = new float[names.Length];
            weights[Array.IndexOf(names, "syntax_score")] = 1f;
            Store.CommitAccepted(new GraderEpochArtifact
            {
                ArtifactId = Guid.NewGuid().ToString("N"),
                Kind = kind,
                Status = GraderEpochArtifact.AcceptedShadowStatus,
                FeatureSchemaVersion = 1,
                TeacherModel = TeacherModel,
                TeacherPromptVersion = Prompt,
                BgeModelId = Bge,
                Trainer = GraderEpochTrainer.SdcaName,
                Weights = weights,
                Bias = 0f,
                FeatureNames = names,
                DroppedSlots = [GraderEpochFeatures.Question, GraderEpochFeatures.Analysis, GraderEpochFeatures.Length],
                SyntaxMean = 0.5,
                EmbeddingDimension = Dimension,
                CreatedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
            }, GraderEpochReservoir.Write([]));
        }

        public async Task<QualityResult> Shadow(string correlationId, double judgeScore = 0.7,
            string? recipe = null, bool syntaxValid = false, bool? questionMissing = null)
        {
            var result = new QualityResult
            {
                RequestCorrelationId = correlationId,
                Model = "answer",
                Dimension = "chat",
                RecipeId = recipe ?? GradingRecipes.Code,
                SyntaxValid = syntaxValid,
                GradeStatus = GradeStatus.Complete,
                JudgeScore = judgeScore,
                GradeSource = GradeSources.Teacher,
                JudgeModel = TeacherModel,
                QuestionVectorMissing = questionMissing,
                ResponseHasCode = false
            };
            await ShadowObserver.ObserveAsync(result);
            return result;
        }

        public GradeReceiptRecordObserver ReceiptObserver() => new(
            store: Receipts,
            pendingWindowCache: new PendingConversationWindowCache(Options.Create(Judge.CurrentValue)),
            questionHmacHasher: new QuestionHmacHasher(Secrets),
            qualityOptions: Options.Create(Quality.CurrentValue),
            judgeOptions: Options.Create(Judge.CurrentValue),
            logger: NullLogger<GradeReceiptRecordObserver>.Instance);

        public void RetargetBge(string modelUrl)
        {
            var previous = Bge;
            Embedding.Set(new EmbeddingOptions { ModelUrl = modelUrl, EmbeddingDimension = Dimension });
            using var connection = Database.OpenConnection();
            using var read = connection.CreateCommand();
            read.CommandText = "SELECT correlation_id, vector FROM response_vectors WHERE embedding_model = $model;";
            read.Parameters.AddWithValue("$model", previous);
            var rows = new List<(string Id, byte[] Blob)>();
            using (var reader = read.ExecuteReader())
            {
                while (reader.Read()) rows.Add((reader.GetString(0), (byte[])reader.GetValue(1)));
            }

            foreach (var (id, blob) in rows)
            {
                Assert.True(ResponseVectorSeal.TryOpen(Keys.GetOrCreate(), id, previous, blob, out var vector));
                Vectors.UpsertAsync(id, Bge, vector, DateTimeOffset.UnixEpoch).GetAwaiter().GetResult();
            }

            Execute("UPDATE memory_entries SET embedding_model = $next WHERE embedding_model = $previous;",
                ("$next", Bge), ("$previous", previous));
        }

        public void DeleteCorrelation(string correlationId)
        {
            Execute("""
                    DELETE FROM grade_receipts WHERE correlation_id = $id;
                    DELETE FROM grader_scores WHERE correlation_id = $id;
                    DELETE FROM memory_entries WHERE correlation_id = $id;
                    DELETE FROM response_vectors WHERE correlation_id = $id;
                    """, ("$id", correlationId));
        }

        public string ScoreSnapshot()
        {
            using var connection = Database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                                  SELECT 'r', correlation_id, unified_score, IFNULL(local_score, -1), grade_source,
                                         weights_version
                                  FROM grade_receipts
                                  UNION ALL
                                  SELECT 'g', correlation_id, score, 0, grader_key, ''
                                  FROM grader_scores
                                  ORDER BY 1, 2, 5;
                                  """;
            using var reader = command.ExecuteReader();
            var lines = new List<string>();
            while (reader.Read())
                lines.Add(string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue)));
            return string.Join('\n', lines);
        }

        public byte[] LogRegBytes() => File.ReadAllBytes(Storage.CurrentValue.ResolveLogRegModelPath());

        public int JudgeRowCount(string correlationId) => (int)(long)Scalar(
            "SELECT COUNT(*) FROM grader_scores WHERE correlation_id = $id AND grader_key = 'judge';",
            ("$id", correlationId));

        public long JudgeScoredFlag(string correlationId) => (long)Scalar(
            "SELECT is_judge_scored FROM memory_entries WHERE correlation_id = $id;",
            ("$id", correlationId));

        public object Scalar(string sql, params (string Name, object Value)[] parameters)
        {
            using var connection = Database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            return command.ExecuteScalar()!;
        }

        public void Execute(string sql, params (string Name, object Value)[] parameters)
        {
            using var connection = Database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            command.ExecuteNonQuery();
        }

        public string ArtifactPath(string kind, string artifactId) =>
            Path.Combine(Storage.CurrentValue.ResolveGraderEpochDirectory(), kind, artifactId + ".json");

        private string ReservoirPath(string kind, string artifactId) =>
            Path.Combine(Storage.CurrentValue.ResolveGraderEpochDirectory(), kind, artifactId + ".reservoir");

        public byte[] ReservoirBytes(string kind, string artifactId)
        {
            var accepted = ReservoirPath(kind, artifactId);
            if (File.Exists(accepted)) return File.ReadAllBytes(accepted);
            return File.ReadAllBytes(FailedReservoirPath(kind, artifactId));
        }

        public IReadOnlyList<string> ReservoirIds(string kind, string artifactId) =>
            GraderEpochReservoir.Read(ReservoirBytes(kind, artifactId))
                .Select(record => record.CorrelationId)
                .ToList();

        public string PointerPath(string kind) =>
            Path.Combine(Storage.CurrentValue.ResolveGraderEpochDirectory(), kind, "accepted.json");

        public string FailedPath(string kind, string artifactId) =>
            Path.Combine(Storage.CurrentValue.ResolveGraderEpochDirectory(), kind, "failed", artifactId + ".json");

        public string FailedReservoirPath(string kind, string artifactId) =>
            Path.Combine(Storage.CurrentValue.ResolveGraderEpochDirectory(), kind, "failed", artifactId + ".reservoir");

        public IReadOnlyList<string> FailedIds(string kind)
        {
            var directory = Path.Combine(Storage.CurrentValue.ResolveGraderEpochDirectory(), kind, "failed");
            if (!Directory.Exists(directory)) return [];
            return Directory.GetFiles(directory, "*.json")
                .Select(path => (Id: Path.GetFileNameWithoutExtension(path), Created: ReadArtifact(path).CreatedAtUtc))
                .OrderBy(item => item.Created)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .Select(item => item.Id)
                .ToList();
        }

        public GraderEpochArtifact ReadWritten(string kind)
        {
            var accepted = Store.TryReadAccepted(kind);
            if (accepted is not null) return accepted;
            return ReadFailed(kind).OrderByDescending(artifact => artifact.CreatedAtUtc).First();
        }

        public IReadOnlyList<GraderEpochArtifact> ReadFailed(string kind) =>
            FailedIds(kind).Select(id => ReadArtifact(FailedPath(kind, id))).ToList();

        public GraderEpochArtifact ReadArtifact(string path)
        {
            var artifact = JsonSerializer.Deserialize<GraderEpochArtifact>(File.ReadAllBytes(path), Json);
            Assert.NotNull(artifact);
            return artifact;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // A pooled SQLite handle can keep the file for a moment.
            }
        }

        private object ModelOrNull(Row row)
        {
            if (row.QuestionModelNull) return DBNull.Value;
            return row.QuestionModel ?? Bge;
        }

        private static byte[] Floats(float[] vector)
        {
            var bytes = new byte[vector.Length * sizeof(float)];
            Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
            return bytes;
        }
    }

    internal sealed class Row
    {
        public required string Id { get; init; }

        public required string Kind { get; init; }

        public DateTimeOffset? CreatedAt { get; init; }

        public string? RawCreated { get; init; }

        public double Judge { get; init; } = 0.5;

        public bool WriteJudge { get; init; } = true;

        public int JudgeRows { get; init; } = 1;

        public float[]? Response { get; init; }

        public bool HasCode { get; init; }

        public string? ResponseModel { get; init; }

        public bool SealResponse { get; init; } = true;

        public bool GarbageResponse { get; init; }

        public float[]? Question { get; init; }

        public string? QuestionModel { get; init; }

        public bool QuestionModelNull { get; init; }

        public bool? QuestionFlag { get; init; }

        public string? SessionId { get; init; }

        public GradeStatus Status { get; init; } = GradeStatus.Complete;

        public string? Incomplete { get; init; }

        public string? Weights { get; init; }

        public double? LocalScore { get; init; }

        public string? LocalGraderVersion { get; init; }

        public double? Syntax { get; init; }

        public bool SpotCheck { get; init; }

        public bool JudgeScoredMemory { get; init; }
    }

    internal sealed class CaptureLog
    {
        public List<string> Lines { get; } = [];

        public ILogger<T> For<T>() => new Adapter<T>(Lines);

        private sealed class Adapter<T>(List<string> lines) : ILogger<T>
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                // LoggerExtensions formats the message only. The exception arrives separately, so append it
                // or a swallowed tick's assert message would hide the type and message.
                var text = formatter(state, exception);
                if (exception is not null)
                    text += Environment.NewLine + exception;
                lines.Add(text);
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed class StepClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            var value = _now;
            _now = _now.AddMinutes(1);
            return value;
        }
    }
}
