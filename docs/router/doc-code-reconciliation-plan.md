# Doc/Code Reconciliation Plan

Realigns the roadmap and design docs with what the code actually does. Every finding below was verified
against source, not inferred from another document.

**Status:** complete — every part executed and verified.

| Part | State |
|---|---|
| Part 1 — the `cluster_best` code fix | **Done** — service, proto comment, and both test layers. See the note under §1.1. |
| §2.1 `src/PLAN.md` Phase N claim | **Done.** |
| §2.2, §2.3, §2.5, §2.6 router-doc corrections | **Done.** |
| §2.4 "four-voter" phrasing | **Done** — both `memory-persistence.md` and `src/PLAN.md`. |
| §2.7 `docs/README.md` index | **Done** — rebuilt as an exhaustive, categorized index of all 38 router docs. The curated-vs-exhaustive question was settled in favour of exhaustive. |

A subsequent pass over all 38 router docs also corrected five further gaps not in the original audit;
they are recorded in §2.8 below.

**Scope:** `src/PLAN.md`, `docs/router/*.md`, `README.md`, `docs/HANDBOOK.md`, `data/README.md`. GUI and
design docs (`docs/gui/`, `docs/design/`) are deliberately out of scope for this pass and unaudited.

## Context

Three plan documents disagree with the code and with each other about what has shipped. The largest is
`src/PLAN.md`, which states that the regret-evaluation harness does not exist — while
`CodeRouterBench/Evaluation/` holds thirteen files implementing it, and
`regret-evaluation-harness-plan.md` correctly records N1–N3 as shipped. A reader trusting `PLAN.md`
would conclude the project's central measurement capability is unbuilt and might rebuild it.

The audit also surfaced one genuine code defect: the fifth Orchestrator voter (`cluster_best`, shipped
in Phase T3) was missing from the routing-mode admin readout, so the GUI could not display it. A unit
test asserted the four-voter list verbatim, which locked the omission in place. Fixed in Part 1 below.

This is drift from rapid phase delivery, not from anyone being careless: each phase updated its own
owning doc, and the cross-cutting roadmap fell behind.

## Part 1 — Code fix (one defect)

### 1.1 `cluster_best` missing from the routing-mode admin surface

`RoutingModeAdminGrpcService.GetRoutingMode` reports four voters; `VoterNames` defines five, and both
`RoutingOptions.EnableClusterBestVoter` and `ClusterBestVoterWeight` exist. No comment justifies the
omission — it was not updated when Phase T3 landed.

- **`src/TotallyHotArcRouter/Router/RoutingModeAdminGrpcService.cs:44-47`** — add the `cluster_best`
  entry after `llm_router`, and replace all five hardcoded name literals with `VoterNames.DimBest`,
  `VoterNames.MemoryKnn`, `VoterNames.LogReg`, `VoterNames.LlmRouter`, `VoterNames.ClusterBest`. The
  literals are the reason this drifted silently; `VoterNames` exists precisely to prevent it, and using
  it makes the next added voter a compile-time prompt rather than a silent omission.
- **`src/Protos/telemetry.proto:387`** — the field comment reads "Always the four voters PLAN.md Phase L
  ships, in dim_best / memory_kNN / logreg / llm_router order." Update to five and name `cluster_best`.
  The field itself is `repeated VoterMode voters = 4` — **no wire-schema change is needed**, only the
  comment.
- **`src/TotallyHotArcRouter.Tests/Router/RoutingModeAdminGrpcServiceTests.cs:33`** — the test
  `GetRoutingMode_ReportsTheFourVotersInFixedOrderWithTheirConfiguredWeights` asserts
  `.Equal("dim_best", "memory_kNN", "logreg", "llm_router")`. Rename to `...ReportsTheFiveVoters...`,
  add `EnableClusterBestVoter`/`ClusterBestVoterWeight` to the fixture options, and extend the expected
  sequence. Add one assertion covering the new entry's enablement and weight.

**No GUI change required.** `Components/RoutingModeAdmin.razor:67` already iterates `mode.Voters`, so
the fifth row renders automatically.

**Not doing:** gating the new entry on `AdaptiveRoutingEnabled`. The readout reports *configuration*,
and `EnableClusterBestVoter` defaults to `true` independently of the master switch; hiding a configured
voter would trade one inaccuracy for another.

