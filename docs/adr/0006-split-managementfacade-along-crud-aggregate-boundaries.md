# 0006. Split ManagementFacade along CRUD-aggregate boundaries, not its public surface

**Status:** accepted <!-- proposed | accepted | rejected | deprecated | superseded by ADR-NNNN -->
**Date:** 2026-09-02
**Deciders:** David Pizon

## Context and Problem Statement

`Proxy/Management/ManagementFacade.cs` is 1926 lines and 19 public methods: provider CRUD, model
CRUD, capability scanning, secret read/write, budget/window parsing, price-override CRUD, and
rate-limit history. Its own doc comment calls it "the single security boundary" for management
operations, shared by the REST layer (`ProviderAdminEndpoints.cs`, 19 distinct method calls) and the
MCP provider tools (`ProviderMcpTools.cs`, 8 distinct method calls). `docs/router/code-smell-refactoring-plan.md`
Phase 3 step 1 already extracted the class's read-only reporting surface
(`GetUsageSummary`/`GetUsageRollup`/`GetRoutingRoiAsync`/rate-limit projections) into
`ManagementReportingService`, cutting the class from 2090 to 1926 lines. What remains is entirely
write/security-boundary surface, which is why that same plan's step 3 deferred further splitting
pending this ADR: shrinking a security boundary is a design decision about what "the boundary" means
afterward, not a mechanical extraction.

A second, independent audit (the "brutal-cozy-pascal" structural audit) re-confirmed the same finding
from scratch and reached the same conclusion — split along the CRUD-aggregate lines already implicit
in the method names, but decide that deliberately rather than mechanically.

## Decision Drivers

- **Security-boundary legibility** — a reviewer diffing one corner of provider/budget/secret handling
  should not have to hold a 1926-line file in working memory to reason about the change's safety.
- **Zero observable behavior change** — `ProviderAdminEndpoints`, `ProviderMcpTools`, `ProxyServer`,
  `McpHostedService`, and all 13 existing test files call `ManagementFacade` today; none of them should
  need to change their call sites as a result of this decision.
- **Reuse the established pattern** — `ManagementFacade` already delegates to optional collaborators
  via `ManagementFacadeDependencies` (budget store, endpoint scanner, capability store, price catalog,
  override store, secret reader/writer, interaction status store); a split should extend that pattern,
  not invent a new one.
- **Don't relitigate what "the security boundary" means without writing it down** — the class's own doc
  comment makes a claim about itself that a structural split would falsify if not re-stated deliberately.

## Considered Options

- Option A — Leave `ManagementFacade` as one class; only move its 22 colocated DTO/enum/record types
  to their own file
- Option B — Split into internal collaborators along the CRUD-aggregate boundary (provider CRUD +
  capability scanning; budget + price-override CRUD; secret read/write), each reachable only through
  `ManagementFacade`'s existing public methods (internal delegation, no public surface change)
- Option C — Split into separate publicly-injectable services (like `ManagementReportingService`),
  changing `ProviderAdminEndpoints`/`ProviderMcpTools` to depend on multiple facades directly

## Decision Outcome

Chosen option: "Option B", because it directly addresses **security-boundary legibility** (each
collaborator class is small enough to review in full) while satisfying **zero observable behavior
change** and **reuse the established pattern** — `ManagementFacade` keeps being the one class every
caller depends on and the one place "is this a security-sensitive operation" is answered, it just stops
being the place where every security-sensitive operation's *implementation* lives.

The security boundary, after this split, is redefined as: **`ManagementFacade`'s public method set**,
not its file. Any code path that mutates provider/model/budget/price-override/secret state must still
go through a `ManagementFacade` public method — the three new internal collaborators
(`ProviderManagementService`, `BudgetAndPriceOverrideService`, `SecretManagementService`, naming TBD at
implementation time) are constructor-injected into `ManagementFacade` itself and are not registered in
DI as independently reachable services. This preserves "one security boundary, one place to audit
which callers can mutate what" while fixing the file-size/cohesion problem.

### Consequences

- Good, because a future review of, say, secret-handling logic touches a ~300-line file instead of a
  1926-line one.
- Good, because `ProviderAdminEndpoints`, `ProviderMcpTools`, `ProxyServer`, `McpHostedService`, and all
  13 existing test files need zero changes — this is pure internal delegation.
- Neutral, because `ManagementFacade`'s constructor grows a few more required internal collaborators
  (the new services), while `ManagementFacadeDependencies`' optional bag stays the same shape — those
  optional collaborators get threaded through to whichever new service now owns them.
- Bad, because introducing three new internal classes without a corresponding public-surface change
  makes the security boundary slightly less self-evident from a file listing alone (a reader has to
  know `ManagementFacade` is still "the" boundary even though the mutating code lives elsewhere) —
  mitigated by keeping this ADR linked from `ManagementFacade`'s class-level doc comment.

## Pros and Cons of the Options

### Option A — DTO move only, no class split

- Good, because it is zero-risk and can happen immediately regardless of this ADR's outcome (and does
  — it's implemented alongside this ADR either way).
- Bad, because it does not address the actual problem: the 1926 remaining lines of executable logic are
  untouched, and the security-boundary legibility concern this ADR exists to resolve is not resolved.

### Option B — Internal collaborators, no public surface change (chosen)

- Good, because it fixes cohesion/size without touching any caller or test.
- Good, because it extends the existing `ManagementFacadeDependencies` optional-collaborator pattern
  rather than inventing a new DI shape.
- Bad, because `ManagementFacade` itself still exists as a (now thinner) pass-through layer — some
  readers may find "the security boundary is the public method set of a mostly-delegating class"
  subtler than a fully separated public service.

### Option C — Separate publicly-injectable services

- Good, because it would shrink `ManagementFacade` the most and give each concern (provider CRUD,
  budget/price-override, secrets) its own independently-testable, independently-injectable service —
  the cleanest long-term shape if the security-boundary framing were dropped.
- Bad, because it changes the public surface: `ProviderAdminEndpoints` (19 call sites) and
  `ProviderMcpTools` (8 call sites) would need to take multiple new dependencies instead of one
  `ManagementFacade`, and — more importantly — it disperses "the single security boundary" across
  three independently-reachable services with no single class left to audit as *the* place management
  mutations are gated. Rejected because it directly conflicts with the class's own stated purpose and
  would itself need to be a separate, larger ADR about whether the security-boundary framing should be
  abandoned — not a decision to make as a side effect of a refactor.

## More Information

Implements `docs/router/code-smell-refactoring-plan.md` Phase 3 step 3, and item C2 of the
brutal-cozy-pascal structural audit. The DTO/enum/record file move (this ADR's Option A, done
unconditionally) corresponds to that audit's item m5.
