# Router Memory Persistence Architecture

The router keeps two independent learned memories, both persisted to the same SQLite database
(`RoutingOptions.EmbeddingMemoryDatabasePath`, default `router_embedding_memory.db`):

| Memory | Keyed by | Table | Bound | Feeds |
|---|---|---|---|---|
| `RouterMemory` | dimension → model | `dimension_scores` | one row per (dimension, model) | `dim_best` voter, `AgentAsARouter`, `UtilityRoutingPolicy`, `RequestInterceptor` |
| `EmbeddingMemory` | task embedding | `memory_entries` | FIFO, `EmbeddingMemoryCapacity` (20,000) | `memory_kNN` voter, `logreg` training |

This document covers the first. `EmbeddingMemory` is specified in PLAN.md Phase J and
`docs/router/live-feedback-learning-plan.md`.

## What `RouterMemory` stores

A running aggregate per (dimension, model) pair, not the raw observations:

```csharp
public sealed record ScoreAggregate(double Sum, int Count)
{
    public double? Average => Count > 0 ? Sum / Count : null;
}
```

`RouterMemory` holds these in a `ConcurrentDictionary<string, ConcurrentDictionary<string, ScoreAggregate>>`
as the hot-path read source, and mirrors every observation into `dimension_scores`.

```csharp
public class RouterMemory
{
    public Task InitializeAsync();                                     // loads the table at startup
    public Task AddScoreAsync(string dimension, string model, double score);
    public double? GetAverageScore(string dimension, string model);    // O(1)
    public int GetObservationCount(string dimension, string model);    // sample size behind the average
    public IEnumerable<string> GetModelsForDimension(string dimension);
}
```

**Why an aggregate rather than the score list this previously kept.** `GetAverageScore` was the only
consumer of the list's contents and needs only the mean, while the list cost three things: unbounded growth
for the life of the installation, an O(n) average recomputed once per candidate per request on the routing
hot path, and a full re-serialization of all accumulated history on every single score. A fixed-size
aggregate bounds both the in-memory structure and the table by the (dimension × model) vocabulary rather
than by observation count.

**Growth is deliberately unbounded in observations folded in.** Unlike `EmbeddingMemory`'s FIFO window,
this memory is meant to accumulate indefinitely — `docs/router/regret-evaluation-harness-plan.md` depends on
that asymmetry. The aggregate is what makes indefinite accumulation free rather than expensive.

## `SqliteRouterMemoryStore`

```csharp
public interface IRouterMemoryStore
{
    Task<ConcurrentDictionary<string, ConcurrentDictionary<string, ScoreAggregate>>> LoadAllAsync(CancellationToken ct = default);
    Task RecordScoreAsync(string dimension, string model, double score, CancellationToken ct = default);
}
```

The write side takes **one observation**, not a whole-memory snapshot, so the store folds it in with a
single statement:

```sql
INSERT INTO dimension_scores (dimension, model, sum, count) VALUES ($d, $m, $score, 1)
ON CONFLICT (dimension, model) DO UPDATE SET sum = sum + excluded.sum, count = count + 1;
```

**The addition happens inside SQLite on purpose.** A read-modify-write would let two racing observations of
the same pair read the same starting value, with the later write silently discarding the earlier score.
Letting the database compute the sum makes the fold atomic regardless of interleaving —
`SqliteRouterMemoryStoreTests.RecordScoreAsync_ConcurrentObservationsOfTheSamePair_LoseNothing` is the guard.

**The store creates its own schema on first use.** It does not assume startup already ran
`RouterMemoryDatabase.EnsureCreated()`, because scores arrive from the sandbox verification path for the
life of the process and `StartupHealthCheckHostedService` runs its `EnsureCreated` call best-effort inside a
catch that only logs. Without self-creation, a startup failure would turn every subsequent score write into
a "no such table" throw instead of a degraded-but-working router.

**Startup loads it.** `StartupHealthCheckHostedService` calls `RouterMemory.InitializeAsync()` alongside
`EmbeddingMemory.InitializeAsync()`, best-effort and log-only like every other startup check.

## History: the JSON store this replaced

Router memory was originally persisted as an indented JSON file at `RoutingOptions.MemoryPath`
(`router_memory.json`), rewritten in full on every observation via `File.WriteAllTextAsync`. It was removed
rather than improved, for four reasons:

- **Write amplification.** Every score re-serialized the entire accumulated history.
- **No crash safety.** An in-place overwrite with no temp-file-and-rename meant a crash mid-write truncated
  the file, and the loader treated an unparseable file as *empty* — silently discarding everything learned.
- **A data race.** Scores were appended to a `List<double>` under a lock the serializer did not take;
  `List<T>` is not safe for concurrent read+write, so a save racing a score could throw or write torn output.
- **It was never read back.** `RouterMemory.InitializeAsync()` had no production caller, so the router paid
  the full rewrite cost on every score to maintain a file it loaded at no point. Accumulated feedback was
  discarded at every restart and `dim_best` began each run from the CodeRouterBench prior alone.

Every item on the old design's wish list — schema versioning, atomic writes, backups, compaction,
precomputed averages instead of raw score lists — was a description of properties a database already has.

**No migration was performed, by explicit decision.** Existing `router_memory.json` files are not imported;
the router starts from an empty `dimension_scores` table and re-accumulates from live traffic, with
`dim_best` falling back to its CodeRouterBench probing prior in the meantime — the cold-start path it
already handles. Any `router_memory.json` left on disk is an orphan that nothing reads.
`RoutingOptions.MemoryPath` is retained as dead configuration (like `RoutingOptions.PolicyName`) so an
operator's existing `appsettings.json` does not fail validation on upgrade.

`VectorStoreRouterMemoryStore`, an in-process `IRouterMemoryStore` that approximated similarity with Jaccard
token overlap over dimension names, was deleted earlier (PLAN.md Phase J) when `EmbeddingMemory` shipped
real embedding-based cosine kNN. `SqliteRouterMemoryStore` is the only implementation today.

## Configuration

| Key | Default | Meaning |
|---|---|---|
| `Routing:EmbeddingMemoryDatabasePath` | `router_embedding_memory.db` | The SQLite file holding **both** `dimension_scores` and `memory_entries`. Relative paths resolve from the application base directory. The name predates the second table and under-describes it; renaming it would break existing `appsettings.json` files, which is the worse trade. |
| `Routing:MemoryPath` | `router_memory.json` | **Dead.** Read by nothing since router memory moved to SQLite. |