> **Done.** All three files landed as specified, plus one addition found during the work: the **GUI test
> fixture** `Gui.Tests/RoutingModeAdminTests.cs` built a four-voter `DefaultMode()` under a test named
> `Renders_the_orchestrator_state_and_every_voter`. The component itself needed no change (it iterates
> `mode.Voters`), but a four-voter fixture asserting "every voter" is the same drift one layer up, so
> `cluster_best` was added to the fixture and to the render assertion. Verified: solution builds with
> zero warnings; 2,980 tests pass across all seven assemblies. The manual GUI check the verification
> section called for is now covered by that rendering test, so it is no longer required.

## Part 2 — Documentation corrections

Ordered by severity. Each is a doc changing to match verified code.

### 2.1 `src/PLAN.md` — Phase N is substantially built (highest severity)

The "What is still missing" bullet claims *"Nothing computes `R_ij`, the per-task oracle, `CumReg`,
`AvgPerf`, `TotTok`, `$Total`, or `Perf/$`."* `RegretReplayResult` computes all five metrics; thirteen
files exist under `CodeRouterBench/Evaluation/`.

- Rewrite the bullet to: N1–N3 shipped (metrics core, replay engine, Always-*m*, DimensionBest,
  LinUCB/LinTS); **N4–N6 remain** (kNN/LogReg OOD baselines, Orchestrator arm + comparison report,
  CLI/GUI surface). The exit criterion is still unmet because N5 has not run — that part of the claim
  is true and should be kept, stated precisely.
- Add a row to the shipped-work table pointing at
  [`regret-evaluation-harness-plan.md`](regret-evaluation-harness-plan.md), matching how every other
  shipped phase is recorded.
- Update "Remaining work, in order" item 1 to scope Phase N to N4–N6 rather than the whole phase.
- Treat `regret-evaluation-harness-plan.md` as the **source of truth** here; it needs no edit.

### 2.2 `self-organizing-classification-plan.md` — header and T5 status

- **Line 15**, `**Status:** proposed` → shipped, T1–T6 complete. Contradicted by its own phase map.
- **Line 314**, T5 row `Proposed` → `Shipped`. Verified present: `ClusterModelAdminGrpcService.cs`,
  `Gui/Components/ClusterModelAdmin.razor`, `Gui.Telemetry/ClusterModelAdminClient.cs`, and the
  `--retrain-clusters` flag at `Program.cs:47`.
- Add a short "Where things stand" note under T5 naming those four artifacts, matching the evidence
  style T1/T2 already use.

### 2.3 `orchestrator-ensemble.md` — the ensemble has five voters

- **Line 3** `Status: shipped — 4 of 4 voters` → 5 of 5, noting `cluster_best` arrived later via
  `self-organizing-classification-plan.md` Phase T3, not Phase L.
- **Line 18** `## The four voters` → `## The five voters`; add a `cluster_best` bullet in the existing
  format (source path `Router/Orchestrator/ClusterBestVoter.cs`, what it reads, when it abstains,
  default weight `0.5`, and that it is gated by `AdaptiveRoutingEnabled`).
- **Line 89** — "the **not-yet-built** `logreg` admin surface" is false; `LogRegModelAdminGrpcService`
  shipped with live-feedback Phase 5. Rewrite to describe it as shipped.
- **Line 91** — "reports all four voters' enablement and weights" becomes true-at-five only *after*
  Part 1 lands. Sequence this edit after the code fix.
- **Line 74** — the default-weights list omits `cluster_best` = 0.5. Add it.

### 2.4 `src/PLAN.md` and `memory-persistence.md` — "four-voter" phrasing

- `src/PLAN.md`: "the four-voter Orchestrator ensemble" in the Where-things-stand paragraph and the
  Phase L table row → five-voter, with the Phase L / Phase T3 provenance distinction preserved.
- `memory-persistence.md:9`: the `memory_entries` consumers cell reads "`memory_kNN` voter, `logreg`
  training" → add `cluster_best` voter and cluster training.

### 2.5 `tracked-todos.md` — remove the pre-migration TypeScript entry

- **Delete #1** entirely. It targets `src/utils.ts`, `src/openai/openaiApi.ts`,
  `src/anthropic/anthropicApi.ts`, `src/gemini/geminiApi.ts`, `src/logger.ts` — none of which exist;
  they predate the C# migration. Unactionable, not merely stale.
- **Mark #2 complete** — its deliverable, `docs/router/tool-call-normalization.md`, exists.
- Keep #3/#4/#5 numbering stable so `src/PLAN.md`'s references to them stay valid, and note the gap at
  the top rather than renumbering.

### 2.6 `unified-api-translation.md` — dangling test reference

Cites `src/TotallyHotArcRouter.Tests/Proxy/Translation/ToolCallEchoGuardTranslatorTests.cs`, which does
not exist. Locate the renamed/merged successor and correct the path; if the coverage genuinely went
away, say so instead of naming a file.

