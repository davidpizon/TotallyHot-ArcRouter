# 0007. ProviderAdminClient stays on HTTP; document the split instead of migrating it

**Status:** accepted <!-- proposed | accepted | rejected | deprecated | superseded by ADR-NNNN -->
**Date:** 2026-09-02
**Deciders:** David Pizon

## Context and Problem Statement

`src/TotallyHotArcRouter.Gui.Admin/ProviderAdminClient.cs` talks to the router's management API over
raw `HttpClient`, while the nine (soon ten, once
[C4/`PersistedSessionsClient`](../router/code-smell-refactoring-plan.md) lands) other Governance-panel
clients in `TotallyHotArcRouter.Gui.Telemetry` all extend the shared `GrpcAdminClientBase<,>` and talk
gRPC. Both the original `code-smell-refactoring-plan.md` survey and the independent brutal-cozy-pascal
audit flagged this as an inconsistency worth a documented decision rather than a guess: same
conceptual role (a Governance-panel client talking to the router), two different transports, no
recorded reason either way.

The historical reason `ProviderAdminClient` was built on HTTP rather than gRPC is **not known** — it
predates this survey and no one currently on the project recalls a deliberate reason. This ADR records
that explicitly rather than inventing a plausible-sounding justification after the fact.

## Decision Drivers

- **Migration cost vs. benefit** — `ProviderAdminClient` is the client behind all provider/model CRUD
  from the Governance UI; migrating it touches every one of those call sites plus the UI's error
  handling, which today branches on `ProviderAdminException` vs. the gRPC clients'
  `IsUnavailable`-flagged exception shape — a genuinely different exception contract, not just a
  renamed type.
- **Stop the pattern from decaying further** — before this audit, `PersistedSessionsClient` had already
  introduced a *third* pattern (gRPC, but hand-rolled instead of extending `GrpcAdminClientBase<,>`).
  That's fixed independently (item C4), but its existence is evidence that "no documented rule" lets
  new clients drift from precedent.
- **Honesty over invented rationale** — recording a plausible-sounding reason (e.g. "TLS/cert
  avoidance") that nobody can confirm would be worse than admitting the reason is unknown; a future
  reader acting on a fabricated rationale is a real risk this ADR should not introduce.

## Considered Options

- Option A — Migrate `ProviderAdminClient` to gRPC, extending `GrpcAdminClientBase<,>` like every other
  admin client
- Option B — Keep `ProviderAdminClient` on HTTP; record that the historical reason is unknown, and that
  the current codebase has no active requirement forcing either transport
- Option C — No ADR; leave the inconsistency undocumented

## Decision Outcome

Chosen option: "Option B", because **migration cost vs. benefit** weighs clearly against a migration
with no identified defect it would fix (this is a consistency concern, not a correctness or security
one), and **honesty over invented rationale** rules out writing a justification for the original HTTP
choice that cannot be substantiated. This still satisfies **stop the pattern from decaying further**:
the next engineer adding a new admin client now has a written rule to follow — extend
`GrpcAdminClientBase<,>` — even though this ADR doesn't change `ProviderAdminClient` itself.

Going forward: **any new Governance-panel admin client must use gRPC via `GrpcAdminClientBase<,>`**,
matching the other ten. `ProviderAdminClient` is the sole, explicitly-grandfathered exception, and
stays HTTP-based until or unless a future ADR identifies a concrete reason to migrate it (e.g. a defect
in the HTTP path, or a broader initiative to retire `HttpClient`-based admin surfaces).

### Consequences

- Good, because this closes the "was this deliberate or an oversight" question a future reader would
  otherwise have to reverse-engineer from a diff or ask around for.
- Good, because it gives a concrete, written rule (new admin clients → gRPC) without spending the
  migration cost on an existing client that has no known correctness reason to move.
- Neutral, because `ProviderAdminClient`'s `ProviderAdminException` vs. the gRPC clients'
  `IsUnavailable`-flagged exception shape remains a real, if narrow, asymmetry in the Governance UI's
  error handling — accepted as the cost of not migrating, not fixed by this ADR.
- Bad, because the codebase keeps two transport patterns indefinitely, which is a small ongoing
  cognitive cost for anyone touching Governance-panel networking code — mitigated by this ADR being the
  answer when that question comes up.

## Pros and Cons of the Options

### Option A — Migrate to gRPC

- Good, because it would make the codebase's admin-client story fully uniform — one transport, one
  base class, one exception shape.
- Bad, because both audits independently rate this High risk: it touches every provider CRUD call site
  in the Gui.Admin project and the Governance UI's error handling, which currently branches on
  `ProviderAdminException` vs. `IsUnavailable`-flagged gRPC exceptions — a real behavioral surface, not
  a mechanical rename.
- Bad, because there is no defect or requirement driving the migration today — it would be pure
  consistency work competing for review time against items with an actual identified risk or bug (e.g.
  C1, C3, M9's usage-extraction gap).

### Option B — Keep HTTP, document why (chosen)

- Good, because it costs nothing beyond writing this document and closes the open question definitively.
- Good, because it establishes a forward rule without requiring anyone to justify a past decision no
  one can actually reconstruct.
- Bad, because it leaves the underlying two-transport reality in place — a reader still has to know
  this ADR exists to understand why `ProviderAdminClient` looks different from its ten siblings.

### Option C — No ADR

- Good, because it requires no effort.
- Bad, because it leaves exactly the ambiguity both audits flagged: a future contributor (or this audit
  process, again) rediscovers the inconsistency with no record of whether it was ever considered,
  wasting the same investigation effort a second time.

## More Information

Corresponds to item M5 of the brutal-cozy-pascal structural audit and the "Inconsistent client
transport" backlog item in `docs/router/code-smell-refactoring-plan.md` Phase 4. The related,
independently-resolved third-pattern regression (`PersistedSessionsClient` hand-rolling gRPC instead of
extending `GrpcAdminClientBase<,>`, audit item C4) is what motivated re-raising this question now
rather than leaving it further deferred; that fix ships as its own change, not as part of this ADR.
