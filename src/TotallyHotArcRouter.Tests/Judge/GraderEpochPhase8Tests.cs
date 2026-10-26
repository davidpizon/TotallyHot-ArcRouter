using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Hosting;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Quality;
using TotallyHot.ArcRouter.Quality.Grading;
using TotallyHot.ArcRouter.Quality.Scoring;
using TotallyHot.ArcRouter.Router;
using TotallyHot.ArcRouter.Router.Embeddings;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>
/// Phase 8 local-grader takeover (issue #304). Recorded fixtures only. No live provider and no BGE download.
/// </summary>
public class GraderEpochPhase8Tests
{
    [Fact]
    public void Defaults_SpotCheckRateAndWindow()
    {
        var options = new GraderEpochOptions();
        Assert.True(options.TakeoverEnabled);
        Assert.Equal(0.05, options.SpotCheckRate);
        Assert.Equal(50, options.SpotCheckWindowSize);
        Assert.Equal(0.10, options.SpotCheckRollbackMae);
    }

    [Fact]
    public void SpotCheck_DrawsFromHash_NotFromRandomSeed()
    {
        var drawn = FindId(drawn: true);
        var missed = FindId(drawn: false);
        Assert.True(SpotCheckDraw.IsDrawn(drawn, 0.05));
        Assert.True(SpotCheckDraw.IsDrawn(drawn, 0.05));
        Assert.False(SpotCheckDraw.IsDrawn(missed, 0.05));
        Assert.False(SpotCheckDraw.IsDrawn(drawn, 0));
        Assert.True(SpotCheckDraw.IsDrawn(missed, 1));
    }

    [Fact]
    public async Task SpotCheckWindow_BelowCount_DoesNotRollBack()
    {
        using var world = World(size: 2);
        var id = world.AcceptLayout(weightOnResponse0: 1f, bias: 0f);
        await AddPair(world, id, "only", local: 0, judge: 1);
        await world.Tick();
        Assert.Equal(id, world.Store.TryReadAccepted(GraderEpochKinds.Code)?.ArtifactId);
        Assert.Null(world.Store.TryReadRollbackMarker(GraderEpochKinds.Code));
    }

    [Fact]
    public async Task SpotCheckWindow_MaeEqualToBar_DoesNotRollBack()
    {
        using var world = World(size: 2);
        var id = world.AcceptLayout(weightOnResponse0: 1f, bias: 0f);
        await AddPair(world, id, "a", local: 0.4, judge: 0.5);
        await AddPair(world, id, "b", local: 0.2, judge: 0.3);
        await world.Tick();
        Assert.Equal(id, world.Store.TryReadAccepted(GraderEpochKinds.Code)?.ArtifactId);
    }

