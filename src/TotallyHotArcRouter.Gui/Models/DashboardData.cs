using TotallyHot.ArcRouter.Gui.Charts;

namespace TotallyHot.ArcRouter.Gui.Models;

/// <summary>Severity of a single routing-decision log step.</summary>
public enum StepStatus
{
    /// <summary>The step succeeded normally.</summary>
    Ok,

    /// <summary>The step completed but flags something worth the user's attention.</summary>
    Warn,

    /// <summary>Purely informational step; no success/failure connotation.</summary>
    Info,
}

/// <summary>One step in the routing decision log shown in the Live Stream inspector.</summary>
public sealed record RoutingStep(StepStatus Status, string Message);

/// <summary>A single routing decision shown in the Live Stream tab.</summary>
public sealed record RoutingEntry(
    string Id,
    string SessionId,
    string TraceId,
    string Agent,
    string Model,
    bool IsFallback,
    int PromptTokens,
    int CompletionTokens,
    decimal ActualCost,
    decimal WorstCaseCost,
    decimal SavingsAmount,
    decimal SavingsPercent,
    string Timestamp,
    IReadOnlyList<RoutingStep> RoutingSteps);

/// <summary>A point on the cumulative savings time series.</summary>
public sealed record CostDataPoint(string Time, decimal Cumulative);

/// <summary>Cost-reduction percentage and absolute savings for one agent.</summary>
public sealed record AgentRoi(string Agent, decimal Reduction, decimal Savings);

/// <summary>Prompt/completion token volume for one time slot.</summary>
public sealed record TokenBucket(string Slot, decimal Prompt, decimal Completion);

/// <summary>Market-share percentage (and display color) for one model.</summary>
public sealed record ModelShare(string Model, decimal Value, string Color);

/// <summary>A single turn (one multi-step agentic workflow) within a conversation.</summary>
public sealed record ConversationTurn(
    string Id,
    string Agent,
    string Model,
    int TurnNumber,
    int PromptTokens,
    int CompletionTokens,
    decimal RoutingRoi,
    decimal TotalCost,
    int ToolExecutionSteps,
    decimal CacheHitRate,
    int TimeToFirstTokenMs,
    decimal ContextBufferPercent,
    string Timestamp,
    IReadOnlyList<RoutingStep> RoutingSteps,
    string? RequestSummary = null,
    string? ResponseSummary = null,
    bool IsFallback = false,
    // Real UTC instant of the turn (the display `Timestamp` above is a lossy "HH:mm:ss" string).
    // Used by the Cost Analytics tab to bucket turns onto a time axis. Optional/defaulted so the
    // hand-written mock turns below and any older call sites keep compiling; live turns get the real
    // value from LiveConversationMapper.
    DateTimeOffset TimestampUtc = default,
    // How TotalCost was arrived at (a Telemetry.CostConfidence name, e.g. "Catalog", "Unknown"), or null
    // for a mock turn with no confidence concept. Backs the turn card's cost-stat confidence indicator
    // (docs/router/token-tracking-implementation-plan.md Phase 3, §5.6).
    string? CostConfidence = null,
    // The client's literal requested model, the model that actually served, and why they differ (a
    // Telemetry.RoutingSubstitutionReason name), or null for a mock turn with no live-routing concept.
    // Plumbed through by Phase M2 (docs/router/orchestrator-live-path-plan.md §M2.2) and rendered by
    // Phase M3.1: LiveConversationMapper.BuildRoutingSteps turns a visible reason (anything but None or
    // AutoSelect) into the Live Stream inspector's substitution warning step, and TurnCard extends its
    // fallback styling/accessible label to the same condition.
    string? RequestedModel = null,
    string? RoutedModel = null,
    string? SubstitutionReason = null);

