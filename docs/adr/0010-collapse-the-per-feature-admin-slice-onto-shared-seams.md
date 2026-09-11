# 0010. Collapse the per-feature admin slice onto shared seams

**Status:** proposed <!-- proposed | accepted | rejected | deprecated | superseded by ADR-NNNN -->
**Date:** 2026-09-11
**Deciders:** David Pizon

## Context and Problem Statement

Every Governance admin knob is built as the same six-file vertical slice across three assemblies: a
`.proto` service, a router-side `*AdminGrpcService`, a `Gui.Telemetry` `*AdminClient` +
`I*AdminClient` + `*AdminException`, a `Gui/Services/*Store`, and a Razor panel — plus registration
edits to `ProxyServer`, `ProxyServerDependencies`, `MauiProgram`, and `Governance.razor`. That slice
now exists **11 times**, and the most recent one (`d2dca10`, judge calibration) cost **42 files and
+3,171 lines**, roughly a third of it transport plumbing for a single read-only panel.

Three of the slice's layers are pure ceremony: the 12 `*AdminException` subclasses add **no state or
behavior** over `GrpcAdminException` (11 admin clients plus `PersistedSessionsClient`); 11 GUI stores
re-implement an identical
`IsLoaded`/`IsReachable`/`LastError`/`Changed` + owned-vs-injected-constructor + swallow-and-flag
shape; and `ProxyServer`'s 351-line constructor is 11 copies of one `if (xxxAdmin is not null) { …
AddSingleton … }` block whose own comments say *"Same reasoning again"* four times.

Collapsing these removes 12 `public` types from `Gui.Telemetry` and adds a registration interface to
`ProxyServerDependencies`. Both are public-surface changes, which `AGENTS.md` and
[`code-smell-refactoring-plan.md`](../router/code-smell-refactoring-plan.md)'s "Constraints carried
forward" gate behind an ADR — hence this record rather than a straight refactor commit.

## Decision Drivers

- The measured cost is **marginal**, not total: what hurts is the price of the *next* knob, so the
  fix must shrink the per-feature edit set, not merely move lines between files.
- [ADR-0007](0007-provider-admin-client-stays-on-http.md) deliberately keeps `ProviderAdminClient` on
  HTTP. Any shared seam must not quietly erase that split or force a transport migration.
- [ADR-0006](0006-split-managementfacade-along-crud-aggregate-boundaries.md)'s constraint stands: no
  change to `ManagementFacade`'s public method set, and no promoting internal collaborators to
  independently injectable services.
- This repo already accepted a dispatch-table answer to exactly this smell class —
  `ProviderRegistration`, introduced for `UsageExtractor`/`ResponseTextExtractor`, which uncovered a
  real Ollama bug in the process. A consistent second application is cheaper to review than a novel
  pattern.
- [ADR-0008 Amendment 1](0008-codegraph-serena-dual-engine-code-smell-pipeline.md#amendment-1-2026-09-02-stop-rules)
  rule 3: a refactor that adds more production lines than it removes must justify itself. Two of the
  three changes here delete lines; the third is roughly line-neutral and is argued on edit-set size.

## Considered Options

- **Option 1 — Leave the slice alone.** Accept the duplication as the cost of a layered,
  transport-separated architecture.
- **Option 2 — Extract one method per feature group.** The remedy the existing catalog recorded for
  C5: give `ProxyServer` a `Configure<Group>` private method per admin service, and leave the GUI
  stores and exception subclasses as they are.
- **Option 3 — Collapse each repeated layer onto a shared seam.** Delete the exception subclasses in
  favor of `GrpcAdminException`; introduce an `AdminStoreBase` template method for the repeated store
  shape; introduce `IAdminServiceModule`, implemented by the `*AdminDependencies`
  records that already exist, so `ProxyServer` iterates instead of enumerating.
- **Option 4 — Generate the slice.** Drive the client, store, and registration code from the `.proto`
  definitions with a source generator.

## Decision Outcome

Chosen option: **"Option 3"**, because it is the only option that reduces the per-feature edit set —
the driver that actually matters here. Option 2 would have relocated 11 copies into 11 methods and
left the next knob costing exactly what the last one did; that is the specific failure mode
Amendment 1 rule 3 warns about, and it is worth recording that the catalog's own C5 remedy was wrong
about the cause. Option 3 also reuses the `ProviderRegistration` precedent rather than inventing a
seam.

`TelemetryGrpcService`, `RoutingModeAdminGrpcService`, `UpdateAdminGrpcService`, and
`RoutingGateAdminGrpcService` stay mapped explicitly and unconditionally. They are core operational
state rather than optional feature groups, and `RoutingModeAdminGrpcService` carries a deliberate
`RoutingOptions` fallback; folding them into the conditional loop would change *when* they map.

### Amendment 1 (2026-09-11): the store base is not generic over its exception, and covers 11 stores

This ADR originally specified `AdminStoreBase<TClient, TException>`, generic over the exception so
"gRPC stores bind `GrpcAdminException` and the two HTTP-backed stores bind `ProviderAdminException`,
so both transports get the same scaffolding without a shared exception hierarchy." **Implementing it
showed that premise was wrong, and the shipped seam is `AdminStoreBase<TClient>`, bound to
`GrpcAdminException`, covering 11 stores rather than 13.**

Three stores were examined and deliberately left on their own shape:

- **`RoutingGateStore`** polls continuously on a background loop behind its own lock, derives
  `IsReachable` from a three-valued `RouterConnectionState`, is `IAsyncDisposable`, and raises
  `Changed` only on an actual change. It shares the name "Store" and none of the shape.
- **`ProviderAdminStore`** and **`UsageStore`** speak HTTP. `ProviderAdminException` carries no
  unavailable-versus-rejected flag for the base's central rule to key off, and both surface failures
  through `ToastService` rather than `LastError` — their mutation paths do not touch reachability at
  all. Fitting them would have meant adding a toast hook and an exception-typed failure callback to
  the base **for two callers**.

With no HTTP store deriving from it, the `TException` parameter bought nothing and would have been
speculative generality — the exact smell this ADR exists to remove. It was dropped, and
`GrpcAdminStoreBase<TClient>` (an intermediate type that existed only to bind it) was folded into
`AdminStoreBase<TClient>`.

**ADR-0007's transport split is still honored, and more plainly than the generic managed:** the base
is documented as the gRPC stores' seam, and the HTTP stores are simply not part of it.

### Consequences

- Good, because the next admin knob stops touching `ProxyServer.cs` at all, and its store shrinks
  from ~120 lines to ~30 (`RoutingModeStore` goes 92 → ~35).
- Good, because ~800 production lines of ceremony and 12 public types disappear, and the
  always-raise-`Changed`-even-on-failure rule — previously re-implemented once per store, and the thing
  that keeps the UI off a permanent "loading" state when the router is down — gets one home and one test.
- Bad, because widening `catch (XxxAdminException)` to `catch (GrpcAdminException)` is a genuine
  behavior change at 33 catch sites. It is safe **only** because no `try` block spans two different
  admin clients; that was enumerated and confirmed before this ADR, and must be re-confirmed before
  the commit lands.
- Bad, because callers lose the ability to distinguish admin services by exception type. Accepted:
  `GrpcAdminException.IsUnavailable` plus the message already carry everything the 33 sites branch on.
- Neutral, because the base **unifies a load-path inconsistency the stores already had**. Most set
  `IsReachable = !ex.IsUnavailable` on a failed load; `PriceSourceStore`, `RoutingModeStore` and
  `PersistedSessionStore` bluntly set it false for any failure. The base takes the connectivity-aware
  reading, because that is what `IsReachable`'s own doc comment says it means everywhere, and what
  every store already did on its mutation path. A *rejected* load therefore now leaves a panel
  rendering its data plus an inline error rather than collapsing to "router unreachable".
- Bad, because the router-side change **adds production lines rather than removing them**: measured at
  `ProxyServer.cs` -97, `ProxyServerDependencies.cs` +160, **net +63**. The estimate in this ADR's first
  draft ("roughly line-neutral") was wrong, and the overshoot is the interface plus six pairs of method
  scaffolding and their doc comments. Per ADR-0008 Amendment 1 rule 3 this needs a justification rather
  than a shrug, and the justification is the edit set, not the volume: adding an optional admin service
  went from editing `ProxyServer.cs` in two places ~120 lines apart **plus** its dependency record, to
  editing the dependency record alone. The two halves of a feature's wiring also can no longer drift
  apart, which is the failure this file's own comments warned about.
- Neutral, because this locks in the `*AdminDependencies` records as the place registration knowledge
  lives. A future admin service needing inner-container wiring implements `IAdminServiceModule` there
  rather than adding a block to `ProxyServer`.

## Pros and Cons of the Options

### Option 1 — Leave the slice alone

- Good, because zero risk to the Governance panels and the proxy's inner Kestrel host.
- Good, because each slice is independently readable with no indirection to follow.
- Bad, because the measured trend is the problem: 554 commits and ~125k lines in two months, with one
  knob costing 42 files. Doing nothing endorses that rate.
- Bad, because three of the layers are duplication with no design rationale behind them — the
  `*AdminException` subclasses in particular were never a decision, just a shape that got copied.

### Option 2 — Extract one method per feature group

- Good, because it is the lowest-risk mechanical change, fully local to `ProxyServer`.
- Good, because it directly addresses the recorded C5 finding as written.
- Bad, because it treats a 351-line constructor as a long-method smell when the measurement says it
  is 11 instances of one pattern. The duplication survives, renamed.
- Bad, because it does nothing for the GUI stores or the exception subclasses, which are two thirds of
  the per-knob cost.

### Option 3 — Collapse each repeated layer onto a shared seam

- Good, because it shrinks the marginal cost, which is the cost that was actually observed.
- Good, because `GrpcAdminClientBase` already proved this exact move on the client half of the same
  slice, so the pattern is established and its tests are the regression net.
- Good, because the generic-over-exception base keeps ADR-0007's transport split explicit in the type
  system rather than papering over it.
- Bad, because it touches all 11 features at once across three assemblies — a wide, if shallow, diff.
- Bad, because a module that silently fails to register is invisible to the unit suite, so it forces a
  manual smoke of every Governance panel rather than a sampled one.

### Option 4 — Generate the slice from `.proto`

- Good, because it would drive the marginal cost close to zero: one proto edit per knob.
- Good, because the `.proto` file is already the single source of truth for the wire contract.
- Bad, because the stores are not mechanical. Busy flags (`IsRefreshing`/`IsSaving`/`IsRunning`),
  per-panel error copy, and reload-after-mutate ordering differ per feature and would need escape
  hatches that erode the generator's value.
- Bad, because a source generator is a large, hard-to-reverse commitment to fix a problem three small
  seams address — and it would have to be debugged against MAUI/Blazor tooling.
- Bad, because it would obscure the `ProviderAdminClient` HTTP exception from ADR-0007, which has no
  proto definition at all.

## More Information

- Survey and staged implementation plan:
  [`docs/router/admin-slice-consolidation-plan.md`](../router/admin-slice-consolidation-plan.md).
- This survey ran **CodeGraph-only — Serena MCP was unreachable** — so its severity classification is
  the agent's own and not a dual-engine result, per
  [ADR-0008](0008-codegraph-serena-dual-engine-code-smell-pipeline.md) step 2.
- Supersedes nothing, but re-scopes the **C5** item in
  [`code-smell-refactoring-plan.md`](../router/code-smell-refactoring-plan.md): that plan recorded
  `ProxyServer`'s constructor as a long-method smell; the measurement here says it is a registry.