    [Fact]
    public async Task SpotCheckWindow_MaeWorseThanBar_ClearsAcceptedPointer()
    {
        using var world = World(size: 2);
        var id = world.AcceptLayout(weightOnResponse0: 1f, bias: 0f, previousArtifactId: "older");
        await AddPair(world, id, "a", local: 0, judge: 1);
        await AddPair(world, id, "b", local: 0, judge: 1);
        var path = world.ArtifactPath(GraderEpochKinds.Code, id);
        await world.Tick();
        Assert.Null(world.Store.TryReadAccepted(GraderEpochKinds.Code));
        Assert.True(File.Exists(path));
        Assert.Equal(id, world.Store.TryReadRollbackMarker(GraderEpochKinds.Code)?.RolledBackArtifactId);
        Assert.Contains(world.StoreLog.Lines, line => line.Contains("Shadow scores stay off", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SpotCheckWindow_UsesJudgeScore_NotUnifiedScore()
    {
        using var world = World(size: 1);
        var id = world.AcceptLayout(weightOnResponse0: 1f, bias: 0f);
        await AddPair(world, id, "code-row", local: 0, judge: 0.2);
        world.Execute("UPDATE grade_receipts SET unified_score = 0.9 WHERE correlation_id = $id;", ("$id", "code-row"));
        await world.Tick();
        Assert.Null(world.Store.TryReadAccepted(GraderEpochKinds.Code));
    }

    [Fact]
    public async Task SpotCheckWindow_IgnoresOtherArtifactAndLowConfidence()
    {
        using var world = World(size: 2);
        var id = world.AcceptLayout(weightOnResponse0: 1f, bias: 0f);
        await AddPair(world, "someone-else", "old", local: 0, judge: 1);
        await AddPair(world, id, "low", local: 0, judge: 1, spot: false);
        await world.Tick();
        Assert.Equal(id, world.Store.TryReadAccepted(GraderEpochKinds.Code)?.ArtifactId);
    }

    [Fact]
    public async Task Probation_BlocksTakeover_UntilWindowIsHealthy()
    {
        using var world = World(size: 2);
        var id = world.AcceptLayout(weightOnResponse0: 1f, bias: 0f, cosineFloor: 0.2,
            reservoir: [("neighbor", [1f, 0f])]);
        world.Store.WriteRollbackMarker(GraderEpochKinds.Code, new GraderEpochRollbackMarker
        {
            RolledBackArtifactId = "previous", Mae = 0.4, WindowSize = 2, AtUtc = DateTimeOffset.UnixEpoch
        });
        await world.Tick();
        Assert.DoesNotContain(world.Events.ListForPlot(), row => row.EpochType == IGraderEpochEventStore.LocalTakeover);
        Assert.NotNull(world.Store.TryReadRollbackMarker(GraderEpochKinds.Code));

        await AddPair(world, id, "p0", local: 0.5, judge: 0.5);
        await AddPair(world, id, "p1", local: 0.4, judge: 0.4);
        await world.Tick();
        Assert.Null(world.Store.TryReadRollbackMarker(GraderEpochKinds.Code));
        var pass = Assert.Single(world.Events.ListForPlot(), row => row.Cause == "live_check_pass");
        Assert.Equal(2, pass.WindowSize);
        Assert.Equal(0, pass.MeanMiss);
        Assert.Equal(id, world.Store.TryReadAccepted(GraderEpochKinds.Code)?.ArtifactId);
    }

    [Fact]
    public async Task Probation_UnhealthyWindow_RollsBackAgain()
    {
        using var world = World(size: 2);
        var id = world.AcceptLayout(weightOnResponse0: 1f, bias: 0f);
        world.Store.WriteRollbackMarker(GraderEpochKinds.Code, new GraderEpochRollbackMarker
        {
            RolledBackArtifactId = "previous", Mae = 0.4, WindowSize = 2, AtUtc = DateTimeOffset.UnixEpoch
        });
        await AddPair(world, id, "bad-0", local: 0, judge: 1);
        await AddPair(world, id, "bad-1", local: 0, judge: 1);
        await world.Tick();
        Assert.Null(world.Store.TryReadAccepted(GraderEpochKinds.Code));
        Assert.Equal(id, world.Store.TryReadRollbackMarker(GraderEpochKinds.Code)?.RolledBackArtifactId);
    }

    [Fact]
    public async Task Retirement_KeepsRollbackMarker()
    {
        using var world = World(size: 2);
        var id = world.AcceptLayout(weightOnResponse0: 1f, bias: 0f);
        world.Store.WriteRollbackMarker(GraderEpochKinds.Code, new GraderEpochRollbackMarker
        {
            RolledBackArtifactId = "previous", Mae = 0.5, WindowSize = 2, AtUtc = DateTimeOffset.UnixEpoch
        });
        world.Judge.Set(world.JudgeOptions(model: "teacher-b"));
        await world.Tick();
        Assert.Null(world.Store.TryReadAccepted(GraderEpochKinds.Code));
        Assert.Equal("previous", world.Store.TryReadRollbackMarker(GraderEpochKinds.Code)?.RolledBackArtifactId);
        Assert.True(File.Exists(world.ArtifactPath(GraderEpochKinds.Code, id)));

        using var clean = World(size: 2);
        clean.AcceptLayout(weightOnResponse0: 1f, bias: 0f);
        clean.Judge.Set(clean.JudgeOptions(model: "teacher-b"));
        await clean.Tick();
        Assert.Null(clean.Store.TryReadRollbackMarker(GraderEpochKinds.Code));
    }

    [Fact]
    public async Task KillSwitch_Off_StopsNewSkips_KeepsArtifact()
    {
        using var world = World(size: 2);
        var id = world.AcceptLayout(weightOnResponse0: 1f, bias: 0f);
        await world.Tick();
        world.Epoch.Set(Quiet(takeover: false));
        var resumed = Assert.Single(world.Events.ListForPlot(), row => row.Cause == "switch_off");
        Assert.Null(resumed.MeanMiss);
        Assert.Equal(id, world.Store.TryReadAccepted(GraderEpochKinds.Code)?.ArtifactId);
        Assert.Null(world.Store.TryReadRollbackMarker(GraderEpochKinds.Code));

        world.Epoch.Set(Quiet(takeover: true));
        Assert.Single(world.Events.ListForPlot(), row => row.Cause == "switch_on");
    }

    [Fact]
    public async Task Kinds_RollBackIndependently()
    {
        using var world = World(size: 2);
        var code = world.AcceptLayout(weightOnResponse0: 1f, bias: 0f);
        var prose = world.AcceptLayout(weightOnResponse0: 1f, bias: 0f, kind: GraderEpochKinds.NonCode);
        await AddPair(world, prose, "n0", local: 0, judge: 1, kind: GraderEpochKinds.NonCode);
        await AddPair(world, prose, "n1", local: 0, judge: 1, kind: GraderEpochKinds.NonCode);
        await world.Tick();
        Assert.Equal(code, world.Store.TryReadAccepted(GraderEpochKinds.Code)?.ArtifactId);
        Assert.Null(world.Store.TryReadAccepted(GraderEpochKinds.NonCode));
        Assert.Single(world.Events.ListForPlot(), row =>
            row.EpochType == IGraderEpochEventStore.TeacherResumed && row.Kind == GraderEpochKinds.NonCode);
        Assert.DoesNotContain(world.Events.ListForPlot(), row =>
            row.EpochType == IGraderEpochEventStore.TeacherResumed && row.Kind == GraderEpochKinds.Code);
    }

    [Fact]
    public async Task FitOff_TakeoverOn_StillRunsWindow()
    {
        using var world = World(size: 2, enabled: false);
        var id = world.AcceptLayout(weightOnResponse0: 1f, bias: 0f);
        await AddPair(world, id, "a", local: 0, judge: 1);
        await AddPair(world, id, "b", local: 0, judge: 1);
        await world.Tick();
        Assert.Contains(world.ServiceLog.Lines, line => line.Contains("disabled", StringComparison.Ordinal));
        Assert.Null(world.Store.TryReadAccepted(GraderEpochKinds.Code));
        Assert.DoesNotContain(world.ServiceLog.Lines, line => line.Contains("Holdout MAE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EpochEvent_OnTakeover_AppendsLocalTakeover()
    {
        using var world = World(size: 2);
        var first = world.AcceptLayout(weightOnResponse0: 1f, bias: 0f);
        await world.Tick();
        var second = world.AcceptLayout(weightOnResponse0: 1f, bias: 0f, previousArtifactId: first);
        world.Store.CommitAcceptedWithEvent(world.Store.TryReadAccepted(GraderEpochKinds.Code)!, [],
            new GraderEpochEventRow
            {
                AtUtc = DateTimeOffset.UnixEpoch.ToString("O"),
                Kind = GraderEpochKinds.Code,
                EpochType = IGraderEpochEventStore.LocalTakeover,
                Cause = "gate_pass",
                ArtifactId = second,
                PreviousArtifactId = first
            });
        var rows = world.Events.ListForPlot().Where(row => row.EpochType == IGraderEpochEventStore.LocalTakeover).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("gate_pass", rows[0].Cause);
        Assert.DoesNotContain(world.Events.ListForPlot(), row => row.EpochType == IGraderEpochEventStore.TeacherResumed);
    }

    [Fact]
    public async Task EpochEvent_OnRollback_AppendsTeacherResumed()
    {
        using var world = World(size: 2);
        var id = world.AcceptLayout(weightOnResponse0: 1f, bias: 0f);
        await AddPair(world, id, "a", local: 0, judge: 1);
        await AddPair(world, id, "b", local: 0, judge: 1);
        await world.Tick();
        var row = Assert.Single(world.Events.ListForPlot(), item => item.Cause == "spot_check_rollback");
        Assert.Equal(2, row.WindowSize);
        Assert.Equal(1, row.MeanMiss);
        Assert.Null(row.PreviousTeacherModel);
    }

    [Fact]
    public async Task EpochEvent_OnSwitchOff_AppendsTeacherResumed()
    {
        using var world = World(size: 2);
        world.AcceptLayout(weightOnResponse0: 1f, bias: 0f);
        await world.Tick();
        world.Epoch.Set(Quiet(takeover: false));
        world.Epoch.Set(Quiet(takeover: false));
        Assert.Single(world.Events.ListForPlot(), row => row.Cause == "switch_off");
    }

    [Fact]
    public async Task EpochEvent_OnTeacherMismatch_AppendsTeacherResumed()
    {
        using var world = World(size: 2);
        var id = world.AcceptLayout(weightOnResponse0: 1f, bias: 0f);
        await world.Tick();
        world.Judge.Set(world.JudgeOptions(model: "teacher-b"));
        await world.Tick();
        var mismatch = Assert.Single(world.Events.ListForPlot(), row => row.Cause == "teacher_mismatch");
        Assert.Null(mismatch.WindowSize);
        Assert.Null(mismatch.MeanMiss);
        Assert.Single(world.Events.ListForPlot(), row => row.Cause == "teacher_model");
        Assert.Null(world.Store.TryReadRollbackMarker(GraderEpochKinds.Code));
        Assert.NotEqual(id, world.Store.TryReadAccepted(GraderEpochKinds.Code)?.ArtifactId);
    }

    [Fact]
    public async Task EpochEvent_OnTeacherModelChange_AppendsTeacherChanged()
    {
        using var world = World(size: 2, takeover: false);
        var id = world.AcceptLayout(weightOnResponse0: 1f, bias: 0f);
        world.Store.WriteRollbackMarker(GraderEpochKinds.Code, new GraderEpochRollbackMarker
        {
            RolledBackArtifactId = "kept", Mae = 0.2, WindowSize = 2, AtUtc = DateTimeOffset.UnixEpoch
        });
        world.Judge.Set(world.JudgeOptions(model: "teacher-b"));
        await world.Tick();
        var row = Assert.Single(world.Events.ListForPlot(), item => item.EpochType == IGraderEpochEventStore.TeacherChanged);
        Assert.Equal("teacher_model", row.Cause);
        Assert.Equal("teacher-a", row.PreviousTeacherModel);
        Assert.Equal("teacher-b", row.TeacherModel);
        Assert.Equal("prompt-a", row.PromptVersion);
        Assert.Equal(1, row.RollbackMarkPresent);
        Assert.Equal("kept", world.Store.TryReadRollbackMarker(GraderEpochKinds.Code)?.RolledBackArtifactId);
        Assert.Equal(id, row.ArtifactId);
    }

    [Fact]
    public async Task EpochEvent_OnPromptVersionChange_AppendsTeacherChanged()
    {
        using var world = World(size: 2, takeover: false);
        world.AcceptLayout(weightOnResponse0: 1f, bias: 0f);
        world.Judge.Set(world.JudgeOptions(prompt: "prompt-b"));
        await world.Tick();
        var row = Assert.Single(world.Events.ListForPlot(), item => item.Cause == "prompt_version");
        Assert.Equal("teacher-a", row.PreviousTeacherModel);
        Assert.Equal("teacher-a", row.TeacherModel);
    }

    [Fact]
    public async Task EpochEvent_TeacherChange_OnceAcrossRestart()
    {
        using var world = World(size: 2, takeover: false);
        world.AcceptLayout(weightOnResponse0: 1f, bias: 0f);
        world.Judge.Set(world.JudgeOptions(model: "teacher-b"));
        await world.Tick();
        await world.Tick();
        await Restart(world).RunTickAsync(CancellationToken.None);
        Assert.Single(world.Events.ListForPlot(), row => row.EpochType == IGraderEpochEventStore.TeacherChanged);
    }

    [Fact]
    public async Task EpochEvent_TeacherChange_NoQuestionOrResponseText()
    {
        using var world = World(size: 2, takeover: false);
        world.AcceptLayout(weightOnResponse0: 1f, bias: 0f);
        world.Judge.Set(world.JudgeOptions(model: "teacher-b"));
        await world.Tick();
        var row = world.Events.ListForPlot().Single(item => item.EpochType == IGraderEpochEventStore.TeacherChanged);
        Assert.Equal("teacher-b", row.TeacherModel);
        Assert.DoesNotContain("question", row.TeacherModel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("response_text", SqliteGraderEpochEventStore.TableSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EpochEvent_OnBgeModelChange_AppendsBgeChanged()
    {
        using var world = World(size: 2, takeover: false);
        world.AcceptLayout(weightOnResponse0: 1f, bias: 0f);
        var previous = world.Bge;
        world.RetargetBge("bge-next");
        await world.Tick();
        var row = Assert.Single(world.Events.ListForPlot(), item => item.EpochType == IGraderEpochEventStore.BgeChanged);
        Assert.Equal("bge_model", row.Cause);
        Assert.Equal(previous, row.PreviousBgeModel);
        Assert.NotEqual(previous, row.BgeModel);
        Assert.Equal(0, row.RollbackMarkPresent);
        Assert.DoesNotContain(world.Events.ListForPlot(), item =>
            item.EpochType is IGraderEpochEventStore.TeacherChanged or IGraderEpochEventStore.FeatureSchemaChanged);
    }

    [Fact]
    public async Task EpochEvent_OnFeatureSchemaChange_AppendsFeatureSchemaChanged()
    {
        using var world = World(size: 2, takeover: false);
        world.AcceptLayout(weightOnResponse0: 1f, bias: 0f, schemaVersion: 2);
        await world.Tick();
        var row = Assert.Single(world.Events.ListForPlot(), item => item.EpochType == IGraderEpochEventStore.FeatureSchemaChanged);
        Assert.Equal(2, row.PreviousFeatureSchemaVersion);
        Assert.Equal(GraderEpochArtifact.SchemaVersion, row.FeatureSchemaVersion);
        Assert.DoesNotContain(world.Events.ListForPlot(), item =>
            item.EpochType is IGraderEpochEventStore.TeacherChanged or IGraderEpochEventStore.BgeChanged);
    }

    [Fact]
    public async Task EpochEvent_BgeAndSchemaTogether_AppendsBoth()
    {
        using var world = World(size: 2, takeover: false);
        world.AcceptLayout(weightOnResponse0: 1f, bias: 0f, schemaVersion: 2);
        world.RetargetBge("bge-next");
        await world.Tick();
        Assert.Contains(world.Events.ListForPlot(), row => row.EpochType == IGraderEpochEventStore.BgeChanged);
        Assert.Contains(world.Events.ListForPlot(), row => row.EpochType == IGraderEpochEventStore.FeatureSchemaChanged);
        Assert.Null(world.Store.TryReadAccepted(GraderEpochKinds.Code));
    }

    [Fact]
    public async Task EpochEvent_BgeChange_OnceAcrossRestart()
    {
        using var world = World(size: 2, takeover: false);
        world.AcceptLayout(weightOnResponse0: 1f, bias: 0f);
        world.RetargetBge("bge-next");
        await world.Tick();
        await world.Tick();
        await Restart(world).RunTickAsync(CancellationToken.None);
        Assert.Single(world.Events.ListForPlot(), row => row.EpochType == IGraderEpochEventStore.BgeChanged);
    }

    [Fact]
    public async Task EpochEvent_FeatureSchemaChange_OnceAcrossRestart()
    {
        using var world = World(size: 2, takeover: false);
        world.AcceptLayout(weightOnResponse0: 1f, bias: 0f, schemaVersion: 2);
        await world.Tick();
        await world.Tick();
        await Restart(world).RunTickAsync(CancellationToken.None);
        Assert.Single(world.Events.ListForPlot(), row => row.EpochType == IGraderEpochEventStore.FeatureSchemaChanged);
    }

    [Fact]
    public async Task EpochEvent_RetirementWhileTeacher_DoesNotAppend()
    {
        using var world = World(size: 2, takeover: false);
        world.AcceptLayout(weightOnResponse0: 1f, bias: 0f);
        world.RetargetBge("bge-next");
        await world.Tick();
        Assert.DoesNotContain(world.Events.ListForPlot(), row => row.EpochType == IGraderEpochEventStore.TeacherResumed);
        Assert.Contains(world.Events.ListForPlot(), row => row.EpochType == IGraderEpochEventStore.BgeChanged);
    }

    [Fact]
    public async Task EpochEvent_CrashAfterRename_NextTickInsertsOnce()
    {
        using var world = World(size: 2);
        var id = world.AcceptLayout(weightOnResponse0: 1f, bias: 0f);
        await world.Tick();
        await world.Tick();
        var rows = world.Events.ListForPlot().Where(row => row.EpochType == IGraderEpochEventStore.LocalTakeover).ToList();
        Assert.Single(rows);
        Assert.Equal(id, rows[0].ArtifactId);
    }

    [Fact]
    public async Task EpochEvent_AppendOnly()
    {
        using var world = World(size: 2);
        world.AcceptLayout(weightOnResponse0: 1f, bias: 0f);
        await world.Tick();
        var first = world.Events.ListForPlot().Single();
        world.Epoch.Set(Quiet(takeover: false));
        world.Epoch.Set(Quiet(takeover: true));
        var again = world.Events.ListForPlot().Single(row => row.Id == first.Id);
        Assert.Equal(first.Cause, again.Cause);
        Assert.Equal(first.ArtifactId, again.ArtifactId);
        Assert.DoesNotContain(typeof(SqliteGraderEpochEventStore).GetMethods(),
            method => method.Name is "Update" or "Delete");
        Assert.DoesNotContain(typeof(SqliteGraderEpochEventStore).GetInterfaces(),
            type => type.Name.Contains("IScoreRetentionStore", StringComparison.Ordinal));
    }

    [Fact]
    public void EpochEvent_NoQuestionOrResponseText()
    {
        Assert.DoesNotContain("prompt_text", SqliteGraderEpochEventStore.TableSql, StringComparison.Ordinal);
        Assert.DoesNotContain("response_text", SqliteGraderEpochEventStore.TableSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Takeover_HighConfidence_SkipsTeacher()
    {
        var graded = await Grade(FindId(drawn: false), cosineFloor: 0.2, question: [1f, 0f]);
        Assert.Equal(GradeSources.Local, graded.Result.GradeSource);
        Assert.Null(graded.Result.JudgeScore);
        Assert.Equal(GradeStatus.Complete, graded.Result.GradeStatus);
        Assert.Equal(0.4, graded.Result.LocalScore!.Value, precision: 5);
        Assert.Equal(0.4, graded.Result.UnifiedScore, precision: 5);
        Assert.Empty(graded.Dispatcher.Calls);
        Assert.Contains(graded.Log, line => line.Contains("case 2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TakeoverOff_MatchesPhase7_TeacherStaysGradeOfRecord()
    {
        var drawn = await Grade(FindId(drawn: true), takeover: false, cosineFloor: 0.2, question: [1f, 0f], teacherScore: 0.7);
        Assert.True(drawn.Result.SpotCheck);
        Assert.Equal(GradeSources.SpotCheck, drawn.Result.GradeSource);
        Assert.Equal(0.7, drawn.Result.JudgeScore);
        Assert.False(drawn.Result.AllowLocalGradeOfRecord);
        Assert.NotEmpty(drawn.Dispatcher.Calls);

        var missed = await Grade(FindId(drawn: false), takeover: false, cosineFloor: 0.2, question: [1f, 0f], teacherScore: 0.7);
        Assert.False(missed.Result.SpotCheck);
        Assert.Equal(GradeSources.Teacher, missed.Result.GradeSource);
    }

    [Fact]
    public async Task LowConfidence_CallsTeacher_SpotCheckFalse()
    {
        var graded = await Grade(FindId(drawn: true), cosineFloor: 0.9, question: [0f, 1f], teacherScore: 0.7);
        Assert.False(graded.Result.SpotCheck);
        Assert.Equal(GradeSources.LowConfidence, graded.Result.GradeSource);
        Assert.Equal(0.7, graded.Result.JudgeScore);
        Assert.Equal(0.4, graded.Result.LocalScore!.Value, precision: 5);
    }

    [Fact]
    public async Task CosineEqualToFloor_IsNotLowConfidence()
    {
        var graded = await Grade(FindId(drawn: false), cosineFloor: 1, question: [1f, 0f]);
        Assert.Empty(graded.Dispatcher.Calls);
        Assert.Equal(GradeSources.Local, graded.Result.GradeSource);
    }

    [Fact]
    public async Task NullFloor_IsNotLowConfidence()
    {
        var graded = await Grade(FindId(drawn: false), cosineFloor: null, question: [1f, 0f]);
        Assert.Null(graded.Result.LocalConfidence);
        Assert.Empty(graded.Dispatcher.Calls);
    }

    [Fact]
    public async Task MissingQuestionVector_IsNotLowConfidence_AndIsNotAZeroVector()
    {
        var graded = await Grade(FindId(drawn: false), cosineFloor: 0.2, question: null);
        Assert.Null(graded.Result.LocalConfidence);
        Assert.Equal(GradeSources.Local, graded.Result.GradeSource);
    }

    [Fact]
    public async Task SpotCheck_TeacherMiss_KeepsLocalScore()
    {
        var graded = await Grade(FindId(drawn: true), cosineFloor: 0.2, question: [1f, 0f], acceptDispatch: false);
        Assert.True(graded.Result.SpotCheck);
        Assert.Equal(GradeStatus.Complete, graded.Result.GradeStatus);
        Assert.Equal(GradeSources.Local, graded.Result.GradeSource);
        Assert.Null(graded.Result.JudgeScore);
    }

    [Fact]
    public async Task LowConfidence_TeacherMiss_IsIncomplete_NotALabel()
    {
        var graded = await Grade(FindId(drawn: false), cosineFloor: 0.9, question: [0f, 1f], willJudge: false, pinMissing: true);
        Assert.Equal(GradeStatus.Incomplete, graded.Result.GradeStatus);
        Assert.Equal(IncompleteReasons.NoJudge, graded.Result.IncompleteReason);
        Assert.Contains(graded.Log, line => line.Contains("case 3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingPin_HighConfidence_StaysComplete()
    {
        var graded = await Grade(FindId(drawn: false), cosineFloor: 0.2, question: [1f, 0f], willJudge: false, pinMissing: true);
        Assert.Equal(GradeStatus.Complete, graded.Result.GradeStatus);
        Assert.Equal(GradeSources.Local, graded.Result.GradeSource);
        Assert.NotEqual(IncompleteReasons.NoJudge, graded.Result.IncompleteReason);
    }

    [Fact]
    public async Task JudgeIsAnswerer_DoesNotTakeOver()
    {
        var graded = await Grade(FindId(drawn: false), answerer: true);
        Assert.Equal(IncompleteReasons.JudgeIsAnswerer, graded.Result.IncompleteReason);
        Assert.Empty(graded.Dispatcher.Calls);
        Assert.Null(graded.Result.LocalScore);
    }

    [Fact]
    public async Task Takeover_SkipsJudge_StillDispatchesPortfolio()
    {
        var completed = await Grade(FindId(drawn: false), cosineFloor: 0.2, question: [1f, 0f],
            portfolio: new PortfolioStub(GraderKeys.CodeJudge), recipe: GradingRecipes.Code,
            dimension: RouterDimension.CodeGeneration, analysis: 0.4, syntaxValid: true, kind: GraderEpochKinds.Code,
            hold: (aggregator, id) => aggregator.CompleteGraderAsync(id, GraderKeys.CodeJudge, 1));
        Assert.DoesNotContain(completed.Dispatcher.Calls[0], key => key == GraderKeys.Judge);
        Assert.Contains(GraderKeys.CodeJudge, completed.Dispatcher.Calls[0]);
        Assert.Equal(GradeStatus.Complete, completed.Result.GradeStatus);
        Assert.Equal(0.60 * completed.Result.LocalScore!.Value + 0.25 * 1 + 0.15 * 0.4, completed.Result.UnifiedScore, precision: 5);

        var abandoned = await Grade(FindId(drawn: false), cosineFloor: 0.2, question: [1f, 0f],
            portfolio: new PortfolioStub(GraderKeys.CodeJudge), recipe: GradingRecipes.Code,
            dimension: RouterDimension.CodeGeneration, analysis: 0.4, syntaxValid: true, kind: GraderEpochKinds.Code,
            hold: (aggregator, id) => aggregator.AbandonGraderAsync(id, GraderKeys.CodeJudge, "codejudge-not-dispatched"),
            dimensionWeights: new DimensionWeightOptions
            {
                Syntax = 0.25, Analysis = 0.15, Judge = 0.50,
                ExtraWeights = new Dictionary<string, double> { [GraderKeys.CodeJudge] = 0.10 }
            });
        Assert.Equal(GradeStatus.Incomplete, abandoned.Result.GradeStatus);
    }

    [Fact]
    public async Task Takeover_CodeRecipe_ReplacesOnlyJudgeAxis()
    {
        var graded = await Grade(FindId(drawn: false), cosineFloor: 0.2, question: [1f, 0f],
            recipe: GradingRecipes.Code, dimension: RouterDimension.CodeGeneration, analysis: 0.4, syntaxValid: true,
            kind: GraderEpochKinds.Code);
        Assert.Equal(0.60 * graded.Result.LocalScore!.Value + 0.25 * 1 + 0.15 * 0.4, graded.Result.UnifiedScore, precision: 5);
        Assert.Null(graded.Result.JudgeScore);
    }

    [Fact]
    public async Task Takeover_Bootstrap_UnifiedScoreIsLocalScore()
    {
        await Grade(FindId(drawn: false), cosineFloor: 0.2, question: [1f, 0f], probe: probe =>
        {
            Assert.Equal(probe.Result.LocalScore!.Value, probe.Result.UnifiedScore, precision: 5);
            var memory = new RouterMemory();
            var observer = new RouterMemoryScoreObserver(
                memory: memory,
                options: Options.Create(new QualityOptions()),
                logger: NullLogger<RouterMemoryScoreObserver>.Instance);
            observer.ObserveAsync(probe.Result).GetAwaiter().GetResult();
            Assert.Empty(memory.GetModelsForDimension("live:"));
        });
    }

    [Fact]
    public async Task Takeover_CodeRecipe_StillUpdatesLiveTally()
    {
        await Grade(FindId(drawn: false), cosineFloor: 0.2, question: [1f, 0f],
            recipe: GradingRecipes.Code, dimension: RouterDimension.CodeGeneration, analysis: 0.4, syntaxValid: true,
            kind: GraderEpochKinds.Code, probe: probe =>
            {
                var memory = new RouterMemory();
                var observer = new RouterMemoryScoreObserver(
                    memory: memory,
                    options: Options.Create(new QualityOptions()),
                    logger: NullLogger<RouterMemoryScoreObserver>.Instance);
                observer.ObserveAsync(probe.Result).GetAwaiter().GetResult();
                var average = memory.GetAverageScore(dimension: "live:code_generation", model: "answer");
                Assert.NotNull(average);
                Assert.Equal(probe.Result.UnifiedScore, average.Value, precision: 5);
            });
    }

    [Fact]
    public async Task LowConfidence_TeacherMiss_QueueFull_AndTimeoutStaysDegraded()
    {
        var queued = await Grade(FindId(drawn: false), cosineFloor: 0.9, question: [0f, 1f], acceptDispatch: false);
        Assert.Equal(GradeStatus.Incomplete, queued.Result.GradeStatus);
        Assert.Equal(IncompleteReasons.QueueFull, queued.Result.IncompleteReason);
        Assert.Contains(queued.Log, line => line.Contains("case 3", StringComparison.Ordinal));

        var timedOut = await Grade(FindId(drawn: false), cosineFloor: 0.9, question: [0f, 1f],
            hold: (aggregator, id) => aggregator.AbandonGraderAsync(id, GraderKeys.Judge, "timeout"));
        Assert.Equal(GradeStatus.Incomplete, timedOut.Result.GradeStatus);
        Assert.Null(timedOut.Result.IncompleteReason);
        Assert.Equal("timeout", timedOut.Result.GraderDegradedReasons[GraderKeys.Judge]);
    }

    [Fact]
    public async Task IngressQueueFull_DoesNotTakeOver()
    {
        var graded = await Grade(FindId(drawn: false), incompleteReason: IncompleteReasons.QueueFull);
        Assert.Null(graded.Result.LocalScore);
        Assert.Empty(graded.Dispatcher.Calls);
        Assert.Equal(0, graded.Embeds);
    }

    [Fact]
    public async Task ResponseVector_EmbeddedOnce()
    {
        await Grade(FindId(drawn: false), cosineFloor: 0.2, question: [1f, 0f], probe: probe =>
        {
            Assert.Equal(1, probe.Embedder.Calls);
            var observer = new ResponseVectorGradeObserver(
                embeddings: probe.Embedder,
                responseText: probe.Text,
                store: probe.Fixture.Vectors,
                logger: NullLogger<ResponseVectorGradeObserver>.Instance);
            observer.ObserveAsync(probe.Result).GetAwaiter().GetResult();
            Assert.Equal(1, probe.Embedder.Calls);
        });
    }

    [Fact]
    public async Task ShadowObserver_DoesNotRescore_WhenTakeoverAlreadyStamped()
    {
        await Grade(FindId(drawn: false), cosineFloor: 0.2, question: [1f, 0f], probe: probe =>
        {
            var decision = probe.Result.LocalScore;
            probe.Fixture.Vectors.UpsertAsync(
                correlationId: probe.Result.RequestCorrelationId,
                embeddingModel: probe.Fixture.Bge,
                vector: [0.9f, 0.9f],
                createdAtUtc: DateTimeOffset.UnixEpoch).GetAwaiter().GetResult();
            var shadow = new ShadowLocalScoreObserver(
                store: probe.Fixture.Store,
                query: probe.Fixture.Query,
                vectors: probe.Fixture.Vectors,
                judge: probe.Fixture.Judge,
                quality: probe.Fixture.Quality,
                embedding: probe.Fixture.Embedding,
                logger: NullLogger<ShadowLocalScoreObserver>.Instance);
            shadow.Apply(probe.Result, GradingRecipes.Bootstrap);
            Assert.Equal(decision, probe.Result.LocalScore);
        });
    }

    [Fact]
    public async Task LocalScore_TakeoverRow_IsNeverTheTrainingLabel()
    {
        using var world = World(size: 2);
        var artifact = world.AcceptLayout(weightOnResponse0: 1f, bias: 0f, kind: GraderEpochKinds.NonCode);
        await world.Add(new GraderEpochPhase7Tests.Row
        {
            Id = "local-only",
            Kind = GraderEpochKinds.NonCode,
            WriteJudge = false,
            LocalScore = 0.4,
            LocalGraderVersion = artifact,
            Response = [0.4f, 0.2f]
        });
        await world.Add(new GraderEpochPhase7Tests.Row
        {
            Id = "spot",
            Kind = GraderEpochKinds.NonCode,
            Judge = 0.8,
            LocalScore = 0.1,
            LocalGraderVersion = artifact,
            SpotCheck = true,
            Response = [0.4f, 0.2f]
        });
        await world.Add(new GraderEpochPhase7Tests.Row
        {
            Id = "spot-miss",
            Kind = GraderEpochKinds.NonCode,
            WriteJudge = false,
            LocalScore = 0.4,
            LocalGraderVersion = artifact,
            SpotCheck = true,
            Response = [0.4f, 0.2f]
        });

        var labels = await world.Query.LoadForKindAsync(GraderEpochKinds.NonCode, CancellationToken.None);
        Assert.DoesNotContain(labels, label => label.CorrelationId == "local-only");
        var spot = Assert.Single(labels);
        Assert.Equal("spot", spot.CorrelationId);
        Assert.Equal(0.8, spot.JudgeScore);
        var pairs = await world.Query.LoadSpotCheckPairsAsync(GraderEpochKinds.NonCode, artifact, CancellationToken.None);
        Assert.DoesNotContain(pairs, pair => pair.CorrelationId == "spot-miss");
        var kept = Assert.Single(pairs, pair => pair.CorrelationId == "spot");
        Assert.Equal(0.8, kept.JudgeScore, precision: 5);
    }

    [Fact]
    public async Task Probation_ConfidentRow_StillCallsTeacher()
    {
        var graded = await Grade(FindId(drawn: false), cosineFloor: 0.2, question: [1f, 0f], teacherScore: 0.7, rollback: true);
        Assert.Equal(GradeSources.Teacher, graded.Result.GradeSource);
        Assert.False(graded.Result.SpotCheck);
        Assert.False(graded.Result.AllowLocalGradeOfRecord);
        Assert.NotEmpty(graded.Dispatcher.Calls);
    }

    private static GraderEpochPhase7Tests.World World(int size, bool enabled = true, bool takeover = true) =>
        GraderEpochPhase7Tests.World.Create(Quiet(size, enabled, takeover));

    private static GraderEpochOptions Quiet(int size = 2, bool enabled = true, bool takeover = true) => new()
    {
        Enabled = enabled,
        TakeoverEnabled = takeover,
        MinimumLabels = 1000,
        HoldoutSize = 6,
        MinimumTrainingRows = 12,
        ReservoirCapacity = 30,
        SpotCheckWindowSize = size,
        SpotCheckRollbackMae = 0.10
    };

    private static async Task AddPair(GraderEpochPhase7Tests.World world, string artifact, string id, double local,
        double judge, bool spot = true, string kind = GraderEpochKinds.Code)
    {
        await world.Add(new GraderEpochPhase7Tests.Row
        {
            Id = id,
            Kind = kind,
            Judge = judge,
            LocalScore = local,
            LocalGraderVersion = artifact,
            SpotCheck = spot,
            CreatedAt = DateTimeOffset.Parse("2026-06-01T00:00:00Z").AddMinutes(id.Length)
        });
    }

    private static GraderEpochHostedService Restart(GraderEpochPhase7Tests.World world) => new(
        logger: new GraderEpochPhase7Tests.CaptureLog().For<GraderEpochHostedService>(),
        options: world.Epoch,
        judge: world.Judge,
        embedding: world.Embedding,
        query: world.Query,
        store: world.Store,
        events: world.Events,
        keys: world.Keys,
        timeProvider: TimeProvider.System);

    private static string FindId(bool drawn)
    {
        for (var i = 0; i < 100_000; i++)
        {
            var id = "corr-" + i.ToString("D5");
            if (SpotCheckDraw.IsDrawn(id, 0.05) == drawn) return id;
        }

        throw new InvalidOperationException("No correlation id matched the draw.");
    }

    private static async Task<Graded> Grade(string id, bool takeover = true, double? cosineFloor = null,
        float[]? question = null, bool willJudge = true, bool pinMissing = false, bool answerer = false,
        double? teacherScore = null, bool acceptDispatch = true, IPortfolioGraderAvailability? portfolio = null,
        string recipe = GradingRecipes.Bootstrap, string dimension = "chat", double? analysis = null,
        bool syntaxValid = false, string kind = GraderEpochKinds.NonCode, string? incompleteReason = null,
        bool rollback = false, DimensionWeightOptions? dimensionWeights = null,
        Func<QualityScoreAggregator, string, Task>? hold = null, Action<GradeProbe>? probe = null)
    {
        using var world = World(size: 50, takeover: takeover);
        if (dimensionWeights is not null)
        {
            world.Quality.Set(new QualityOptions
            {
                WeightsVersion = world.Quality.CurrentValue.WeightsVersion,
                DimensionWeights = new Dictionary<string, DimensionWeightOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    [dimension] = dimensionWeights
                }
            });
        }
        var artifact = world.AcceptLayout(weightOnResponse0: 1f, bias: 0f, kind: kind,
            cosineFloor: cosineFloor, reservoir: question is null ? null : [("neighbor", [1f, 0f])]);
        if (rollback)
        {
            world.Store.WriteRollbackMarker(kind, new GraderEpochRollbackMarker
            {
                RolledBackArtifactId = "previous", Mae = 0.4, WindowSize = 2, AtUtc = DateTimeOffset.UnixEpoch
            });
        }

        var text = new PendingResponseTextCache(Options.Create(world.Judge.CurrentValue));
        text.Set(id, "a prose answer");
        var questions = new PendingValueCache<float[]>(Options.Create(new RoutingOptions()));
        if (question is not null) questions.Set(id, question);
        var dispatcher = new RecordingDispatcher { Accept = acceptDispatch };
        var log = new List<string>();
        var observer = new RecordingObserver();
        var embedder = new FixedEmbedder([0.4f, 0.2f], world.Bge);
        var takeoverService = new LocalGradeTakeover(
            store: world.Store,
            epoch: world.Epoch,
            judge: world.Judge,
            quality: world.Quality,
            embeddingOptions: world.Embedding,
            embeddings: embedder,
            responseText: text,
            questions: questions,
            vectors: world.Vectors,
            logger: NullLogger<LocalGradeTakeover>.Instance);
        var aggregator = new QualityScoreAggregator(
            observer: observer,
            scorer: new QualityScorer(Options.Create(world.Quality.CurrentValue)),
            judgeAvailability: new JudgeStub(willJudge, pinMissing, answerer),
            asyncGraderDispatcher: dispatcher,
            options: Options.Create(world.Quality.CurrentValue),
            logger: new ListLogger<QualityScoreAggregator>(log),
            portfolioGraderAvailability: portfolio,
            localGradeTakeover: takeoverService);
        var result = new QualityResult
        {
            RequestCorrelationId = id,
            Model = "answer",
            Dimension = dimension,
            RecipeId = recipe,
            QuestionPresent = true,
            ResponseLengthChars = 14,
            AnalysisScore = analysis,
            SyntaxValid = syntaxValid,
            SyntaxAuthoritative = syntaxValid,
            SyntaxConfidence = syntaxValid ? 1 : 0,
            IncompleteReason = incompleteReason
        };
        await aggregator.SubmitAsync(result);
        if (hold is not null) await hold(aggregator, id);
        if (teacherScore is not null && dispatcher.Calls.Count > 0 && acceptDispatch)
            await aggregator.CompleteGraderAsync(id, GraderKeys.Judge, teacherScore.Value);

        var written = observer.Written.Last();
        probe?.Invoke(new GradeProbe(Fixture: world, Result: written, Embedder: embedder, Text: text));
        return new Graded(written, dispatcher, log, artifact, embedder.Calls);
    }

    private sealed record Graded(QualityResult Result, RecordingDispatcher Dispatcher, List<string> Log, string Artifact, int Embeds);

    private sealed record GradeProbe(
        GraderEpochPhase7Tests.World Fixture, QualityResult Result, FixedEmbedder Embedder, PendingResponseTextCache Text);

    private sealed class RecordingDispatcher : IAsyncGraderDispatcher
    {
        public List<IReadOnlySet<string>> Calls { get; } = [];

        public bool Accept { get; init; } = true;

        public Task<IReadOnlySet<string>> DispatchAsync(QualityResult result, IReadOnlySet<string> pendingGraderKeys,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(pendingGraderKeys);
            IReadOnlySet<string> accepted = Accept
                ? pendingGraderKeys
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(accepted);
        }
    }

    private sealed class RecordingObserver : IQualityScoreObserver
    {
        public List<QualityResult> Written { get; } = [];

        public Task ObserveAsync(QualityResult result, CancellationToken cancellationToken = default)
        {
            Written.Add(result);
            return Task.CompletedTask;
        }
    }

    private sealed class JudgeStub(bool willJudge, bool pinMissing, bool answerer) : IJudgeAvailability
    {
        public bool WillJudge(QualityResult result) => willJudge && !string.IsNullOrEmpty(result.RequestCorrelationId);

        public bool IsJudgeEnabled() => true;

        public bool IsPinMissing() => pinMissing;

        public bool IsAnswererTheJudge(QualityResult result) => answerer;
    }

    private sealed class FixedEmbedder(float[] vector, string model) : IEmbeddingClient
    {
        public string ModelIdentity => model;

        public int Calls { get; private set; }

        public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new EmbeddingResult(vector, 1));
        }
    }

    private sealed class PortfolioStub(params string[] keys) : IPortfolioGraderAvailability
    {
        public IReadOnlySet<string> DetermineGraderKeys(QualityResult result) =>
            new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ListLogger<T>(List<string> lines) : ILogger<T>
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => lines.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
