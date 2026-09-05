using Microsoft.Data.Sqlite;
using TotallyHot.ArcRouter.Router.Embeddings;

namespace TotallyHot.ArcRouter.CodeRouterBench.Evaluation;

/// <summary>
/// <inheritdoc cref="IRegretHarnessRunner"/>
/// </summary>
/// <remarks>
/// Reproduces exactly the recipe <c>N5ComparisonReportReconciliationTests.Replay_OnRealCorpus_ProducesTheFullComparisonReport</c>
/// proves by hand: load the probing/OOD/ID-test splits, train the standalone <c>logreg</c> baseline, build
/// the OOD kNN index (the one real embedding-client call), build the isolated Orchestrator arm, then
/// replay every baseline plus the Orchestrator arm over both the ID-test and OOD splits.
/// </remarks>
public sealed class RegretHarnessRunner : IRegretHarnessRunner
{
    private readonly BenchmarkDatabase _database;
    private readonly IEmbeddingClient _embeddingClient;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RegretHarnessRunner> _logger;

    /// <summary>Initializes a new instance of the <see cref="RegretHarnessRunner"/> class.</summary>
    /// <param name="database">The CodeRouterBench corpus to read the probing/OOD/ID-test splits from.</param>
    /// <param name="embeddingClient">Embeds the OOD split's task text to build the kNN index and the Orchestrator arm's <c>logreg</c> voter.</param>
    /// <param name="loggerFactory">Creates the Orchestrator arm's voters' and policy's own loggers.</param>
    /// <param name="logger">The logger.</param>
    public RegretHarnessRunner(
        BenchmarkDatabase database,
        IEmbeddingClient embeddingClient,
        ILoggerFactory loggerFactory,
        ILogger<RegretHarnessRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(embeddingClient);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _embeddingClient = embeddingClient;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <inheritdoc/>
    public RegretHarnessRunResult? LastResult { get; private set; }

    /// <inheritdoc/>
    public async Task<RegretHarnessRunResult> RunAsync(
        IProgress<RegretHarnessStage>? stageProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation(
                "Regret harness run requested while another run is already in progress; skipping.");
            return new RegretHarnessRunResult(Kind: RegretHarnessRunResultKind.AlreadyRunning,
                Message: "A run was already in progress.", null, Splits: []);
        }

        try
        {
            var result = await RunCoreAsync(stageProgress: stageProgress, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (result.Kind == RegretHarnessRunResultKind.Completed) LastResult = result;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RegretHarnessRunResult> RunCoreAsync(
        IProgress<RegretHarnessStage>? stageProgress,
        CancellationToken cancellationToken)
    {
        if (!IsCorpusReady())
        {
            const string declinedReason =
                "The CodeRouterBench corpus needs at least one resolved OOD result and at least one " +
                "id_test row - sync it first (Governance -> Benchmark Data, the sync_benchmark_data MCP " +
                "tool, or --sync-benchmark-data).";
            _logger.LogWarning(message: "Regret harness run declined: {Reason}", declinedReason);
            return new RegretHarnessRunResult(RegretHarnessRunResultKind.Declined, Message: declinedReason, null,
                Splits: []);
        }

        stageProgress?.Report(RegretHarnessStage.LoadingCorpus);
        var probingMatrix = DimensionModelScoreMatrix.FromDatabase(database: _database, split: "probing");
        var probingOutcomes = IdSplitRegretTaskOutcomeLoader.Load(database: _database, split: "probing");
        var idTestOutcomes = IdSplitRegretTaskOutcomeLoader.Load(database: _database, split: "id_test");
        var oodOutcomes = OodRegretTaskOutcomeLoader.Load(_database);

        stageProgress?.Report(RegretHarnessStage.TrainingLogReg);
        var logRegArtifact = LogRegTrainer.Train(_database);

        stageProgress?.Report(RegretHarnessStage.BuildingKnnIndex);
        var knnArtifact = await KnnRetrievalIndexBuilder
            .BuildAsync(database: _database, embeddingClient: _embeddingClient, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        stageProgress?.Report(RegretHarnessStage.BuildingOrchestratorArm);
        var orchestratorArm = OrchestratorArmFactory.Build(database: _database, oodOutcomes: oodOutcomes,
            embeddingIndex: knnArtifact, loggerFactory: _loggerFactory);

        stageProgress?.Report(RegretHarnessStage.BuildingReports);
        var idTestReport = RegretComparisonReportBuilder.BuildReport(
            outcomes: idTestOutcomes, probingOutcomes: probingOutcomes, probingMatrix: probingMatrix,
            logRegArtifact: logRegArtifact, knnArtifact: knnArtifact, orchestratorArm: orchestratorArm,
            weights: RewardWeights.Canonical);
        var oodReport = RegretComparisonReportBuilder.BuildReport(
            outcomes: oodOutcomes, probingOutcomes: probingOutcomes, probingMatrix: probingMatrix,
            logRegArtifact: logRegArtifact, knnArtifact: knnArtifact, orchestratorArm: orchestratorArm,
            weights: RewardWeights.Canonical);

        var ranAtUtc = DateTimeOffset.UtcNow;
        var splits = new List<RegretHarnessSplitReport>
        {
            new("ID test", RegretComparisonReportBuilder.FormatMarkdownTable(title: "ID test", rows: idTestReport)),
            new("OOD", RegretComparisonReportBuilder.FormatMarkdownTable(title: "OOD", rows: oodReport))
        };

        var message =
            $"Completed: {idTestOutcomes.Count} ID-test task(s), {oodOutcomes.Count} OOD task(s) replayed.";
        _logger.LogInformation(message: "Regret harness run {Message}", message);

        return new RegretHarnessRunResult(RegretHarnessRunResultKind.Completed, Message: message, RanAtUtc: ranAtUtc,
            Splits: splits);
    }

    /// <summary>
    /// The same corpus-readiness precondition <c>N5ComparisonReportReconciliationTests.CorpusIsReadyForN5</c>
    /// checks before replaying: at least one resolved OOD result and at least one <c>id_test</c> row.
    /// </summary>
    private bool IsCorpusReady()
    {
        if (!File.Exists(_database.DatabasePath)) return false;

        try
        {
            using var connection = _database.OpenConnection();
            using var oodCommand = connection.CreateCommand();
            oodCommand.CommandText = "SELECT COUNT(*) FROM benchmark_ood_results WHERE resolved = 1;";
            if (Convert.ToInt64(oodCommand.ExecuteScalar()) == 0) return false;

            using var idCommand = connection.CreateCommand();
            idCommand.CommandText = "SELECT COUNT(*) FROM benchmark_id_results WHERE split = 'id_test';";
            return Convert.ToInt64(idCommand.ExecuteScalar()) > 0;
        }
        catch (SqliteException)
        {
            return false;
        }
    }
}
