# Admin-Slice Consolidation Plan

**Status: Phases 0-3 implemented (2026-09-11).** Two items outstanding, per the end condition below:
the manual golden-path smoke (see [Validation gate](#validation-gate)) and one subsequent admin knob
added through the new seams.

**End condition (stated up front, per [ADR-0008 Amendment 1](../adr/0008-codegraph-serena-dual-engine-code-smell-pipeline.md#amendment-1-2026-09-02-stop-rules)
rule 4):** this document closes when the smoke below has run **and one subsequent admin knob has been
added through the new seams** — that addition is the only real proof the marginal cost actually fell.
Findings after that start a new document rather than extending this one.

**Engine note:** this survey ran **CodeGraph-only. Serena MCP was unreachable** (cached connection
failure). Per ADR-0008 step 2 the Critical/Major/Minor classification below is **the agent's own and
not a dual-engine result** — do not read this catalog as one. **Serena skipped.**

**Trigger:** maintainer-reported pain — "the application keeps growing and growing" — not a cadence
or a phase boundary, per ADR-0008 step 3.

**Scope surveyed:** production only (`TotallyHotArcRouter`, `.Quality`, `.Gui`, `.Gui.Admin`,
`.Gui.Charts`, `.Gui.Console`, `.Gui.Telemetry`). `Installer` and all `*.Tests` projects excluded —
tests are the regression net, not the catalog.

---

## The finding

Growth in this repo is not diffuse. It has **one dominant mechanism**, and the existing
[code-smell refactoring plan](code-smell-refactoring-plan.md) never looked at it: every Governance
admin knob is the same **six-file vertical slice across three assemblies**, and that slice now exists
**11 times**.

| Layer | Count | Lines | Consolidated? |
|---|---|---|---|
| `*AdminGrpcService` (router) | 11 | 1,783 | No — left alone, each is genuinely per-feature |
| `*AdminClient` + `I*AdminClient` (`Gui.Telemetry`) | 11 | 2,666 | **Was half** — `GrpcAdminClientBase` had taken the channel/dispose/wrap scaffolding |
| `*AdminException` subclasses | **12** | ~150 | **Now deleted** — every one added zero behavior over `GrpcAdminException` |
| `*Store` (`Gui/Services`) | 11 | ~2,400 | **Now on `AdminStoreBase<TClient>`** |
| `ProxyServer` ctor + endpoint blocks | 11 | ~200 | **Now an `IAdminServiceModule` registry** |

`ProxyServer.cs`'s 351-line constructor was 11 copies of one block, and its own comments said
*"Same reasoning again"* four times. The code was already telling us it was a pattern.

The exception count turned out to be **12, not 11** — `PersistedSessionsClient` had one too, which the
initial survey missed.

### Observed cost (Amendment 1 rule 1)

- **`d2dca10` (judge calibration) cost 42 files and +3,171 lines** for a single read-only panel.
  ~630 production and ~420 test lines of that were pure transport plumbing.
- The slice forced edits to the same shared files every time: `ProxyServer.cs`,
  `ProxyServerDependencies.cs`, `MauiProgram.cs`, `Governance.razor`,
  `ProxyServiceCollectionExtensions.cs`, `telemetry.proto` — the Shotgun Surgery signature, on the same
  evidence basis that justified **A2**.
- The maintainer named the cost directly. This survey exists because of that.

---

## What shipped

| Phase | Commit | Net production lines |
|---|---|---|
| 0 — [ADR-0010](../adr/0010-collapse-the-per-feature-admin-slice-onto-shared-seams.md) | `f1e8705` | docs only |
| 1a — delete 12 `*AdminException` subclasses | `f1e8705` | −112 |
| 1b — 11 stores onto `AdminStoreBase<TClient>` | `762e369` | **−263** (already absorbing the new 224-line base) |
| 2 — `IAdminServiceModule` registry | `87f856d` | **+63** — see the honest note below |
| 3 — analyzer gate (D1) | `6c9f9e9` | −4 usings |

**Phase 2 adds lines, and that is stated rather than smoothed over** (Amendment 1 rule 3):
`ProxyServer.cs` −97, `ProxyServerDependencies.cs` +160. The justification is the edit set, not the
volume — adding an optional admin service went from editing `ProxyServer.cs` in two places ~120 lines
apart *plus* its dependency record, to editing the dependency record alone.

### Three deviations from ADR-0010 as written

All three are recorded in that ADR's Amendment 1; they are summarized here because each was a case of
the plan being wrong rather than the work being incomplete.

1. **`AdminStoreBase` is not generic over its exception.** The ADR specified
   `AdminStoreBase<TClient, TException>` so the HTTP stores could bind `ProviderAdminException`. No HTTP
   store ended up deriving from it, so the parameter bought nothing and would itself have been
   speculative generality — the exact smell this work exists to remove.
2. **Three stores are deliberately *not* on the base.** `RoutingGateStore` polls on a background loop
   behind its own lock, with a three-valued connection state and `IAsyncDisposable`. `ProviderAdminStore`
   and `UsageStore` speak HTTP ([ADR-0007](../adr/0007-provider-admin-client-stays-on-http.md)), have no
   `IsUnavailable` for the base's central rule to key off, and report failures through `ToastService`
   rather than `LastError`. Fitting them would have meant growing the base for two callers, which is how
   a useful base class turns into a burden.
3. **The load path is now connectivity-aware everywhere.** Most stores already set
   `IsReachable = !ex.IsUnavailable` on a failed load; `PriceSourceStore`, `RoutingModeStore` and
   `PersistedSessionStore` bluntly set it false for any failure. The base takes the connectivity-aware
   reading, matching what `IsReachable`'s own doc comment says it means and what every store already did
   on its *mutation* path. A rejected load now renders inline instead of collapsing the panel.

### What Phase 3 measured

The dead-code question has an answer now, and it is **essentially none**:

| Rule | Hits | Verdict |
|---|---|---|
| `IDE0051` unused private members | **2** | Both false positives — see below |
| `IDE0005` unnecessary usings | **4** | All introduced by this series' own `ProxyServer` change |
| `IDE0052` unread private members | **0** | |
| `CA1823` unused private fields | **0** | |

Both `IDE0051` hits were `ProviderOptions.PrintMembers` and `ResolvedModelRoute.PrintMembers`, and
**deleting them would have been a security regression**: a record's user-declared `PrintMembers`
replaces the compiler-generated one and is called by the generated `ToString()`, which the analyzer does
not model. Both exist to redact AWS secret keys, session tokens, and header values out of `ToString()`.
Suppressed at both sites with that reasoning inline.

`CA2000` (1,758) and `CA1849` (188) stay silent with their measured counts and reasons recorded in
`src/.editorconfig`. Neither is a to-do list; both are dominated by patterns the analyzer cannot see
through (container-owned lifetimes, a synchronous SQLite provider behind an async API).

---

## What was checked and rejected

A survey that finds only work is a survey that was not honest about its misses.

- **Performance was not measured, and no performance problem was found.** The prior audit recorded 0
  `async void` and 0 `Thread.Sleep`; C7's nine sync-over-async sites are off the hot path. Nothing here
  is a runtime optimization — "optimization" in this document means structural. If runtime cost is the
  real question, that is a profiling exercise and a different document.
- **A hand-rolled dead-code sweep produced 36 candidates; 35 were false positives.** Static-class call
  sites and JSON DTO graphs — `ChatChoice`/`ChoiceLogprobs`/`TopLogprobCandidate`,
  `FencedCodeBlockParser`, `DelimiterBalance`, `GateCheck`, `KnownPriceSource`,
  `*ServiceCollectionExtensions` — all read as unreferenced to a text scan and are live. Phase 3 exists
  because of this: the compiler is the only sound oracle, which is what **D1** always said.
- **The 11 `*AdminGrpcService` classes were left alone.** They look duplicated by name but each maps a
  genuinely different RPC surface onto different collaborators. Same verdict the earlier plan reached
  for the per-provider payload translators.
- **`src/TotallyHotArcRouter.Sandbox{,.Tests}`** hold only `bin/`+`obj/`, are **untracked**, and are
  absent from `TotallyHotArcRouter.slnx`. Local disk litter, not a repo change.
- **`TaxonomyPromotionCriterion` is dark but deliberate — kept.** 100 production + 97 test lines with
  **zero callers**, confirmed via `codegraph callers` and again by `IDE0051` finding nothing (it is
  `public`, so the unused-member analyzers cannot see it — that gap is real and worth knowing). It
  implements Phase T4 of [`self-organizing-classification-plan.md`](self-organizing-classification-plan.md)
  and is built ahead of its consumer, not abandoned. **Recorded here so a future audit does not re-flag
  it as dead code.**

---

## Validation gate

| # | Gate | Status |
|---|---|---|
| 1 | `dotnet build` zero warnings, zero errors | ✅ with the new analyzer gate live |
| 2 | Touched XML docs re-read for staleness | ✅ — incl. 24 `cref`s repointed as members moved to the base |
| 3 | All tests pass | ✅ router 2,719 (8 new), Gui 430 (7 new), Gui.Telemetry 225, Gui.Admin 106, Gui.Charts 71, Gui.Console 30 |
| 4 | No test over 5 seconds | ✅ slowest suite 18s total |
| 5 | Serilog templates stay static literals | ✅ — the base logs `"Admin load failed: could not {Operation}."` with the operation as a structured property |
| 6 | **Manual golden-path smoke across all 11 Governance panels** | ❌ **outstanding** |
| 7 | Deferred items recorded with evidence | ✅ above, and in `src/.editorconfig` |

**Gate 6 is the one open item.** `ProxyServer` owns the Kestrel host and the gRPC endpoint mapping, and
`MapGrpcService` only reflects over the service type — it never constructs it — so a registration
mistake surfaces as a service that maps and *then* throws on its first RPC, which no unit test sees.

`AdminServiceModuleTests` was added as partial insurance: it finds the module groups by reflection
rather than a hand-maintained list (a list would need the same edit as the thing it guards, so it would
go stale in exactly the case that matters) and fails if a group implements the seam but never reaches
`AdminModules`. **That is not a substitute for the smoke** — it proves the registry carries every group,
not that each service answers over the wire.

### New tests this work added

- `AdminStoreBaseTests` — the always-raise-`Changed`-even-on-failure rule (previously re-implemented
  once per store and asserted nowhere), the rejection-keeps-reachable rule, cleanup-before-notify
  ordering, and that disposal releases what a store built but never what it was handed.
- `AdminServiceModuleTests` — see above.
