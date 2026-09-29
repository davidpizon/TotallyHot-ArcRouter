# 0018. Persist request telemetry off the request path via a bounded channel

**Status:** proposed
**Date:** 2026-09-28
**Deciders:** David Pizon

## Context and Problem Statement

`ProxyMiddleware` awaits `RequestTelemetryPublisher.PublishAsync` inside the request pipeline. That call
performs three synchronous SQLite writes wearing async signatures: `SqliteTranscriptStore.InsertAsync`,
`ProviderBudgetStore.RecordUsageAsync` (via the synchronous `AddProviderSpend`), and `UsageLedger.RecordAsync`.
Each blocks a thread-pool thread for a connection and commit round trip. The middleware now calls
`Response.CompleteAsync()` before this work (survey item HS-02 step 1), so the client no longer waits for
persistence; but the request still occupies a pipeline thread and the in-flight gauge for its duration, and
throughput under concurrency is bounded by the writes.

Moving the writes onto a background writer changes a hot-path contract: the ordering and durability of
spend, ledger and transcript rows relative to the response, and how quickly budget enforcement sees new
spend. That is why it needs a decision rather than a refactor. It also gives the 25-parameter
`PublishAsync` call (open smell C2) a natural replacement: one `ServedRequest` record.

Source: `docs/router/codebase-health-survey-2026-09-28.md`, HS-02.

## Decision Drivers

- Do not hold a pipeline thread for synchronous disk I/O after the response is complete.
- Spend accounting must stay honest: a budget cap should not be exceeded by an unbounded margin.
- Shutdown and crash behavior must be explicit, not accidental.
- Reuse an existing pattern in the codebase over inventing a new one (`RateLimitHeaderCapture` already runs a channel consumer loop).
- The in-flight gauge pauses background work; persistence in flight must still count as work in flight.

## Considered Options

- Keep persistence inline (after `CompleteAsync`), as today
- Bounded `Channel<ServedRequest>` with a single background writer
- Fire-and-forget `Task.Run` per request

## Decision Outcome

Chosen option: "Bounded `Channel<ServedRequest>` with a single background writer", because it removes
synchronous I/O from the request path (driver 1) while keeping ordering, back-pressure and a defined shutdown
drain (drivers 2 and 3) and matching the shape `RateLimitHeaderCapture` already uses (driver 4).

Guarantees this ADR commits to:

- **Ordering:** rows are written in enqueue order by one writer.
- **Loss window:** a process crash between response completion and drain loses the queued rows. Accepted.
- **Budget lag:** enforcement sees new spend only after the writer drains it; the lag is bounded by queue depth times write latency.
- **Back-pressure:** the channel is bounded; when full, the producer waits (it never drops spend rows) and logs at Warning.
- **Shutdown:** the hosted service drains the queue up to a configured deadline, then logs how many rows were abandoned.
- **In-flight gauge:** the writer holds a gauge scope while it has queued work, so background jobs stay paused.

### Consequences

- Good, because request threads are released as soon as the response is complete.
- Good, because the 25-parameter `PublishAsync` call collapses into one record (closes C2).
- Bad, because a crash loses rows queued but not yet written, where inline persistence loses none after completion.
- Bad, because budget enforcement lags by the drain latency, so a cap can be overshot by the in-queue spend.
- Neutral, because tests that assert a row exists after `InvokeAsync` returns must await a drain hook.

## Pros and Cons of the Options

### Keep persistence inline (after `CompleteAsync`)

- Good, because it is already done and has no new failure modes.
- Good, because durability and budget visibility are immediate.
- Bad, because each request holds a thread for three synchronous commits.

### Bounded `Channel<ServedRequest>` with a single background writer

- Good, because writes can be batched into one transaction per drain.
- Good, because back-pressure is explicit and bounded.
- Bad, because it adds a loss window and a shutdown-drain obligation.

### Fire-and-forget `Task.Run` per request

- Good, because it is the smallest code change.
- Bad, because there is no ordering, no bound and no shutdown drain; unbounded concurrent SQLite writers contend on the file lock.

## More Information

- Step 1 (`CompleteAsync` before telemetry) is implemented in `ProxyMiddleware`.
- Existing pattern: `src/TotallyHotArcRouter/Telemetry/RateLimitHeaderCapture.cs`.
- Validation plan: measure last-`data:` frame to EOF before and after; run the golden-path smoke plus
  `ProxyMiddlewareCaptureRecoveryTests` and `RequestTelemetryPublisherTests`.
