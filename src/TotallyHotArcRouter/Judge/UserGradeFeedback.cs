using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Quality;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// Pure detectors for counted user signals (issue #304 phase 4): agent switch and thanks.
/// </summary>
public static partial class UserGradeFeedback
{
    /// <summary>
    /// Whether <paramref name="newestUserMessage"/> is a short thanks / "that worked" message.
    /// </summary>
    /// <param name="newestUserMessage">The newest user message text.</param>
    /// <param name="maxChars">Maximum trimmed length; from <see cref="QualityOptions.ThanksMaxChars"/>.</param>
    /// <returns><see langword="true"/> when the message matches the thanks vocabulary.</returns>
    public static bool IsThanks(string? newestUserMessage, int maxChars = 80)
    {
        if (string.IsNullOrWhiteSpace(newestUserMessage)) return false;

        var trimmed = newestUserMessage.Trim();
        if (trimmed.Length > maxChars) return false;

        var normalized = PunctuationStrip().Replace(trimmed.ToLowerInvariant(), string.Empty);
        normalized = WhitespaceCollapse().Replace(normalized, " ").Trim();
        return normalized is "thanks" or "thank you" or "that worked" or "that works";
    }

    /// <summary>
    /// Whether a later request counts as an agent-switch signal against a prior graded answer.
    /// </summary>
    /// <param name="isExplicitClientPick">
    /// True only when the client named the model (substitution reason none) and the request was not an
    /// <c>auto</c> route.
    /// </param>
    /// <param name="isToolChainContinuation">
    /// True when the request continues a tool loop; those are never switches.
    /// </param>
    /// <param name="requestedModel">The model the client named on this request.</param>
    /// <param name="priorAnswerModel">The model that served the earlier graded response.</param>
    /// <returns><see langword="true"/> when the later request is a counted agent switch.</returns>
    public static bool IsAgentSwitch(
        bool isExplicitClientPick,
        bool isToolChainContinuation,
        string? requestedModel,
        string? priorAnswerModel)
    {
        if (!isExplicitClientPick || isToolChainContinuation) return false;
        if (string.IsNullOrWhiteSpace(requestedModel) || string.IsNullOrWhiteSpace(priorAnswerModel))
            return false;
        if (IsAutoModelName(requestedModel)) return false;

        return !string.Equals(requestedModel.Trim(), priorAnswerModel.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a counted signal contradicts the grade of record at <paramref name="unifiedScore"/>.
    /// </summary>
    /// <param name="userSignal"><see cref="UserSignals.AgentSwitch"/> or <see cref="UserSignals.Thanks"/>.</param>
    /// <param name="unifiedScore">The receipt's unified score in [0, 1].</param>
    /// <param name="cutoff">
    /// Contradiction midpoint from <see cref="QualityOptions.ContradictionCutoff"/>.
    /// </param>
    /// <returns><see langword="true"/> when the receipt should become disputed.</returns>
    public static bool ContradictsGrade(string userSignal, double unifiedScore, double cutoff = 0.5)
    {
        if (string.Equals(userSignal, UserSignals.AgentSwitch, StringComparison.Ordinal))
            return unifiedScore >= cutoff;

        if (string.Equals(userSignal, UserSignals.Thanks, StringComparison.Ordinal))
            return unifiedScore < cutoff;

        return false;
    }

    /// <summary>Whether <paramref name="modelName"/> is the auto-route sentinel.</summary>
    public static bool IsAutoModelName(string? modelName) =>
        string.Equals(modelName?.Trim(), "auto", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"[^\p{L}\p{N}\s]+")]
    private static partial Regex PunctuationStrip();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceCollapse();
}

/// <summary>
/// Looks up prior receipts and applies agent-switch / thanks signals (issue #304 phase 4).
/// </summary>
public sealed class UserGradeFeedbackService
{
    private readonly GradeDisputeService _dispute;
    private readonly IGradeReceiptStore _receipts;
    private readonly QuestionHmacHasher _hmac;
    private readonly IOptions<QualityOptions> _qualityOptions;
    private readonly ILogger<UserGradeFeedbackService> _logger;

    /// <summary>Initializes a new instance of the <see cref="UserGradeFeedbackService"/> class.</summary>
    public UserGradeFeedbackService(
        IGradeReceiptStore receipts,
        GradeDisputeService dispute,
        QuestionHmacHasher hmac,
        IOptions<QualityOptions> qualityOptions,
        ILogger<UserGradeFeedbackService> logger)
    {
        ArgumentNullException.ThrowIfNull(receipts);
        ArgumentNullException.ThrowIfNull(dispute);
        ArgumentNullException.ThrowIfNull(hmac);
        ArgumentNullException.ThrowIfNull(qualityOptions);
        ArgumentNullException.ThrowIfNull(logger);
        _receipts = receipts;
        _dispute = dispute;
        _hmac = hmac;
        _qualityOptions = qualityOptions;
        _logger = logger;
    }

    /// <summary>
    /// Evaluates the newest user message of a served turn against prior grades in the same session or
    /// with the same question HMAC. Best-effort: failures never affect the proxy forward.
    /// </summary>
    /// <param name="sessionId">The current session id.</param>
    /// <param name="newestUserMessage">The newest user message text.</param>
    /// <param name="isExplicitClientPick">Whether this turn was an explicit client model pick.</param>
    /// <param name="requestedModel">The model the client named (or was auto-routed onto).</param>
    /// <param name="isToolChainContinuation">Whether this turn continues a tool loop.</param>
    public void TryApply(
        string sessionId,
        string? newestUserMessage,
        bool isExplicitClientPick,
        string? requestedModel,
        bool isToolChainContinuation)
    {
        try
        {
            var options = _qualityOptions.Value;
            if (UserGradeFeedback.IsThanks(newestUserMessage, options.ThanksMaxChars))
            {
                var prior = _receipts.TryGetLatestCompleteInSessionAsync(sessionId).GetAwaiter().GetResult();
                if (prior is null) return;

                var contradicts = UserGradeFeedback.ContradictsGrade(
                    UserSignals.Thanks, prior.UnifiedScore, options.ContradictionCutoff);
                _dispute.ApplySignal(prior.CorrelationId, UserSignals.Thanks, contradicts);
                return;
            }

            if (!isExplicitClientPick || isToolChainContinuation) return;

            var hmac = _hmac.TryCompute(newestUserMessage);
            if (hmac is null) return;

            var sameQuestion = _receipts.TryGetLatestCompleteByQuestionHmacAsync(hmac).GetAwaiter().GetResult();
            if (sameQuestion is null) return;

            if (!UserGradeFeedback.IsAgentSwitch(
                    isExplicitClientPick: true,
                    isToolChainContinuation: false,
                    requestedModel: requestedModel,
                    priorAnswerModel: sameQuestion.AnswerModel ?? sameQuestion.Model))
                return;

            // Same model re-ask is not a signal.
            if (string.Equals(
                    requestedModel?.Trim(),
                    (sameQuestion.AnswerModel ?? sameQuestion.Model).Trim(),
                    StringComparison.OrdinalIgnoreCase))
                return;

            var switchContradicts = UserGradeFeedback.ContradictsGrade(
                UserSignals.AgentSwitch, sameQuestion.UnifiedScore, options.ContradictionCutoff);
            _dispute.ApplySignal(sameQuestion.CorrelationId, UserSignals.AgentSwitch, switchContradicts);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(exception: ex, message: "User grade feedback evaluation failed; the forwarded response was unaffected.");
        }
    }
}