/// <summary>A conversation (session) whose turns are shown in the Live Stream tab.</summary>
/// <param name="Id">The session id.</param>
/// <param name="Title">The card's display title (marks untracked/synthesized sessions distinctly).</param>
/// <param name="FirstTimestamp">The first turn's display timestamp.</param>
/// <param name="LastTimestamp">The most recent turn's display timestamp.</param>
/// <param name="TotalCost">The sum of every turn's known cost.</param>
/// <param name="TotalPromptTokens">The sum of every turn's prompt tokens.</param>
/// <param name="TotalCompletionTokens">The sum of every turn's completion tokens.</param>
/// <param name="HasFallbackTurns">Whether any turn was served by fallback routing.</param>
/// <param name="Turns">This conversation's turns, in chronological order.</param>
/// <param name="UnpricedTurns">
/// How many of <paramref name="Turns"/> had no cost at all, and so contributed nothing to
/// <paramref name="TotalCost"/> - a non-zero value means the total is a floor, not a complete sum (§5.6).
/// </param>
/// <param name="IsUsedForTraining">
/// Whether any turn's persisted transcript was folded into the live-learning corpus
/// (docs/router/sessions-tab-training-data-plan.md Phase 2) - the Sessions tab's "used for live training"
/// badge. Always <see langword="false"/> for a session sourced only from the live telemetry stream
/// (<see cref="TotallyHot.ArcRouter.Gui.Services.LiveConversationMapper"/>), since the linkage this flag
/// reports is only known once a transcript has been persisted and its embedding backfilled.
/// </param>
public sealed record Conversation(
    string Id,
    string Title,
    string FirstTimestamp,
    string LastTimestamp,
    decimal TotalCost,
    int TotalPromptTokens,
    int TotalCompletionTokens,
    bool HasFallbackTurns,
    IReadOnlyList<ConversationTurn> Turns,
    int UnpricedTurns = 0,
    bool IsUsedForTraining = false);

