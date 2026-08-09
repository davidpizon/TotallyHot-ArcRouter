# Current Implementation Plan: Remaining Work Only

This plan excludes completed phases and tracks only unfinished work.

## Active Phases

### Phase E: Advanced Price Tier Support
- Add batch/cached/multimodal tier support in schema, ingestion, and lookup estimation.
- Add regression tests for tier permutations and graceful fallback behavior.
- Exit: tier-aware cost estimates are correct for supported providers.

### Phase F: Deferred GUI Backlog Completion
- Add missing telemetry fields for currently mock-backed tabs/metrics.
- Implement GUI settings persistence and harden loopback gRPC stream behavior.
- Keep dialogs/modals aligned with `docs/gui/DESIGN.md`.
- Reference: `docs/gui/backlog.md`.
- Exit: remaining GUI views run on live data and settings survive restart.

## Final Validation Gate
1. `dotnet build` passes with zero warnings/errors.
2. Relevant tests pass for each changed phase.
3. Documentation is updated to match delivered behavior.
4. Any intentionally deferred items are explicitly documented with rationale.