### 2.7 `docs/README.md` — index completeness (lowest severity, decision needed)

14 of 37 `docs/router/*.md` files are linked. The 23 unlinked ones are listed in the audit output. This
may be a deliberate curation rather than a gap — **confirm intent before changing anything.** If it
should be exhaustive, add the missing entries grouped by topic; if curated, add one line at the top
saying so, so the next reader does not re-raise it.

### 2.8 Further gaps found while reading all 38 router docs — **done**

Not in the original audit; surfaced by the full read-through and corrected in the same pass.

- **`telemetry.md`** — the status banner claimed the pipeline had "never been compiled or run here"
  because the authoring environment was a Linux agent with no .NET SDK. That environment is long gone;
  the solution builds and its tests run green. Rewritten as implemented and test-verified.
- **`mcp-endpoint-plan.md`** — had **no status banner at all**, so a shipped plan read as a proposal.
  Added one naming the delivered `Mcp/` surface, `ManagementFacade`, and `ManagementAccessToken`.
- **`security-hardening-plan.md`** — the blanket "no code has been changed" is stale:
  `mcp-endpoint-plan.md` subsequently shipped the `X-Admin-Token` requirement on `/admin/*` and MCP
  bearer auth over TLS, which is T-02/T-04's subject matter. The banner now says so and marks the
  per-finding statuses **unverified** rather than silently implying either state — re-auditing 16
  security findings is a security review, not a documentation pass, and was deliberately not attempted.
- **`live-feedback-learning-plan.md`** — its phase-map row for Phase 6 said `Proposed` while both its
  own header and the Phase 6 section said "partially shipped". Row corrected.
- **`unified-api-translation.md`** — cited `ToolCallEchoGuardTranslatorTests.cs`, deleted by
  `tool-call-normalization.md` Phase 4. Repointed at the successor that carries the suite forward,
  `ToolCalling/ToolCallNormalizingTranslatorTests.cs`.

## Out of scope, recorded so it is not lost

- **`.github/copilot-instructions.md` relative links.** It symlinks to root `AGENTS.md`, so links like
  `docs/gui/DESIGN.md` resolve from `.github/` and 404 when read at that path. Inherent to the
  single-source-file convention `AGENTS.md` deliberately adopts; fixing it means either root-absolute
  links or abandoning the symlink. Needs its own decision, not a drive-by change.
- **`docs/gui/` and `docs/design/`** — roughly 25 files, unaudited. A second pass if wanted.

## Verification

Documentation-only changes cannot be compiled, so the code fix and the doc claims are checked
differently.

**For Part 1:**

```bash
dotnet build src/TotallyHotArcRouter.slnx -v q --nologo
```

Zero warnings required — `TreatWarningsAsErrors` is repo-wide.

```bash
./src/TotallyHotArcRouter.Tests/bin/Debug/net10.0/TotallyHotArcRouter.Tests.exe -noLogo -class "TotallyHot.ArcRouter.Tests.Router.RoutingModeAdminGrpcServiceTests"
```

Then the full suite across all seven assemblies (`dotnet test` reports zero tests under xUnit v3 — run
each built `.exe` directly). Baseline to hold: **2,108 passing / 6 skipped** in
`TotallyHotArcRouter.Tests`, plus 165 Quality, 105 Gui.Admin, 71 Gui.Charts, 30 Gui.Console, 142
Gui.Telemetry, 317 Gui.

Manual check for the GUI path, since no automated test covers rendering: launch the app, open
Governance → Routing Mode, confirm a `cluster_best` row appears with weight 0.5.

**For Part 2**, re-run the two audit scripts, which are already written:

- `scratchpad/audit_links.py` — broken relative links, dangling `src/**.cs` references. Expect 2.6 to
  drop the one missing-path finding to zero.
- `scratchpad/audit_values.py` — line-anchored links past EOF, doc-quoted option defaults vs code.
  Currently clean; must stay clean.

Neither script checks phase-status claims — those are prose. Each edit in Part 2 cites the file and line
that proves it, so re-verification means re-reading those specific sources.

## Sequencing

1. Part 1 (code + proto comment + test) — lands independently.
2. §2.1, §2.2 — the false and self-contradictory claims; highest reader impact.
3. §2.3 — must follow Part 1, since line 91 only becomes true once the code reports five voters.
4. §2.4, §2.5, §2.6 — independent, any order.
5. §2.7 — blocked on the curated-vs-exhaustive decision.