/// <summary>
/// Hard-coded mock data for the dashboard. The dashboard is not yet wired up to the live TotallyHot.ArcRouter
/// proxy; replacing this class with real telemetry is the intended integration seam.
/// </summary>
public static class MockData
{
    /// <summary>Mock conversation history for the Console tab.</summary>
    public static readonly IReadOnlyList<Conversation> Conversations =
    [
        new(
            Id: "sess-001",
            Title: "Code Review Analysis - PR #4521",
            FirstTimestamp: "14:15:32",
            LastTimestamp: "14:22:18",
            TotalCost: 0.045230m,
            TotalPromptTokens: 15456,
            TotalCompletionTokens: 3894,
            HasFallbackTurns: false,
            Turns:
            [
                new(
                    Id: "sess-001-t1",
                    Agent: "Code Review Bot",
                    Model: "claude-3-haiku",
                    TurnNumber: 1,
                    PromptTokens: 2104,
                    CompletionTokens: 891,
                    RoutingRoi: 85.01m,
                    TotalCost: 0.006310m,
                    ToolExecutionSteps: 2,
                    CacheHitRate: 0m,
                    TimeToFirstTokenMs: 245,
                    ContextBufferPercent: 26.2m,
                    Timestamp: "14:15:32",
                    RoutingSteps:
                    [
                        new(StepStatus.Ok, "Code diff parsed (214 changed lines)"),
                        new(StepStatus.Ok, "Anthropic budget nominal; claude-3-haiku selected"),
                        new(StepStatus.Info, "Route Confirmed: claude-3-haiku"),
                    ],
                    RequestSummary: "Review the diff for PR #4521 (src/auth/token_service.py, 214 changed lines) and flag any security issues.",
                    ResponseSummary: "Found 3 issues: missing null check on refresh_token (L87), unbounded retry loop (L112), and the token is logged in plaintext (L145)."),
                new(
                    Id: "sess-001-t2",
                    Agent: "Code Review Bot",
                    Model: "claude-3-haiku",
                    TurnNumber: 2,
                    PromptTokens: 3240,
                    CompletionTokens: 1205,
                    RoutingRoi: 84.50m,
                    TotalCost: 0.009870m,
                    ToolExecutionSteps: 3,
                    CacheHitRate: 72m,
                    TimeToFirstTokenMs: 198,
                    ContextBufferPercent: 40.5m,
                    Timestamp: "14:17:45",
                    RoutingSteps:
                    [
                        new(StepStatus.Ok, "History carried forward (3,240 prompt tokens)"),
                        new(StepStatus.Ok, "Prompt cache hit: 2,333 tokens read from cache"),
                        new(StepStatus.Info, "Route Confirmed: claude-3-haiku"),
                    ],
                    RequestSummary: "Suggest a concrete fix for the unbounded retry loop you flagged at L112.",
                    ResponseSummary: "Replace the while-loop with a tenacity retry decorator: stop_after_attempt(3) with exponential backoff, re-raising on final failure."),
                new(
                    Id: "sess-001-t3",
                    Agent: "Code Review Bot",
                    Model: "claude-3-haiku",
                    TurnNumber: 3,
                    PromptTokens: 4567,
                    CompletionTokens: 1798,
                    RoutingRoi: 83.20m,
                    TotalCost: 0.013820m,
                    ToolExecutionSteps: 4,
                    CacheHitRate: 68m,
                    TimeToFirstTokenMs: 211,
                    ContextBufferPercent: 57.0m,
                    Timestamp: "14:19:22",
                    RoutingSteps:
                    [
                        new(StepStatus.Ok, "History carried forward (4,567 prompt tokens)"),
                        new(StepStatus.Warn, "Prompt growth trending up 41% turn-over-turn"),
                        new(StepStatus.Ok, "Prompt cache hit: 3,106 tokens read from cache"),
                        new(StepStatus.Info, "Route Confirmed: claude-3-haiku"),
                    ],
                    RequestSummary: "Apply the same retry pattern to session_service.py and show me the diff.",
                    ResponseSummary: "Patched 2 call sites; session_refresh now shares the retry policy. Diff: +18/-9 across session_service.py and retry_util.py."),
                new(
                    Id: "sess-001-t4",
                    Agent: "Code Review Bot",
                    Model: "claude-3-haiku",
                    TurnNumber: 4,
                    PromptTokens: 5545,
                    CompletionTokens: 0,
                    RoutingRoi: 88.75m,
                    TotalCost: 0.015230m,
                    ToolExecutionSteps: 1,
                    CacheHitRate: 75m,
                    TimeToFirstTokenMs: 189,
                    ContextBufferPercent: 69.2m,
                    Timestamp: "14:22:18",
                    RoutingSteps:
                    [
                        new(StepStatus.Ok, "Final summary pass (no completion requested)"),
                        new(StepStatus.Ok, "Prompt cache hit: 4,159 tokens read from cache"),
                        new(StepStatus.Info, "Route Confirmed: claude-3-haiku"),
                    ],
                    RequestSummary: "Summarize all applied changes for the PR description."),
            ]),
        new(
            Id: "sess-002",
            Title: "Data Pipeline Debugging - ETL Job #892",
            FirstTimestamp: "14:08:15",
            LastTimestamp: "14:14:42",
            TotalCost: 0.025450m,
            TotalPromptTokens: 8932,
            TotalCompletionTokens: 2456,
            HasFallbackTurns: false,
            Turns:
            [
                new(
                    Id: "sess-002-t1",
                    Agent: "Data Analyst Wrapper",
                    Model: "gpt-4o-mini",
                    TurnNumber: 1,
                    PromptTokens: 1890,
                    CompletionTokens: 623,
                    RoutingRoi: 87.56m,
                    TotalCost: 0.005340m,
                    ToolExecutionSteps: 2,
                    CacheHitRate: 0m,
                    TimeToFirstTokenMs: 312,
                    ContextBufferPercent: 23.4m,
                    Timestamp: "14:08:15",
                    RoutingSteps:
                    [
                        new(StepStatus.Ok, "SQL error log parsed (1,234 lines)"),
                        new(StepStatus.Ok, "Short context: gpt-4o-mini sufficient"),
                        new(StepStatus.Info, "Route Confirmed: gpt-4o-mini"),
                    ],
                    RequestSummary: "ETL job #892 failed at stage 3 with ORA-01555. Here is the log excerpt - what is the root cause?",
                    ResponseSummary: "ORA-01555 (snapshot too old): the MERGE reads a table that is mutated mid-run. Isolate the read with a staging CTAS before the merge."),
                new(
                    Id: "sess-002-t2",
                    Agent: "Data Analyst Wrapper",
                    Model: "gpt-4o-mini",
                    TurnNumber: 2,
                    PromptTokens: 3456,
                    CompletionTokens: 912,
                    RoutingRoi: 86.20m,
                    TotalCost: 0.009870m,
                    ToolExecutionSteps: 3,
                    CacheHitRate: 45m,
                    TimeToFirstTokenMs: 267,
                    ContextBufferPercent: 42.8m,
                    Timestamp: "14:10:33",
                    RoutingSteps:
                    [
                        new(StepStatus.Ok, "Query execution plan added to context"),
                        new(StepStatus.Ok, "Prompt cache hit: 1,555 tokens read from cache"),
                        new(StepStatus.Info, "Route Confirmed: gpt-4o-mini"),
                    ],
                    RequestSummary: "Here is the EXPLAIN PLAN output for the failing MERGE - where is the long read window coming from?",
                    ResponseSummary: "The full-table scan on FACT_ORDERS forces a 40-minute read window. Add a partition-pruning predicate on LOAD_DATE to cut it."),
                new(
                    Id: "sess-002-t3",
                    Agent: "Data Analyst Wrapper",
                    Model: "gpt-4o-mini",
                    TurnNumber: 3,
                    PromptTokens: 3586,
                    CompletionTokens: 921,
                    RoutingRoi: 85.90m,
                    TotalCost: 0.010240m,
                    ToolExecutionSteps: 2,
                    CacheHitRate: 52m,
                    TimeToFirstTokenMs: 278,
                    ContextBufferPercent: 44.3m,
                    Timestamp: "14:14:42",
                    RoutingSteps:
                    [
                        new(StepStatus.Ok, "Fix verification pass (3,586 prompt tokens)"),
                        new(StepStatus.Ok, "Prompt cache hit: 1,865 tokens read from cache"),
                        new(StepStatus.Info, "Route Confirmed: gpt-4o-mini"),
                    ],
                    RequestSummary: "Validate the revised MERGE statement before I schedule the rerun.",
                    ResponseSummary: "The revised statement is safe: partition pruning cuts the read window to roughly 90 seconds, well inside undo retention."),
            ]),
        new(
            Id: "sess-003",
            Title: "Customer Support - Issue #78234",
            FirstTimestamp: "13:52:10",
            LastTimestamp: "14:05:33",
            TotalCost: 0.004560m,
            TotalPromptTokens: 6234,
            TotalCompletionTokens: 1845,
            HasFallbackTurns: true,
            Turns:
            [
                new(
                    Id: "sess-003-t1",
                    Agent: "Customer Support NLP",
                    Model: "claude-3-haiku",
                    TurnNumber: 1,
                    PromptTokens: 1456,
                    CompletionTokens: 567,
                    RoutingRoi: 82.30m,
                    TotalCost: 0.004560m,
                    ToolExecutionSteps: 1,
                    CacheHitRate: 0m,
                    TimeToFirstTokenMs: 189,
                    ContextBufferPercent: 18.0m,
                    Timestamp: "13:52:10",
                    RoutingSteps:
                    [
                        new(StepStatus.Ok, "Customer inquiry classified: billing dispute"),
                        new(StepStatus.Ok, "Anthropic budget nominal; claude-3-haiku selected"),
                        new(StepStatus.Info, "Route Confirmed: claude-3-haiku"),
                    ],
                    RequestSummary: "Ticket #78234: customer reports being charged twice for the June invoice. Verify and draft a response.",
                    ResponseSummary: "Duplicate charge confirmed against payment records. A refund-and-apology draft is ready for agent review."),
                new(
                    Id: "sess-003-t2",
                    Agent: "Customer Support NLP",
                    Model: "fallback-cheapest-local",
                    TurnNumber: 2,
                    PromptTokens: 2389,
                    CompletionTokens: 734,
                    RoutingRoi: 0m,
                    TotalCost: 0.000000m,
                    ToolExecutionSteps: 2,
                    CacheHitRate: 0m,
                    TimeToFirstTokenMs: 445,
                    ContextBufferPercent: 29.5m,
                    Timestamp: "13:54:28",
                    RoutingSteps:
                    [
                        new(StepStatus.Warn, "Anthropic hourly budget breached; routing restricted"),
                        new(StepStatus.Ok, "Fallback routing activated: local model"),
                        new(StepStatus.Info, "Route Confirmed: fallback-cheapest-local"),
                    ],
                    RequestSummary: "The customer replied asking about the refund timeline. Draft a follow-up.",
                    ResponseSummary: "Refunds post within 5-7 business days of approval. Suggested reply drafted with the confirmation number.",
                    IsFallback: true),
                new(
                    Id: "sess-003-t3",
                    Agent: "Customer Support NLP",
                    Model: "fallback-cheapest-local",
                    TurnNumber: 3,
                    PromptTokens: 2389,
                    CompletionTokens: 544,
                    RoutingRoi: 0m,
                    TotalCost: 0.000000m,
                    ToolExecutionSteps: 1,
                    CacheHitRate: 0m,
                    TimeToFirstTokenMs: 512,
                    ContextBufferPercent: 29.5m,
                    Timestamp: "14:05:33",
                    RoutingSteps:
                    [
                        new(StepStatus.Warn, "Anthropic budget still breached; staying on fallback"),
                        new(StepStatus.Ok, "Local model serving request"),
                        new(StepStatus.Info, "Route Confirmed: fallback-cheapest-local"),
                    ],
                    RequestSummary: "Close out the ticket with a resolution summary.",
                    ResponseSummary: "Ticket #78234 resolved: duplicate June charge refunded and a confirmation email queued to the customer.",
                    IsFallback: true),
            ]),
    ];

