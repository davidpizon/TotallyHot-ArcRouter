using System.Reflection;
using Microsoft.Extensions.Logging;
using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// The <c>logreg</c> voter (PLAN.md Phase L): TF-IDF features over the task text, scored against a
/// checked-in one-vs-rest logistic-regression model (<see cref="LogRegModelArtifact"/>), argmax restricted
/// to <see cref="VotingContext.Candidates"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Data-availability caveat (see PLAN.md Phase L's deferral note):</b> the checked-in
/// <c>CodeRouterBench/Resources/logreg_voter_model.json</c> is, as of this phase, a small hand-built
/// placeholder rather than a real training run - the CodeRouterBench corpus is synced on demand
/// (<c>data/README.md</c>) and was not present in the environment this phase was implemented in. It is
/// marked <see cref="LogRegModelArtifact.IsPlaceholder"/> and exercises this class's tokenize → TF-IDF →
/// score → argmax → restrict-to-candidates → abstain-without-text mechanics deterministically, but its
/// weights carry no real predictive signal. <see cref="CodeRouterBench.LogRegTrainer"/> is the documented,
/// reproducible training step - run it against a synced <c>coderouterbench.db</c> and replace the checked-in
/// JSON with its output to ship a real model.
/// </para>
/// </remarks>
public sealed class LogRegVoter : IRoutingVoter
{
    private const string EmbeddedResourceName = "TotallyHot.ArcRouter.CodeRouterBench.Resources.logreg_voter_model.json";

    private static readonly Lazy<LogRegModelArtifact> EmbeddedModel = new(LoadEmbeddedModel);

    private readonly ILogger<LogRegVoter> _logger;
    private readonly LogRegModelArtifact _model;
    private readonly IReadOnlyDictionary<string, int> _vocabularyIndex;

    /// <summary>
    /// Initializes a new instance of the <see cref="LogRegVoter"/> class using the checked-in embedded
    /// model artifact.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public LogRegVoter(ILogger<LogRegVoter> logger)
        : this(logger, EmbeddedModel.Value)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LogRegVoter"/> class with an explicit model artifact -
    /// the seam tests use to exercise this voter against a small, deterministic model without depending on
    /// the checked-in resource.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="model">The model artifact to score against.</param>
    public LogRegVoter(ILogger<LogRegVoter> logger, LogRegModelArtifact model)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(model);

        _logger = logger;
        _model = model;

        var index = new Dictionary<string, int>(model.Vocabulary.Count, StringComparer.Ordinal);
        for (var i = 0; i < model.Vocabulary.Count; i++)
        {
            index[model.Vocabulary[i]] = i;
        }

        _vocabularyIndex = index;

        if (model.IsPlaceholder)
        {
            _logger.LogInformation(
                "logreg voter loaded a placeholder model ({TrainedFrom}); scores carry no real predictive signal.",
                model.TrainedFrom);
        }
    }

    /// <inheritdoc />
    public string Name => VoterNames.LogReg;

    /// <inheritdoc />
    public Task<VoterVote> VoteAsync(VotingContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(context.TaskText))
        {
            return Task.FromResult(VoterVote.Abstain(Name));
        }

        var tfidf = ComputeTfIdf(context.TaskText);

        var scored = new List<(string Model, double Score)>();
        foreach (var candidate in context.Candidates)
        {
            var key = ModelNameCanonicalizer.Canonicalize(candidate.ModelName);
            if (!_model.ClassWeights.TryGetValue(key, out var weights))
            {
                continue;
            }

            var score = weights[0]; // bias
            foreach (var (index, value) in tfidf)
            {
                score += weights[index + 1] * value;
            }

            scored.Add((candidate.ModelName, score));
        }

        if (scored.Count == 0)
        {
            return Task.FromResult(VoterVote.Abstain(Name));
        }

        // Softmax over the restricted candidate scores turns raw logits into a [0, 1] confidence without
        // changing the argmax - softmax is monotonic in its inputs.
        var max = scored.Max(s => s.Score);
        var expScores = scored.Select(s => Math.Exp(s.Score - max)).ToList();
        var sumExp = expScores.Sum();

        var bestIndex = 0;
        for (var i = 1; i < scored.Count; i++)
        {
            if (expScores[i] > expScores[bestIndex])
            {
                bestIndex = i;
            }
        }

        var confidence = sumExp > 0 ? expScores[bestIndex] / sumExp : 1d / scored.Count;
        return Task.FromResult(new VoterVote(Name, scored[bestIndex].Model, Math.Clamp(confidence, 0d, 1d)));
    }

    /// <summary>
    /// Tokenizes <paramref name="text"/> and computes a sparse TF-IDF vector restricted to vocabulary
    /// terms actually present - the same sparse representation <see cref="CodeRouterBench.LogRegTrainer"/>
    /// trains against, since a task's text touches only a small fraction of a low-thousands vocabulary.
    /// </summary>
    private IReadOnlyList<(int Index, double Value)> ComputeTfIdf(string text)
    {
        var tokens = LogRegTextTokenizer.Tokenize(text);
        if (tokens.Count == 0)
        {
            return [];
        }

        var counts = new Dictionary<int, int>();
        foreach (var token in tokens)
        {
            if (_vocabularyIndex.TryGetValue(token, out var index))
            {
                counts[index] = counts.GetValueOrDefault(index) + 1;
            }
        }

        var result = new List<(int, double)>(counts.Count);
        foreach (var (index, count) in counts)
        {
            var termFrequency = (double)count / tokens.Count;
            result.Add((index, termFrequency * _model.InverseDocumentFrequency[index]));
        }

        return result;
    }

    /// <summary>Loads and validates the checked-in model artifact embedded as a build resource.</summary>
    /// <exception cref="InvalidOperationException">The embedded resource is missing.</exception>
    private static LogRegModelArtifact LoadEmbeddedModel()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded logreg voter model resource '{EmbeddedResourceName}' was not found in {assembly.FullName}.");

        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        return LogRegModelArtifactSerializer.Deserialize(json);
    }
}
