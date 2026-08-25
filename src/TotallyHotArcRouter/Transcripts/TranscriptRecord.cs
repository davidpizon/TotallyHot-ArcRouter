namespace TotallyHot.ArcRouter.Transcripts;

/// <summary>
/// One captured request/response pair, per docs/router/self-organizing-classification-plan.md Phase T1a's
/// schema - written once by <c>ProxyMiddleware</c> with everything known at response time (score left
/// <see langword="null"/>), and later backfilled by <see cref="TranscriptScoreObserver"/> once the
/// verifier's score arrives.
/// </summary>
/// <param name="Id">The store-assigned identity. Zero for a record not yet persisted.</param>
/// <param name="CorrelationId">
/// The same correlation id <c>QualityResult.RequestCorrelationId</c> carries, joining this row to its
/// later-arriving score.
/// </param>
/// <param name="CreatedAtUtc">When this row was written, in UTC.</param>
/// <param name="RequestedModel">The client's literal requested model name.</param>
/// <param name="RoutedModel">The model that actually served the request.</param>
/// <param name="Dimension">
/// The Phase H heuristic dimension label at capture time (<c>RequestClassification.Dimension</c>), or
/// <see langword="null"/> when classification was unavailable.
/// </param>
/// <param name="Difficulty">The Phase H heuristic difficulty label, or <see langword="null"/>.</param>
/// <param name="Language">The Phase H heuristic language label, or <see langword="null"/>.</param>
/// <param name="IsUtility">Whether the request was classified as utility traffic.</param>
/// <param name="PromptText">The newest user message's extracted text, or <see langword="null"/> when unavailable.</param>
/// <param name="ResponseText">The extracted response text, or <see langword="null"/> until the response completes (or extraction fails).</param>
/// <param name="Score">The verifier's observed quality score in [0, 1], or <see langword="null"/> until backfilled.</param>
/// <param name="Cost">The estimated dollar cost of serving this request, or <see langword="null"/> when unknown.</param>
/// <param name="IsExploratory">Whether the routing decision was an epsilon-greedy exploratory pick.</param>
/// <param name="Propensity">The propensity of the model actually chosen, under the policy's own arm-selection distribution.</param>
/// <param name="InputTokens">The request's prompt token count, or <see langword="null"/> when unknown.</param>
/// <param name="OutputTokens">The response's completion token count, or <see langword="null"/> when unknown.</param>
/// <param name="MemoryEntryId">
/// The linked <c>memory_entries</c> row id, once Phase T1d's embedding backfill (out of scope for this
/// pass) links this transcript row to one. Left unused by every phase T1a-c ships; present now so a
/// future backfill needs no further schema migration.
/// </param>
/// <param name="DimBestModel">
/// The model the frozen nine-dimension <c>dim_best</c> voter alone would have chosen, captured at decision
/// time (Phase T4), or <see langword="null"/> when it abstained, no policy was consulted, or the row
/// predates this column.
/// </param>
public sealed record TranscriptRecord(
    long Id,
    string CorrelationId,
    DateTimeOffset CreatedAtUtc,
    string RequestedModel,
    string RoutedModel,
    string? Dimension,
    string? Difficulty,
    string? Language,
    bool IsUtility,
    string? PromptText,
    string? ResponseText,
    double? Score,
    decimal? Cost,
    bool IsExploratory,
    double Propensity,
    int? InputTokens,
    int? OutputTokens,
    long? MemoryEntryId,
    string? DimBestModel = null);