    /// <summary>Mock routing decisions for the Live Stream tab.</summary>
    public static readonly IReadOnlyList<RoutingEntry> Entries =
    [
        new(
            Id: "1",
            SessionId: "e89a2bc",
            TraceId: "a4f89c02",
            Agent: "Data Analyst Wrapper",
            Model: "gpt-4o-mini",
            IsFallback: false,
            PromptTokens: 1230,
            CompletionTokens: 702,
            ActualCost: 0.000480m,
            WorstCaseCost: 0.003860m,
            SavingsAmount: 0.003380m,
            SavingsPercent: 87.56m,
            Timestamp: "14:32:01",
            RoutingSteps:
            [
                new(StepStatus.Ok, "Input text contains source code telemetry"),
                new(StepStatus.Ok, "Budget nominal: gpt-4o-mini selected"),
                new(StepStatus.Ok, "Context window validated (1,932 tokens)"),
                new(StepStatus.Info, "Route Confirmed: gpt-4o-mini"),
            ]),
        new(
            Id: "2",
            SessionId: "d32a1ff",
            TraceId: "b7c21e45",
            Agent: "Code Review Bot",
            Model: "fallback-cheapest-local",
            IsFallback: true,
            PromptTokens: 890,
            CompletionTokens: 312,
            ActualCost: 0.000000m,
            WorstCaseCost: 0.002100m,
            SavingsAmount: 0.000000m,
            SavingsPercent: 0.00m,
            Timestamp: "14:31:48",
            RoutingSteps:
            [
                new(StepStatus.Ok, "Input text contains source code telemetry"),
                new(StepStatus.Warn, "OpenAI budget breached; routing restricted"),
                new(StepStatus.Ok, "Fallback routing activated for destination"),
                new(StepStatus.Info, "Route Confirmed: fallback-cheapest-local"),
            ]),
        new(
            Id: "3",
            SessionId: "f12b8de",
            TraceId: "c9d34f67",
            Agent: "Customer Support NLP",
            Model: "claude-3-haiku",
            IsFallback: false,
            PromptTokens: 2104,
            CompletionTokens: 891,
            ActualCost: 0.000631m,
            WorstCaseCost: 0.004210m,
            SavingsAmount: 0.003579m,
            SavingsPercent: 85.01m,
            Timestamp: "14:31:35",
            RoutingSteps:
            [
                new(StepStatus.Ok, "Conversation context parsed (3 turns)"),
                new(StepStatus.Ok, "Anthropic budget nominal; claude-3-haiku selected"),
                new(StepStatus.Ok, "Response latency target: <800ms"),
                new(StepStatus.Info, "Route Confirmed: claude-3-haiku"),
            ]),
        new(
            Id: "4",
            SessionId: "a45c7fe",
            TraceId: "d2e56g78",
            Agent: "SQL Query Optimizer",
            Model: "gpt-4o-mini",
            IsFallback: false,
            PromptTokens: 445,
            CompletionTokens: 203,
            ActualCost: 0.000096m,
            WorstCaseCost: 0.000780m,
            SavingsAmount: 0.000684m,
            SavingsPercent: 87.69m,
            Timestamp: "14:31:22",
            RoutingSteps:
            [
                new(StepStatus.Ok, "SQL schema fingerprint matched"),
                new(StepStatus.Ok, "Short context: mini-model sufficient"),
                new(StepStatus.Info, "Route Confirmed: gpt-4o-mini"),
            ]),
        new(
            Id: "5",
            SessionId: "c78d2ae",
            TraceId: "e3f67h89",
            Agent: "Log Anomaly Detector",
            Model: "gemini-1.5-flash",
            IsFallback: false,
            PromptTokens: 3840,
            CompletionTokens: 128,
            ActualCost: 0.000192m,
            WorstCaseCost: 0.002304m,
            SavingsAmount: 0.002112m,
            SavingsPercent: 91.67m,
            Timestamp: "14:31:09",
            RoutingSteps:
            [
                new(StepStatus.Ok, "Long-context log batch detected (3,840 tokens)"),
                new(StepStatus.Ok, "Gemini flash selected for cost efficiency"),
                new(StepStatus.Ok, "Output: classification only (128 tokens)"),
                new(StepStatus.Info, "Route Confirmed: gemini-1.5-flash"),
            ]),
        new(
            Id: "6",
            SessionId: "b91e3cd",
            TraceId: "f4a78i90",
            Agent: "Summarization Pipeline",
            Model: "fallback-cheapest-local",
            IsFallback: true,
            PromptTokens: 1560,
            CompletionTokens: 420,
            ActualCost: 0.000000m,
            WorstCaseCost: 0.003120m,
            SavingsAmount: 0.000000m,
            SavingsPercent: 0.00m,
            Timestamp: "14:30:55",
            RoutingSteps:
            [
                new(StepStatus.Ok, "Document chunking completed (4 chunks)"),
                new(StepStatus.Warn, "OpenAI monthly cap reached: fallback triggered"),
                new(StepStatus.Warn, "Gemini quota exhausted for current period"),
                new(StepStatus.Ok, "Local model activated as final fallback"),
                new(StepStatus.Info, "Route Confirmed: fallback-cheapest-local"),
            ]),
        new(
            Id: "7",
            SessionId: "g23f4bc",
            TraceId: "g5b89j01",
            Agent: "Embedding Generator",
            Model: "text-embedding-3-small",
            IsFallback: false,
            PromptTokens: 512,
            CompletionTokens: 0,
            ActualCost: 0.000002m,
            WorstCaseCost: 0.000010m,
            SavingsAmount: 0.000008m,
            SavingsPercent: 80.00m,
            Timestamp: "14:30:42",
            RoutingSteps:
            [
                new(StepStatus.Ok, "Embedding task detected: no completion needed"),
                new(StepStatus.Ok, "text-embedding-3-small selected (optimal cost)"),
                new(StepStatus.Info, "Route Confirmed: text-embedding-3-small"),
            ]),
        new(
            Id: "8",
            SessionId: "h67g8de",
            TraceId: "h6c90k12",
            Agent: "Data Analyst Wrapper",
            Model: "claude-3-5-sonnet",
            IsFallback: false,
            PromptTokens: 4200,
            CompletionTokens: 1890,
            ActualCost: 0.018270m,
            WorstCaseCost: 0.037800m,
            SavingsAmount: 0.019530m,
            SavingsPercent: 51.67m,
            Timestamp: "14:30:29",
            RoutingSteps:
            [
                new(StepStatus.Ok, "Complex reasoning task detected"),
                new(StepStatus.Ok, "High token count requires Sonnet-tier model"),
                new(StepStatus.Ok, "Anthropic budget nominal"),
                new(StepStatus.Info, "Route Confirmed: claude-3-5-sonnet"),
            ]),
    ];

