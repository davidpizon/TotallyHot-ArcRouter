using System.Collections.ObjectModel;

namespace TotallyHot.ArcRouter.Models;

/// <summary>
/// Represents an immutable routing outcome for a single task.
/// </summary>
public sealed record RoutingDecision
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RoutingDecision"/> record.
    /// </summary>
    /// <param name="selectedModel">The selected model identifier.</param>
    /// <param name="confidence">The confidence score in the range [0, 1].</param>
    /// <param name="rationale">The textual rationale for the decision.</param>
    /// <param name="timestampUtc">The UTC timestamp when the decision was made.</param>
    /// <param name="candidateScores">Optional candidate score map copied into an immutable view.</param>
    /// <param name="isExploratory">
    /// Whether this decision was an epsilon-greedy exploratory pick rather than the policy's normal
    /// choice. See <see cref="IsExploratory"/>.
    /// </param>
    /// <param name="propensity">
    /// The probability, under the policy's own arm-selection distribution, that the arm actually
    /// selected would be chosen. See <see cref="Propensity"/>.
    /// </param>
    /// <exception cref="ArgumentException">Thrown when required string arguments are null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="confidence"/> is outside [0, 1].</exception>
    public RoutingDecision(
        string selectedModel,
        double confidence,
        string rationale,
        DateTimeOffset timestampUtc,
        IReadOnlyDictionary<string, double>? candidateScores = null,
        bool isExploratory = false,
        double propensity = 1.0)
    {
        if (string.IsNullOrWhiteSpace(selectedModel))
        {
            throw new ArgumentException("A selected model is required.", nameof(selectedModel));
        }

        if (confidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(confidence), "Confidence must be between 0 and 1.");
        }

        if (string.IsNullOrWhiteSpace(rationale))
        {
            throw new ArgumentException("A rationale is required.", nameof(rationale));
        }

        SelectedModel = selectedModel;
        Confidence = confidence;
        Rationale = rationale;
        TimestampUtc = timestampUtc;
        IsExploratory = isExploratory;
        Propensity = propensity;

        var copiedScores = candidateScores is null
            ? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, double>(candidateScores, StringComparer.OrdinalIgnoreCase);

        CandidateScores = new ReadOnlyDictionary<string, double>(copiedScores);
    }

    /// <summary>
    /// Gets the selected model identifier.
    /// </summary>
    public string SelectedModel { get; }

    /// <summary>
    /// Gets the confidence score in the range [0, 1].
    /// </summary>
    public double Confidence { get; }

    /// <summary>
    /// Gets the rationale string describing why the model was chosen.
    /// </summary>
    public string Rationale { get; }

    /// <summary>
    /// Gets the timestamp for this decision in UTC.
    /// </summary>
    public DateTimeOffset TimestampUtc { get; }

    /// <summary>
    /// Gets the immutable candidate score map captured with the decision.
    /// </summary>
    public IReadOnlyDictionary<string, double> CandidateScores { get; }

    /// <summary>
    /// Gets whether this decision was an epsilon-greedy exploratory pick
    /// (docs/router/orchestrator-live-path-plan.md M1.2) - a deliberate random sample of a currently-
    /// eligible candidate, rolled instead of the policy's normal choice, so the router keeps sampling
    /// models its current scores disfavor rather than starving them permanently. <see langword="false"/>
    /// for every decision made by a policy with no exploration mechanism, and always <see langword="false"/>
    /// for <see cref="CreateFallback"/> - an all-abstain fallback is already a degraded outcome, and
    /// exploration deliberately does not compound it with a random pick on top.
    /// </summary>
    public bool IsExploratory { get; }

    /// <summary>
    /// Gets the propensity - the probability, under the policy's own arm-selection distribution, that
    /// the arm actually selected (<see cref="SelectedModel"/>) would be chosen. Under epsilon-greedy
    /// exploration with <c>K</c> eligible candidates and exploration rate <c>eps</c>, this is
    /// <c>eps / K</c> when <see cref="IsExploratory"/> is <see langword="true"/>, and
    /// <c>(1 - eps) + eps / K</c> for the greedy arm otherwise. This is the input inverse-propensity
    /// weighting needs to de-bias an off-policy estimate from live traffic
    /// (docs/router/self-organizing-classification-plan.md Phase T1c); it is not itself consumed by any
    /// shipped estimator yet. Defaults to <c>1.0</c> - certain selection - for every policy with no
    /// exploration mechanism, matching <see cref="IsExploratory"/>'s default of <see langword="false"/>.
    /// </summary>
    public double Propensity { get; }

    /// <summary>
    /// Creates a fallback routing decision for the supplied model.
    /// </summary>
    /// <param name="selectedModel">The model to use as the fallback selection.</param>
    /// <returns>A fallback decision instance with zero confidence.</returns>
    public static RoutingDecision CreateFallback(string selectedModel)
    {
        return new RoutingDecision(
            selectedModel,
            confidence: 0,
            rationale: RouterConstants.FallbackReason,
            timestampUtc: DateTimeOffset.UtcNow,
            candidateScores: null);
    }
}

