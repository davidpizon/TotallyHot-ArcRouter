# 0008. Adopt CodeGraph + Serena as the standing dual-engine code-smell pipeline

**Status:** proposed <!-- proposed | accepted | rejected | deprecated | superseded by ADR-NNNN -->
**Date:** 2026-09-02
**Deciders:** David Pizon
**Amendments:** [Amendment 1 (2026-09-02) — stop rules](#amendment-1-2026-09-02-stop-rules)

## Context and Problem Statement

The repository already has an in-flight mechanical refactoring tracker —
[`docs/router/code-smell-refactoring-plan.md`](../router/code-smell-refactoring-plan.md) — produced by
an earlier CodeGraph + Serena survey plus a second independent audit. Phases 1–3 of that plan are
shipped; remaining work is either closed by [ADR-0006](0006-split-managementfacade-along-crud-aggregate-boundaries.md)
and [ADR-0007](0007-provider-admin-client-stays-on-http.md), adopted as a going-forward norm (GUI
store interfaces), or tracked as an explicit product TODO ([`tracked-todos.md` #6](../router/tracked-todos.md#6-build-a-real-iprovidercostreconciler-for-gemini)).
That document is the **work list**. It is not the **repeatable method** for the next survey.

Without a standing method, each audit invents its own scope, severity scale, and tracker — which is
how a parallel "brutal-cozy-pascal" write-up had to be folded back into the same plan. The forces
are: (1) structural facts (call graphs, type hierarchies, blast radius) are cheap and should be
machine-derived; (2) naming an anti-pattern and scheduling a refactor is a judgment call that must
respect existing ADRs and the hot-path/security-boundary constraints those ADRs locked in; (3) the
workspace already has two MCP engines that split those jobs — **CodeGraph** for structure,
**Serena** for design reasoning — but agents were not instructed to use them as a pair, nor what to
do when one is down.

This ADR **complements** the existing plan. It does not supersede it, does not re-open ADR-0006/0007,
and does not turn the Gemini cost reconciler into a smell. It records the pipeline, the classification
matrix, the safety protocol, and a **live CodeGraph catalog of production `src/` as of 2026-09-02**.
Serena MCP (`plugin-serena-serena`) failed live tool discovery on this pass; classification below is
the human/agent judgment layer using CodeGraph evidence, with Serena's intended role specified so a
future healthy session can re-run the cognitive half without re-deriving the graph.

**Scope of smell scoring:** production source under `src/TotallyHotArcRouter*` (router, Quality, GUI
assemblies). `*.Tests`, the installer, `docs/`, and generated data are out of scope for scoring;
tests are the regression net, not the catalog.

### Deep Code Smell Analysis Plan (execution workflow)

```mermaid
flowchart TD
    subgraph codegraph ["CodeGraph MCP — structural engine"]
        G1["Index / explore production src/"]
        G2["Call paths, type hierarchies, blast radius"]
        G3["Hubs, cycles, hidden coupling"]
        G1 --> G2 --> G3
    end

    subgraph serena ["Serena MCP — cognitive engine"]
        S1["Name anti-patterns from evidence"]
        S2["Classify Critical / Major / Minor"]
        S3["Phased refactor vs leave-alone"]
        S1 --> S2 --> S3
    end

    subgraph safety ["Safety gate"]
        H1["Existing ADRs and the mechanical plan"]
        H2["Characterization tests + golden-path smoke"]
        H3["Build with zero warnings; coverage floor"]
    end

    G3 --> S1
    S3 --> H1
    H1 --> H2 --> H3
    H3 --> Fold["Fold findings into code-smell-refactoring-plan.md"]
```

1. **Map (CodeGraph first).** Call `codegraph_explore` with the symbols or question spanning the
   area (for a whole-repo survey: the known hubs plus a size-ranked file list of production
   `*.cs`/`*.razor`). Treat returned source as already read. Record blast radius for any symbol with
   tens of callers. Do not re-grep indexed code to "confirm" the graph.
2. **Judge (Serena when available).** Feed the graph evidence — not a file dump — to Serena:
   anti-pattern name, cyclomatic/cognitive notes, whether the size is *protocol complexity*
   (payload translators) vs *mixed responsibilities* (a remaining God Object). If Serena is
   unavailable, the agent performs this step explicitly and records "Serena skipped" so the catalog
   is not mistaken for a dual-engine result.
3. **Classify** using the matrix in Decision Outcome. Reject candidates that the existing plan
   already ruled out (per-provider translators, `ParseBudgetWindow` vs `BudgetWindowCodec`, acyclic
   project references, Orchestrator voters / Quality analyzers) unless CodeGraph shows the
   *structure* changed.
4. **Schedule.** New work that is mechanical goes into
   [`code-smell-refactoring-plan.md`](../router/code-smell-refactoring-plan.md). Work that changes a
   security boundary, public surface, or transport gets a new ADR first. Product features (Gemini
   reconciler) stay in [`tracked-todos.md`](../router/tracked-todos.md).
5. **Edit only after blast-radius review.** Hubs named in the live catalog (`RequestInterceptor`,
   `ManagementFacade`, `ProxyMiddleware`) require the safety protocol below.

### Live CodeGraph catalog (2026-09-02, production `src/` only)

CodeGraph (workspace `user-codegraph`, ~907 indexed files; two explore calls this pass) plus a
line-count ranking of production `*.cs`/`*.razor` (excluding `obj`/`bin` and `*.Tests`).

**Structural hubs (blast radius):**

| Symbol | File | CodeGraph callers (approx.) | Role |
|---|---|---|---|
| `RequestInterceptor` | `Proxy/RequestInterceptor.cs` | **152** (incl. `ProxyMiddleware`, `LocalEndpointResponder`, plus tests) | Routing/introspection hub — Shotgun Surgery risk on any signature or candidate-builder change |
| `ManagementFacade` | `Proxy/Management/ManagementFacade.cs` | **21** (`ProxyServer`, `ProviderAdminEndpoints`, `ProviderMcpTools`, `McpHostedService`) | Security-boundary façade (ADR-0006): public method set is the boundary; implementation is delegated |
| `ManagementFacadeDependencies` | `Proxy/Management/ManagementFacadeDependencies.cs` | **15** | Optional-collaborator bag threaded into internal services |
| `ProxyServer` | `Proxy/ProxyServer.cs` | **10** (`ProxyHostedService` + tests) | Composition root for HTTP/gRPC/admin mapping |
| `McpHostedService` / `McpServer` | `Mcp/McpHostedService.cs`, `Mcp/McpServer.cs` | Hosted-service wiring | Same 11-dependency constructor bag duplicated across host and server |

**Type / composition notes:** `ManagementFacade` still *instantiates* `ProviderManagementService`,
`BudgetAndPriceOverrideService`, and `SecretManagementService` (internal delegation, matching
ADR-0006). `ProviderManagementService` holds a `Func<ProvidersResponse> buildProvidersResponse`
callback into the façade — callback coupling, not a project cycle. `RequestInterceptor` implements
routing via `IModelRouteResolver`, `ICircuitBreaker`, `IDimensionInferrer`, `IRequestClassifier`,
`IRoutingPolicy`; `CircuitBreaker` → `ICircuitBreaker`, `KeywordDimensionInferrer` →
`IDimensionInferrer`, `HeuristicRequestClassifier` → `IRequestClassifier`. No new circular *project*
coupling was observed; the earlier plan's acyclic reference graph still holds.

**Size outliers (production lines, not a smell by themselves):**

| Lines | Path | Reading against the existing plan |
|---|---|---|
| 1345 | `Proxy/ProxyMiddleware.cs` | Down from 2751; Phase 2 extracted `LocalEndpointResponder`, `RequestTelemetryPublisher`, `CandidateGates`. Remaining God-pipeline: `InvokeCoreAsync` still owns failover + gate walk + header/error writers |
| 919 | `Hosting/ServiceCollectionExtensions.cs` | Phase 1 split into `AddRouterCore` / `AddProxyRequestPipeline` / … — registration volume, not mixed domain logic |
| 889 | `Proxy/Management/ProviderManagementService.cs` | Largest remaining *implementation* behind the façade (provider/model CRUD + capability scan + dialect) |
| 799 | `Proxy/RequestTelemetryPublisher.cs` | Phase 2 extract; still a wide collaborator set (session, usage, spend, budget, ledger, transcript, quality ingress) |
| 688 | `Gui/Platforms/Windows/TrayWindowManager.cs` | Platform glue; single-UI-thread invariant already documented |
| 670 | `Proxy/RequestInterceptor.cs` | Down from 945; `RoutingCandidateBuilder` already extracted. Residual mixed interception + classification |
| 656 | `Proxy/Translation/ToolCalling/ToolCallNormalizingStreamTranslator.cs` | Protocol complexity |
| 599 | `Transcripts/TaxonomyComparisonService.cs` | Domain-sized, not previously flagged |
| 589 | `Telemetry/UsageRollupStore.cs` | Persistence |
| 580 | `PriceCatalog/PriceCatalogDatabase.cs` | ADO.NET/schema owner |
| 455 | `Proxy/Management/ManagementFacade.cs` | Down from ~2090 / 1562 / ~450 as ADR-0006 intended — thin delegating façade |
| 368 | `Gui.Admin/ProviderAdminClient.cs` | HTTP by [ADR-0007](0007-provider-admin-client-stays-on-http.md) — not a migration candidate |

**GUI stores** (14 files under `Gui/Services/*Store.cs`): still concrete singletons without
interfaces — the existing plan's going-forward norm, not a dedicated pass.

### Non-smell use: the CodeGraph step applied to a correctness bug

Step 1 of this pipeline (map with CodeGraph first) is not exclusive to smell surveys — it is the
same "trace the call graph before touching code" discipline this ADR mandates for any change to a
catalog hub. It was applied on 2026-09-05 to a functional-correctness investigation rather than a
smell audit: whether `JudgeShadowScoreObserver`'s write-time trigger could still fire under the
hold-based `QualityScoreAggregator` introduced by Phase N3. The defect found (the judge could never
be started once holding replaced write-time observation, silently degrading every judged request to
the join timeout) was not a smell with a severity grade — it was a shipped feature contributing
nothing — so it is tracked as its own fix plan rather than added to the smell catalog above:
[`judge-join-deadlock-fix-plan.md`](../router/judge-join-deadlock-fix-plan.md).

## Decision Drivers

- **Complement, don't fork** — one mechanical tracker; this ADR is the method and the current
  structural snapshot.
- **Structure before judgment** — CodeGraph blast radius and hierarchies before naming a smell.
- **Judgment is not optional, tools might be** — Serena classifies when healthy; a down Serena must
  not invent a third tracker or skip the catalog.
- **Respect locked decisions** — ADR-0006 (façade public surface), ADR-0007 (HTTP admin client),
  hot-path `CandidateGates` order.
- **Production `src/` only** — tests measure safety; they are not scored as smells.
- **Zero-warning build and 80% coverage** — same gates as `AGENTS.md` / the existing plan's
  validation section.

## Considered Options

- Option A — Keep ad-hoc audits only (no standing pipeline; each session writes a new plan)
- Option B — Adopt CodeGraph (structure) + Serena (judgment) as the standing dual-engine pipeline,
  folding results into the existing mechanical plan
- Option C — Rely on Roslyn analyzers / file-size CI only, without MCP graph or design classification

## Decision Outcome

Chosen option: **"Option B"**, because **complement, don't fork** forbids a parallel smell tracker,
**structure before judgment** requires CodeGraph as the first engine, and **judgment is not optional,
tools might be** requires Serena's role to be written down even when this pass cannot call it.
Option A is what produced the second audit that had to be merged by hand. Option C catches unused
usings, not God Objects or Shotgun Surgery.

### Smell classification matrix

| Severity | Meaning | Typical evidence | Default action |
|---|---|---|---|
| **Critical** | Change is unsafe without a new ADR or would break a locked boundary / hot-path contract | Hub with large blast radius *and* a proposed public-surface or gate-order change; security-boundary split; missing characterization tests on the proxy path | Stop. Write or cite an ADR. Do not "just extract." |
| **Major** | Cohesion/size/coupling that still hurts review, but existing tests + an internal split can proceed | Files ≫ 800 lines with mixed responsibilities; wide constructors; callback coupling inside an already-decided façade | Schedule a phase in the mechanical plan; blast-radius review; characterization tests if coverage is thin |
| **Minor** | Size, duplication, or missing interface that is legitimate complexity or a going-forward norm | Protocol translators; DI registration volume; GUI stores without interfaces; documented platform constraints | Note; extract only when the file is touched for another reason |

Apply the matrix to **this pass's catalog** (Serena skipped — agent judgment on CodeGraph + sizes):

| ID | Smell | Location | Severity | Why this grade | Relation to existing plan |
|---|---|---|---|---|---|
| S1 | Residual pipeline God Object | `ProxyMiddleware` (`InvokeCoreAsync` + error/header helpers, 1345 lines) | **Major** | Still the largest production file and the request hot path; Phase 2 already removed the worst of it | Continue only with staged extracts + golden-path smoke (existing validation gate item 6) |
| S2 | Shotgun Surgery hub | `RequestInterceptor` (152 CodeGraph callers, 670 lines) | **Major** | Any further split touches middleware, local endpoints, and a wide test set | Phase 4 item 8 partially done (`RoutingCandidateBuilder`); further cuts need interceptor-specific coverage first |
| S3 | Concentrated CRUD + scan behind the façade | `ProviderManagementService` (889 lines) | **Major** | Internal to ADR-0006's boundary; file is now where provider/model mutation *implementation* lives | Eligible for *internal* decomposition only — must not become a second public façade |
| S4 | Telemetry side-effect hub | `RequestTelemetryPublisher` (799 lines, wide ctor) | **Major** | Already decomposed into named private methods + characterization tests | Leave unless a new collaborator forces another split |
| S5 | Duplicated management wiring | `McpHostedService` / `McpServer` 11-dep constructors | **Minor** | Feature envy of the same bag; not a behavior bug | Optional extract of an options/record bag when MCP hosting is next touched |
| S6 | Callback coupling | `ProviderManagementService` → `BuildProvidersResponse` | **Minor** | Intentional: one snapshot builder for every CRUD cluster | Do not "fix" by making the service public |
| S7 | Composition-root bulk | `ServiceCollectionExtensions` (919 lines) | **Minor** | Already split by subsystem | No further phase |
| S8 | GUI stores without interfaces | `Gui/Services/*Store.cs` | **Minor** | Existing going-forward norm | Extract interface when a store is touched |
| S9 | Protocol-sized translators / stream normalizers | Anthropic/Gemini translators, `ToolCallNormalizingStreamTranslator` | **Minor** / **rejected as duplication** | Different wire shapes (existing "checked and rejected") | Do not unify bodies |
| S10 | Thin security façade | `ManagementFacade` (455 lines, 21 callers) | **Critical if public surface changes**; otherwise **not a smell** | ADR-0006's desired end state | Public-surface change requires a new ADR |

### Safety protocols

1. **Blast radius first.** Before editing a catalog hub, re-run CodeGraph on that symbol and list
   production callers. Do not start with a test-only or docs-only split that leaves the hub's
   contract implied.
2. **Locked ADRs.** Do not register `ProviderManagementService` / `BudgetAndPriceOverrideService` /
   `SecretManagementService` as independently injectable public services (ADR-0006). Do not migrate
   `ProviderAdminClient` to gRPC (ADR-0007).
3. **Hot path.** Changes under `ProxyMiddleware`, `CandidateGates`, `RequestInterceptor`, or
   `RequestTelemetryPublisher` keep Serilog templates as static literals, carry existing log events
   to the new home, and require the golden-path smoke (streaming, buffered, Bedrock, `/v1/models`,
   `/api/tags`, `/api/show`) when request handling changes.
4. **Validation gate** (same as the mechanical plan / `AGENTS.md`): `dotnet build` with zero
   warnings; XML docs accurate after moves; unit tests pass; ≥80% line coverage on non-GUI
   assemblies; no unusually heavy test over 5 seconds.
5. **Serena down.** Record the skip, still classify, still fold into the one mechanical plan. Retry
   Serena on the next smell survey; do not treat a CodeGraph-only catalog as dual-engine complete.
6. **Out of scope.** Do not score test projects. Do not open `tracked-todos.md` #6 as a refactor.

### Phased refactoring roadmap (this pipeline's output, not a new tracker)

- **Now (method):** agents follow this ADR + `AGENTS.md` dual-engine section on every architectural
  survey.
- **When touching the hot path:** S1/S2 only as internal extracts with characterization tests —
  append steps to the existing plan's Phase 2/4, do not open Phase 5 elsewhere.
- **When touching management implementation:** S3 internal-only; S6 stays.
- **Opportunistic:** S5, S8.
- **Closed / do not relitigate here:** S9, S10's public surface, ADR-0007, Gemini reconciler.

### Consequences

- Good, because the next audit has a named pair of engines, a severity scale, and one place to put
  mechanical work — reducing the chance of a third parallel plan.
- Good, because this pass's CodeGraph catalog is dated evidence, so "ProxyMiddleware is 2751 lines"
  cannot silently persist after Phase 2.
- Neutral, because Serena was unavailable; a later dual-engine run may regrade S1–S4 without a new
  ADR if the *method* is unchanged.
- Bad, because a standing pipeline adds agent latency (mandatory CodeGraph before architectural
  edits) and can over-fit file size as a smell — mitigated by the "checked and rejected" rule and
  the Minor bucket for protocol complexity.
- Bad, because documenting MCP tool names in `AGENTS.md` will drift if Cursor namespace ids change —
  mitigated by describing *roles* (structural vs cognitive) first and ids second.

## Amendment 1 (2026-09-02): stop rules

**Status:** proposed. Amends the Decision Outcome above; does not change the chosen option or
re-open ADR-0006/0007.

### Why

The pipeline above defines how to *find* smells and says nothing about when to stop acting on them.
Three audits have now run against this codebase: the original CodeGraph + Serena survey, the
independent "brutal-cozy-pascal" pass, and the blind dual-engine pass catalogued above. Each found
new items. The third opened Phase 5 with 9 items and 2 Criticals *after* Phases 1–4 were reported
complete, and one of those items — `PersistedSessionsClient` re-diverging from
`GrpcAdminClientBase<,>` — is a regression of a smell Phase 1 already fixed once.
[`code-smell-refactoring-plan.md`](../router/code-smell-refactoring-plan.md) has reached 837 lines
and now carries its own caveat that closing everything raised should be read as
"not as 'the codebase is clean.'"

A smell audit is a generator, not a checklist: run it against any codebase and it returns findings,
because that is what it is for. Option B as written therefore has no terminating condition — Phase 6
is guaranteed to exist. The severity matrix grades findings; it never rejects one for being *not
worth fixing*.

Measurement does not support treating this as code decay. Production `src/` only (excluding
`*.Tests`, `obj`, `bin`), sampled on `main`'s first-parent history:

| Date | Production files | Production lines | Avg lines/file |
|---|---|---|---|
| 2026-08-05 | 211 | 29,615 | **140** |
| 2026-08-14 | 290 | 42,239 | **145** |
| 2026-08-22 | 382 | 57,294 | **149** |
| 2026-09-02 | 475 | 70,394 | **148** |

Average file size is flat across a 2.4x growth in production lines. Had refactoring been shredding
the codebase into ever-smaller fragments, that column would fall as the file count climbed. The file
count roughly doubled because five projects (`Gui.Admin`, `Gui.Charts`, `Gui.Console`,
`Gui.Telemetry`, `Sandbox`) were added in the same four weeks. The codebase is growing by feature,
not fragmenting under refactoring. What this amendment constrains is the **process** — audit cadence
and an unbounded plan — not measured decay in the code.

### Stop rules

These are gates on *entering* the mechanical plan. They apply after the classification matrix and
can only remove items, never add them.

1. **A finding needs an observed cost, not a name.** "God Object," "Feature Envy," and "wide
   constructor" are descriptions, not defects. Before an item enters the mechanical plan it must
   cite at least one observed cost: a bug traced to the structure, a merge conflict or review
   round-trip it caused, a feature it blocked or measurably slowed, or a comprehension failure
   someone actually hit. Blast radius and line count are evidence *about* an item; they are never
   the cost itself. An item with no observed cost is recorded in this ADR's catalog as a known shape
   of the code and left alone.
2. **No scheduled audits.** A dual-engine pass runs when something hurts — a bug cluster in one
   area, a feature that proved hard to land, a regression of a previously-fixed smell — and names
   that trigger in its write-up. It does not run on a cadence, at a phase boundary, or because it
   has been a while. "Standing" in this ADR's title means *the standing method for when an audit
   happens*, not a standing obligation to audit.
3. **Net-lines budget.** A refactor that adds more production lines than it removes, without
   deleting a behavior, fixing a bug, or unblocking a named feature, states that justification in
   its PR description. Splitting one 900-line file into six 200-line files is a net add plus five
   new indirections; that is sometimes right, but it is a trade to argue, not a default.
4. **The plan terminates.** `code-smell-refactoring-plan.md` closes once S1
   (`ProxyMiddleware.InvokeCoreAsync`) and its outstanding golden-path smoke are done. The remaining
   Phase 5 items are re-tested against rule 1 first and dropped if they cannot pass it. Anything
   found afterwards starts a new document with a stated end condition — no single document absorbs a
   fourth audit.

### Effect on the classification matrix

The three severities are unchanged. What changes is that **"no finding" is now a legal outcome**: an
item that cannot show an observed cost under rule 1 is not downgraded to Minor, it is not filed at
all. Minor continues to mean "real, but extract only when the file is touched for another reason."

Re-graded against rule 1, S1 is the only catalogued item with a demonstrated cost — a 715-line method
on the request hot path, which the plan's own history shows is where failover regressions land. S2–S8
stay catalogued but unscheduled until one of them produces a cost.

### Consequences of this amendment

- Good, because the pipeline now has an exit. Phase 6 has to be *earned* by an observed cost rather
  than produced automatically by running the tools again.
- Good, because it protects against the failure the size table rules out today but that an unbounded
  plan invites: refactoring that trades one large file for many small indirections and calls it
  progress.
- Good, because rule 1 gives an agent a defensible reason to answer an architectural survey with
  "nothing here is worth scheduling," which the original matrix did not permit.
- Bad, because "observed cost" is a judgment call and a determined reader can always construct one.
  Mitigated by requiring the cost to be *cited* in the plan entry, so a thin justification is visible
  in review.
- Bad, because genuine structural problems may sit uncatalogued until they cause a bug — accepted
  deliberately: this ADR's catalog still records them, so the evidence is on hand when one does.
- Neutral, because the CodeGraph-first / Serena-second method, the safety protocols, and the locked
  ADRs are all unchanged.

## Pros and Cons of the Options

### Option A

Ad-hoc audits: each session surveys from scratch and writes whatever document it likes.

- Good, because it needs no ceremony and can go deep on one file.
- Bad, because independent audits already forked and had to be merged by hand.
- Bad, because severity and scope reset every time, so Phase 4 "backlog" items get rediscovered as
  if they were new Criticals.

### Option B

CodeGraph maps structure; Serena (or the agent, if Serena is down) classifies; results fold into
the existing mechanical plan; this ADR holds the method.

- Good, because it matches the tools the workspace already exposes and the "structure then
  judgment" split those tools imply.
- Good, because it preserves ADR-0006/0007 and the existing plan instead of rewriting them.
- Bad, because it depends on MCP health and on agents actually following `AGENTS.md`.

### Option C

CI analyzers and line-count budgets only.

- Good, because it is deterministic and does not need MCP.
- Bad, because it cannot distinguish protocol complexity from mixed responsibilities, nor report
  blast radius (the 152-caller `RequestInterceptor` fact).
- Bad, because it would flag `ServiceCollectionExtensions` and payload translators as equally
  "too big."

## More Information

- Mechanical work list: [`docs/router/code-smell-refactoring-plan.md`](../router/code-smell-refactoring-plan.md)
- Agent instructions for this pipeline: [`AGENTS.md`](../../AGENTS.md) (Dual-engine code-smell analysis)
- Related decisions: [ADR-0006](0006-split-managementfacade-along-crud-aggregate-boundaries.md),
  [ADR-0007](0007-provider-admin-client-stays-on-http.md)
- Product TODO, not a smell: [`tracked-todos.md` #6](../router/tracked-todos.md#6-build-a-real-iprovidercostreconciler-for-gemini)
- Amendment 1 measurements: `git ls-tree` line counts over `main`'s first-parent history, production
  `src/` only. Reproduce by summing `.cs` line counts excluding `*.Tests`, `obj`, and `bin` at each
  sampled commit.
- This pass: CodeGraph MCP only; Serena MCP tool discovery failed (`plugin-serena-serena`).
  Re-run the cognitive half when that namespace is healthy.