    /// <summary>Mock monthly cost trend for the Cost Analytics tab.</summary>
    public static readonly IReadOnlyList<CostDataPoint> CostData =
    [
        new("Jun 1", 0m),
        new("Jun 3", 4.20m),
        new("Jun 5", 9.80m),
        new("Jun 7", 17.60m),
        new("Jun 9", 26.10m),
        new("Jun 11", 38.40m),
        new("Jun 13", 51.20m),
        new("Jun 15", 67.80m),
        new("Jun 17", 82.50m),
        new("Jun 19", 99.10m),
        new("Jun 21", 112.40m),
        new("Jun 23", 124.70m),
        new("Jun 25", 133.20m),
        new("Jun 27", 138.90m),
        new("Jun 29", 141.50m),
        new("Jul 1", 142.36m),
    ];

    /// <summary>Mock per-agent ROI figures for the Cost Analytics tab.</summary>
    public static readonly IReadOnlyList<AgentRoi> AgentRoi =
    [
        new("Log Anomaly Detector", 91.67m, 38.20m),
        new("SQL Query Optimizer", 87.69m, 22.40m),
        new("Data Analyst Wrapper", 85.12m, 41.80m),
        new("Customer Support NLP", 84.30m, 18.60m),
        new("Summarization Pipeline", 79.50m, 12.40m),
        new("Embedding Generator", 78.20m, 5.80m),
        new("Code Review Bot", 64.10m, 2.90m),
    ];

