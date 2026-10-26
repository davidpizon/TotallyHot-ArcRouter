using System.Globalization;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Router;

namespace TotallyHot.ArcRouter.Hosting;

/// <summary>
/// The phase 7 grader epoch (issue #304). A <see cref="PeriodicTimer"/> wakes it; the wake does not
/// force a fit. Each tick visits code and non-code independently, and the loop waits for the tick to
/// finish before the next wait so a fit is not overlapped by the next tick. A candidate that clears
/// the gate is stored as <c>accepted, shadow</c>. The teacher stays the grade of record. Takeover is
/// phase 8.
/// </summary>
public sealed class GraderEpochHostedService : BackgroundService
{
    private readonly IOptionsMonitor<EmbeddingOptions> _embedding;
    private readonly IGraderEpochEventStore _events;
    private readonly GraderEpochArtifactStore _store;
    private readonly IOptionsMonitor<JudgeOptions> _judge;
    private readonly ResponseVectorKeyHolder _keys;
    private readonly ILogger<GraderEpochHostedService> _logger;
    private readonly IOptionsMonitor<GraderEpochOptions> _options;
    private readonly GraderEpochLabelQuery _query;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new instance of the <see cref="GraderEpochHostedService"/> class.</summary>
    /// <param name="logger">Every tick line. Templates are static literals; no question text and no vectors.</param>
    /// <param name="options">The bars, the seed, and whether the job is on.</param>
    /// <param name="judge">The current teacher model and prompt version. A change retires the artifact.</param>
    /// <param name="embedding">The current BGE identity. A change retires the artifact.</param>
    /// <param name="query">The eligibility join.</param>
    /// <param name="store">Accepted and failed artifacts. Rollback clears the pointer and does not promote a file.</param>
    /// <param name="events">Append-only epoch history. Authority rows and identity rows are written here.</param>
    /// <param name="keys">The vector key the reservoir is sealed with.</param>
    /// <param name="timeProvider">Clock for <c>createdAtUtc</c>. Defaults to <see cref="TimeProvider.System"/>.</param>
    public GraderEpochHostedService(
        ILogger<GraderEpochHostedService> logger,
        IOptionsMonitor<GraderEpochOptions> options,
        IOptionsMonitor<JudgeOptions> judge,
        IOptionsMonitor<EmbeddingOptions> embedding,
        GraderEpochLabelQuery query,
        GraderEpochArtifactStore store,
        IGraderEpochEventStore events,
        ResponseVectorKeyHolder keys,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(judge);
        ArgumentNullException.ThrowIfNull(embedding);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(keys);
        _logger = logger;
        _options = options;
        _judge = judge;
        _embedding = embedding;
        _query = query;
        _store = store;
        _events = events;
        _keys = keys;
        _time = timeProvider ?? TimeProvider.System;
        _options.OnChange((current, _) =>
        {
            foreach (var kind in GraderEpochKinds.Both)
                ReconcileAuthority(kind: kind, options: current, bge: _embedding.CurrentValue.ResolveModelIdentity());
        });
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.CurrentValue.CheckIntervalMinutes));
        using var timer = new PeriodicTimer(interval);
        try
        {
            do
            {
                try
                {
                    await RunTickAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(exception: ex,
                        message: "Grader epoch tick threw unexpectedly; continuing.");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Runs one tick for both kinds. Internal so a test can drive it without waiting on the timer.
    /// A disabled tick logs that the fit is off and still visits each kind for retirement and the
    /// spot-check window. It returns before the trainer check, so the fit does not run.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    internal async Task RunTickAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled)
            _logger.LogInformation("Grader epoch is disabled; this tick will not fit.");

        foreach (var kind in GraderEpochKinds.Both)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await RunKindAsync(kind: kind, options: options, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _store.DeleteTemps(kind);
                _logger.LogError(exception: ex,
                    message: "Grader epoch fit for kind {Kind} threw unexpectedly; continuing.",
                    kind);
            }
        }
    }

    private async Task RunKindAsync(string kind, GraderEpochOptions options, CancellationToken cancellationToken)
    {
        var judge = _judge.CurrentValue;
        var embedding = _embedding.CurrentValue;
        var bge = embedding.ResolveModelIdentity();
        ReconcileAuthority(kind: kind, options: options, bge: bge);
        RecordIdentityAndRetire(kind: kind, options: options, judge: judge, bge: bge);
        await RunWindowAsync(kind: kind, options: options, bge: bge, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!options.Enabled) return;

        if (!string.Equals(options.Trainer, GraderEpochTrainer.SdcaName, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                message: "Grader epoch skipped {Kind}: trainer {Trainer} is not supported.",
                kind,
                options.Trainer);
            return;
        }

        var labels = await _query.LoadForKindAsync(kind, cancellationToken).ConfigureAwait(false);
        if (labels.Count < options.MinimumLabels)
        {
            _logger.LogInformation(
                message:
                "Grader epoch skipped {Kind}: eligible labels {EligibleCount} are below the minimum {MinimumLabels}.",
                kind,
                labels.Count,
                options.MinimumLabels);
            return;
        }

        var split = GraderEpochFit.Split(labels, options.HoldoutSize);
        if (split.Holdout.Count < options.HoldoutSize)
        {
            _logger.LogInformation(
                message:
                "Grader epoch skipped {Kind}: holdout {HoldoutCount} is shorter than {HoldoutSize}. Eligible labels {EligibleCount}.",
                kind,
                split.Holdout.Count,
                options.HoldoutSize,
                labels.Count);
            return;
        }

        if (split.Training.Count < options.MinimumTrainingRows)
        {
            _logger.LogInformation(
                message:
                "Grader epoch skipped {Kind}: training rows {TrainingCount} are below the minimum {MinimumTrainingRows} after session exclusion. Eligible labels {EligibleCount}, holdout {HoldoutCount}, session-excluded {SessionExcludedCount}.",
                kind,
                split.Training.Count,
                options.MinimumTrainingRows,
                labels.Count,
                split.Holdout.Count,
                split.SessionExcluded);
            return;
        }

        var live = _store.TryReadAccepted(kind);
        var outcome = GraderEpochFit.Run(
            kind: kind,
            eligibleCount: labels.Count,
            split: split,
            options: options,
            teacherModel: judge.ModelName,
            promptVersion: judge.PromptVersion,
            bgeModelId: bge,
            dimension: embedding.EmbeddingDimension,
            live: live,
            key: _keys.GetOrCreate(),
            createdAtUtc: _time.GetUtcNow());

        if (outcome.Passed)
        {
            var takeover = TakeoverRow(kind: kind, options: options, artifact: outcome.Artifact, cause: "gate_pass",
                windowSize: null, meanMiss: null);
            if (takeover is null) _store.CommitAccepted(outcome.Artifact, outcome.Reservoir);
            else _store.CommitAcceptedWithEvent(outcome.Artifact, outcome.Reservoir, takeover);
        }
        else _store.CommitFailed(artifact: outcome.Artifact, reservoir: outcome.Reservoir,
            retention: options.FailedCandidateRetention);

        _logger.LogInformation(
            message: "Grader epoch length correlation for kind {Kind} artifact {ArtifactId} is {LengthCorrelation}.",
            kind,
            outcome.Artifact.ArtifactId,
            Marker(outcome.Artifact.LengthCorrelation));

        if (outcome.Passed)
        {
            _logger.LogInformation(
                message:
                "Grader epoch accepted artifact {NewArtifactId} for kind {Kind} in shadow. Previous artifact {PreviousArtifactId}. Holdout MAE {HoldoutMae}, Spearman {Spearman}, eligible {EligibleCount}, holdout {HoldoutCount}, training {TrainingCount}, session-excluded {SessionExcludedCount}, bootstrap low {BootstrapLow}, bootstrap high {BootstrapHigh}. Status {Status}.",
                outcome.Artifact.ArtifactId,
                kind,
                Marker(outcome.Artifact.PreviousArtifactId),
                Marker(outcome.Artifact.HoldoutMae),
                Marker(outcome.Artifact.Spearman),
                outcome.Artifact.EligibleCount,
                outcome.Artifact.HoldoutCount,
                outcome.Artifact.TrainingCount,
                outcome.Artifact.SessionExcludedCount,
                Marker(outcome.Artifact.BootstrapLow),
                Marker(outcome.Artifact.BootstrapHigh),
                GraderEpochArtifact.AcceptedShadowStatus);
            return;
        }

        _logger.LogInformation(
            message:
            "Grader epoch gate failed for kind {Kind}. Holdout MAE {HoldoutMae}, Spearman {Spearman}, eligible {EligibleCount}, holdout {HoldoutCount}, training {TrainingCount}, session-excluded {SessionExcludedCount}, bootstrap low {BootstrapLow}, bootstrap high {BootstrapHigh}. The accepted artifact was left unchanged.",
            kind,
            Marker(outcome.Artifact.HoldoutMae),
            Marker(outcome.Artifact.Spearman),
            outcome.Artifact.EligibleCount,
            outcome.Artifact.HoldoutCount,
            outcome.Artifact.TrainingCount,
            outcome.Artifact.SessionExcludedCount,
            Marker(outcome.Artifact.BootstrapLow),
            Marker(outcome.Artifact.BootstrapHigh));
    }

    private void RecordIdentityAndRetire(string kind, GraderEpochOptions options, JudgeOptions judge, string bge)
    {
        var artifact = _store.TryReadAccepted(kind);
        if (artifact is null) return;

        var mark = _store.TryReadRollbackMarker(kind) is not null ? 1 : 0;
        var wasLocal = options.TakeoverEnabled && mark == 0 && AuthorityIsThisArtifact(kind, artifact.ArtifactId);
        var rows = new List<GraderEpochEventRow>();
        var teacherDiffers = !string.Equals(artifact.TeacherModel, judge.ModelName, StringComparison.Ordinal);
        var promptDiffers = !string.Equals(artifact.TeacherPromptVersion, judge.PromptVersion, StringComparison.Ordinal);
        if (teacherDiffers || promptDiffers)
        {
            var latest = _events.LatestOfType(kind, IGraderEpochEventStore.TeacherChanged);
            var already = latest is not null
                          && string.Equals(latest.TeacherModel, judge.ModelName, StringComparison.Ordinal)
                          && string.Equals(latest.PromptVersion, judge.PromptVersion, StringComparison.Ordinal);
            if (!already)
            {
                rows.Add(Identity(kind: kind, artifact: artifact, epochType: IGraderEpochEventStore.TeacherChanged,
                    cause: teacherDiffers ? "teacher_model" : "prompt_version", mark: mark,
                    previousTeacher: artifact.TeacherModel, teacher: judge.ModelName,
                    previousPrompt: artifact.TeacherPromptVersion, prompt: judge.PromptVersion));
            }
        }

        if (!string.Equals(artifact.BgeModelId, bge, StringComparison.Ordinal))
        {
            var latest = _events.LatestOfType(kind, IGraderEpochEventStore.BgeChanged);
            if (latest is null || !string.Equals(latest.BgeModel, bge, StringComparison.Ordinal))
            {
                rows.Add(Identity(kind: kind, artifact: artifact, epochType: IGraderEpochEventStore.BgeChanged,
                    cause: "bge_model", mark: mark, previousBge: artifact.BgeModelId, bge: bge));
            }
        }

        if (artifact.FeatureSchemaVersion != GraderEpochArtifact.SchemaVersion)
        {
            var latest = _events.LatestOfType(kind, IGraderEpochEventStore.FeatureSchemaChanged);
            if (latest?.FeatureSchemaVersion != GraderEpochArtifact.SchemaVersion)
            {
                rows.Add(Identity(kind: kind, artifact: artifact,
                    epochType: IGraderEpochEventStore.FeatureSchemaChanged, cause: "feature_schema", mark: mark,
                    previousSchema: artifact.FeatureSchemaVersion, schema: GraderEpochArtifact.SchemaVersion));
            }
        }

        _store.CommitIdentity(rows);
        if (!_store.TryRetireMismatch(kind: kind, teacherModel: judge.ModelName, promptVersion: judge.PromptVersion,
                bgeModelId: bge, featureSchemaVersion: GraderEpochArtifact.SchemaVersion,
                artifactId: out var retiredId, field: out var field))
            return;

        _logger.LogInformation(
            message: "Grader epoch retired artifact {ArtifactId} for kind {Kind} because {Field} does not match.",
            retiredId,
            kind,
            field);
        if (!wasLocal) return;
        _store.AppendAuthority(AuthorityRow(kind: kind, epochType: IGraderEpochEventStore.TeacherResumed,
            cause: "teacher_mismatch", artifactId: artifact.ArtifactId, previousArtifactId: null,
            windowSize: null, meanMiss: null));
    }

    private async Task RunWindowAsync(string kind, GraderEpochOptions options, string bge, CancellationToken cancellationToken)
    {
        var judge = _judge.CurrentValue;
        var artifact = _store.TryReadAccepted(kind);
        var matches = artifact is not null
                      && artifact.MatchesTeacher(teacherModel: judge.ModelName, promptVersion: judge.PromptVersion, bgeModelId: bge)
                      && artifact.FeatureSchemaVersion == GraderEpochArtifact.SchemaVersion;
        if (!matches || artifact is null)
        {
            LogWindow(kind: kind, artifactId: null, count: 0, mae: null, authority: "teacher");
            return;
        }

        var pairs = (await _query.LoadSpotCheckPairsAsync(kind, artifact.ArtifactId, cancellationToken).ConfigureAwait(false))
            .OrderByDescending(pair => pair.CreatedAtUtc)
            .ThenBy(pair => pair.CorrelationId, StringComparer.Ordinal)
            .Take(options.SpotCheckWindowSize)
            .ToList();
        var mae = pairs.Count == 0
            ? null
            : RankCorrelation.MeanAbsoluteDifference(
                pairs.Select(pair => pair.LocalScore).ToList(),
                pairs.Select(pair => pair.JudgeScore).ToList());
        var mark = _store.TryReadRollbackMarker(kind) is not null;
        var authority = options.TakeoverEnabled && !mark ? "local" : "teacher";
        LogWindow(kind: kind, artifactId: artifact.ArtifactId, count: pairs.Count, mae: mae, authority: authority);

        if (pairs.Count < options.SpotCheckWindowSize || mae is null) return;
        if (mae > options.SpotCheckRollbackMae)
        {
            _logger.LogInformation(
                message: "Grader epoch spot-check MAE {Mae} for kind {Kind} artifact {ArtifactId} is above the bar {Bar}.",
                mae.Value.ToString("G17", CultureInfo.InvariantCulture),
                kind,
                artifact.ArtifactId,
                options.SpotCheckRollbackMae.ToString("G17", CultureInfo.InvariantCulture));
            var row = authority == "local"
                ? AuthorityRow(kind: kind, epochType: IGraderEpochEventStore.TeacherResumed, cause: "spot_check_rollback",
                    artifactId: artifact.ArtifactId, previousArtifactId: null, windowSize: pairs.Count, meanMiss: mae)
                : null;
            _store.RollbackForSpotCheck(kind, new GraderEpochRollbackMarker
            {
                RolledBackArtifactId = artifact.ArtifactId,
                Mae = mae.Value,
                WindowSize = pairs.Count,
                AtUtc = _time.GetUtcNow()
            }, row);
            return;
        }

        if (!mark || mae > options.SpotCheckRollbackMae) return;
        _store.ClearRollbackMarker(kind);
        _logger.LogInformation(
            message: "Grader epoch audition cleared the rollback marker for kind {Kind} artifact {ArtifactId} mae {Mae}.",
            kind,
            artifact.ArtifactId,
            mae.Value.ToString("G17", CultureInfo.InvariantCulture));
        if (!options.TakeoverEnabled) return;
        _store.AppendAuthority(AuthorityRow(kind: kind, epochType: IGraderEpochEventStore.LocalTakeover,
            cause: "live_check_pass", artifactId: artifact.ArtifactId, previousArtifactId: null,
            windowSize: pairs.Count, meanMiss: mae));
    }

    private void ReconcileAuthority(string kind, GraderEpochOptions options, string bge)
    {
        var artifact = _store.TryReadAccepted(kind);
        var mismatched = artifact is not null && !ArtifactMatches(artifact, bge);
        if (mismatched) return;

        var local = artifact is not null && options.TakeoverEnabled && _store.TryReadRollbackMarker(kind) is null;
        var latest = _events.LatestAuthority(kind);
        var matches = latest is null
            ? !local
            : local
                ? latest.EpochType == IGraderEpochEventStore.LocalTakeover && latest.ArtifactId == artifact!.ArtifactId
                : latest.EpochType == IGraderEpochEventStore.TeacherResumed;
        if (matches) return;

        if (local)
        {
            var cause = latest?.EpochType == IGraderEpochEventStore.TeacherResumed && latest.ArtifactId == artifact!.ArtifactId
                ? "switch_on"
                : "gate_pass";
            _store.AppendAuthority(AuthorityRow(kind: kind, epochType: IGraderEpochEventStore.LocalTakeover, cause: cause,
                artifactId: artifact!.ArtifactId, previousArtifactId: latest?.ArtifactId, windowSize: null, meanMiss: null));
            return;
        }

        var resumeCause = !options.TakeoverEnabled
            ? "switch_off"
            : _store.TryReadRollbackMarker(kind) is not null ? "spot_check_rollback" : "teacher_mismatch";
        _store.AppendAuthority(AuthorityRow(kind: kind, epochType: IGraderEpochEventStore.TeacherResumed, cause: resumeCause,
            artifactId: latest?.ArtifactId, previousArtifactId: null, windowSize: null, meanMiss: null));
    }

    private bool ArtifactMatches(GraderEpochArtifact artifact, string bge)
    {
        var judge = _judge.CurrentValue;
        return artifact.MatchesTeacher(teacherModel: judge.ModelName, promptVersion: judge.PromptVersion, bgeModelId: bge)
               && artifact.FeatureSchemaVersion == GraderEpochArtifact.SchemaVersion;
    }

    private bool AuthorityIsThisArtifact(string kind, string artifactId)
    {
        var latest = _events.LatestAuthority(kind);
        return latest?.EpochType == IGraderEpochEventStore.LocalTakeover && latest.ArtifactId == artifactId;
    }

    private GraderEpochEventRow? TakeoverRow(string kind, GraderEpochOptions options, GraderEpochArtifact artifact,
        string cause, int? windowSize, double? meanMiss)
    {
        if (!options.TakeoverEnabled || _store.TryReadRollbackMarker(kind) is not null) return null;
        return AuthorityRow(kind: kind, epochType: IGraderEpochEventStore.LocalTakeover, cause: cause,
            artifactId: artifact.ArtifactId, previousArtifactId: artifact.PreviousArtifactId,
            windowSize: windowSize, meanMiss: meanMiss);
    }

    private GraderEpochEventRow AuthorityRow(string kind, string epochType, string cause, string? artifactId,
        string? previousArtifactId, int? windowSize, double? meanMiss) => new()
    {
        AtUtc = _time.GetUtcNow().ToString("O"),
        Kind = kind,
        EpochType = epochType,
        Cause = cause,
        ArtifactId = artifactId,
        PreviousArtifactId = previousArtifactId,
        WindowSize = windowSize,
        MeanMiss = meanMiss
    };

    private GraderEpochEventRow Identity(string kind, GraderEpochArtifact artifact, string epochType, string cause,
        int mark, string? previousTeacher = null, string? teacher = null, string? previousPrompt = null,
        string? prompt = null, string? previousBge = null, string? bge = null, int? previousSchema = null,
        int? schema = null) => new()
    {
        AtUtc = _time.GetUtcNow().ToString("O"),
        Kind = kind,
        EpochType = epochType,
        Cause = cause,
        ArtifactId = artifact.ArtifactId,
        RollbackMarkPresent = mark,
        PreviousTeacherModel = previousTeacher,
        TeacherModel = teacher,
        PreviousPromptVersion = previousPrompt,
        PromptVersion = prompt,
        PreviousBgeModel = previousBge,
        BgeModel = bge,
        PreviousFeatureSchemaVersion = previousSchema,
        FeatureSchemaVersion = schema
    };

    private void LogWindow(string kind, string? artifactId, int count, double? mae, string authority)
    {
        _logger.LogInformation(
            message: "Grader epoch spot-check window for kind {Kind} artifact {ArtifactId} count {Count} mae {Mae} authority {Authority}.",
            kind,
            Marker(artifactId),
            count,
            mae?.ToString("G17", CultureInfo.InvariantCulture) ?? "null",
            authority);
    }

    private static string Marker(double? value) =>
        value?.ToString("G17", CultureInfo.InvariantCulture) ?? "null";

    private static string Marker(string? value) => value ?? "null";
}
