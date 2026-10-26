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
    /// <param name="keys">The vector key the reservoir is sealed with.</param>
    /// <param name="timeProvider">Clock for <c>createdAtUtc</c>. Defaults to <see cref="TimeProvider.System"/>.</param>
    public GraderEpochHostedService(
        ILogger<GraderEpochHostedService> logger,
        IOptionsMonitor<GraderEpochOptions> options,
        IOptionsMonitor<JudgeOptions> judge,
        IOptionsMonitor<EmbeddingOptions> embedding,
        GraderEpochLabelQuery query,
        GraderEpochArtifactStore store,
        ResponseVectorKeyHolder keys,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(judge);
        ArgumentNullException.ThrowIfNull(embedding);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(keys);
        _logger = logger;
        _options = options;
        _judge = judge;
        _embedding = embedding;
        _query = query;
        _store = store;
        _keys = keys;
        _time = timeProvider ?? TimeProvider.System;
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
    /// A disabled tick logs that the job is off and does not visit the kinds. An enabled tick logs
    /// that the spot-check window is not active even when the fit is skipped.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    internal async Task RunTickAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled)
        {
            _logger.LogInformation("Grader epoch is disabled; this tick will not fit.");
            return;
        }

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
        _logger.LogInformation(
            message: "Grader epoch spot-check window is not active in phase 7 for kind {Kind}.",
            kind);

        var judge = _judge.CurrentValue;
        var embedding = _embedding.CurrentValue;
        var bge = embedding.ResolveModelIdentity();
        if (_store.TryRetireMismatch(kind: kind, teacherModel: judge.ModelName, promptVersion: judge.PromptVersion,
                bgeModelId: bge, featureSchemaVersion: GraderEpochArtifact.SchemaVersion,
                artifactId: out var retiredId, field: out var field))
        {
            _logger.LogInformation(
                message:
                "Grader epoch retired artifact {ArtifactId} for kind {Kind} because {Field} does not match.",
                retiredId,
                kind,
                field);
        }

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

        if (outcome.Passed) _store.CommitAccepted(outcome.Artifact, outcome.Reservoir);
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

    private static string Marker(double? value) =>
        value?.ToString("G17", CultureInfo.InvariantCulture) ?? "null";

    private static string Marker(string? value) => value ?? "null";
}