    /// <summary>Mock daily prompt/completion token volumes for the Cost Analytics tab.</summary>
    public static readonly IReadOnlyList<TokenBucket> TokenBuckets =
    [
        new("Mon", 2_840_000m, 980_000m),
        new("Tue", 3_120_000m, 1_140_000m),
        new("Wed", 4_200_000m, 1_680_000m),
        new("Thu", 3_890_000m, 1_520_000m),
        new("Fri", 2_960_000m, 1_020_000m),
        new("Sat", 1_840_000m, 620_000m),
        new("Sun", 1_240_000m, 380_000m),
    ];

    /// <summary>Mock per-model token-volume market share for the Model Distribution tab.</summary>
    public static readonly IReadOnlyList<ModelShare> ModelShares =
    [
        new("gpt-4o-mini", 38m, "#10b981"),
        new("claude-3-haiku", 22m, "#38bdf8"),
        new("gemini-1.5-flash", 18m, "#818cf8"),
        new("fallback-local", 10m, "#f59e0b"),
        new("claude-3-5-sonnet", 7m, "#fb7185"),
        new("text-embedding-3-small", 5m, "#a78bfa"),
    ];

    /// <summary>
    /// Models the mock history draws from, each with an approximate ($/M input, $/M output) price used
    /// to derive per-turn cost. Deliberately a spread of tiers (cheap routed models through a premium
    /// model and a $0 local fallback) so the Cost Analytics model-choice bars show a real mix.
    /// </summary>
    private static readonly (string Model, decimal InputPerM, decimal OutputPerM)[] HistoryModels =
    [
        ("gpt-4o-mini", 0.15m, 0.60m),
        ("claude-3-haiku", 0.25m, 1.25m),
        ("gemini-1.5-flash", 0.075m, 0.30m),
        ("claude-3-5-sonnet", 3.00m, 15.00m),
        ("fallback-cheapest-local", 0m, 0m),
    ];

