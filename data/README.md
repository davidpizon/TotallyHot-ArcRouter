# CodeRouterBench data

The CodeRouterBench benchmark tables that the router's regret-evaluation harness (PLAN.md Phase N) and
the `dim_best` orchestrator voter (Phase L) consume live in a local SQLite database, not in this
directory (docs/router/coderouterbench-sqlite-migration-plan.md). The database is **synced on demand,
not checked in** - at ~12 MB transferred it is large for a git checkout, and the dataset already has a
stable, versioned home on Hugging Face.

This directory now holds only this file. `data/coderouterbench/` is gone: nothing outside documentation
reads it, and `.gitignore` keeps it from being committed again if a stray fetch recreates it locally.

## Syncing

The corpus lives at `%ProgramData%\TotallyHotArcRouter\coderouterbench.db` (configurable via
`Storage:BenchmarkDatabasePath`), synced from
[`huggingface.co/datasets/Lance1573/CodeRouterBench`](https://huggingface.co/datasets/Lance1573/CodeRouterBench)
(`main` branch by default; pin a ref via `CodeRouterBench:DatasetRef`). Three ways to trigger a sync:

- **Governance → Benchmark Data** in the GUI - shows the corpus's `Current`/`Update`/`Check Failed`
  state and streams per-file progress.
- The `sync_benchmark_data` MCP tool (and `get_benchmark_data_status` to read the state first).
- `TotallyHotArcRouter --sync-benchmark-data` - runs one sync and exits; the CI/headless entry point.

Each file's checksum (a git blob SHA-1, not MD5 - see the migration plan's "Checksums" section) and row
count are verified before that file's rows are committed; a mismatch leaves the prior table and ledger
row untouched rather than importing a truncated or tampered file:

| File | Rows | Meaning |
|---|---|---|
| `id_probing_results_long.csv` | 56,640 | 7,080 probing (train+val) tasks x 8 models |
| `id_test_results_long.csv` | 23,352 | 2,919 held-out ID test tasks x 8 models |
| `ood176_results_long.csv` | 1,408 | 176 OOD tasks x 8 models |
| `id_probing_tasks.jsonl` | 7,080 | Probing-split task metadata |
| `id_test_tasks.jsonl` | 2,919 | Held-out ID test task metadata |
| `ood176_tasks.jsonl` | 176 | OOD task metadata |
| `models.json` | - | The 8 canonical backend models and USD pricing |
| `summary.json` | - | Integrity counts and source paths, as published |

`id_results_long.csv` (79,992 rows) and `id_tasks.jsonl` (9,999 lines) - the two files a straight file
listing on Hugging Face also shows - are never synced: they are exact unions of the two ID-schema splits
above, discriminated by `benchmark_id_results.split`/`benchmark_id_tasks.split`, so `id_results_long` is
a query (`SELECT * FROM benchmark_id_results`), not a second copy. See the migration plan's "Derived
files" section.

## Consuming it in code

`TotallyHot.ArcRouter.CodeRouterBench.DimensionModelScoreMatrix.FromDatabase` reads
`benchmark_id_results` (filtered to one `split`) and aggregates it into a dimension x model
average-score matrix - the shape research-doc Table 10/11 publish - canonicalizing model ids on read
through `ModelNameCanonicalizer.Canonicalize`, because the released tables spell several models
differently from the router's own `ModelRouting:ModelList` configuration (`MiniMax-M2.7` vs
`minimax-m2.7`, `claude-opus-4-6` vs `claude-opus-4.6`); both the importers (on write) and this factory
(on read) canonicalize onto the same comparison key, so a query in any spelling resolves.
`CodeRouterBenchTable10ReconciliationTests` (in `TotallyHotArcRouter.Tests/CodeRouterBench/`) checks a
matrix built from the real, synced probing split against the published Table 10; it skips itself when
`benchmark_id_results` has no `probing`-split rows yet, matching the pattern
`Integration/LiteLlmParityTests.cs` uses for its sidecar dependency.

**Known data-fidelity limit (Phase K settled deferral, unaffected by the database migration):**
per-model row averages (AvgPerf) reproduce Table 10 to within 0.05 for every one of the eight backend
models, but individual `bug_fixing`, `algorithm`, and `test_generation` cells diverge from the published
table by up to 0.32 for GLM-5, Qwen3-Max, Qwen3.5-Plus, and MiniMax-M2.7 specifically - while every cell
for Claude Opus 4.6, GPT-5.4, Claude Sonnet 4.6, and Kimi-K2.5 matches to within 0.01. This looks like
run-to-run noise in the LLM-as-Judge-scored dimensions (research-doc Table 5) baked into the released
data rather than a parsing bug: the per-cell errors for the affected models are large in both directions
and largely cancel out in the row average, which is what the `dim_best` voter and Phase N's AvgPerf
metric actually consume. Exact per-cell parity with Table 10 is not pursued further - this mirrors Phase
N's own "ordering, not absolute parity" standard, applied one phase earlier where the evidence for it
was actually found.

## Not yet restored

`outputs/`, `agentic-artifacts/`, and the nested `raw_matrices/` audit trail that the same Hugging Face
dataset also publishes are **not** synced or documented further here. Nothing in Phase K, L, or N as
currently scoped reads them: the loader above only needs the eight files in the table, and
`outputs/baselines_ood176/` (Phase N's comparison baselines) is generated by that phase's own harness
rather than replayed from a checked-in snapshot. `README.md` and `docs/HANDBOOK.md` no longer claim
these paths exist; if a later phase needs them, sync them from the same dataset repo rather than
re-deciding this.