    /// <summary>
    /// One mock session: how long ago its first turn was, how many turns it has, and how far apart the
    /// turns are. Chosen so every Cost Analytics time range is populated - two sessions land inside the
    /// last hour (each turn a distinct point), more inside the day/week/month, and several stretch back
    /// months for the all-time view.
    /// </summary>
    private static readonly (int StartMinutesAgo, int Turns, int SpacingMinutes)[] HistorySessions =
    [
        (45, 5, 6),        // within the last hour
        (52, 3, 5),        // within the last hour
        (6 * 60, 5, 9),    // within the last day
        (14 * 60, 4, 12),  // within the last day
        (2 * 24 * 60, 6, 15),   // within the last week
        (4 * 24 * 60, 5, 20),   // within the last week
        (10 * 24 * 60, 6, 18),  // within the last month
        (20 * 24 * 60, 5, 22),  // within the last month
        (45 * 24 * 60, 6, 16),  // all-time
        (75 * 24 * 60, 5, 25),  // all-time
        (110 * 24 * 60, 7, 14), // all-time
    ];

    /// <summary>
    /// Builds a deterministic, timestamped corpus of turn-level metrics anchored to <paramref name="now"/>,
    /// used by the Cost Analytics tab so every metric and time range renders even with no proxy running.
    /// Deterministic (fixed RNG seed) so the shapes are stable across renders; only the timestamps move
    /// with <paramref name="now"/> so the range windows stay populated. Live turns, when present, are
    /// merged on top of this by the component - this is the "mock fills the gaps" corpus.
    /// A handful of exemplar events are injected at fixed positions so each bespoke chart shows its
    /// special state within the default (Week) window: a token runaway, a TTFT spike, a fallback (which
    /// draws a below-zero ROI bar and a model handoff), and context sessions that breach the 90% line.
    /// </summary>
    public static IReadOnlyList<MetricTurnPoint> BuildMetricHistory(DateTimeOffset now)
    {
        var random = new Random(20260710);
        var points = new List<MetricTurnPoint>();

        for (var s = 0; s < HistorySessions.Length; s++)
        {
            var (startMinutesAgo, turns, spacing) = HistorySessions[s];
            var sessionId = $"hist-sess-{s + 1:D2}";
            // Each session mostly reuses one primary model (as agentic sessions do); a later turn may
            // fall back to the local model, so the per-bucket bars show occasional model switches.
            var primary = HistoryModels[s % HistoryModels.Length];

            var start = now.AddMinutes(-startMinutesAgo);
            // Context fills faster in some sessions than others; the heavier ones (growth ~20%/turn)
            // cross the 90% threshold on their later turns so the Context chart shows real breaches.
            var contextGrowth = 12m + (s % 3) * 4m;

            for (var t = 0; t < turns; t++)
            {
                var timestamp = start.AddMinutes(t * spacing);
                if (timestamp > now)
                {
                    break;
                }

                // Fixed exemplar events (see the summary) plus low-probability random texture.
                var forceFallback = s == 5 && t == 2;
                var forceRunaway = s == 2 && t == 3;
                var forceSpike = s == 3 && t == 2;

                var isFallback = forceFallback || (t > 0 && random.Next(0, 100) < 7);
                var (model, inputPerM, outputPerM) = isFallback ? HistoryModels[^1] : primary;

                // Prompt tokens compound turn-over-turn within a session (the hockey-stick shape);
                // a runaway turn injects a large recursive-loop spike (> 2.5x the prior turn).
                var basePrompt = 1500 + (t * 1200) + random.Next(0, 900);
                var promptTokens = forceRunaway ? basePrompt + random.Next(90_000, 150_000) : basePrompt;
                var completionTokens = 250 + random.Next(0, 1300);

                var cost = (promptTokens / 1_000_000m * inputPerM) + (completionTokens / 1_000_000m * outputPerM);
                // A demo counterfactual: the baseline would have spent 1.6x-4.0x this turn's cost, except
                // on a fallback turn where it would have been cheaper (a routing loss). Expressed as a
                // baseline cost rather than a saved-percentage because that is what the real feed carries.
                var baselineCost = isFallback ? cost * 0.7m : cost * (1.6m + (random.Next(0, 240) / 100m));
                var toolSteps = 1 + random.Next(0, Math.Min(6, t + 2));
                var cacheHit = t == 0 ? 0m : 40m + random.Next(0, 400) / 10m;      // 40.0 - 80.0 %
                var spike = forceSpike || random.Next(0, 100) < 5;
                var ttft = spike ? 1800 + random.Next(0, 3200) : 150 + random.Next(0, 450);
                // Grows per turn; heavier sessions breach the 90% safety line, capped below the edge.
                var contextPct = Math.Min(97m, 12m + (t * contextGrowth));

                points.Add(new MetricTurnPoint(
                    TimestampUtc: timestamp,
                    SessionId: sessionId,
                    Model: model,
                    BaselineCostUsd: baselineCost,
                    TotalCost: cost,
                    PromptTokens: promptTokens,
                    CompletionTokens: completionTokens,
                    ToolExecutionSteps: toolSteps,
                    CacheHitRate: cacheHit,
                    TimeToFirstTokenMs: ttft,
                    ContextBufferPercent: contextPct,
                    BaselineModel: "demo-baseline",
                    IsExploratory: isFallback));
            }
        }

        return points;
    }
}

